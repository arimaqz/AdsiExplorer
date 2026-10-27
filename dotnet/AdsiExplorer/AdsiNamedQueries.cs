namespace AdsiExplorer;

internal static class AdsiNamedQueries
{
    public static string Build(string name, int staleDays = 90)
    {
        const string userBase = "(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))";
        return name.ToLowerInvariant() switch
        {
            "disabled" or "disabled-users" or "disabled_users" => $"(&{userBase}(userAccountControl:1.2.840.113556.1.4.803:=2))",
            "locked" or "locked-users" or "locked_users" => $"(&{userBase}(lockoutTime>=1))",
            "pwd-never-expires" or "password-never-expires" or "dont-expire" => $"(&{userBase}(userAccountControl:1.2.840.113556.1.4.803:=65536))",
            "spn" or "users-with-spn" or "users_with_spn" => $"(&{userBase}(servicePrincipalName=*))",
            "empty-groups" or "empty_groups" or "emptygroups" => "(&(objectClass=group)(!(member=*)))",
            "stale-computers" or "stale_computers" or "stalecomputers" => staleDays < 1
                ? throw new InvalidOperationException("StaleDays must be >= 1.")
                : $"(&(objectClass=computer)(lastLogonTimestamp<={DateTime.UtcNow.AddDays(-staleDays).ToFileTimeUtc()}))",
            _ => throw new InvalidOperationException($"Unknown named query '{name}'.")
        };
    }
}

internal static class UserAccountControlHelper
{
    public static bool IsDisabled(object? value)
    {
        if (value is null) return false;
        if (!TryGetNumeric(value, out var uac)) return false;
        return (uac & 2) == 2;
    }

    private static bool TryGetNumeric(object value, out int uac)
    {
        uac = 0;
        switch (value)
        {
            case int i:
                uac = i;
                return true;
            case long l:
                uac = (int)l;
                return true;
            case string s:
            {
                var end = 0;
                while (end < s.Length && char.IsDigit(s[end])) end++;
                if (end > 0 && int.TryParse(s[..end], out uac)) return true;
                return false;
            }
            default:
                return int.TryParse(value.ToString(), out uac);
        }
    }

    public static string Format(object? value)
    {
        if (value is null) return string.Empty;
        var uac = Convert.ToInt32(value);
        var flags = new List<string>();
        if ((uac & 0x1) != 0) flags.Add("SCRIPT");
        if ((uac & 0x2) != 0) flags.Add("ACCOUNTDISABLE");
        if ((uac & 0x8) != 0) flags.Add("HOMEDIR_REQUIRED");
        if ((uac & 0x10) != 0) flags.Add("LOCKOUT");
        if ((uac & 0x20) != 0) flags.Add("PASSWD_NOTREQD");
        if ((uac & 0x40) != 0) flags.Add("PASSWD_CANT_CHANGE");
        if ((uac & 0x100) != 0) flags.Add("TEMP_DUPLICATE_ACCOUNT");
        if ((uac & 0x200) != 0) flags.Add("NORMAL_ACCOUNT");
        if ((uac & 0x1000) != 0) flags.Add("WORKSTATION_TRUST_ACCOUNT");
        if ((uac & 0x2000) != 0) flags.Add("SERVER_TRUST_ACCOUNT");
        if ((uac & 0x10000) != 0) flags.Add("DONT_EXPIRE_PASSWORD");
        if ((uac & 0x20000) != 0) flags.Add("MNS_LOGON_ACCOUNT");
        if ((uac & 0x80000) != 0) flags.Add("TRUSTED_FOR_DELEGATION");
        if ((uac & 0x100000) != 0) flags.Add("NOT_DELEGATED");
        if ((uac & 0x800000) != 0) flags.Add("PASSWORD_EXPIRED");
        if ((uac & 0x1000000) != 0) flags.Add("TRUSTED_TO_AUTH_FOR_DELEGATION");
        return flags.Count == 0 ? uac.ToString() : $"{uac} ({string.Join(", ", flags)})";
    }
}
