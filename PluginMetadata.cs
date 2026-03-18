namespace Dataverse.PluginRegistration;

/// <summary>
/// Represents one plugin step registration extracted from CrmPluginRegistrationAttribute.
/// </summary>
public class PluginStepInfo
{
    public required string PluginTypeName { get; set; }
    public required string Message { get; set; }
    public required string Name { get; set; }
    public string? EntityLogicalName { get; set; }
    public int Stage { get; set; }
    public int ExecutionMode { get; set; }
    public string? FilteringAttributes { get; set; }
    public int ExecutionOrder { get; set; } = 1;
    public int IsolationMode { get; set; } = 2; // Sandbox
    public string? Description { get; set; }
    public string? UnSecureConfiguration { get; set; }
    public string? SecureConfiguration { get; set; }
    public bool DeleteAsyncOperation { get; set; }

    // Image 1 — type -1 means not configured
    public int Image1Type { get; set; } = -1;
    public string? Image1Name { get; set; }
    public string? Image1Attributes { get; set; }

    // Image 2
    public int Image2Type { get; set; } = -1;
    public string? Image2Name { get; set; }
    public string? Image2Attributes { get; set; }
}
