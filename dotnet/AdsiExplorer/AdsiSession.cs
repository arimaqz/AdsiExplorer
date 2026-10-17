using System.DirectoryServices;

namespace AdsiExplorer;

internal sealed class AdsiSession
{
    public string? Server { get; private set; }
    public string BaseDn { get; private set; } = string.Empty;
    public string SearchBaseDn { get; private set; } = string.Empty;
    public bool UseGlobalCatalog { get; private set; }
    public string? UserName { get; private set; }
    public string? Password { get; private set; }

    public DirectoryEntry SearchEntry { get; private set; } = null!;

    public static AdsiSession Connect(string? server, string? baseDn, string? userName, string? password, bool useGlobalCatalog)
    {
        var protocol = useGlobalCatalog ? "GC" : "LDAP";
        var rootPath = server is null ? $"{protocol}://RootDSE" : $"{protocol}://{server}/RootDSE";
        using var rootEntry = CreateEntry(rootPath, userName, password);

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
            UserName = userName,
            Password = password
        };

        session.SearchEntry = session.CreateSearchEntry(resolvedBase);
        session.SearchEntry.RefreshCache();
        return session;
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

    public DirectoryEntry CreateEntry(string path) => CreateEntry(path, UserName, Password);

    private DirectoryEntry CreateSearchEntry(string dn)
    {
        var protocol = UseGlobalCatalog ? "GC" : "LDAP";
        var path = Server is null ? $"{protocol}://{dn}" : $"{protocol}://{Server}/{dn}";
        return CreateEntry(path);
    }

    private static DirectoryEntry CreateEntry(string path, string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return new DirectoryEntry(path);
        }

        return new DirectoryEntry(path, userName, password);
    }
}
