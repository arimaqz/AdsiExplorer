using System.Security.Principal;
using System.Text.RegularExpressions;

namespace AdsiExplorer;

internal static class LdapFilter
{
    private static readonly Regex SidFilterPattern = new(@"^S-1(-\d+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex AttributeNamePattern = new(@"^[\w.-]+$", RegexOptions.CultureInvariant);

    public static void ValidateAttributeName(string attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute) || !AttributeNamePattern.IsMatch(attribute))
        {
            throw new InvalidOperationException($"Invalid LDAP attribute name for filter: {attribute}");
        }
    }
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

        ValidateAttributeName(attribute);

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
            return BuildObjectSidFilter(value);
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

    public static string BuildObjectSidFilter(string sid)
    {
        var trimmed = sid.Trim();
        if (!SidFilterPattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException($"Invalid SID format for LDAP filter: {sid}");
        }

        var sidObj = new SecurityIdentifier(trimmed);
        var bytes = new byte[sidObj.BinaryLength];
        sidObj.GetBinaryForm(bytes, 0);
        var hex = string.Concat(bytes.Select(b => "\\" + b.ToString("x2")));
        return $"(objectSid={hex})";
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
