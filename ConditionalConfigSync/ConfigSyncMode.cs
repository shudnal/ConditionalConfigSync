using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Defines how ownership and synchronization of a registered configuration entry are determined.
/// </summary>
/// <remarks>
/// Choose a fixed mode when a setting must always belong to the server or the local client. Use
/// <see cref="ConfigSyncMode.Conditional"/> only when server policy may safely change the effective owner at runtime.
/// </remarks>
[Description("Controls whether a config is always server-owned, policy-controlled, or always client-owned.")]
public enum ConfigSyncMode
{
    /// <summary>
    /// The server always owns and synchronizes this setting. SyncPolicy entries are ignored.
    /// </summary>
    /// <remarks>
    /// Use this mode for settings that control shared mod mechanics or synchronized state whose behavior must remain
    /// consistent on the server and every client. Examples include gameplay rules, world-state generation, shared event
    /// behavior, network-visible calculations, and feature switches that would malfunction when peers disagree.
    /// </remarks>
    AlwaysServerControlled,

    /// <summary>SyncPolicy may override the mod-defined default ownership.</summary>
    Conditional,

    /// <summary>Each client always owns this setting. SyncPolicy entries are ignored.</summary>
    AlwaysClientControlled,
}
