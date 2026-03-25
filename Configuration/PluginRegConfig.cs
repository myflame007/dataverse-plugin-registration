using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Configuration file model (pluginreg.json).
/// Stores environment connections and plugin assembly paths.
/// </summary>
public class PluginRegConfig
{
    public const string DefaultFileName = "pluginreg.json";

    [JsonPropertyName("assemblies")]
    public List<AssemblyConfig> Assemblies { get; set; } = [];

    [JsonPropertyName("environments")]
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PluginRegConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PluginRegConfig>(json, SerializerOptions) ?? new PluginRegConfig();
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
    }

    public static PluginRegConfig CreateDefault(string assemblyName, string assemblyPath)
    {
        return new PluginRegConfig
        {
            Assemblies =
            [
                new AssemblyConfig
                {
                    Name = assemblyName,
                    Path = assemblyPath,
                    NupkgPath = $"bin\\Debug\\{assemblyName}.1.0.0.nupkg",
                    Profile = "debug"
                }
            ],
            Environments = new Dictionary<string, EnvironmentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["dev"] = new()
                {
                    Url = "${DATAVERSE_DEV_URL}",
                    AuthType = "OAuth",
                    AppId = "${DATAVERSE_APPID}",
                    RedirectUri = "${DATAVERSE_REDIRECT_URI}",
                    LoginPrompt = "Auto"
                },
                ["live"] = new()
                {
                    Url = "${DATAVERSE_LIVE_URL}",
                    AuthType = "OAuth",
                    AppId = "${DATAVERSE_APPID}",
                    RedirectUri = "${DATAVERSE_REDIRECT_URI}",
                    LoginPrompt = "Auto"
                }
            }
        };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public class AssemblyConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("nupkgPath")]
    public string? NupkgPath { get; set; }

    [JsonPropertyName("publisherPrefix")]
    public string PublisherPrefix { get; set; } = "";

    [JsonPropertyName("solutionName")]
    public string? SolutionName { get; set; }

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "debug";
}

public class EnvironmentConfig
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "OAuth";

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("redirectUri")]
    public string? RedirectUri { get; set; }

    [JsonPropertyName("loginPrompt")]
    public string? LoginPrompt { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Optional path to the MSAL token cache file.
    /// Default: %LOCALAPPDATA%/Dataverse.PluginRegistration/msal_token_cache.bin
    /// Supports ${VAR} placeholders.
    /// </summary>
    [JsonPropertyName("tokenCachePath")]
    public string? TokenCachePath { get; set; }

    /// <summary>Builds a Dataverse connection string from the config properties.</summary>
    public string BuildConnectionString()
    {
        // If a full connection string is provided, use it directly
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return ConnectionString;

        var parts = new List<string>
        {
            $"AuthType={AuthType}",
            $"Url={Url}"
        };

        if (!string.IsNullOrWhiteSpace(AppId))
            parts.Add($"AppId={AppId}");
        if (!string.IsNullOrWhiteSpace(RedirectUri))
            parts.Add($"RedirectUri={RedirectUri}");
        if (!string.IsNullOrWhiteSpace(LoginPrompt))
            parts.Add($"LoginPrompt={LoginPrompt}");
        if (!string.IsNullOrWhiteSpace(Username))
            parts.Add($"Username={Username}");

        return string.Join(";", parts);
    }
}
