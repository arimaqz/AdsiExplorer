namespace AdsiExplorer;

internal static class AdsiDirectoryAdvanced
{
    public static string ResolveSid(AdsiSession session, string sid)
    {
        var output = AdsiSearchService.Search(
            session,
            ObjectType.All,
            $"(objectSid={sid})",
            null,
            null,
            ["distinguishedName", "samAccountName"],
            1000,
            1);

        if (output.Results.Count == 0)
        {
            return sid;
        }

        var obj = output.Results[0];
        var sam = obj.GetValueOrDefault("samAccountName")?.ToString();
        return sam is not null ? $"{sam} ({sid})" : $"{obj.GetValueOrDefault("distinguishedName")} ({sid})";
    }

    public static void PrintSchemaClass(AdsiSession session, string className)
    {
        var saved = session.SearchBaseDn;
        session.SetNamingContext("schema");
        try
        {
            var output = AdsiSearchService.Search(
                session,
                ObjectType.All,
                $"(&(objectClass=classSchema)(|(lDAPDisplayName={className})(cn={className})))",
                null,
                null,
                ["lDAPDisplayName", "mustContain", "systemMustContain", "mayContain", "systemMayContain", "subClassOf"],
                500,
                1);

            if (output.Results.Count == 0)
            {
                throw new InvalidOperationException($"Schema class not found: {className}");
            }

            var obj = output.Results[0];
            var attrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "mustContain", "systemMustContain", "mayContain", "systemMayContain" })
            {
                if (!obj.TryGetValue(key, out var val) || val is null)
                {
                    continue;
                }

                if (val is IEnumerable<object> items)
                {
                    foreach (var item in items)
                    {
                        attrs.Add(item.ToString() ?? string.Empty);
                    }
                }
                else
                {
                    attrs.Add(val.ToString() ?? string.Empty);
                }
            }

            Console.WriteLine($"Class: {className} (subClassOf: {obj.GetValueOrDefault("subClassOf")})");
            Console.WriteLine("Attributes: " + string.Join(", ", attrs.OrderBy(a => a)));
        }
        finally
        {
            session.SetSearchBase(saved);
        }
    }

    public static void PrintMembers(AdsiSession session, string dn, bool recursive)
    {
        var output = AdsiSearchService.Search(
            session,
            ObjectType.All,
            $"(distinguishedName={LdapFilter.EscapeValue(dn)})",
            null,
            null,
            ["member", "samAccountName"],
            1000,
            1);

        if (output.Results.Count == 0)
        {
            throw new InvalidOperationException($"Object not found: {dn}");
        }

        var members = output.Results[0].GetValueOrDefault("member");
        var dns = new List<string>();
        if (members is IEnumerable<object> items)
        {
            dns.AddRange(items.Select(i => i.ToString() ?? string.Empty));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<string> memberDns)
        {
            foreach (var memberDn in memberDns)
            {
                if (string.IsNullOrWhiteSpace(memberDn) || !seen.Add(memberDn))
                {
                    continue;
                }

                var obj = AdsiSearchService.Search(
                    session,
                    ObjectType.All,
                    $"(distinguishedName={LdapFilter.EscapeValue(memberDn)})",
                    null,
                    null,
                    ["distinguishedName", "samAccountName", "member"],
                    1000,
                    1).Results.FirstOrDefault();

                Console.WriteLine($"{obj?.GetValueOrDefault("samAccountName")}  {memberDn}");
                if (recursive && obj?.GetValueOrDefault("member") is IEnumerable<object> nested)
                {
                    Walk(nested.Select(x => x.ToString() ?? string.Empty));
                }
            }
        }

        Walk(dns);
        Console.WriteLine($"Total: {seen.Count}");
    }

    public static void PrintMemberOf(AdsiSession session, string dn, bool recursive)
    {
        var output = AdsiSearchService.Search(
            session,
            ObjectType.All,
            $"(distinguishedName={LdapFilter.EscapeValue(dn)})",
            null,
            null,
            ["memberOf"],
            1000,
            1);

        if (output.Results.Count == 0)
        {
            throw new InvalidOperationException($"Object not found: {dn}");
        }

        var groups = output.Results[0].GetValueOrDefault("memberOf");
        var initial = new List<string>();
        if (groups is IEnumerable<object> items)
        {
            initial.AddRange(items.Select(i => i.ToString() ?? string.Empty));
        }

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(initial);
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (!all.Add(g))
            {
                continue;
            }

            Console.WriteLine(g);
            if (!recursive)
            {
                continue;
            }

            var parent = AdsiSearchService.Search(
                session,
                ObjectType.Groups,
                $"(distinguishedName={LdapFilter.EscapeValue(g)})",
                null,
                null,
                ["memberOf"],
                1000,
                1).Results.FirstOrDefault();

            if (parent?.GetValueOrDefault("memberOf") is IEnumerable<object> parents)
            {
                foreach (var p in parents)
                {
                    queue.Enqueue(p.ToString() ?? string.Empty);
                }
            }
        }

        Console.WriteLine($"Total: {all.Count}");
    }
}
