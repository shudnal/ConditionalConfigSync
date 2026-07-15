using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Describes whether and why the current process can change a Conditional setting's server ownership policy.
/// </summary>
[Description("Availability state for changing a Conditional config entry's server ownership policy.")]
public enum ConfigSyncPolicyControlState
{
    /// <summary>The setting has a fixed ownership mode and cannot be changed by server policy.</summary>
    Fixed,

    /// <summary>The current process may request or apply a policy change.</summary>
    Available,

    /// <summary>A compatible active server session is required before policy can be changed.</summary>
    RequiresCompatibleServerSession,

    /// <summary>The server session is active, but the current player lacks administrator access.</summary>
    RequiresAdministratorAccess,
}
