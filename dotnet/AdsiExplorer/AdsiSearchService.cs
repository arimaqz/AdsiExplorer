using System.Collections;
using System.DirectoryServices;

namespace AdsiExplorer;

internal sealed record SearchOutput(
    string Filter,
    IReadOnlyList<Dictionary<string, object?>> Results,
    bool Truncated,
    bool HasMore,
    int Skip,
    int? TotalCount = null,
    bool SortLimited = false);

internal static class AdsiSearchService
{
    public static SearchOutput Search(
        AdsiSession session,
        ObjectType objectType,
        string? filter,
        string? attribute,
        string? value,
        IReadOnlyList<string> properties,
        int pageSize,
        int maxResults,
        int skip = 0,
        bool countOnly = false,
        string? sortProperty = null)
    {
        var ldapFilter = filter;
        if (string.IsNullOrWhiteSpace(ldapFilter))
        {
            ldapFilter = attribute is not null && value is not null
                ? LdapFilter.BuildAttributeFilter(objectType, attribute, value)
                : LdapFilter.GetPresetFilter(objectType);
        }

        using var searcher = new DirectorySearcher(session.SearchEntry)
        {
            Filter = ldapFilter,
            SearchScope = SearchScope.Subtree,
            PageSize = Math.Max(1, pageSize)
        };

        searcher.PropertiesToLoad.Clear();
        if (properties.Count > 0)
        {
            foreach (var prop in properties.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var loadName = prop.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase)
                    ? "distinguishedname"
                    : prop.ToLowerInvariant();
                searcher.PropertiesToLoad.Add(loadName);
            }

            if (!searcher.PropertiesToLoad.Contains("distinguishedname"))
            {
                searcher.PropertiesToLoad.Add("distinguishedname");
            }
        }

        var results = new List<Dictionary<string, object?>>();
        var skipped = 0;
        var hasMore = false;
        using var found = searcher.FindAll();

        if (countOnly)
        {
            var count = 0;
            foreach (SearchResult _ in found)
            {
                count++;
            }

            return new SearchOutput(ldapFilter, [], false, false, skip, count);
        }

        foreach (SearchResult entry in found)
        {
            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            if (maxResults > 0 && results.Count >= maxResults)
            {
                hasMore = true;
                break;
            }

            results.Add(ReadSearchResult(entry, properties));
        }

        var sortLimited = false;
        if (!string.IsNullOrWhiteSpace(sortProperty) && results.Count > 1)
        {
            if (hasMore || skip > 0)
            {
                sortLimited = true;
            }

            results = results
                .OrderBy(r => r.TryGetValue(sortProperty, out var v) ? v?.ToString() : null, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new SearchOutput(ldapFilter, results, hasMore, hasMore, skip, SortLimited: sortLimited);
    }

    public static Dictionary<string, object?> ReadAllObjectProperties(AdsiSession session, string distinguishedName)
    {
        var protocol = session.UseGlobalCatalog ? "GC" : "LDAP";
        var path = session.Server is null
            ? $"{protocol}://{distinguishedName}"
            : $"{protocol}://{session.Server}/{distinguishedName}";

        using var entry = session.CreateEntry(path);
        entry.RefreshCache();

        var props = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entry.Properties.PropertyNames)
        {
            props[name] = ReadPropertyValues(entry.Properties[name], name);
        }

        props.TryAdd("distinguishedName", distinguishedName);
        return props;
    }

    private static Dictionary<string, object?> ReadSearchResult(SearchResult result, IReadOnlyList<string> propertyNames)
    {
        var names = propertyNames.Count > 0
            ? propertyNames
            : result.Properties.PropertyNames.Cast<string>().ToList();

        var props = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            props[name] = ReadResultProperty(result, name);
        }

        if (result.Properties.Contains("distinguishedname"))
        {
            props["distinguishedName"] = result.Properties["distinguishedname"][0]?.ToString();
        }

        return props;
    }

    private static object? ReadResultProperty(SearchResult result, string propertyName)
    {
        foreach (string candidate in result.Properties.PropertyNames)
        {
            if (!candidate.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ReadPropertyValues(result.Properties[candidate], propertyName);
        }

        return null;
    }

    private static object? ReadPropertyValues(object? raw, string attributeName)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw is IEnumerable enumerable and not string and not byte[])
        {
            var values = new List<object?>();
            foreach (var item in enumerable)
            {
                values.Add(AdsiPropertyFormatter.Format(item, attributeName));
            }

            return values.Count switch
            {
                0 => null,
                1 => values[0],
                _ => values.ToArray()
            };
        }

        return AdsiPropertyFormatter.Format(raw, attributeName);
    }
}
