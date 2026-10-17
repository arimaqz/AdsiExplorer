using System.Text;
using System.Text.Json;

namespace AdsiExplorer;

internal sealed class InteractiveShell
{
    private readonly ShellState _state = new();
    private readonly bool _quiet;

    public InteractiveShell(bool quiet) => _quiet = quiet;

    public int ExitCode => _state.ExitCode;

    public void ConnectStartup(string? server, string? userName, string? password, string? baseDn, bool useGlobalCatalog)
    {
        _state.Session = AdsiSession.Connect(server, baseDn, userName, password, useGlobalCatalog);
        if (!_quiet)
        {
            var target = server ?? "(default domain)";
            Console.WriteLine($"Connected to {target} (Base DN: {_state.Session.BaseDn})");
        }
    }

    public void Run(IReadOnlyList<string>? commands)
    {
        if (commands is null || commands.Count == 0)
        {
            PrintHelp();
        }

        if (commands is not null && commands.Count > 0)
        {
            foreach (var line in commands)
            {
                Console.WriteLine($"adsi> {line}");
                try
                {
                    if (!ExecuteLine(line))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: {ex.Message}");
                    _state.ExitCode = 1;
                }
            }

            return;
        }

        while (true)
        {
            Console.Write("adsi> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            try
            {
                if (!ExecuteLine(line))
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: {ex.Message}");
            }
        }
    }

    private bool ExecuteLine(string line)
    {
        line = ExpandAlias(line);
        ShellPersistence.AppendHistory(line);
        _state.History.Add(line);

        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            return true;
        }

        var verb = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();

        switch (verb)
        {
            case "connect":
                Connect(args);
                break;
            case "status":
                Status();
                break;
            case "list":
                List(args);
                break;
            case "search":
                Search(args);
                break;
            case "show":
                Show(args);
                break;
            case "props":
                Props(args);
                break;
            case "limit":
                Limit(args);
                break;
            case "page":
                Page(args);
                break;
            case "export":
                Export(args);
                break;
            case "base":
                Base(args);
                break;
            case "width":
                Width(args);
                break;
            case "count":
                Count(args);
                break;
            case "next":
                Next();
                break;
            case "context":
                Context(args);
                break;
            case "alias":
                Alias(args);
                break;
            case "history":
                History(args);
                break;
            case "resolve":
                ResolveSid(args);
                break;
            case "schema":
                Schema(args);
                break;
            case "members":
                Members(args);
                break;
            case "memberof":
                MemberOf(args);
                break;
            case "tree":
                Tree();
                break;
            case "help":
            case "?":
                if (args.Count > 0)
                {
                    PrintTopicHelp(args[0]);
                }
                else
                {
                    PrintHelp();
                }
                break;
            case "quit":
            case "exit":
                return false;
            default:
                throw new InvalidOperationException($"Unknown command: {tokens[0]}. Type help.");
        }

        return true;
    }

    private void Connect(List<string> args)
    {
        string? server = null;
        string? user = null;
        string? baseDn = null;
        var useGc = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token.Equals("-server", StringComparison.OrdinalIgnoreCase))
            {
                server = args[++i];
            }
            else if (token.Equals("-user", StringComparison.OrdinalIgnoreCase) || token.Equals("-username", StringComparison.OrdinalIgnoreCase))
            {
                user = args[++i];
            }
            else if (token.Equals("-base", StringComparison.OrdinalIgnoreCase) || token.Equals("-basedn", StringComparison.OrdinalIgnoreCase))
            {
                baseDn = args[++i];
            }
            else if (token.Equals("-gc", StringComparison.OrdinalIgnoreCase))
            {
                useGc = true;
            }
            else if (server is null)
            {
                server = token;
            }
            else if (user is null)
            {
                user = token;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected connect argument: {token}");
            }
        }

        string? password = null;
        if (!string.IsNullOrWhiteSpace(user))
        {
            Console.Write($"Password for {user}: ");
            password = ReadPassword();
        }

        _state.Session = AdsiSession.Connect(server, baseDn, user, password, useGc);
        _state.LastSearch = null;
        _state.LastResults.Clear();

        var target = server ?? "(default domain)";
        Console.WriteLine($"Connected to {target}");
        Console.WriteLine($"  Base DN: {_state.Session.BaseDn}");
        if (useGc)
        {
            Console.WriteLine("  Global Catalog: yes");
        }
    }

    private void List(List<string> args) => RunQuery(ParseQueryOptions(args, isSearch: false), 0);

    private void Search(List<string> args) => RunQuery(ParseQueryOptions(args, isSearch: true), 0);

    private void Count(List<string> args)
    {
        var plan = ParseQueryOptions(args, isSearch: false) with { IsCount = true };
        RunQuery(plan, 0);
    }

    private void Next()
    {
        if (_state.LastQueryPlan is null)
        {
            throw new InvalidOperationException("No previous query. Run list or search first.");
        }

        if (_state.LastSearch is null || !_state.LastSearch.HasMore)
        {
            throw new InvalidOperationException("No more results for the last query.");
        }

        RunQuery(_state.LastQueryPlan, _state.QuerySkip + _state.MaxResults);
    }

    private void RunQuery(QueryPlan plan, int skip)
    {
        EnsureConnected();
        _state.ObjectType = plan.ObjectType;
        _state.LastDisplayProperties = plan.Properties;
        _state.LastQueryPlan = plan;
        _state.QuerySkip = skip;

        var output = AdsiSearchService.Search(
            _state.Session!,
            plan.ObjectType,
            plan.Filter,
            plan.Attribute,
            plan.Value,
            plan.Properties,
            _state.PageSize,
            plan.IsCount ? 0 : _state.MaxResults,
            skip,
            plan.IsCount,
            plan.SortProperty);

        if (plan.IsCount)
        {
            Console.WriteLine($"Filter: {output.Filter}");
            Console.WriteLine($"Count: {output.TotalCount ?? 0}");
            return;
        }

        StoreResults(output);
        PrintQueryHeader(output);
        PrintTable(_state.LastResults, plan.Properties);
    }

    private string ExpandAlias(string line)
    {
        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            return line;
        }

        if (_state.Aliases.TryGetValue(tokens[0], out var expansion))
        {
            var rest = tokens.Count > 1 ? " " + string.Join(' ', tokens.Skip(1)) : string.Empty;
            return expansion + rest;
        }

        return line;
    }

    private void Context(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            Console.WriteLine($"Search base: {_state.Session!.SearchBaseDn}");
            return;
        }

        var dn = _state.Session!.SetNamingContext(args[0]);
        Console.WriteLine($"Search base set to {dn} ({args[0]})");
    }

    private void Alias(List<string> args)
    {
        if (args.Count < 2)
        {
            foreach (var pair in _state.Aliases)
            {
                Console.WriteLine($"{pair.Key} => {pair.Value}");
            }

            return;
        }

        if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in _state.Aliases)
            {
                Console.WriteLine($"{pair.Key} => {pair.Value}");
            }

            return;
        }

        var name = args[0].ToLowerInvariant();
        var command = string.Join(' ', args.Skip(1));
        _state.Aliases[name] = command;
        ShellPersistence.SaveAliases(_state.Aliases);
        Console.WriteLine($"Alias set: {name} => {command}");
    }

    private void History(List<string> args)
    {
        var count = args.Count > 0 ? int.Parse(args[0]) : 20;
        foreach (var item in _state.History.TakeLast(count))
        {
            Console.WriteLine(item);
        }
    }

    private void ResolveSid(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: resolve <sid>");
        }

        Console.WriteLine(AdsiDirectoryAdvanced.ResolveSid(_state.Session!, args[0]));
    }

    private void Schema(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: schema <className>");
        }

        AdsiDirectoryAdvanced.PrintSchemaClass(_state.Session!, args[0]);
    }

    private void Members(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: members <#|dn> [-recursive]");
        }

        var recursive = args.Contains("-recursive", StringComparer.OrdinalIgnoreCase);
        var reference = string.Join(' ', args.Where(a => !a.Equals("-recursive", StringComparison.OrdinalIgnoreCase)));
        var dn = ResolveReferenceDn(reference);
        AdsiDirectoryAdvanced.PrintMembers(_state.Session!, dn, recursive);
    }

    private void MemberOf(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: memberof <#|dn> [-recursive]");
        }

        var recursive = args.Contains("-recursive", StringComparer.OrdinalIgnoreCase);
        var reference = string.Join(' ', args.Where(a => !a.Equals("-recursive", StringComparison.OrdinalIgnoreCase)));
        var dn = ResolveReferenceDn(reference);
        AdsiDirectoryAdvanced.PrintMemberOf(_state.Session!, dn, recursive);
    }

    private void Tree()
    {
        EnsureConnected();
        PrintOuTree(_state.Session!.SearchBaseDn, 0);
    }

    private void PrintOuTree(string baseDn, int depth)
    {
        var saved = _state.Session!.SearchBaseDn;
        _state.Session.SetSearchBase(baseDn);
        var output = AdsiSearchService.Search(
            _state.Session,
            ObjectType.OrganizationalUnits,
            null,
            null,
            null,
            ["distinguishedName", "name"],
            500,
            0,
            0,
            false,
            "name");

        foreach (var ou in output.Results)
        {
            var name = ou.GetValueOrDefault("name")?.ToString() ?? "?";
            var dn = ou.GetValueOrDefault("distinguishedName")?.ToString() ?? string.Empty;
            Console.WriteLine($"{new string(' ', depth * 2)}{name} [{dn}]");
            if (!string.IsNullOrEmpty(dn))
            {
                PrintOuTree(dn, depth + 1);
            }
        }

        _state.Session.SetSearchBase(saved);
    }

    private string ResolveReferenceDn(string reference)
    {
        if (int.TryParse(reference, out var index))
        {
            var hit = _state.LastResults.FirstOrDefault(r => r.Index == index)
                ?? throw new InvalidOperationException($"No result #{index}.");
            return hit.Object.GetValueOrDefault("distinguishedName")?.ToString()
                ?? throw new InvalidOperationException("Missing distinguishedName.");
        }

        return reference;
    }

    private void Show(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: show <#|distinguishedName> [-all]");
        }

        var showAll = args.Any(a => a.Equals("-all", StringComparison.OrdinalIgnoreCase));
        var refTokens = args.Where(a => !a.Equals("-all", StringComparison.OrdinalIgnoreCase)).ToList();
        if (refTokens.Count == 0)
        {
            throw new InvalidOperationException("Usage: show <#|distinguishedName> [-all]");
        }

        var reference = string.Join(' ', refTokens);
        string? dn;
        if (int.TryParse(reference, out var index))
        {
            var hit = _state.LastResults.FirstOrDefault(r => r.Index == index)
                ?? throw new InvalidOperationException($"No result #{index}. Run list or search first.");
            dn = hit.Object.TryGetValue("distinguishedName", out var dnValue) ? dnValue?.ToString() : null;
        }
        else
        {
            dn = reference;
        }

        if (string.IsNullOrWhiteSpace(dn))
        {
            throw new InvalidOperationException("Could not resolve distinguishedName for show.");
        }

        Dictionary<string, object?> obj = showAll
            ? AdsiSearchService.ReadAllObjectProperties(_state.Session!, dn)
            : LoadByDn(dn, _state.DetailProperties);

        PrintObject(obj);
    }

    private Dictionary<string, object?> LoadByDn(string dn, IReadOnlyList<string> properties)
    {
        var escaped = LdapFilter.EscapeValue(dn);
        var output = AdsiSearchService.Search(
            _state.Session!,
            ObjectType.All,
            $"(distinguishedName={escaped})",
            null,
            null,
            properties,
            _state.PageSize,
            1);

        if (output.Results.Count == 0)
        {
            throw new InvalidOperationException($"Object not found: {dn}");
        }

        return output.Results[0];
    }

    private void Props(List<string> args)
    {
        if (args.Count == 0)
        {
            Console.WriteLine($"List properties: {string.Join(", ", _state.ListProperties)}");
            Console.WriteLine($"Detail properties: {string.Join(", ", _state.DetailProperties)}");
            Console.WriteLine();
            Console.WriteLine("Presets: minimal, user, group, computer, ou, detail");
            Console.WriteLine("Usage:");
            Console.WriteLine("  props list <preset|attr1,attr2,...>");
            Console.WriteLine("  props detail <preset|attr1,attr2,...>");
            return;
        }

        var target = args[0].ToLowerInvariant();
        var value = args.Count > 1 ? string.Join(' ', args.Skip(1)) : null;
        if (target is not ("list" or "detail") || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Usage: props {target} <preset|attr1,attr2,...>");
        }

        var props = PropertyPresets.Resolve(value);
        if (target == "list")
        {
            _state.ListProperties = props;
            Console.WriteLine($"List properties set: {string.Join(", ", props)}");
        }
        else
        {
            _state.DetailProperties = props;
            Console.WriteLine($"Detail properties set: {string.Join(", ", props)}");
        }
    }

    private void Limit(List<string> args)
    {
        if (args.Count == 0)
        {
            Console.WriteLine($"MaxResults: {_state.MaxResults}");
            return;
        }

        var n = int.Parse(args[0]);
        if (n < 0)
        {
            throw new InvalidOperationException("MaxResults must be >= 0 (0 = no limit).");
        }

        _state.MaxResults = n;
        Console.WriteLine($"MaxResults set to {n}");
    }

    private void Page(List<string> args)
    {
        if (args.Count == 0)
        {
            Console.WriteLine($"PageSize: {_state.PageSize}");
            return;
        }

        var n = int.Parse(args[0]);
        if (n < 1)
        {
            throw new InvalidOperationException("PageSize must be >= 1.");
        }

        _state.PageSize = n;
        Console.WriteLine($"PageSize set to {n}");
    }

    private void Base(List<string> args)
    {
        EnsureConnected();
        if (args.Count == 0)
        {
            Console.WriteLine($"Search base: {_state.Session!.SearchBaseDn}");
            return;
        }

        var target = string.Join(' ', args);
        if (target.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _state.Session.SetSearchBase(_state.Session.BaseDn);
            Console.WriteLine($"Search base reset to {_state.Session.BaseDn}");
            return;
        }

        _state.Session.SetSearchBase(target);
        Console.WriteLine($"Search base set to {target}");
    }

    private void Width(List<string> args)
    {
        if (args.Count == 0)
        {
            Console.WriteLine($"Table cell width: {_state.TableCellWidth}");
            return;
        }

        var n = int.Parse(args[0]);
        if (n < 0)
        {
            throw new InvalidOperationException("Width must be >= 0 (0 = no truncation).");
        }

        _state.TableCellWidth = n;
        Console.WriteLine($"Table cell width set to {n}");
    }

    private void Export(List<string> args)
    {
        if (_state.LastResults.Count == 0)
        {
            throw new InvalidOperationException("Nothing to export. Run list or search first.");
        }

        var useDetail = args.Any(a => a.Equals("-detail", StringComparison.OrdinalIgnoreCase));
        var path = args.FirstOrDefault(a => !a.StartsWith('-')) ?? throw new InvalidOperationException("Usage: export <file.json|file.csv> [-detail]");

        var rows = new List<Dictionary<string, object?>>();
        foreach (var hit in _state.LastResults)
        {
            var obj = hit.Object;
            if (useDetail && obj.TryGetValue("distinguishedName", out var dnObj) && dnObj?.ToString() is { } dn)
            {
                obj = LoadByDn(dn, _state.DetailProperties);
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Index"] = hit.Index };
            foreach (var (key, value) in obj)
            {
                row[key] = Flatten(value);
            }

            rows.Add(row);
        }

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            WriteCsv(path, rows);
        }
        else
        {
            throw new InvalidOperationException("Export path must end with .json or .csv");
        }

        Console.WriteLine($"Exported {rows.Count} row(s) to {path}");
    }

    private void Status()
    {
        if (_state.Session is null)
        {
            Console.WriteLine("Not connected.");
            return;
        }

        Console.WriteLine($"Server: {_state.Session.Server ?? "(default)"}");
        Console.WriteLine($"Base DN: {_state.Session.BaseDn}");
        Console.WriteLine($"Search base: {_state.Session.SearchBaseDn}");
        Console.WriteLine($"Global Catalog: {_state.Session.UseGlobalCatalog}");
        Console.WriteLine($"Object type: {_state.ObjectType}");
        Console.WriteLine($"MaxResults: {_state.MaxResults}");
        Console.WriteLine($"PageSize: {_state.PageSize}");
        Console.WriteLine($"Last result count: {_state.LastResults.Count}");
        if (_state.LastSearch is not null)
        {
            Console.WriteLine($"Last filter: {_state.LastSearch.Filter}");
        }
    }

    private void StoreResults(SearchOutput output)
    {
        _state.LastSearch = output;
        _state.LastResults.Clear();
        var index = 0;
        foreach (var obj in output.Results)
        {
            _state.LastResults.Add(new IndexedResult(++index, obj));
        }
    }

    private void PrintQueryHeader(SearchOutput output)
    {
        Console.WriteLine($"Filter: {output.Filter}");
        Console.WriteLine($"Search base: {_state.Session!.SearchBaseDn}");
        var suffix = output.Truncated ? " (page full)" : string.Empty;
        var more = output.HasMore ? " — type next for more" : string.Empty;
        var skip = output.Skip > 0 ? $" (skip {output.Skip})" : string.Empty;
        Console.WriteLine($"Returned {output.Results.Count} object(s){skip}{suffix}{more}.");
    }

    private void PrintTable(IReadOnlyList<IndexedResult> results, IReadOnlyList<string> properties)
    {
        if (results.Count == 0)
        {
            Console.WriteLine("No results.");
            return;
        }

        var headers = new List<string> { "#" };
        headers.AddRange(properties);

        var rows = new List<string[]>();
        foreach (var row in results)
        {
            var cells = new string[headers.Count];
            cells[0] = row.Index.ToString();
            for (var i = 0; i < properties.Count; i++)
            {
                row.Object.TryGetValue(properties[i], out var value);
                var text = FormatCell(value);
                if (properties[i].Equals("samAccountName", StringComparison.OrdinalIgnoreCase)
                    && UserAccountControlHelper.IsDisabled(row.Object.GetValueOrDefault("userAccountControl"))
                    && !string.IsNullOrEmpty(text))
                {
                    text = "[D] " + text;
                }

                cells[i + 1] = text;
            }

            rows.Add(cells);
        }

        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            var max = headers[i].Length;
            foreach (var row in rows)
            {
                max = Math.Max(max, row[i].Length);
            }

            widths[i] = max;
        }

        Console.WriteLine(FormatTableRow(headers.ToArray(), widths));
        Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));

        for (var i = 0; i < rows.Count; i++)
        {
            var line = FormatTableRow(rows[i], widths);
            if (UserAccountControlHelper.IsDisabled(results[i].Object.GetValueOrDefault("userAccountControl")))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(line);
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    private string FormatCell(object? value)
    {
        var text = Flatten(value)?.ToString() ?? string.Empty;
        if (_state.TableCellWidth > 0 && text.Length > _state.TableCellWidth)
        {
            return text[..Math.Max(0, _state.TableCellWidth - 3)] + "...";
        }

        return text;
    }

    private static object? Flatten(object? value) =>
        value switch
        {
            null => null,
            Array array => string.Join("; ", array.Cast<object?>().Select(Flatten)),
            IEnumerable<object> items => string.Join("; ", items.Select(Flatten)),
            _ => value
        };

    private static void PrintObject(Dictionary<string, object?> obj)
    {
        foreach (var key in obj.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var value = Flatten(obj[key]);
            Console.WriteLine($"{key} : {value}");
        }
    }

    private static string FormatTableRow(string[] cells, int[] widths)
    {
        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var text = cells[i];
            var width = widths[i];
            if (text.Length > width)
            {
                text = text[..width];
            }

            parts[i] = text.PadRight(width);
        }

        return string.Join(" | ", parts);
    }

    private static void WriteCsv(string path, List<Dictionary<string, object?>> rows)
    {
        var headers = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var writer = new StreamWriter(path);
        writer.WriteLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            var line = headers.Select(h => EscapeCsv(row.TryGetValue(h, out var v) ? Flatten(v)?.ToString() : null));
            writer.WriteLine(string.Join(",", line));
        }
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private QueryPlan ParseQueryOptions(List<string> args, bool isSearch)
    {
        if (isSearch && args.Count < 2)
        {
            throw new InvalidOperationException("Usage: search <attribute> <value> [-type users|groups|computers|ous|all] [-props preset|a,b,c]");
        }

        var objectType = _state.ObjectType;
        string? filter = null;
        string? attribute = null;
        string? value = null;
        string? propsSpec = null;
        string? queryName = null;
        string? sortProperty = null;
        var staleDays = _state.DefaultStaleDays;
        var index = 0;

        if (isSearch)
        {
            attribute = args[0];
            value = args[1];
            index = 2;
        }

        while (index < args.Count)
        {
            var token = args[index];
            if (token.Equals("-filter", StringComparison.OrdinalIgnoreCase))
            {
                filter = args[++index];
            }
            else if (token.Equals("-type", StringComparison.OrdinalIgnoreCase))
            {
                objectType = ParseObjectType(args[++index]);
            }
            else if (token.Equals("-props", StringComparison.OrdinalIgnoreCase))
            {
                propsSpec = args[++index];
            }
            else if (token.Equals("-query", StringComparison.OrdinalIgnoreCase))
            {
                queryName = args[++index];
            }
            else if (token.Equals("-sort", StringComparison.OrdinalIgnoreCase))
            {
                sortProperty = args[++index];
            }
            else if (token.Equals("-days", StringComparison.OrdinalIgnoreCase))
            {
                staleDays = int.Parse(args[++index]);
            }
            else if (!isSearch)
            {
                objectType = ParseObjectType(token);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected argument: {token}");
            }

            index++;
        }

        if (queryName is not null)
        {
            filter = AdsiNamedQueries.Build(queryName, staleDays);
        }

        if (attribute is not null)
        {
            var attrLower = attribute.ToLowerInvariant();
            if (attrLower is "anr" or "objectsid" or "objectguid")
            {
                filter = LdapFilter.BuildIdentityFilter(objectType, attribute, value!);
                attribute = null;
                value = null;
            }
        }

        var properties = propsSpec is not null
            ? PropertyPresets.Resolve(propsSpec)
            : _state.ListProperties;

        return new QueryPlan(objectType, filter, attribute, value, properties, sortProperty, false);
    }

    private static ObjectType ParseObjectType(string name) =>
        name.ToLowerInvariant() switch
        {
            "user" or "users" => ObjectType.Users,
            "group" or "groups" => ObjectType.Groups,
            "computer" or "computers" => ObjectType.Computers,
            "ou" or "ous" or "organizationalunit" or "organizationalunits" => ObjectType.OrganizationalUnits,
            "all" => ObjectType.All,
            _ => throw new InvalidOperationException($"Unknown object type '{name}'.")
        };

    private void EnsureConnected()
    {
        if (_state.Session is null)
        {
            throw new InvalidOperationException("Not connected. Run: connect [server] [domain\\user]");
        }
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var inQuote = false;
        char quoteChar = '"';

        foreach (var c in line)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string ReadPassword()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            sb.Append(key.KeyChar);
        }

        return sb.ToString();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Commands:
  connect [server] [domain\user]   Connect (prompts for password if user set)
  connect -server X -user Y -gc  Named connect options
  status                           Show session settings
  list [users|groups|...]          List objects (current type)
  list -filter "(...)"             List with custom LDAP filter
  list users -props user           One-off property set for this query
  search <attr> <value>              Search by attribute (supports * wildcards)
  show <#|distinguishedName>       Show details for last result index or DN
  show 1 -all                      All attributes via LDAP object read
  base [dn|reset]                  Scope list/search to an OU/container
  width [chars]                    Truncate table cells (0 = full width)
  props list|detail <preset|...>   Configure default columns/detail fields
  limit [n]                        Max rows returned (0 = unlimited)
  page [n]                         LDAP page size
  export out.json|out.csv          Export last result set
  export out.csv -detail           Export with detail property reload
  count [type] [-query name]       Count matches without listing rows
  next                             Next page of last list/search
  context domain|config|schema     Switch naming context search base
  alias name cmd... | alias list   Saved command shortcuts
  history [n]                      Recent commands
  help [list|search]               Help topics
  quit | exit                      Exit

Named queries (-query): disabled, locked, pwd-never-expires, spn, empty-groups, stale-computers (-days 90)
Presets: minimal, user, group, computer, ou, detail
""");
    }

    private static void PrintTopicHelp(string topic)
    {
        switch (topic.ToLowerInvariant())
        {
            case "list":
                Console.WriteLine("list [type] [-filter \"(...)\"] [-query name] [-days N] [-props preset] [-sort attr]");
                break;
            case "search":
                Console.WriteLine("search <attr> <value> [-type users|...] [-props preset] [-sort attr]");
                Console.WriteLine("Special attrs: anr, objectSid, objectGUID");
                break;
            default:
                PrintHelp();
                break;
        }
    }
}
