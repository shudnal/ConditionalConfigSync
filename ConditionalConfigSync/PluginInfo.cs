using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Shared package metadata used by the BepInEx bootstrap, both assemblies, and the publishing pipeline.
/// Change <see cref="PluginVersion"/> here when preparing a new release.
/// </summary>
[Description("Shared Conditional Config Sync package metadata. PluginVersion is the single release version source.")]
public static class PluginInfo
{
    /// <summary>BepInEx plugin identifier and Harmony owner ID.</summary>
    public const string PluginGuid = "_shudnal.ConditionalConfigSync";

    /// <summary>Human-readable plugin name shown by BepInEx and package managers.</summary>
    public const string PluginName = "Conditional Config Sync";

    /// <summary>Canonical source repository and release page.</summary>
    public const string RepositoryUrl = "https://github.com/shudnal/ConditionalConfigSync";

    /// <summary>
    /// Package and assembly version. Update this single value for a new release.
    /// The Thunderstore manifest reads the compiled plugin assembly version during packaging.
    /// </summary>
    public const string PluginVersion = "1.0.0";

    /// <summary>
    /// Current ConditionalConfigSync wire protocol version.
    /// Increase this value only when the network format becomes incompatible.
    /// </summary>
    public const int ProtocolVersion = 1;
}
