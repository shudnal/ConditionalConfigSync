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
    internal bool IsServerControlled = true;
    internal bool IsHidden;
    internal bool PolicyStateInitialized;

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
