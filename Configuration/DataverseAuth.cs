using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Handles MSAL-based authentication with persistent token caching.
/// Tokens are cached to disk so users don't need to re-authenticate on every run.
/// Silent token refresh is attempted first; interactive login only when needed.
/// </summary>
public static class DataverseAuth
{
    /// <summary>
    /// Default directory for the MSAL token cache file.
    /// Can be overridden via EnvironmentConfig.TokenCachePath.
    /// </summary>
    private static readonly string DefaultCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dataverse.PluginRegistration");

    private const string CacheFileName = "msal_token_cache.bin";
    private const string SuccessHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <title>Login erfolgreich</title>
            <style>
                body {
                    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    min-height: 100vh;
                    margin: 0;
                    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
                    color: #fff;
                }
                .card {
                    background: rgba(255,255,255,0.15);
                    backdrop-filter: blur(10px);
                    border-radius: 16px;
                    padding: 48px;
                    text-align: center;
                    max-width: 480px;
                    box-shadow: 0 8px 32px rgba(0,0,0,0.2);
                }
                .icon { font-size: 64px; margin-bottom: 16px; }
                h1 { margin: 0 0 8px 0; font-size: 24px; font-weight: 600; }
                p { margin: 8px 0; opacity: 0.9; font-size: 15px; line-height: 1.5; }
                .hint { margin-top: 24px; opacity: 0.7; font-size: 13px; }
                a { color: #ffd700; text-decoration: none; }
                a:hover { text-decoration: underline; }
                .coffee { margin-top: 20px; font-size: 14px; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="icon">✅</div>
                <h1>Authentifizierung erfolgreich!</h1>
                <p>Du bist jetzt verbunden.<br/>Das Plugin-Deployment läuft im Terminal weiter.</p>
                <p class="hint">Du kannst diesen Tab jetzt schließen.</p>
                <p class="coffee">☕ <a href="https://buymeacoffee.com/rstickler.dev" target="_blank">Buy me a coffee</a></p>
            </div>
        </body>
        </html>
        """;

    private const string ErrorHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <title>Login fehlgeschlagen</title>
            <style>
                body {
                    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    min-height: 100vh;
                    margin: 0;
                    background: linear-gradient(135deg, #e74c3c 0%, #c0392b 100%);
                    color: #fff;
                }
                .card {
                    background: rgba(255,255,255,0.15);
                    backdrop-filter: blur(10px);
                    border-radius: 16px;
                    padding: 48px;
                    text-align: center;
                    max-width: 480px;
                    box-shadow: 0 8px 32px rgba(0,0,0,0.2);
                }
                .icon { font-size: 64px; margin-bottom: 16px; }
                h1 { margin: 0 0 8px 0; font-size: 24px; font-weight: 600; }
                p { margin: 8px 0; opacity: 0.9; font-size: 15px; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="icon">❌</div>
                <h1>Authentifizierung fehlgeschlagen</h1>
                <p>Bitte starte den Befehl erneut im Terminal.</p>
            </div>
        </body>
        </html>
        """;

    /// <summary>
    /// Connects to Dataverse using MSAL auth with persistent token cache.
    /// 1. Tries AcquireTokenSilent (cached/refresh token) — no browser needed
    /// 2. Falls back to interactive login (browser) only when silent fails
    /// Token cache is persisted to disk so logins survive across runs.
    /// </summary>
    public static async Task<ServiceClient> ConnectAsync(
        EnvironmentConfig envConfig, CancellationToken ct = default)
    {
        // If a raw connection string is configured, use it directly (no custom UI possible)
        if (!string.IsNullOrWhiteSpace(envConfig.ConnectionString))
            return new ServiceClient(envConfig.ConnectionString);

        var appId = envConfig.AppId ?? throw new InvalidOperationException("AppId is required.");
        var url = envConfig.Url ?? throw new InvalidOperationException("Url is required.");

        var app = PublicClientApplicationBuilder
            .Create(appId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
            .WithRedirectUri("http://localhost")
            .Build();

        // Register persistent file-based token cache
        await RegisterTokenCacheAsync(app, envConfig.TokenCachePath);

        var scopes = new[] { $"{url}/.default" };

        // Try silent authentication first (uses cached access token or refresh token)
        AuthenticationResult? authResult = null;
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                authResult = await app.AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                Console.WriteLine("  Token from cache (no browser login needed).");
            }
            catch (MsalUiRequiredException)
            {
                // Silent failed — refresh token expired or consent needed → interactive
            }
        }

        // Fall back to interactive login
        if (authResult == null)
        {
            var forceLogin = envConfig.LoginPrompt?.Equals("Always", StringComparison.OrdinalIgnoreCase) == true;
            authResult = await app.AcquireTokenInteractive(scopes)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = SuccessHtml,
                    HtmlMessageError = ErrorHtml
                })
                .WithPrompt(forceLogin ? Prompt.ForceLogin : Prompt.SelectAccount)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
        }

        // Connect ServiceClient with a token provider that auto-refreshes
        var client = new ServiceClient(
            instanceUrl: new Uri(url),
            tokenProviderFunction: async _ =>
            {
                // On every API call: try silent first (MSAL handles refresh internally)
                var currentAccounts = await app.GetAccountsAsync().ConfigureAwait(false);
                var currentAccount = currentAccounts.FirstOrDefault();
                if (currentAccount != null)
                {
                    try
                    {
                        var refreshed = await app.AcquireTokenSilent(scopes, currentAccount)
                            .ExecuteAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        return refreshed.AccessToken;
                    }
                    catch (MsalUiRequiredException)
                    {
                        // Extremely rare during a single session — fall through
                    }
                }
                return authResult.AccessToken;
            },
            useUniqueInstance: true);

        if (!client.IsReady)
            throw new InvalidOperationException(
                $"ServiceClient not ready: {client.LastError}");

        return client;
    }

    /// <summary>
    /// Registers a cross-platform file-based token cache using MSAL.Extensions.
    /// Tokens (access + refresh) are encrypted and stored on disk.
    /// </summary>
    private static async Task RegisterTokenCacheAsync(IPublicClientApplication app, string? customCachePath)
    {
        var cacheDir = !string.IsNullOrWhiteSpace(customCachePath)
            ? Path.GetDirectoryName(Path.GetFullPath(customCachePath)) ?? DefaultCacheDir
            : DefaultCacheDir;

        var cacheFile = !string.IsNullOrWhiteSpace(customCachePath)
            ? Path.GetFileName(customCachePath)
            : CacheFileName;

        Directory.CreateDirectory(cacheDir);

        var storageProperties = new StorageCreationPropertiesBuilder(cacheFile, cacheDir)
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
        cacheHelper.RegisterCache(app.UserTokenCache);
    }
}
