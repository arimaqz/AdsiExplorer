namespace AdsiExplorer;

internal sealed class ShellState
{
    public AdsiSession? Session { get; set; }
    public SearchOutput? LastSearch { get; set; }
    public List<IndexedResult> LastResults { get; } = [];
    public ObjectType ObjectType { get; set; } = ObjectType.Users;
    public string[] ListProperties { get; set; } = PropertyPresets.Map["minimal"];
    public string[] DetailProperties { get; set; } = PropertyPresets.Map["detail"];
    public string[] LastDisplayProperties { get; set; } = PropertyPresets.Map["minimal"];
    public int MaxResults { get; set; } = 100;
    public int PageSize { get; set; } = 1000;
    public int TableCellWidth { get; set; } = 48;
    public int DefaultStaleDays { get; set; } = 90;
    public int QuerySkip { get; set; }
    public QueryPlan? LastQueryPlan { get; set; }
    public Dictionary<string, string> Aliases { get; set; } = ShellPersistence.LoadAliases();
    public List<string> History { get; } = [];
    public int ExitCode { get; set; }
}

internal sealed record QueryPlan(
    ObjectType ObjectType,
    string? Filter,
    string? Attribute,
    string? Value,
    string[] Properties,
    string? SortProperty,
    bool IsCount);

internal sealed record IndexedResult(int Index, Dictionary<string, object?> Object);
