# Performance Findings: StepRegistrar

## Current Bottleneck

The StepRegistrar makes 3-5 sequential API calls per step:

1. `ResolveMessage` (cached after first call)
2. `ResolveFilter` (cached after first call)
3. `FindExistingStep` — always one call per step
4. `Create` or `Update` — always one call per step
5. `FindExistingImage` + `Create/Update` per image

With 20 steps this results in ~60-80 individual HTTP roundtrips at ~100-300ms
each.

## Recommended Approach: ExecuteMultipleRequest

Not parallelization — **batching**. Dataverse supports batch requests natively.

### Phase 1 — Bulk-fetch existing state

Instead of calling `FindExistingStep` per step, fetch **all existing steps for
the assembly** in one query and match locally:

```csharp
var allExistingSteps = FetchAllStepsForAssembly(pluginTypeIds);
var allExistingImages = FetchAllImagesForSteps(stepIds);
```

### Phase 2 — Batch all writes

```csharp
var batch = new ExecuteMultipleRequest
{
    Requests = new OrganizationRequestCollection(),
    Settings = new ExecuteMultipleSettings
    {
        ContinueOnError = false,
        ReturnResponses = true
    }
};

foreach (var step in changedSteps)
    batch.Requests.Add(new UpsertRequest { Target = stepEntity });

_svc.Execute(batch);  // Single HTTP call for all changes
```

### Expected Impact

| Approach             | Calls (20 steps) | Estimated Time |
| -------------------- | ---------------- | -------------- |
| Current (sequential) | ~60-80           | ~10-20s        |
| Bulk-fetch + batch   | ~3-4             | ~1-2s          |

~10x improvement without added complexity.

## Why Not Parallelization?

- `IOrganizationService` / `ServiceClient` is **not thread-safe** per instance
- Multiple ServiceClient instances = multiple auth tokens = overhead
- Dataverse has **API throttling limits** (~6000 requests/5min) — parallel hits
  them faster
- `ExecuteMultipleRequest` is the **Microsoft-recommended** path for batch
  operations

Für codereadability enums erstellen u verwenden
