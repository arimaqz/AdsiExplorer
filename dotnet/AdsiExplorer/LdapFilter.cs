namespace AdsiExplorer;

internal static class LdapFilter
{
    public static string EscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\5c"); break;
                case '*': builder.Append("\\2a"); break;
                case '(': builder.Append("\\28"); break;
                case ')': builder.Append("\\29"); break;
                case '\0': builder.Append("\\00"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    public static string GetPresetFilter(ObjectType objectType) =>
        objectType switch
        {
            ObjectType.Users => "(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))",
            ObjectType.Groups => "(objectClass=group)",
            ObjectType.Computers => "(objectClass=computer)",
            ObjectType.OrganizationalUnits => "(objectClass=organizationalUnit)",
            ObjectType.All => "(objectClass=*)",
            _ => "(objectClass=*)"
        };

    public static string BuildAttributeFilter(ObjectType objectType, string attribute, string value)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            throw new ArgumentException("Attribute cannot be empty.", nameof(attribute));
        }

        var escaped = value.Contains('*', StringComparison.Ordinal)
            ? string.Join("*", value.Split('*').Select(EscapeValue))
            : EscapeValue(value);

        if (objectType == ObjectType.All)
        {
            return $"({attribute}={escaped})";
        }

        return $"(&{GetPresetFilter(objectType)}({attribute}={escaped}))";
    }

    public static string BuildIdentityFilter(ObjectType objectType, string attribute, string value)
    {
        var attr = attribute.ToLowerInvariant();
        if (attr == "anr")
        {
            var escaped = value.Contains('*', StringComparison.Ordinal)
                ? string.Join("*", value.Split('*').Select(EscapeValue))
                : EscapeValue(value);
            return objectType == ObjectType.All
                ? $"(anr={escaped})"
                : $"(&{GetPresetFilter(objectType)}(anr={escaped}))";
        }

        if (attr == "objectsid")
        {
            if (!value.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("objectSid must be in S-1-... form.");
            }

            return $"(objectSid={value})";
        }

        if (attr == "objectguid")
        {
            var guid = Guid.Parse(value.Trim('{', ' '));
            var bytes = guid.ToByteArray();
            var adBytes = new byte[]
            {
                bytes[3], bytes[2], bytes[1], bytes[0],
                bytes[5], bytes[4], bytes[7], bytes[6],
                bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]
            };
            var hex = string.Concat(adBytes.Select(b => "\\" + b.ToString("x2")));
            return $"(objectGUID={hex})";
        }

        return BuildAttributeFilter(objectType, attribute, value);
    }
}

internal enum ObjectType
{
    Users,
    Groups,
    Computers,
    OrganizationalUnits,
    All
}
