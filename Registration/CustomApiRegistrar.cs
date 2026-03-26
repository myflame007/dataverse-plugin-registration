using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Dataverse.PluginRegistration;

/// <summary>
/// Registers/updates Custom APIs and their request parameters + response properties
/// in Dataverse. Analogous to StepRegistrar but for the customapi / customapirequestparameter /
/// customapiresponseproperty entities.
/// </summary>
public class CustomApiRegistrar
{
    private readonly IOrganizationService _svc;
    private readonly Action<string> _log;
    private string? _solutionName;

    public CustomApiRegistrar(IOrganizationService service, Action<string> log)
    {
        _svc = service;
        _log = log;
    }

    /// <summary>
    /// Registers all Custom APIs for the given assembly.
    /// </summary>
    public void RegisterCustomApis(string assemblyName, List<CustomApiInfo> apis, string? solutionName = null)
    {
        if (apis.Count == 0) return;
        _solutionName = solutionName;

        // Find plugin types (same lookup as StepRegistrar)
        var pluginTypes = FindPluginTypes(assemblyName);

        if (pluginTypes.Count == 0)
        {
            _log($"ERROR: No registered PluginTypes found for assembly '{assemblyName}'.");
            _log("Make sure the assembly/package is already deployed.");
            return;
        }

        _log($"Found {pluginTypes.Count} registered PluginType(s) for '{assemblyName}'.");

        foreach (var api in apis)
        {
            if (!pluginTypes.TryGetValue(api.PluginTypeName, out var pluginTypeId))
            {
                _log($"  SKIP: PluginType '{api.PluginTypeName}' not found in Dataverse. Skipping Custom API '{api.UniqueName}'.");
                continue;
            }

            // Upsert the Custom API entity
            var apiId = UpsertCustomApi(pluginTypeId, api);

            // Upsert request parameters
            foreach (var param in api.RequestParameters)
            {
                UpsertRequestParameter(apiId, api.UniqueName, param);
            }

            // Upsert response properties
            foreach (var prop in api.ResponseProperties)
            {
                UpsertResponseProperty(apiId, api.UniqueName, prop);
            }

            // Warn about orphaned parameters/properties in Dataverse
            CheckOrphanedParameters(apiId, api);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom API Upsert
    // ═══════════════════════════════════════════════════════════════

    private Guid UpsertCustomApi(Guid pluginTypeId, CustomApiInfo api)
    {
        var existing = FindExistingCustomApi(api.UniqueName);

        var displayName = !string.IsNullOrEmpty(api.DisplayName) ? api.DisplayName : api.UniqueName;

        var entity = new Entity("customapi")
        {
            ["uniquename"] = api.UniqueName,
            ["name"] = displayName,
            ["displayname"] = displayName,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["bindingtype"] = new OptionSetValue(api.BindingType),
            ["isfunction"] = api.IsFunction,
            ["isprivate"] = api.IsPrivate,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(api.AllowedProcessingStepType)
        };

        if (!string.IsNullOrEmpty(api.Description))
            entity["description"] = api.Description;

        if (!string.IsNullOrEmpty(api.BoundEntity) && api.BindingType != 0)
            entity["boundentitylogicalname"] = api.BoundEntity;

        if (!string.IsNullOrEmpty(api.ExecutePrivilegeName))
            entity["executeprivilegename"] = api.ExecutePrivilegeName;

        if (existing != null)
        {
            if (!CustomApiHasChanges(existing, api, pluginTypeId))
                _log($"  UNCHANGED api: {api.UniqueName}");
            else
            {
                entity.Id = existing.Id;
                _svc.Update(entity);
                _log($"  UPDATED api: {api.UniqueName}");
            }

            EnsureInSolution(existing.Id);
            return existing.Id;
        }
        else
        {
            var request = new CreateRequest { Target = entity };
            if (!string.IsNullOrEmpty(_solutionName))
                request.Parameters["SolutionUniqueName"] = _solutionName;
            var response = (CreateResponse)_svc.Execute(request);
            _log($"  CREATED api: {api.UniqueName}");
            return response.id;
        }
    }

    /// <summary>
    /// Adds a component to the configured solution. Safe to call repeatedly — idempotent.
    /// Resolves the component type dynamically from solutioncomponent to avoid hardcoding.
    /// </summary>
    private void EnsureInSolution(Guid componentId)
    {
        if (string.IsNullOrEmpty(_solutionName)) return;

        var typeQuery = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("componenttype"),
            TopCount = 1
        };
        typeQuery.Criteria.AddCondition("objectid", ConditionOperator.Equal, componentId);
        var typeResult = _svc.RetrieveMultiple(typeQuery);
        var componentType = typeResult.Entities.FirstOrDefault()?.GetAttributeValue<OptionSetValue>("componenttype")?.Value;

        if (componentType == null)
        {
            _log($"    WARN: Could not resolve component type — skipping solution assignment.");
            return;
        }

        var request = new OrganizationRequest("AddSolutionComponent")
        {
            Parameters =
            {
                ["ComponentId"]           = componentId,
                ["ComponentType"]         = componentType.Value,
                ["SolutionUniqueName"]    = _solutionName,
                ["AddRequiredComponents"] = false,
            }
        };
        _svc.Execute(request);
        _log($"    Added to solution: {_solutionName}");
    }

    internal static bool CustomApiHasChanges(Entity existing, CustomApiInfo api, Guid pluginTypeId)
    {
        var displayName = !string.IsNullOrEmpty(api.DisplayName) ? api.DisplayName : api.UniqueName;

        if ((existing.GetAttributeValue<string>("displayname") ?? "") != displayName) return true;
        if ((existing.GetAttributeValue<string>("description") ?? "") != (api.Description ?? "")) return true;
        if (existing.GetAttributeValue<OptionSetValue>("bindingtype")?.Value != api.BindingType) return true;
        if (existing.GetAttributeValue<bool>("isfunction") != api.IsFunction) return true;
        if (existing.GetAttributeValue<bool>("isprivate") != api.IsPrivate) return true;
        if (existing.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value != api.AllowedProcessingStepType) return true;
        if ((existing.GetAttributeValue<string>("boundentitylogicalname") ?? "") != (api.BoundEntity ?? "")) return true;

        var existingPluginType = existing.GetAttributeValue<EntityReference>("plugintypeid")?.Id;
        if (existingPluginType != pluginTypeId) return true;

        return false;
    }

    private Entity? FindExistingCustomApi(string uniqueName)
    {
        var query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet(
                "customapiid", "displayname", "description", "bindingtype",
                "isfunction", "isprivate", "allowedcustomprocessingsteptype",
                "boundentitylogicalname", "plugintypeid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName)
                }
            }
        };

        return _svc.RetrieveMultiple(query).Entities.FirstOrDefault();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Request Parameter Upsert
    // ═══════════════════════════════════════════════════════════════

    private void UpsertRequestParameter(Guid apiId, string apiUniqueName, CustomApiParameterInfo param)
    {
        var fullName = $"{apiUniqueName}.{param.UniqueName}";
        var existing = FindExistingParameter("customapirequestparameter", apiId, param.UniqueName);

        var displayName = !string.IsNullOrEmpty(param.DisplayName) ? param.DisplayName : param.UniqueName;

        var entity = new Entity("customapirequestparameter")
        {
            ["uniquename"] = param.UniqueName,
            ["name"] = displayName,
            ["displayname"] = displayName,
            ["type"] = new OptionSetValue(param.Type),
            ["isoptional"] = !param.IsRequired,
            ["customapiid"] = new EntityReference("customapi", apiId)
        };

        if (!string.IsNullOrEmpty(param.Description))
            entity["description"] = param.Description;

        if (!string.IsNullOrEmpty(param.LogicalEntityName))
            entity["logicalentityname"] = param.LogicalEntityName;

        if (existing != null)
        {
            if (!ParameterHasChanges(existing, param, isRequest: true))
            {
                _log($"    UNCHANGED request param: {fullName}");
                return;
            }

            entity.Id = existing.Id;
            _svc.Update(entity);
            _log($"    UPDATED request param: {fullName}");
        }
        else
        {
            _svc.Create(entity);
            _log($"    CREATED request param: {fullName}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response Property Upsert
    // ═══════════════════════════════════════════════════════════════

    private void UpsertResponseProperty(Guid apiId, string apiUniqueName, CustomApiParameterInfo prop)
    {
        var fullName = $"{apiUniqueName}.{prop.UniqueName}";
        var existing = FindExistingParameter("customapiresponseproperty", apiId, prop.UniqueName);

        var displayName = !string.IsNullOrEmpty(prop.DisplayName) ? prop.DisplayName : prop.UniqueName;

        var entity = new Entity("customapiresponseproperty")
        {
            ["uniquename"] = prop.UniqueName,
            ["name"] = displayName,
            ["displayname"] = displayName,
            ["type"] = new OptionSetValue(prop.Type),
            ["customapiid"] = new EntityReference("customapi", apiId)
        };

        if (!string.IsNullOrEmpty(prop.Description))
            entity["description"] = prop.Description;

        if (!string.IsNullOrEmpty(prop.LogicalEntityName))
            entity["logicalentityname"] = prop.LogicalEntityName;

        if (existing != null)
        {
            if (!ParameterHasChanges(existing, prop, isRequest: false))
            {
                _log($"    UNCHANGED response prop: {fullName}");
                return;
            }

            entity.Id = existing.Id;
            _svc.Update(entity);
            _log($"    UPDATED response prop: {fullName}");
        }
        else
        {
            _svc.Create(entity);
            _log($"    CREATED response prop: {fullName}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Shared Helpers
    // ═══════════════════════════════════════════════════════════════

    internal static bool ParameterHasChanges(Entity existing, CustomApiParameterInfo param, bool isRequest)
    {
        var displayName = !string.IsNullOrEmpty(param.DisplayName) ? param.DisplayName : param.UniqueName;

        if ((existing.GetAttributeValue<string>("displayname") ?? "") != displayName) return true;
        if ((existing.GetAttributeValue<string>("description") ?? "") != (param.Description ?? "")) return true;
        if (existing.GetAttributeValue<OptionSetValue>("type")?.Value != param.Type) return true;

        if (isRequest)
        {
            if (existing.GetAttributeValue<bool>("isoptional") != !param.IsRequired) return true;
        }

        var existingEntity = existing.GetAttributeValue<string>("logicalentityname") ?? "";
        if (existingEntity != (param.LogicalEntityName ?? "")) return true;

        return false;
    }

    private Entity? FindExistingParameter(string entityName, Guid apiId, string uniqueName)
    {
        var query = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet(
                $"{entityName}id", "uniquename", "displayname", "description",
                "type", "logicalentityname",
                entityName == "customapirequestparameter" ? "isoptional" : "type"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("customapiid", ConditionOperator.Equal, apiId),
                    new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName)
                }
            }
        };

        return _svc.RetrieveMultiple(query).Entities.FirstOrDefault();
    }

    /// <summary>
    /// Checks for parameters/properties that exist in Dataverse but are no longer defined in code.
    /// </summary>
    private void CheckOrphanedParameters(Guid apiId, CustomApiInfo api)
    {
        // Check request parameters
        var existingParams = FindAllParameters("customapirequestparameter", apiId);
        foreach (var existing in existingParams)
        {
            var name = existing.GetAttributeValue<string>("uniquename");
            if (name != null && !api.RequestParameters.Any(p => p.UniqueName == name))
            {
                _log($"    WARN: Request parameter '{api.UniqueName}.{name}' exists in Dataverse but not in code.");
            }
        }

        // Check response properties
        var existingProps = FindAllParameters("customapiresponseproperty", apiId);
        foreach (var existing in existingProps)
        {
            var name = existing.GetAttributeValue<string>("uniquename");
            if (name != null && !api.ResponseProperties.Any(p => p.UniqueName == name))
            {
                _log($"    WARN: Response property '{api.UniqueName}.{name}' exists in Dataverse but not in code.");
            }
        }
    }

    private List<Entity> FindAllParameters(string entityName, Guid apiId)
    {
        var query = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet("uniquename"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("customapiid", ConditionOperator.Equal, apiId)
                }
            }
        };

        return _svc.RetrieveMultiple(query).Entities.ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Plugin Type Lookup (shared logic with StepRegistrar)
    // ═══════════════════════════════════════════════════════════════

    private Dictionary<string, Guid> FindPluginTypes(string assemblyName)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Try PluginAssembly
        var assemblyQuery = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, assemblyName) } }
        };

        var assemblies = _svc.RetrieveMultiple(assemblyQuery);
        Guid? assemblyId = assemblies.Entities.FirstOrDefault()?.Id;

        // Try PluginPackage (NuGet-based)
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
        catch (Exception ex) when (ex.Message.Contains("pluginpackage", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            _log("  INFO: PluginPackage entity not available — using PluginAssembly fallback.");
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
}
