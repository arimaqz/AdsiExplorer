using System.Text.Json;

namespace AdsiExplorer;

internal static class ShellPersistence
{
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdsiExplorer");

    public static Dictionary<string, string> LoadAliases()
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["disabled"] = "list users -query disabled",
            ["spn"] = "list users -query spn"
        };

        var file = Path.Combine(DataDir, "aliases.json");
        if (!File.Exists(file))
        {
            return defaults;
        }

        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
        if (loaded is null)
        {
            return defaults;
        }

        foreach (var pair in loaded)
        {
            defaults[pair.Key] = pair.Value;
        }

        return defaults;
    }

    public static void SaveAliases(Dictionary<string, string> aliases)
    {
        Directory.CreateDirectory(DataDir);
        var file = Path.Combine(DataDir, "aliases.json");
        File.WriteAllText(file, JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void AppendHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Directory.CreateDirectory(DataDir);
        File.AppendAllText(Path.Combine(DataDir, "history.txt"), line + Environment.NewLine);
    }
}
