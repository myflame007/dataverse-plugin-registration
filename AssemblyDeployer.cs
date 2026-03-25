using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Deploys (creates or updates) a classic PluginAssembly in Dataverse.
/// Used when no .nupkg is available (e.g. .NET Framework / ILMerge / non-SDK-style projects).
/// Reads the DLL as base64 and pushes it as a Sandbox/Database assembly.
/// Also ensures PluginType records exist for each IPlugin class.
/// </summary>
public class AssemblyDeployer
{
    private readonly IOrganizationService _svc;
    private readonly Action<string> _log;

    public AssemblyDeployer(IOrganizationService service, Action<string> log)
    {
        _svc = service;
        _log = log;
    }

    /// <summary>
    /// Pushes a DLL file to Dataverse as a PluginAssembly and registers PluginTypes.
    /// Creates a new assembly if it doesn't exist, updates if it does.
    /// <paramref name="pluginTypeNames"/> are the fully-qualified type names from the DLL that implement IPlugin.
    /// Returns the PluginAssembly ID.
    /// </summary>
    public Guid Push(string dllPath, string assemblyName, IEnumerable<string> pluginTypeNames)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Assembly not found: {dllPath}");

        var fileSize = new FileInfo(dllPath).Length;
        var bytes = File.ReadAllBytes(dllPath);
        var content = Convert.ToBase64String(bytes);

        // Read assembly metadata (version, culture, publickeytoken) via reflection
        var asmName = AssemblyName.GetAssemblyName(dllPath);
        var version = asmName.Version?.ToString() ?? "1.0.0.0";
        var culture = string.IsNullOrEmpty(asmName.CultureName) ? "neutral" : asmName.CultureName;
        var publicKeyToken = asmName.GetPublicKeyToken();
        var pktString = publicKeyToken != null && publicKeyToken.Length > 0
            ? BitConverter.ToString(publicKeyToken).Replace("-", "").ToLowerInvariant()
            : "null";

        _log($"  Assembly: {asmName.Name}, Version: {version}, Size: {fileSize:N0} bytes");
        _log($"  Culture: {culture}, PublicKeyToken: {pktString}");

        var existing = FindExistingAssembly(assemblyName);
        Guid assemblyId;

        if (existing.HasValue)
        {
            _log($"  Updating existing PluginAssembly '{assemblyName}'...");
            var update = new Entity("pluginassembly", existing.Value)
            {
                ["content"] = content,
                ["version"] = version,
                ["culture"] = culture,
                ["publickeytoken"] = pktString
            };
            _svc.Update(update);
            _log($"  PluginAssembly updated: {existing.Value}");
            assemblyId = existing.Value;
        }
        else
        {
            _log($"  Creating new PluginAssembly '{assemblyName}'...");
            var entity = new Entity("pluginassembly")
            {
                ["name"] = assemblyName,
                ["content"] = content,
                ["version"] = version,
                ["culture"] = culture,
                ["publickeytoken"] = pktString,
                ["isolationmode"] = new OptionSetValue(2),  // Sandbox
                ["sourcetype"] = new OptionSetValue(0)       // Database
            };

            assemblyId = _svc.Create(entity);
            _log($"  PluginAssembly created: {assemblyId}");
        }

        // Ensure PluginType records exist for each plugin class
        EnsurePluginTypes(assemblyId, pluginTypeNames);

        return assemblyId;
    }

    /// <summary>
    /// Creates PluginType records for any IPlugin types that don't exist yet.
    /// </summary>
    private void EnsurePluginTypes(Guid assemblyId, IEnumerable<string> typeNames)
    {
        var existingTypes = GetExistingPluginTypes(assemblyId);

        foreach (var typeName in typeNames)
        {
            if (existingTypes.ContainsKey(typeName))
            {
                _log($"  PluginType already exists: {typeName}");
                continue;
            }

            var entity = new Entity("plugintype")
            {
                ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
                ["typename"] = typeName,
                ["friendlyname"] = typeName,
                ["name"] = typeName
            };

            var id = _svc.Create(entity);
            _log($"  CREATED PluginType: {typeName} ({id})");
        }
    }

    /// <summary>Gets all existing PluginType records for an assembly.</summary>
    private Dictionary<string, Guid> GetExistingPluginTypes(Guid assemblyId)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "plugintypeid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId)
                }
            }
        };

        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _svc.RetrieveMultiple(query).Entities)
        {
            var name = e.GetAttributeValue<string>("typename");
            if (!string.IsNullOrEmpty(name))
                result[name] = e.Id;
        }
        return result;
    }

    /// <summary>Checks if a PluginAssembly with the given name already exists.</summary>
    private Guid? FindExistingAssembly(string assemblyName)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, assemblyName)
                }
            }
        };

        var result = _svc.RetrieveMultiple(query);
        return result.Entities.FirstOrDefault()?.Id;
    }
}
