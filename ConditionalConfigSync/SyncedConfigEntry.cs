using System.ComponentModel;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

/// <summary>
/// Common metadata attached to every BepInEx config entry registered with ConditionalConfigSync.
/// </summary>
/// <remarks>
/// Most mods use the typed <see cref="SyncedConfigEntry{T}"/> returned by
/// <see cref="ConditionalConfigSync.AddConfigEntry{T}(ConfigEntry{T})"/> instead of working with this base class directly.
/// </remarks>
[Description("Base metadata for a BepInEx config entry registered with ConditionalConfigSync.")]
public abstract class OwnConfigEntryBase
{
    /// <summary>
    /// The client's original local value while a server-controlled value is active.
    /// </summary>
    /// <remarks>
    /// This field is public for compatibility with ServerSync integrations. New code should normally use
    /// <c>AssignLocalValue</c> on the typed wrapper instead of modifying this field directly.
    /// </remarks>
    [Description("The client's saved local fallback while a server-controlled value is active.")]
    public object? LocalBaseValue;
    internal bool HasLocalBaseValue;

    // ConditionalConfigSync also keeps the last server value, even when the entry is client-controlled.
    internal object? ServerValue;
    internal bool HasServerValue;

    /// <summary>
    /// Gets whether this setting is currently controlled and synchronized by the server.
    /// </summary>
    /// <remarks>
    /// For <see cref="ConfigSyncMode.Conditional"/> settings this value reflects the effective server policy after it
    /// has been initialized. Before that point it returns the normalized mod-defined default, including compatibility
    /// code that assigns <see cref="SynchronizedConfig"/> after registration.
    /// </remarks>
    [Description("Whether this setting is currently server-controlled after applying effective policy.")]
    public bool IsServerControlled
    {
        get => IsPolicyStateInitialized ? isServerControlled : ServerControlledByDefault;
        internal set => isServerControlled = value;
    }

    private bool isServerControlled = true;

    /// <summary>Gets whether effective server policy currently hides this setting from compatible config UIs.</summary>
    [Description("Whether effective server policy currently hides this setting from compatible config UIs.")]
    public bool IsHidden { get; internal set; }

    /// <summary>
    /// Gets whether the effective policy state has been initialized for the current server session.
    /// </summary>
    /// <remarks>
    /// This is normally true on a running server after policy loading and on a connected client after receiving config
    /// state from the server. It returns to false when the server connection is reset and local defaults are restored.
    /// </remarks>
    [Description("Whether effective policy state has been initialized for the current server session.")]
    public bool IsPolicyStateInitialized { get; internal set; }

    /// <summary>
    /// Gets the underlying BepInEx config entry.
    /// </summary>
    [Description("The underlying BepInEx config entry.")]
    public abstract ConfigEntryBase BaseConfig { get; }

    /// <summary>
    /// Defines how policy is allowed to control this setting. The default is <see cref="ConfigSyncMode.Conditional"/>.
    /// </summary>
    /// <remarks>
    /// Prefer passing the mode directly to an <c>AddConfigEntry</c> overload so the wrapper is fully configured before
    /// registration. This public field remains available for source compatibility with existing integration helpers.
    /// </remarks>
    [Description("Whether this setting is always server-owned, policy-controlled, or always client-owned.")]
    public ConfigSyncMode SyncMode = ConfigSyncMode.Conditional;

    /// <summary>
    /// Defines the default ownership used when <see cref="SyncMode"/> is <see cref="ConfigSyncMode.Conditional"/>.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> means server-controlled by default; <see langword="false"/> means client-controlled by
    /// default. Prefer the <c>AddConfigEntry</c> overloads instead of assigning this field after registration.
    /// </remarks>
    [Description("Default ownership for Conditional mode: true for server-controlled, false for client-controlled.")]
    public bool SynchronizedConfig = true;

    /// <summary>
    /// Gets the normalized ownership defined by the mod before server policy is applied.
    /// </summary>
    /// <remarks>
    /// Fixed modes always return their fixed ownership. Conditional mode returns <see cref="SynchronizedConfig"/>.
    /// </remarks>
    [Description("Normalized mod-defined ownership before server policy is applied.")]
    public bool ServerControlledByDefault => SyncMode switch
    {
        ConfigSyncMode.AlwaysServerControlled => true,
        ConfigSyncMode.AlwaysClientControlled => false,
        _ => SynchronizedConfig,
    };

    /// <summary>
    /// Gets whether effective policy changes the ownership defined by the mod.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ConfigSyncMode.Conditional"/> settings can be overridden. A policy rule that resolves to the same
    /// ownership as the mod default is not reported as an override because it does not change runtime behavior.
    /// </remarks>
    [Description("Whether effective policy changes the mod-defined ownership.")]
    public bool IsSynchronizationOverridden =>
        SyncMode == ConfigSyncMode.Conditional && IsServerControlled != ServerControlledByDefault;

    /// <summary>Gets the effective policy override that changes this setting's ownership.</summary>
    [Description("Effective ownership override, or None when effective ownership matches the mod default.")]
    public ConfigSyncOverride EffectiveOverride => !IsSynchronizationOverridden
        ? ConfigSyncOverride.None
        : IsServerControlled
            ? ConfigSyncOverride.ForceServerControlled
            : ConfigSyncOverride.ForceClientControlled;

    /// <summary>Gets whether the current process may change this Conditional setting's server policy.</summary>
    /// <remarks>
    /// Policy changes require an active server session. On a connected client the local player must have server
    /// administrator access. Fixed synchronization modes always return <see langword="false"/>.
    /// </remarks>
    [Description("Whether the current process may change this Conditional setting's server policy.")]
    public bool CanChangeSynchronizationPolicy => ConditionalConfigSync.CanChangePolicyFor(this);

    /// <summary>Requests the opposite effective ownership policy for this Conditional setting.</summary>
    /// <returns><see langword="true"/> when the change was applied locally or dispatched to the server.</returns>
    /// <remarks>
    /// The server persists the resulting exact-setting rule in <c>ConditionalConfigSync.SyncPolicy.cfg</c>. When the
    /// requested ownership is already provided by the mod default or a section rule, an unnecessary exact rule is removed.
    /// </remarks>
    [Description("Switches this Conditional setting between server-controlled and client-controlled policy.")]
    public bool ToggleSynchronizationPolicy() => ConditionalConfigSync.TogglePolicyFor(this);

    internal void StoreLocalBaseValue(object? value)
    {
        LocalBaseValue = value;
        HasLocalBaseValue = true;
    }

    internal void ClearLocalBaseValue()
    {
        LocalBaseValue = null;
        HasLocalBaseValue = false;
    }

    internal void StoreServerValue(object? value)
    {
        ServerValue = value;
        HasServerValue = true;
    }

    internal void ClearServerValue()
    {
        ServerValue = null;
        HasServerValue = false;
    }
}

/// <summary>
/// Typed synchronization wrapper for a regular BepInEx <see cref="ConfigEntry{T}"/>.
/// </summary>
/// <typeparam name="T">The config value type.</typeparam>
/// <remarks>
/// The wrapper keeps the client's local value separate while a server value is active. When the client disconnects,
/// or when policy changes the entry back to client-controlled, the local value is restored.
/// </remarks>
[Description("Typed wrapper for a regular BepInEx config entry with separate local and server values.")]
public class SyncedConfigEntry<T> : OwnConfigEntryBase
{
    /// <summary>Creates a synchronization wrapper for the supplied BepInEx config entry.</summary>
    /// <param name="sourceConfig">The BepInEx config entry being registered.</param>
    public SyncedConfigEntry(ConfigEntry<T> sourceConfig)
    {
        SourceConfig = sourceConfig;
    }

    /// <inheritdoc/>
    public override ConfigEntryBase BaseConfig => SourceConfig;

    /// <summary>
    /// The original typed BepInEx config entry.
    /// </summary>
    [Description("The original typed BepInEx config entry.")]
    public readonly ConfigEntry<T> SourceConfig;

    /// <summary>
    /// Gets or sets the currently active value of the underlying config entry.
    /// </summary>
    /// <remarks>
    /// On a connected client this may be the server value rather than the value stored in the client's cfg file.
    /// Use <see cref="AssignLocalValue(T)"/> when changing the client's own fallback value.
    /// </remarks>
    [Description("The currently active value. On a client this may be the server value.")]
    public T Value
    {
        get => SourceConfig.Value;
        set => SourceConfig.Value = value;
    }

    /// <summary>
    /// Assigns the value owned by the local side without overwriting an active server value on a client.
    /// </summary>
    /// <param name="value">The new local value.</param>
    /// <remarks>
    /// On the server or in a local world this updates the active config entry. On a connected client controlled by
    /// the server it updates only the saved local fallback, which is restored later. BepInEx itself suppresses
    /// <c>SettingChanged</c> when the typed value is equal.
    /// </remarks>
    [Description("Updates the local fallback without overwriting an active server value on a client.")]
    public void AssignLocalValue(T value)
    {
        if (!HasLocalBaseValue)
        {
            Value = value;
        }
        else
        {
            LocalBaseValue = value;
        }
    }
}
