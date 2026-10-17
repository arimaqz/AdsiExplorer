using System.Collections;
using System.Security.Principal;

namespace AdsiExplorer;

internal static class AdsiPropertyFormatter
{
    private static readonly HashSet<string> FileTimeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "lastLogon", "lastLogonTimestamp", "pwdLastSet", "accountExpires",
        "badPasswordTime", "lockoutTime", "lastLogoff", "ms-Mcs-AdmPwdExpirationTime"
    };

    private static readonly HashSet<string> GuidAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectGUID", "objectSid", "sidHistory", "ms-DS-ConsistencyGuid",
        "msExchMailboxGuid", "ms-DS-SourceObjectDN"
    };

    public static object? Format(object? value, string attributeName)
    {
        if (value is null)
        {
            return null;
        }

        if (attributeName.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase))
        {
            return UserAccountControlHelper.Format(value);
        }

        if (value is string)
        {
            return value;
        }

        if (value is byte[] bytes)
        {
            if (GuidAttributes.Contains(attributeName) && bytes.Length == 16)
            {
                return new Guid(bytes).ToString();
            }

            if (attributeName.Equals("objectSid", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new SecurityIdentifier(bytes, 0).Value;
                }
                catch
                {
                    return Convert.ToBase64String(bytes);
                }
            }

            return Convert.ToBase64String(bytes);
        }

        if (value is IEnumerable enumerable and not byte[])
        {
            var items = enumerable.Cast<object?>().Select(v => Format(v, attributeName)).Where(v => v is not null).ToList();
            return items.Count switch
            {
                0 => null,
                1 => items[0],
                _ => string.Join("; ", items!)
            };
        }

        if (value is long longValue && FileTimeAttributes.Contains(attributeName))
        {
            if (longValue is 0 or 9223372036854775807)
            {
                return null;
            }

            try
            {
                return DateTime.FromFileTime(longValue).ToString("o");
            }
            catch
            {
                return longValue;
            }
        }

        if (value is int intValue && FileTimeAttributes.Contains(attributeName))
        {
            return Format((long)intValue, attributeName);
        }

        return value.ToString();
    }
}
