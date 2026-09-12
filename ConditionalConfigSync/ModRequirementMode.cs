using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Defines whether a mod's remote-installation requirement is fixed by the author or may be relaxed or strengthened by
/// server policy for incoming clients.
/// </summary>
/// <remarks>
/// This controls connection admission only. It does not synchronize a configuration value and it does not provide
/// general mod-list enforcement. The author's <c>ModRequired</c> value remains the default
/// requirement and continues to determine whether a client running the mod requires the connected server to provide it.
/// </remarks>
[Description("Controls whether ModRequired is fixed or may be overridden by server admission policy.")]
public enum ModRequirementMode
{
    /// <summary>The author-defined <c>ModRequired</c> value cannot be overridden by server policy.</summary>
    Fixed,

    /// <summary>
    /// Server policy may override the author-defined default when deciding whether connecting clients must provide the mod.
    /// </summary>
    Conditional,
}
