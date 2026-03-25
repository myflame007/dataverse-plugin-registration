using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Registers/updates plugin steps and images in Dataverse,
/// similar to what spkl does for ILMerge-based plugins.
/// Supports both classic PluginAssembly and NuGet-based PluginPackage deployments.
/// </summary>
public class StepRegistrar
{
    private readonly IOrganizationService _svc;
    private readonly Action<string> _log;

    public StepRegistrar(IOrganizationService service, Action<string> log)
    {
        _svc = service;
        _log = log;
    }

    /// <summary>
    /// Registers all steps for the given plugin assembly.
    /// Uses bulk-fetch + ExecuteMultipleRequest for optimal performance.
    /// </summary>
    public void RegisterSteps(string assemblyName, List<PluginStepInfo> steps)
    {
        // 1. Find plugin types that are already registered
        var pluginTypes = FindPluginTypes(assemblyName);

        if (pluginTypes.Count == 0)
        {
            _log($"ERROR: No registered PluginTypes found for assembly '{assemblyName}'.");
            _log("Make sure the assembly/package is already deployed (e.g. via 'pac plugin push').");
            return;
        }

        _log($"Found {pluginTypes.Count} registered PluginType(s) for '{assemblyName}'.");

        // 2. Cache SdkMessage + SdkMessageFilter lookups
        var messageCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var filterCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // 3. Bulk-fetch ALL existing steps for this assembly's plugin types (single query)
        var allExistingSteps = FetchAllStepsForPluginTypes(pluginTypes.Values);

        // 4. Build step write batch
        var stepBatch = new ExecuteMultipleRequest
        {
            Requests = new OrganizationRequestCollection(),
            Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
        };

        // Track resolved steps: step info + step ID (Empty for pending creates)
        var resolvedSteps = new List<(PluginStepInfo Step, Guid StepId)>();
        // Map: batch request index → (resolvedSteps index, log message, isCreate)
        var batchStepMap = new Dictionary<int, (int ResultIndex, string Message, bool IsCreate)>();

        foreach (var step in steps)
        {
            var typeFullName = step.PluginTypeName;
            if (!pluginTypes.TryGetValue(typeFullName, out var pluginTypeId))
            {
                _log($"  SKIP: PluginType '{typeFullName}' not found in Dataverse. Skipping step '{step.Name}'.");
                continue;
            }

            // Resolve SdkMessage
            var messageId = ResolveMessage(step.Message, messageCache);
            if (messageId == Guid.Empty)
            {
                _log($"  SKIP: SdkMessage '{step.Message}' not found. Skipping step '{step.Name}'.");
                continue;
            }

            // Resolve SdkMessageFilter (optional, depends on entity)
            Guid? filterId = null;
            if (!string.IsNullOrEmpty(step.EntityLogicalName))
            {
                filterId = ResolveFilter(messageId, step.EntityLogicalName, filterCache);
                if (filterId == null)
                    _log($"  WARN: No SdkMessageFilter for '{step.Message}' on '{step.EntityLogicalName}'. Registering without filter.");
            }

            // Look up existing step from bulk-fetched data
            var stepKey = $"{pluginTypeId}|{step.Name}";
            allExistingSteps.TryGetValue(stepKey, out var existing);

            var entity = BuildStepEntity(pluginTypeId, messageId, filterId, step);

            if (existing != null)
            {
                if (!StepHasChanges(existing, step, messageId, filterId))
                {
                    _log($"  UNCHANGED step: {step.Name}");
                    resolvedSteps.Add((step, existing.Id));
                    continue;
                }

                entity.Id = existing.Id;
                batchStepMap[stepBatch.Requests.Count] = (resolvedSteps.Count, $"  UPDATED step: {step.Name}", false);
                stepBatch.Requests.Add(new UpdateRequest { Target = entity });
                resolvedSteps.Add((step, existing.Id));
            }
            else
            {
                batchStepMap[stepBatch.Requests.Count] = (resolvedSteps.Count, $"  CREATED step: {step.Name}", true);
                stepBatch.Requests.Add(new CreateRequest { Target = entity });
                resolvedSteps.Add((step, Guid.Empty)); // placeholder, filled from response
            }
        }

        // 5. Execute step batch (single HTTP call for all step writes)
        if (stepBatch.Requests.Count > 0)
        {
            var stepResponse = (ExecuteMultipleResponse)_svc.Execute(stepBatch);

            foreach (var resp in stepResponse.Responses)
            {
                if (!batchStepMap.TryGetValue(resp.RequestIndex, out var info))
                    continue;

                if (resp.Fault != null)
                {
                    _log($"  ERROR step '{resolvedSteps[info.ResultIndex].Step.Name}': {resp.Fault.Message}");
                    continue;
                }

                _log(info.Message);

                // For creates, capture the new step ID from the response
                if (info.IsCreate && resp.Response is CreateResponse createResp)
                {
                    var (step, _) = resolvedSteps[info.ResultIndex];
                    resolvedSteps[info.ResultIndex] = (step, createResp.id);
                }
            }
        }

        // 6. Collect all valid step IDs for image processing (exclude failed creates)
        var validSteps = resolvedSteps.Where(r => r.StepId != Guid.Empty).ToList();
        if (validSteps.Count == 0) return;

        // 7. Bulk-fetch ALL existing images for these steps (single query)
        var allExistingImages = FetchAllImagesForSteps(validSteps.Select(s => s.StepId));

        // 8. Build image write batch
        var imageBatch = new ExecuteMultipleRequest
        {
            Requests = new OrganizationRequestCollection(),
            Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
        };
        var imageLogMessages = new Dictionary<int, string>();

        foreach (var (step, stepId) in validSteps)
        {
            CollectImageOperation(step, stepId, step.Image1Name, step.Image1Type, step.Image1Attributes,
                allExistingImages, imageBatch, imageLogMessages);
            CollectImageOperation(step, stepId, step.Image2Name, step.Image2Type, step.Image2Attributes,
                allExistingImages, imageBatch, imageLogMessages);
        }

        // 9. Execute image batch (single HTTP call for all image writes)
        if (imageBatch.Requests.Count > 0)
        {
            var imgResponse = (ExecuteMultipleResponse)_svc.Execute(imageBatch);

            foreach (var resp in imgResponse.Responses)
            {
                if (resp.Fault != null)
                    _log($"    ERROR image: {resp.Fault.Message}");
                else if (imageLogMessages.TryGetValue(resp.RequestIndex, out var msg))
                    _log(msg);
            }
        }
    }

    /// <summary>Finds all PluginType records for a given assembly name.</summary>
    private Dictionary<string, Guid> FindPluginTypes(string assemblyName)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Try PluginAssembly first
        var assemblyQuery = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, assemblyName) } }
        };

        var assemblies = _svc.RetrieveMultiple(assemblyQuery);
        Guid? assemblyId = assemblies.Entities.FirstOrDefault()?.Id;

        // Also try PluginPackage (NuGet-based)
        Guid? packageId = null;
        try
        {
            var packageQuery = new QueryExpression("pluginpackage")
            {
                ColumnSet = new ColumnSet("pluginpackageid"),
                Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, assemblyName) } }
            };
            var packages = _svc.RetrieveMultiple(packageQuery);
            packageId = packages.Entities.FirstOrDefault()?.Id;
        }
        catch
        {
            // PluginPackage entity might not exist in older environments
        }

        // Query PluginType by assembly or package
        var typeQuery = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "plugintypeid")
        };

        if (assemblyId.HasValue && packageId.HasValue)
        {
            var orFilter = new FilterExpression(LogicalOperator.Or);
            orFilter.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId.Value);
            orFilter.AddCondition("pluginpackageid", ConditionOperator.Equal, packageId.Value);
            typeQuery.Criteria.AddFilter(orFilter);
        }
        else if (assemblyId.HasValue)
        {
            typeQuery.Criteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId.Value);
        }
        else if (packageId.HasValue)
        {
            typeQuery.Criteria.AddCondition("pluginpackageid", ConditionOperator.Equal, packageId.Value);
        }
        else
        {
            return result;
        }

        var types = _svc.RetrieveMultiple(typeQuery);
        foreach (var t in types.Entities)
        {
            var typeName = t.GetAttributeValue<string>("typename");
            if (!string.IsNullOrEmpty(typeName))
                result[typeName] = t.Id;
        }

        return result;
    }

    private Guid ResolveMessage(string messageName, Dictionary<string, Guid> cache)
    {
        if (cache.TryGetValue(messageName, out var cached)) return cached;

        var query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) } }
        };

        var msg = _svc.RetrieveMultiple(query).Entities.FirstOrDefault();
        var id = msg?.Id ?? Guid.Empty;
        cache[messageName] = id;
        return id;
    }

    private Guid? ResolveFilter(Guid messageId, string entityLogicalName, Dictionary<string, Guid> cache)
    {
        var key = $"{messageId}|{entityLogicalName}";
        if (cache.TryGetValue(key, out var cached)) return cached;

        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                    new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entityLogicalName)
                }
            }
        };

        var filter = _svc.RetrieveMultiple(query).Entities.FirstOrDefault();
        if (filter != null)
        {
            cache[key] = filter.Id;
            return filter.Id;
        }

        return null;
    }

    /// <summary>Fetches all existing steps for the given plugin types in a single query.</summary>
    private Dictionary<string, Entity> FetchAllStepsForPluginTypes(IEnumerable<Guid> pluginTypeIds)
    {
        var result = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var ids = pluginTypeIds.ToList();
        if (ids.Count == 0) return result;

        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet(
                "sdkmessageprocessingstepid", "name", "plugintypeid",
                "stage", "mode", "rank", "filteringattributes",
                "description", "configuration", "asyncautodelete",
                "sdkmessageid", "sdkmessagefilterid")
        };
        query.Criteria.AddCondition("plugintypeid", ConditionOperator.In, ids.Cast<object>().ToArray());

        foreach (var e in _svc.RetrieveMultiple(query).Entities)
        {
            var pluginTypeId = e.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty;
            var name = e.GetAttributeValue<string>("name") ?? "";
            result[$"{pluginTypeId}|{name}"] = e;
        }

        return result;
    }

    /// <summary>Fetches all existing images for the given step IDs in a single query.</summary>
    private Dictionary<string, Entity> FetchAllImagesForSteps(IEnumerable<Guid> stepIds)
    {
        var result = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var ids = stepIds.ToList();
        if (ids.Count == 0) return result;

        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet(
                "sdkmessageprocessingstepimageid", "sdkmessageprocessingstepid",
                "name", "imagetype", "attributes")
        };
        query.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.In, ids.Cast<object>().ToArray());

        foreach (var e in _svc.RetrieveMultiple(query).Entities)
        {
            var stepId = e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty;
            var name = e.GetAttributeValue<string>("name") ?? "";
            result[$"{stepId}|{name}"] = e;
        }

        return result;
    }

    private static Entity BuildStepEntity(Guid pluginTypeId, Guid messageId, Guid? filterId, PluginStepInfo step)
    {
        var entity = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = step.Name,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
            ["stage"] = new OptionSetValue(step.Stage),
            ["mode"] = new OptionSetValue(step.ExecutionMode),
            ["rank"] = step.ExecutionOrder,
            ["supporteddeployment"] = new OptionSetValue(0), // ServerOnly
            ["asyncautodelete"] = step.DeleteAsyncOperation,
            ["statecode"] = new OptionSetValue(0),      // Enabled
            ["statuscode"] = new OptionSetValue(1)       // Active
        };

        if (filterId.HasValue)
            entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

        if (!string.IsNullOrEmpty(step.FilteringAttributes))
            entity["filteringattributes"] = step.FilteringAttributes;

        if (!string.IsNullOrEmpty(step.Description))
            entity["description"] = step.Description;

        if (!string.IsNullOrEmpty(step.UnSecureConfiguration))
            entity["configuration"] = step.UnSecureConfiguration;

        return entity;
    }

    /// <summary>
    /// Checks an image and adds it to the batch if it needs to be created or updated.
    /// Unchanged and invalid images are logged immediately and skipped.
    /// </summary>
    private void CollectImageOperation(PluginStepInfo step, Guid stepId,
        string? imageName, int imageType, string? imageAttributes,
        Dictionary<string, Entity> allExistingImages,
        ExecuteMultipleRequest batch, Dictionary<int, string> logMessages)
    {
        if (imageType < 0 || string.IsNullOrEmpty(imageName)) return;

        if (!IsValidImageType(step.Message, imageType))
        {
            _log($"    SKIP image '{imageName}': {step.Message} does not support image type {imageType} (0=Pre, 1=Post, 2=Both)");
            return;
        }

        allExistingImages.TryGetValue($"{stepId}|{imageName}", out var existing);

        var entity = new Entity("sdkmessageprocessingstepimage")
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["name"] = imageName,
            ["entityalias"] = imageName,
            ["imagetype"] = new OptionSetValue(imageType),
            ["messagepropertyname"] = GetMessagePropertyName(step.Message)
        };

        if (!string.IsNullOrEmpty(imageAttributes))
            entity["attributes"] = imageAttributes;

        if (existing != null)
        {
            if (!ImageHasChanges(existing, imageType, imageAttributes))
            {
                _log($"    UNCHANGED image: {imageName} (type={imageType})");
                return;
            }

            entity.Id = existing.Id;
            var idx = batch.Requests.Count;
            batch.Requests.Add(new UpdateRequest { Target = entity });
            logMessages[idx] = $"    UPDATED image: {imageName} (type={imageType})";
        }
        else
        {
            var idx = batch.Requests.Count;
            batch.Requests.Add(new CreateRequest { Target = entity });
            logMessages[idx] = $"    CREATED image: {imageName} (type={imageType})";
        }
    }

    /// <summary>Compares existing step attributes with desired values to detect changes.</summary>
    private static bool StepHasChanges(Entity existing, PluginStepInfo step, Guid messageId, Guid? filterId)
    {
        if (existing.GetAttributeValue<OptionSetValue>("stage")?.Value != step.Stage) return true;
        if (existing.GetAttributeValue<OptionSetValue>("mode")?.Value != step.ExecutionMode) return true;
        if (existing.GetAttributeValue<int>("rank") != step.ExecutionOrder) return true;
        if (existing.GetAttributeValue<bool>("asyncautodelete") != step.DeleteAsyncOperation) return true;
        if ((existing.GetAttributeValue<string>("filteringattributes") ?? "") != (step.FilteringAttributes ?? "")) return true;
        if ((existing.GetAttributeValue<string>("description") ?? "") != (step.Description ?? "")) return true;
        if ((existing.GetAttributeValue<string>("configuration") ?? "") != (step.UnSecureConfiguration ?? "")) return true;

        var existingMsg = existing.GetAttributeValue<EntityReference>("sdkmessageid")?.Id;
        if (existingMsg != messageId) return true;

        var existingFilter = existing.GetAttributeValue<EntityReference>("sdkmessagefilterid")?.Id;
        if (existingFilter != filterId) return true;

        return false;
    }

    /// <summary>Compares existing image attributes with desired values to detect changes.</summary>
    private static bool ImageHasChanges(Entity existing, int imageType, string? attributes)
    {
        if (existing.GetAttributeValue<OptionSetValue>("imagetype")?.Value != imageType) return true;
        if ((existing.GetAttributeValue<string>("attributes") ?? "") != (attributes ?? "")) return true;
        return false;
    }

    /// <summary>Maps message names to their MessagePropertyName for images.</summary>
    private static string GetMessagePropertyName(string message) => message.ToUpperInvariant() switch
    {
        "CREATE" => "Id",
        "UPDATE" => "Target",
        "DELETE" => "Target",
        "SETSTATE" or "SETSTATEDYNAMICENTITY" => "EntityMoniker",
        "ASSIGN" => "Target",
        "MERGE" => "Target",
        "RETRIEVE" => "Target",
        "RETRIEVEMULTIPLE" => "Query",
        _ => "Target"
    };

    /// <summary>
    /// Validates whether an image type is supported for the given message.
    /// Create only supports PostImage, Delete only PreImage.
    /// </summary>
    private static bool IsValidImageType(string message, int imageType) => message.ToUpperInvariant() switch
    {
        // 0=PreImage, 1=PostImage, 2=Both
        "CREATE" => imageType == 1,              // Only PostImage
        "DELETE" => imageType == 0,              // Only PreImage
        _ => true                                // Update, SetState, etc. support all types
    };
}
