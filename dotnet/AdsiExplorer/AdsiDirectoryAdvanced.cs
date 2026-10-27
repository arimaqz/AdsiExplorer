namespace AdsiExplorer;

internal static class AdsiDirectoryAdvanced
{
    public static string ResolveSid(AdsiSession session, string sid)
    {
        var output = AdsiSearchService.Search(
            session,
            ObjectType.All,
            LdapFilter.BuildObjectSidFilter(sid),
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
                $"(&(objectClass=classSchema)(|(lDAPDisplayName={LdapFilter.EscapeValue(className)})(cn={LdapFilter.EscapeValue(className)})))",
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
        var dns = ToDnList(members);

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
                if (recursive)
                {
                    Walk(ToDnList(obj?.GetValueOrDefault("member")));
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
        var initial = ToDnList(groups);

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(initial);
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(g) || !all.Add(g))
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

            foreach (var p in ToDnList(parent?.GetValueOrDefault("memberOf")))
            {
                queue.Enqueue(p);
            }
        }

        Console.WriteLine($"Total: {all.Count}");
    }

    private static List<string> ToDnList(object? value)
    {
        var dns = new List<string>();
        if (value is null)
        {
            return dns;
        }

        if (value is string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                dns.Add(s);
            }
            return dns;
        }

        if (value is System.Collections.IEnumerable items and not string)
        {
            foreach (var item in items)
            {
                var dn = item?.ToString();
                if (!string.IsNullOrWhiteSpace(dn))
                {
                    dns.Add(dn);
                }
            }
        }

        return dns;
    }
}
