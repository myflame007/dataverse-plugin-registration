using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Dataverse.PluginRegistration;

internal static class RegisterCommand
{
    internal static async Task<int> RunAsync(string[] args, Dictionary<string, string> envVars)
    {
        var (assemblyPath, connectionString, assemblyName, nupkgPath, publisherPrefix, solutionName, envConfig, error)
            = ArgsResolver.ResolveArgs(args, requireConnection: true, envVars);
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

        // 2. Connect
        Console.WriteLine("Connecting to Dataverse...");
        Console.WriteLine("  (Waiting for browser login — close the tab or press Ctrl+C to cancel)");
        ServiceClient client;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            if (envConfig != null)
            {
                client = await DataverseAuth.ConnectAsync(envConfig, cts.Token);
            }
            else
            {
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
                    Console.Error.WriteLine($"ERROR: Connection failed.");
                    Console.Error.WriteLine($"  LastError: {client.LastError ?? ""}");
                    if (client.LastException != null)
                        Console.Error.WriteLine($"  Exception: {client.LastException.Message}");
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

        // ── Solution check ─────────────────────────────────────────
        if (!string.IsNullOrEmpty(solutionName))
        {
            bool autoCreate = args.Any(a => a.Equals("--create-solution", StringComparison.OrdinalIgnoreCase));
            if (!EnsureSolution(client, solutionName, publisherPrefix, autoCreate))
                return 1;
            Console.WriteLine();
        }

        // ── Step 1/3: Push Assembly or NuGet package ───────────────
        Console.WriteLine("Step 1/3: Push Plugin Assembly/Package");
        Console.WriteLine(new string('─', 40));
        if (!string.IsNullOrEmpty(nupkgPath) && File.Exists(nupkgPath))
        {
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

        // ── Step 2/3: Register steps ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Step 2/3: Register Steps");
        Console.WriteLine(new string('─', 40));
        if (steps.Count > 0)
        {
            var registrar = new StepRegistrar(client, Console.WriteLine);
            try
            {
                registrar.RegisterSteps(assemblyName!, steps, solutionName);
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
                apiRegistrar.RegisterCustomApis(assemblyName!, customApis, solutionName);
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

    // ── Solution helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Ensures the target solution exists. If not, prompts the user (or auto-creates
    /// when --create-solution is passed). Returns false if the caller should abort.
    /// </summary>
    private static bool EnsureSolution(IOrganizationService svc, string solutionName, string? publisherPrefix, bool autoCreate)
    {
        Console.WriteLine($"Checking solution: {solutionName}");

        if (SolutionExists(svc, solutionName))
        {
            Console.WriteLine($"  Solution found.");
            return true;
        }

        Console.WriteLine($"  Solution '{solutionName}' not found in this environment.");

        if (!autoCreate)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine($"ERROR: Solution '{solutionName}' does not exist.");
                Console.Error.WriteLine($"  Run with --create-solution to create it automatically.");
                return false;
            }

            Console.Write("  Create it now? (y/N): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer != "y")
            {
                Console.Error.WriteLine("Aborted. Create the solution in Dataverse and re-run.");
                return false;
            }
        }

        return CreateSolution(svc, solutionName, publisherPrefix);
    }

    private static bool SolutionExists(IOrganizationService svc, string solutionName)
    {
        var query = new QueryExpression("solution") { TopCount = 1 };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);
        return svc.RetrieveMultiple(query).Entities.Count > 0;
    }

    private static bool CreateSolution(IOrganizationService svc, string solutionName, string? publisherPrefix)
    {
        if (string.IsNullOrWhiteSpace(publisherPrefix))
        {
            Console.Error.WriteLine("ERROR: Cannot create solution — 'publisherPrefix' is not set in pluginreg.json.");
            return false;
        }

        var publisherQuery = new QueryExpression("publisher")
        {
            ColumnSet = new ColumnSet("publisherid", "friendlyname"),
            TopCount = 1
        };
        publisherQuery.Criteria.AddCondition("customizationprefix", ConditionOperator.Equal, publisherPrefix);
        var publishers = svc.RetrieveMultiple(publisherQuery);

        if (publishers.Entities.Count == 0)
        {
            Console.Error.WriteLine($"ERROR: No publisher with prefix '{publisherPrefix}' found.");
            Console.Error.WriteLine($"  Create the publisher in Dataverse first, or correct 'publisherPrefix' in pluginreg.json.");
            return false;
        }

        var publisher = publishers.Entities[0];
        var solution = new Entity("solution")
        {
            ["uniquename"]   = solutionName,
            ["friendlyname"] = solutionName,
            ["publisherid"]  = new EntityReference("publisher", publisher.Id),
            ["version"]      = "1.0.0.0"
        };

        svc.Create(solution);
        Console.WriteLine($"  Created solution '{solutionName}' (publisher: {publisher.GetAttributeValue<string>("friendlyname") ?? publisherPrefix})");
        return true;
    }
}
