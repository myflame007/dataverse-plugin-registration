using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Deploys (creates or updates) a NuGet-based PluginPackage in Dataverse.
/// Replaces the need for 'pac plugin push'.
/// </summary>
public class PackageDeployer
{
    private readonly IOrganizationService _svc;
    private readonly Action<string> _log;

    public PackageDeployer(IOrganizationService service, Action<string> log)
    {
        _svc = service;
        _log = log;
    }

    /// <summary>
    /// Pushes a .nupkg file to Dataverse as a PluginPackage.
    /// Creates a new package if it doesn't exist, updates if it does.
    /// Returns the PluginPackage ID.
    /// </summary>
    public Guid Push(string nupkgPath, string packageName, string publisherPrefix, string? solutionName = null)
    {
        if (!File.Exists(nupkgPath))
            throw new FileNotFoundException($"NuGet package not found: {nupkgPath}");

        // Extract actual package ID and version from the nupkg's nuspec
        var (nupkgId, nupkgVersion) = ExtractNupkgMetadata(nupkgPath);
        var fileSize = new FileInfo(nupkgPath).Length;
        _log($"  NuGet ID: {nupkgId}, Version: {nupkgVersion}, Size: {fileSize:N0} bytes");

        var content = Convert.ToBase64String(File.ReadAllBytes(nupkgPath));
        var existing = FindExistingPackage(nupkgId);

        if (existing.HasValue)
        {
            _log($"Updating existing PluginPackage '{nupkgId}'...");
            var update = new Entity("pluginpackage", existing.Value)
            {
                ["content"] = content
            };
            _svc.Update(update);
            _log($"PluginPackage updated: {existing.Value}");
            return existing.Value;
        }
        else
        {
            _log($"Creating new PluginPackage '{nupkgId}' v{nupkgVersion}...");
            if (!string.IsNullOrEmpty(solutionName))
                _log($"  Adding to solution: {solutionName}");

            var entity = new Entity("pluginpackage")
            {
                ["name"] = nupkgId,
                ["uniquename"] = nupkgId,
                ["version"] = nupkgVersion,
                ["content"] = content
            };

            var request = new CreateRequest { Target = entity };
            if (!string.IsNullOrEmpty(solutionName))
                request.Parameters["SolutionUniqueName"] = solutionName;

            var response = (CreateResponse)_svc.Execute(request);
            _log($"PluginPackage created: {response.id}");
            return response.id;
        }
    }

    /// <summary>Extracts the package ID and version from the nuspec inside a .nupkg file.</summary>
    private static (string id, string version) ExtractNupkgMetadata(string nupkgPath)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);
        var nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("No .nuspec found in the nupkg file.");

        using var stream = nuspec.Open();
        var doc = XDocument.Load(stream);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var metadata = doc.Root?.Element(ns + "metadata");

        var id = metadata?.Element(ns + "id")?.Value
            ?? throw new InvalidOperationException("Cannot read package ID from nuspec.");
        var version = metadata?.Element(ns + "version")?.Value
            ?? throw new InvalidOperationException("Cannot read package version from nuspec.");

        return (id, version);
    }

    /// <summary>Checks if a PluginPackage with the given name already exists.</summary>
    private Guid? FindExistingPackage(string nupkgId)
    {
        var query = new QueryExpression("pluginpackage")
        {
            ColumnSet = new ColumnSet("pluginpackageid", "name", "uniquename"),
            Criteria = { FilterOperator = LogicalOperator.Or }
        };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, nupkgId);
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, nupkgId);

        var result = _svc.RetrieveMultiple(query);
        return result.Entities.FirstOrDefault()?.Id;
    }
}
