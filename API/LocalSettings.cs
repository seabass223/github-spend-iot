using System.Text.Json;

/// <summary>
/// Reads configuration from an Azure Functions style local.settings.json ("Values" object),
/// falling back to environment variables when a key is absent.
/// </summary>
public sealed class LocalSettings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private LocalSettings(IReadOnlyDictionary<string, string> values) => _values = values;

    public static LocalSettings Load(string fileName = "local.settings.json")
    {
        var path = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(AppContext.BaseDirectory, fileName);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("Values", out var section) &&
                section.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in section.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        values[property.Name] = property.Value.GetString()!;
                    }
                }
            }
        }

        return new LocalSettings(values);
    }

    public string? this[string key] => Get(key);

    public string? Get(string key)
    {
        if (_values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    public string GetRequired(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Missing setting '{key}'. Set it in local.settings.json under \"Values\", or as an environment variable.");
}
