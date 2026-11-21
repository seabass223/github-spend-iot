using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Remembers the startup choices in the user's profile (not next to the exe, because a publish
/// empties dist/app): which meters were selected, weekly or 5-hour limits, and how often the LCD
/// refreshes. Saving one keeps the others.
/// </summary>
internal static class UsageSelection
{
    internal const int DefaultRefreshMinutes = 5;
    internal const int MinRefreshMinutes = 1;
    internal const int MaxRefreshMinutes = 1440; // a day

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-spend", "selection.json");

    /// <summary>The saved selection, or null if nothing valid was saved yet.</summary>
    internal static IReadOnlyList<UsageSource>? Load(string path)
    {
        if (ReadObject(path)?["selected"] is not JsonArray list)
        {
            return null;
        }

        // Unknown names (from a newer or older version) are skipped rather than failing.
        var names = list.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var name) ? name : null)
            .ToList();
        return UsageSources.All
            .Where(s => names.Contains(s.ToString(), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    internal static void Save(string path, IEnumerable<UsageSource> selected)
    {
        var names = UsageSources.All.Where(selected.Contains).Select(s => (JsonNode?)JsonValue.Create(s.ToString()));
        Update(path, root => root["selected"] = new JsonArray(names.ToArray()));
    }

    /// <summary>The saved refresh interval in minutes, or null if none (or an invalid one) was saved.</summary>
    internal static int? LoadRefreshMinutes(string path) =>
        ReadObject(path)?["refreshMinutes"] is JsonValue value && value.TryGetValue<int>(out var minutes) &&
        minutes is >= MinRefreshMinutes and <= MaxRefreshMinutes
            ? minutes
            : null;

    internal static void SaveRefreshMinutes(string path, int minutes) =>
        Update(path, root => root["refreshMinutes"] = minutes);

    /// <summary>The saved choice of weekly or 5-hour limits, or null if none (or an unknown one) was saved.</summary>
    internal static UsageWindow? LoadWindow(string path) =>
        ReadObject(path)?["window"] is JsonValue value && value.TryGetValue<string>(out var name) &&
        Enum.TryParse<UsageWindow>(name, ignoreCase: true, out var window) && Enum.IsDefined(window)
            ? window
            : null;

    internal static void SaveWindow(string path, UsageWindow window) =>
        Update(path, root => root["window"] = window.ToString());

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Update(string path, Action<JsonObject> change)
    {
        var root = ReadObject(path) ?? new JsonObject();
        change(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
