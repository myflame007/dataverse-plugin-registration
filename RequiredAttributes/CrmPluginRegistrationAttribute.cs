// ─────────────────────────────────────────────────────────────────────────────
// Scaffolded by 'plugin-reg init'. Re-run 'plugin-reg init' to update.
// Change the namespace below to match your project.
// ─────────────────────────────────────────────────────────────────────────────
using System;

namespace PluginAttributes
{
    /// <summary>
    /// Registers a Dataverse plugin step, or marks a class as a Custom API handler.
    /// Processed by the Dataverse.PluginRegistration tool — not executed at runtime.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class CrmPluginRegistrationAttribute : Attribute
    {
        // ── Custom API constructor ─────────────────────────────────────────────
        /// <summary>
        /// Marks this class as the handler for a Custom API.
        /// Combine with [CustomApiDefinition], [CustomApiRequestParameter], [CustomApiResponseProperty].
        /// </summary>
        /// <param name="message">The unique name of the Custom API (e.g. "acme_DoSomething").</param>
        public CrmPluginRegistrationAttribute(string message)
        {
            Message       = message;
            IsolationMode = IsolationModeEnum.Sandbox;
        }

        // ── Plugin Step constructor ────────────────────────────────────────────
        /// <summary>Registers a plugin step.</summary>
        /// <param name="message">SDK message name (e.g. "Update", "Create").</param>
        /// <param name="entityLogicalName">Target entity (e.g. "contact"), or "" for global.</param>
        /// <param name="stage">Execution stage.</param>
        /// <param name="executionMode">Synchronous or asynchronous.</param>
        /// <param name="filteringAttributes">Comma-separated attributes that trigger the step, or "".</param>
        /// <param name="stepName">Unique display name for the step.</param>
        /// <param name="executionOrder">Rank when multiple plugins handle the same message (typically 1).</param>
        /// <param name="isolationMode">Sandbox (recommended) or None.</param>
        public CrmPluginRegistrationAttribute(
            string            message,
            string            entityLogicalName,
            StageEnum         stage,
            ExecutionModeEnum executionMode,
            string            filteringAttributes,
            string            stepName,
            int               executionOrder,
            IsolationModeEnum isolationMode)
        {
            Message             = message;
            EntityLogicalName   = entityLogicalName;
            Stage               = stage;
            ExecutionMode       = executionMode;
            FilteringAttributes = filteringAttributes;
            Name                = stepName;
            ExecutionOrder      = executionOrder;
            IsolationMode       = isolationMode;
        }

        // ── Constructor properties ─────────────────────────────────────────────

        public string            Message             { get; }
        public string            EntityLogicalName   { get; }
        public StageEnum         Stage               { get; }
        public ExecutionModeEnum ExecutionMode       { get; }
        public IsolationModeEnum IsolationMode       { get; }
        public string            FilteringAttributes { get; }
        public string            Name                { get; }
        public int               ExecutionOrder      { get; }

        // ── Optional named properties ──────────────────────────────────────────

        public string Description          { get; set; }
        public string UnSecureConfiguration { get; set; }
        public string SecureConfiguration   { get; set; }
        public bool   DeleteAsyncOperation  { get; set; }

        // ── Pre/Post Image ─────────────────────────────────────────────────────
        // Set Image1Name + Image1Type to register a pre/post image on this step.

        public ImageTypeEnum Image1Type       { get; set; }
        public string        Image1Name       { get; set; }
        public string        Image1Attributes { get; set; }

        public ImageTypeEnum Image2Type       { get; set; }
        public string        Image2Name       { get; set; }
        public string        Image2Attributes { get; set; }
    }

    // ─── Stage ────────────────────────────────────────────────────────────────

    public enum StageEnum
    {
        PreValidation = 10,
        PreOperation  = 20,
        PostOperation = 40,
    }

    // ─── ExecutionMode ────────────────────────────────────────────────────────

    public enum ExecutionModeEnum
    {
        Asynchronous = 0,
        Synchronous  = 1,
    }

    // ─── IsolationMode ────────────────────────────────────────────────────────

    public enum IsolationModeEnum
    {
        None    = 0,
        Sandbox = 1,
    }

    // ─── ImageType ────────────────────────────────────────────────────────────
    // Only set Image1Type/Image2Type if you also set Image1Name/Image2Name.
    // Create supports only PostImage; Delete supports only PreImage.

    public enum ImageTypeEnum
    {
        PreImage  = 0,
        PostImage = 1,
        Both      = 2,
    }
}
