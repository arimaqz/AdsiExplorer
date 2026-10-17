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
        return (Convert.ToInt32(value) & 2) == 2;
    }

    public static string Format(object? value)
    {
        if (value is null) return string.Empty;
        var uac = Convert.ToInt32(value);
        var flags = new List<string>();
        if ((uac & 0x2) != 0) flags.Add("ACCOUNTDISABLE");
        if ((uac & 0x10) != 0) flags.Add("LOCKOUT");
        if ((uac & 0x20) != 0) flags.Add("PASSWD_NOTREQD");
        if ((uac & 0x10000) != 0) flags.Add("DONT_EXPIRE_PASSWORD");
        if ((uac & 0x800000) != 0) flags.Add("PASSWORD_EXPIRED");
        return flags.Count == 0 ? uac.ToString() : $"{uac} ({string.Join(", ", flags)})";
    }
}
