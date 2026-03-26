// ─────────────────────────────────────────────────────────────────────────────
// Scaffolded by 'plugin-reg init'. Re-run 'plugin-reg init' to update.
// Change the namespace below to match your project.
// ─────────────────────────────────────────────────────────────────────────────
using System;

namespace PluginAttributes
{
    // ─── Enums ────────────────────────────────────────────────────────────────

    public enum CustomApiBindingType
    {
        Global          = 0,
        Entity          = 1,
        EntityCollection = 2,
    }

    public enum CustomApiProcessingStepType
    {
        None         = 0,
        AsyncOnly    = 1,
        SyncAndAsync = 2,
    }

    public enum CustomApiParameterType
    {
        Boolean         = 0,
        DateTime        = 1,
        Decimal         = 2,
        Entity          = 3,
        EntityCollection = 4,
        EntityReference = 5,
        Float           = 6,
        Integer         = 7,
        Money           = 8,
        Picklist        = 9,
        String          = 10,
        StringArray     = 11,
        Guid            = 12,
    }

    // ─── CustomApiDefinitionAttribute ─────────────────────────────────────────

    /// <summary>
    /// Describes a Custom API registered via [CrmPluginRegistration("message_name")].
    /// All properties are optional — the tool uses sensible defaults when omitted.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class CustomApiDefinitionAttribute : Attribute
    {
        public string                    DisplayName               { get; set; } = "";
        public string                    Description               { get; set; } = "";
        public CustomApiBindingType      BindingType               { get; set; } = CustomApiBindingType.Global;
        public string                    BoundEntity               { get; set; } = "";
        public bool                      IsFunction                { get; set; } = false;
        public bool                      IsPrivate                 { get; set; } = false;
        public CustomApiProcessingStepType AllowedProcessingStepType { get; set; } = CustomApiProcessingStepType.SyncAndAsync;
        public string                    ExecutePrivilegeName      { get; set; } = "";
    }

    // ─── CustomApiRequestParameterAttribute ───────────────────────────────────

    /// <summary>
    /// Declares a request (input) parameter for a Custom API.
    /// Apply once per parameter — multiple allowed on the same class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class CustomApiRequestParameterAttribute : Attribute
    {
        /// <param name="uniqueName">The logical name of the parameter (e.g. "targetid").</param>
        /// <param name="type">The Dataverse type of the parameter.</param>
        public CustomApiRequestParameterAttribute(string uniqueName, CustomApiParameterType type)
        {
            UniqueName = uniqueName;
            Type       = type;
        }

        public string               UniqueName        { get; }
        public CustomApiParameterType Type            { get; }
        public bool                 IsRequired        { get; set; } = true;
        public string               DisplayName       { get; set; } = "";
        public string               Description       { get; set; } = "";
        public string               LogicalEntityName { get; set; } = "";
    }

    // ─── CustomApiResponsePropertyAttribute ───────────────────────────────────

    /// <summary>
    /// Declares a response (output) property for a Custom API.
    /// Apply once per property — multiple allowed on the same class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class CustomApiResponsePropertyAttribute : Attribute
    {
        /// <param name="uniqueName">The logical name of the response property (e.g. "result").</param>
        /// <param name="type">The Dataverse type of the property.</param>
        public CustomApiResponsePropertyAttribute(string uniqueName, CustomApiParameterType type)
        {
            UniqueName = uniqueName;
            Type       = type;
        }

        public string               UniqueName        { get; }
        public CustomApiParameterType Type            { get; }
        public string               DisplayName       { get; set; } = "";
        public string               Description       { get; set; } = "";
        public string               LogicalEntityName { get; set; } = "";
    }
}
