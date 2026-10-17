using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Security;

namespace AdsiExplorer;

internal sealed class AdsiSession
{
    public string? Server { get; private set; }
    public string BaseDn { get; private set; } = string.Empty;
    public string SearchBaseDn { get; private set; } = string.Empty;
    public bool UseGlobalCatalog { get; private set; }
    public bool UseSsl { get; private set; }
    public string? UserName { get; private set; }
    private SecureString? PasswordSecure { get; set; }

    public DirectoryEntry SearchEntry { get; private set; } = null!;

    public static AdsiSession Connect(
        string? server,
        string? baseDn,
        string? userName,
        SecureString? password,
        bool useGlobalCatalog,
        bool useSsl = false)
    {
        if (useSsl && useGlobalCatalog)
        {
            throw new InvalidOperationException("UseSsl cannot be combined with Global Catalog in this tool.");
        }

        var protocol = useGlobalCatalog ? "GC" : "LDAP";
        var rootPath = server is null ? $"{protocol}://RootDSE" : $"{protocol}://{server}/RootDSE";
        using var rootEntry = CreateEntry(rootPath, userName, password, useSsl);

        var resolvedBase = baseDn;
        if (string.IsNullOrWhiteSpace(resolvedBase))
        {
            resolvedBase = rootEntry.Properties["defaultNamingContext"]?.Cast<object?>().FirstOrDefault()?.ToString()
                ?? throw new InvalidOperationException($"Could not read defaultNamingContext from {rootPath}");
        }

        var session = new AdsiSession
        {
            Server = server,
            BaseDn = resolvedBase,
            SearchBaseDn = resolvedBase,
            UseGlobalCatalog = useGlobalCatalog,
            UseSsl = useSsl,
            UserName = userName,
            PasswordSecure = CopySecureString(password)
        };

        session.SearchEntry = session.CreateSearchEntry(resolvedBase);
        session.SearchEntry.RefreshCache();
        return session;
    }

    public static SecureString? ToSecureString(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return null;
        }

        var secure = new SecureString();
        foreach (var ch in plain)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    public void SetSearchBase(string searchBaseDn)
    {
        SearchBaseDn = searchBaseDn;
        SearchEntry = CreateSearchEntry(searchBaseDn);
        SearchEntry.RefreshCache();
    }

    public string SetNamingContext(string context)
    {
        var attr = context.ToLowerInvariant() switch
        {
            "config" => "configurationNamingContext",
            "schema" => "schemaNamingContext",
            _ => "defaultNamingContext"
        };

        var protocol = UseGlobalCatalog ? "GC" : "LDAP";
        var rootPath = Server is null ? $"{protocol}://RootDSE" : $"{protocol}://{Server}/RootDSE";
        using var root = CreateEntry(rootPath);
        var dn = root.Properties[attr]?.Cast<object?>().FirstOrDefault()?.ToString()
            ?? throw new InvalidOperationException($"Could not read {attr} from RootDSE.");
        SetSearchBase(dn);
        return dn;
    }

    public DirectoryEntry CreateEntry(string path) =>
        CreateEntry(path, UserName, PasswordSecure, UseSsl);

    private DirectoryEntry CreateSearchEntry(string dn)
    {
        var protocol = UseGlobalCatalog ? "GC" : "LDAP";
        var path = Server is null ? $"{protocol}://{dn}" : $"{protocol}://{Server}/{dn}";
        return CreateEntry(path);
    }

    private static DirectoryEntry CreateEntry(string path, string? userName, SecureString? password, bool useSsl)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return useSsl
                ? new DirectoryEntry(path, null, null, AuthenticationTypes.SecureSocketsLayer)
                : new DirectoryEntry(path);
        }

        var auth = useSsl ? AuthenticationTypes.SecureSocketsLayer : AuthenticationTypes.Secure;

        if (password is null)
        {
            throw new InvalidOperationException("Password is required when a user name is set.");
        }

        var bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(password);
            var plain = Marshal.PtrToStringBSTR(bstr);
            return new DirectoryEntry(path, userName, plain, auth);
        }
        finally
        {
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }
    }

    private static SecureString? CopySecureString(SecureString? source)
    {
        return source is null ? null : source.Copy();
    }
}
