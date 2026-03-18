using System.Text.RegularExpressions;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Loads variables from a .env file and resolves ${VAR} placeholders in strings.
/// Falls back to system environment variables when a key is not in the .env file.
/// </summary>
public static partial class EnvFile
{
    private const string DefaultFileName = ".env";

    /// <summary>
    /// Loads a .env file into a dictionary. Supports # comments, KEY=VALUE,
    /// optional quoting with " or ', and blank lines.
    /// </summary>
    public static Dictionary<string, string> Load(string? directory = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = directory ?? Directory.GetCurrentDirectory();
        var path = Path.Combine(dir, DefaultFileName);

        if (!File.Exists(path))
            return vars;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            // Strip surrounding quotes
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            vars[key] = value;
        }

        return vars;
    }

    /// <summary>
    /// Replaces ${VAR_NAME} placeholders in a string with values from the
    /// env dictionary, falling back to system environment variables.
    /// Returns the original string if no placeholders are found.
    /// </summary>
    public static string Resolve(string input, Dictionary<string, string> envVars)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${"))
            return input;

        return PlaceholderRegex().Replace(input, match =>
        {
            var varName = match.Groups[1].Value;
            if (envVars.TryGetValue(varName, out var val))
                return val;
            return Environment.GetEnvironmentVariable(varName) ?? match.Value;
        });
    }

    /// <summary>
    /// Resolves all string properties of an EnvironmentConfig that contain ${} placeholders.
    /// </summary>
    public static void ResolveConfig(EnvironmentConfig config, Dictionary<string, string> envVars)
    {
        config.Url = Resolve(config.Url, envVars);
        if (config.AppId != null) config.AppId = Resolve(config.AppId, envVars);
        if (config.RedirectUri != null) config.RedirectUri = Resolve(config.RedirectUri, envVars);
        if (config.Username != null) config.Username = Resolve(config.Username, envVars);
        if (config.ConnectionString != null) config.ConnectionString = Resolve(config.ConnectionString, envVars);
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
