using Dataverse.PluginRegistration;
using Microsoft.PowerPlatform.Dataverse.Client;

// ─────────────────────────────────────────────────────────────────────
//  Plugin Registration Tool
//
//  Reads CrmPluginRegistrationAttribute from compiled plugin assemblies
//  and registers steps + images in Dataverse — like spkl, but for
//  NuGet-based (dependent assembly) plugins.
//
//  Commands:
//    init            Create a pluginreg.json config file
//    register        Register plugin steps from config or CLI args
//    list            List steps found in an assembly (dry-run)
//
//  Usage with config file (recommended):
//    plugin-reg init
//    plugin-reg register --env dev
//    plugin-reg list
//
//  Usage with direct arguments:
//    plugin-reg register --dll <path> --connection <connStr>
// ─────────────────────────────────────────────────────────────────────

// Load .env file from current directory (if present)
var envVars = EnvFile.Load();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

return command switch
{
    "init" => RunInit(args),
    "register" => await RunRegister(args),
    "list" => RunList(args),
    _ => ShowHelp()
};

// ═══════════════════════════════════════════════════════════════════
//  Commands
// ═══════════════════════════════════════════════════════════════════

int ShowHelp()
{
    Console.WriteLine("""
    Dataverse Plugin Registration Tool
    ═════════════════════════════════

    Commands:
      init                     Create a pluginreg.json config file in the current directory
      register [options]       Register plugin steps in Dataverse
      list     [options]       List discovered steps (dry-run, no connection needed)

    Options for 'register':
      --env <name>             Use named environment from pluginreg.json (e.g. --env dev)
      --config <path>          Path to pluginreg.json (default: ./pluginreg.json)
      --dll <path>             Path to plugin DLL (overrides config)
      --connection <string>    Dataverse connection string (overrides config)
      --assembly-name <name>   Assembly/package name in Dataverse (default: DLL filename)

    Options for 'list':
      --dll <path>             Path to plugin DLL (overrides config)
      --config <path>          Path to pluginreg.json (default: ./pluginreg.json)

    Examples:
      plugin-reg init
      plugin-reg register --env dev
      plugin-reg register --env dev --dll ..\bin\Debug\net462\MyPlugin.dll
      plugin-reg register --dll MyPlugin.dll --connection "AuthType=OAuth;Url=..."
      plugin-reg list
      plugin-reg list --dll ..\bin\Debug\net462\MyPlugin.dll
    """);
    return 0;
}

int RunInit(string[] args)
{
    var configPath = GetArg(args, "--config") ?? PluginRegConfig.DefaultFileName;

    if (File.Exists(configPath))
    {
        Console.WriteLine($"Config file already exists: {configPath}");
        Console.Write("Overwrite? (y/N): ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer != "y") return 0;
    }

    // Auto-detect assembly name from current directory
    var currentDir = Path.GetFileName(Directory.GetCurrentDirectory()) ?? "MyPlugin";
    var config = PluginRegConfig.CreateDefault(currentDir, $@"bin\Debug\net462\{currentDir}.dll");

    config.Save(configPath);
    Console.WriteLine($"Created: {Path.GetFullPath(configPath)}");
    Console.WriteLine("Edit environments and assembly paths, then run: plugin-reg register --env dev");
    return 0;
}

int RunList(string[] args)
{
    var (assemblyPath, _, _, _, _, _, _, error) = ResolveArgs(args, requireConnection: false);
    if (error != null) { Console.Error.WriteLine(error); return 1; }

    Console.WriteLine($"Reading: {assemblyPath}");
    var steps = AttributeReader.ReadFromAssembly(assemblyPath!);
    var customApis = AttributeReader.ReadCustomApisFromAssembly(assemblyPath!, Console.WriteLine);

    if (steps.Count == 0 && customApis.Count == 0)
    {
        Console.WriteLine("No plugin step or Custom API registrations found.");
        return 0;
    }

    // ── Plugin Steps ──────────────────────────────────────────
    if (steps.Count > 0)
    {
        Console.WriteLine($"\n  Steps:");
        Console.WriteLine($"  {"Plugin Type",-55} {"Message",-15} {"Entity",-20} {"Stage",-6} {"Mode",-6}");
        Console.WriteLine($"  {new string('─', 108)}");
        foreach (var s in steps)
        {
            var stage = s.Stage switch { 10 => "PreVal", 20 => "PreOp", 40 => "PostOp", _ => s.Stage.ToString() };
            var mode = s.ExecutionMode == 0 ? "Sync" : "Async";
            Console.WriteLine($"  {Truncate(s.PluginTypeName, 54),-55} {s.Message,-15} {s.EntityLogicalName ?? "(all)",-20} {stage,-6} {mode,-6}");

            if (s.Image1Type >= 0)
                Console.WriteLine($"    └─ Image1: {s.Image1Name} (type={s.Image1Type}, attrs={s.Image1Attributes})");
            if (s.Image2Type >= 0)
                Console.WriteLine($"    └─ Image2: {s.Image2Name} (type={s.Image2Type}, attrs={s.Image2Attributes})");
        }
    }

    // ── Custom APIs ───────────────────────────────────────────
    if (customApis.Count > 0)
    {
        Console.WriteLine($"\n  Custom APIs:");
        foreach (var api in customApis)
        {
            var binding = api.BindingType switch
            {
                0 => "Global",
                1 => $"Entity-bound: {api.BoundEntity}",
                2 => $"EntityCollection-bound: {api.BoundEntity}",
                _ => $"BindingType={api.BindingType}"
            };
            var kind = api.IsFunction ? "Function" : "Action";
            Console.WriteLine($"    {api.UniqueName}  [{kind}, {binding}]");

            foreach (var p in api.RequestParameters)
            {
                var req = p.IsRequired ? "required" : "optional";
                var typeName = MapParameterTypeName(p.Type);
                Console.WriteLine($"      Request:  {p.UniqueName} ({typeName}, {req})");
            }
            foreach (var p in api.ResponseProperties)
            {
                var typeName = MapParameterTypeName(p.Type);
                Console.WriteLine($"      Response: {p.UniqueName} ({typeName})");
            }
        }
    }

    Console.WriteLine($"\nTotal: {steps.Count} step(s), {customApis.Count} Custom API(s)");
    return 0;
}

async Task<int> RunRegister(string[] args)
{
    var (assemblyPath, connectionString, assemblyName, nupkgPath, publisherPrefix, solutionName, envConfig, error) = ResolveArgs(args, requireConnection: true);
    if (error != null) { Console.Error.WriteLine(error); return 1; }

    // 1. Read attributes
    Console.WriteLine($"Reading: {assemblyPath}");
    List<PluginStepInfo> steps;
    List<CustomApiInfo> customApis;
    try
    {
        steps = AttributeReader.ReadFromAssembly(assemblyPath!);
        customApis = AttributeReader.ReadCustomApisFromAssembly(assemblyPath!, Console.WriteLine);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR reading assembly: {ex.Message}");
        return 1;
    }

    if (steps.Count == 0 && customApis.Count == 0)
    {
        Console.WriteLine("No plugin step or Custom API registrations found.");
        return 0;
    }

    Console.WriteLine($"Found {steps.Count} step(s), {customApis.Count} Custom API(s).\n");

    // 2. Connect (use DataverseAuth for custom browser page, fall back to connection string)
    Console.WriteLine("Connecting to Dataverse...");
    Console.WriteLine("  (Waiting for browser login — close the tab or press Ctrl+C to cancel)");
    ServiceClient client;
    try
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (envConfig != null)
        {
            // Use DataverseAuth for custom browser success page
            client = await DataverseAuth.ConnectAsync(envConfig, cts.Token);
        }
        else
        {
            // Fallback: raw connection string
            try
            {
                var connectTask = Task.Run(() => new ServiceClient(connectionString), cts.Token);
                client = await connectTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\nAborted by user (Ctrl+C).");
                return 1;
            }

            if (!client.IsReady)
            {
                var lastErr = client.LastError ?? "";
                var lastEx = client.LastException;
                Console.Error.WriteLine($"ERROR: Connection failed.");
                Console.Error.WriteLine($"  LastError: {lastErr}");
                if (lastEx != null)
                    Console.Error.WriteLine($"  Exception: {lastEx.Message}");
                return 1;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nAborted by user (Ctrl+C).");
        return 1;
    }
    catch (Exception ex) when (
        ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
        ex.InnerException?.Message?.Contains("canceled", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true)
    {
        Console.Error.WriteLine("\nAuthentication was cancelled (browser tab closed or login denied).");
        Console.Error.WriteLine("Run the command again to retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        if (ex.InnerException != null)
            Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
        return 1;
    }

    Console.WriteLine($"Connected: {client.ConnectedOrgFriendlyName} ({client.ConnectedOrgUniqueName})\n");

    // ── Step 1/3: Push Assembly or NuGet package ───────────────
    Console.WriteLine("Step 1/3: Push Plugin Assembly/Package");
    Console.WriteLine(new string('─', 40));
    if (!string.IsNullOrEmpty(nupkgPath) && File.Exists(nupkgPath))
    {
        // NuGet-based PluginPackage deployment
        var deployer = new PackageDeployer(client, Console.WriteLine);
        try
        {
            deployer.Push(nupkgPath, assemblyName!, publisherPrefix!, solutionName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR pushing package: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
            return 1;
        }
    }
    else
    {
        // Classic PluginAssembly deployment (DLL directly)
        // Collect all plugin type names from steps + custom APIs
        var pluginTypeNames = steps.Select(s => s.PluginTypeName)
            .Concat(customApis.Select(a => a.PluginTypeName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var asmDeployer = new AssemblyDeployer(client, Console.WriteLine);
        try
        {
            asmDeployer.Push(assemblyPath!, assemblyName!, pluginTypeNames);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR pushing assembly: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
            return 1;
        }
    }

    // ── Step 2/3: Register steps (with change detection) ─────────
    Console.WriteLine();
    Console.WriteLine("Step 2/3: Register Steps");
    Console.WriteLine(new string('─', 40));
    if (steps.Count > 0)
    {
        var registrar = new StepRegistrar(client, Console.WriteLine);
        try
        {
            registrar.RegisterSteps(assemblyName!, steps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR registering steps: {ex.Message}");
            return 1;
        }
    }
    else
    {
        Console.WriteLine("  No plugin steps to register.");
    }

    // ── Step 3/3: Register Custom APIs ───────────────────────────
    Console.WriteLine();
    Console.WriteLine("Step 3/3: Register Custom APIs");
    Console.WriteLine(new string('─', 40));
    if (customApis.Count > 0)
    {
        var apiRegistrar = new CustomApiRegistrar(client, Console.WriteLine);
        try
        {
            apiRegistrar.RegisterCustomApis(assemblyName!, customApis);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR registering Custom APIs: {ex.Message}");
            return 1;
        }
    }
    else
    {
        Console.WriteLine("  No Custom APIs to register.");
    }

    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine("  ✓ Plugin deployment completed successfully!");
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine($"  Environment:  {client.ConnectedOrgFriendlyName}");
    Console.WriteLine($"  Package:      {assemblyName}");
    Console.WriteLine($"  Steps:        {steps.Count} checked & synced");
    Console.WriteLine($"  Custom APIs:  {customApis.Count} checked & synced");
    if (!string.IsNullOrEmpty(solutionName))
        Console.WriteLine($"  Solution:     {solutionName}");
    Console.WriteLine();
    Console.WriteLine("  ☕ Like this tool? Buy me a coffee:");
    Console.WriteLine("     https://buymeacoffee.com/rstickler.dev");
    Console.WriteLine();
    return 0;
}

// ═══════════════════════════════════════════════════════════════════
//  Helpers
// ═══════════════════════════════════════════════════════════════════

(string? assemblyPath, string? connectionString, string? assemblyName, string? nupkgPath, string? publisherPrefix, string? solutionName, EnvironmentConfig? envConfig, string? error)
    ResolveArgs(string[] args, bool requireConnection)
{
    var dllArg = GetArg(args, "--dll");
    var connArg = GetArg(args, "--connection");
    var envArg = GetArg(args, "--env");
    var nameArg = GetArg(args, "--assembly-name");
    var configPath = GetArg(args, "--config") ?? PluginRegConfig.DefaultFileName;

    string? assemblyPath = null;
    string? connectionString = null;
    string? assemblyName = null;
    string? nupkgPath = null;
    string? publisherPrefix = null;
    string? solutionName = null;
    EnvironmentConfig? envConfig = null;

    // Try loading config if it exists
    PluginRegConfig? config = null;
    if (File.Exists(configPath))
    {
        try { config = PluginRegConfig.Load(configPath); }
        catch (Exception ex) { return (null, null, null, null, null, null, null, $"ERROR loading config: {ex.Message}"); }
    }

    // Resolve assembly path
    if (!string.IsNullOrEmpty(dllArg))
    {
        assemblyPath = dllArg;
    }
    else if (config?.Assemblies.Count > 0)
    {
        assemblyPath = config.Assemblies[0].Path;
    }

    if (string.IsNullOrEmpty(assemblyPath))
        return (null, null, null, null, null, null, null, "ERROR: No assembly path. Use --dll <path> or create a pluginreg.json (run 'init').");

    if (!File.Exists(assemblyPath))
        return (null, null, null, null, null, null, null, $"ERROR: DLL not found: {assemblyPath}");

    assemblyName = nameArg ?? config?.Assemblies.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(assemblyPath);

    // Resolve nupkg path, publisher prefix, and solution name from config
    var assemblyConfig = config?.Assemblies.FirstOrDefault();
    nupkgPath = assemblyConfig?.NupkgPath;
    publisherPrefix = assemblyConfig?.PublisherPrefix ?? "";
    solutionName = assemblyConfig?.SolutionName;

    // Resolve connection
    if (requireConnection)
    {
        if (!string.IsNullOrEmpty(connArg))
        {
            connectionString = EnvFile.Resolve(connArg, envVars);
        }
        else if (!string.IsNullOrEmpty(envArg) && config?.Environments.TryGetValue(envArg, out var ec) == true)
        {
            // Resolve ${VAR} placeholders from .env / system env vars
            EnvFile.ResolveConfig(ec, envVars);
            envConfig = ec;
            connectionString = ec.BuildConnectionString();
            Console.WriteLine($"Using environment: {envArg} ({ec.Url})");
        }
        else if (!string.IsNullOrEmpty(envArg))
        {
            var available = config?.Environments.Keys;
            var hint = available?.Count > 0 ? $" Available: {string.Join(", ", available)}" : "";
            return (null, null, null, null, null, null, null, $"ERROR: Environment '{envArg}' not found in config.{hint}");
        }
        else
        {
            return (null, null, null, null, null, null, null, "ERROR: No connection. Use --env <name>, --connection <string>, or set DATAVERSE_CONNECTION_STRING.");
        }
    }

    return (assemblyPath, connectionString, assemblyName, nupkgPath, publisherPrefix, solutionName, envConfig, null);
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 2)] + "..";

static string MapParameterTypeName(int type) => type switch
{
    0 => "Boolean", 1 => "DateTime", 2 => "Decimal", 3 => "Entity",
    4 => "EntityCollection", 5 => "EntityReference", 6 => "Float",
    7 => "Integer", 8 => "Money", 9 => "Picklist", 10 => "String",
    11 => "StringArray", 12 => "Guid", _ => $"Type({type})"
};
