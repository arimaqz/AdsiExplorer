namespace AdsiExplorer;

internal static class PropertyPresets
{
    public static IReadOnlyDictionary<string, string[]> Map { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["minimal"] =
            [
                "distinguishedName", "samAccountName", "name", "objectClass"
            ],
            ["user"] =
            [
                "distinguishedName", "samAccountName", "name", "displayName", "userPrincipalName",
                "mail", "title", "department", "memberOf", "lastLogonTimestamp", "pwdLastSet",
                "userAccountControl", "whenCreated", "whenChanged"
            ],
            ["group"] =
            [
                "distinguishedName", "samAccountName", "name", "description", "member",
                "memberOf", "groupType", "whenCreated", "whenChanged"
            ],
            ["computer"] =
            [
                "distinguishedName", "samAccountName", "name", "dnsHostName", "operatingSystem",
                "operatingSystemVersion", "lastLogonTimestamp", "whenCreated", "whenChanged"
            ],
            ["ou"] =
            [
                "distinguishedName", "name", "description", "whenCreated", "whenChanged"
            ],
            ["detail"] =
            [
                "distinguishedName", "objectClass", "objectCategory", "samAccountName", "name",
                "displayName", "userPrincipalName", "mail", "memberOf", "member", "description",
                "userAccountControl", "lastLogonTimestamp", "pwdLastSet", "whenCreated", "whenChanged"
            ]
        };

    public static string[] Resolve(string spec)
    {
        if (Map.TryGetValue(spec, out var preset))
        {
            return preset;
        }

        return spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
