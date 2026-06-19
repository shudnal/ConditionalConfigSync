using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace ConditionalConfigSync;

/// <summary>
/// Registers and synchronizes BepInEx config entries and runtime custom values for one mod.
/// </summary>
/// <remarks>
/// Create one instance per mod, normally with the mod GUID as <see cref="Name"/>. The standalone
/// ConditionalConfigSync BepInEx plugin must be installed and declared as a hard dependency.
/// <example>
/// <code>
/// [BepInDependency("_shudnal.ConditionalConfigSync", BepInDependency.DependencyFlags.HardDependency)]
/// public sealed class Plugin : BaseUnityPlugin
/// {
///     private static readonly ConditionalConfigSync sync = new("author.mod")
///     {
///         DisplayName = "My Mod",
///         CurrentVersion = "1.2.0",
///         MinimumRequiredVersion = "1.2.0"
///     };
/// }
/// </code>
/// </example>
/// </remarks>
[Description("Registers and synchronizes config entries and runtime values for one mod.")]
public partial class ConditionalConfigSync
{
    /// <summary>
    /// Indicates that at least one incoming synchronization package is currently being applied.
    /// </summary>
    /// <remarks>
    /// This remains public for compatibility and diagnostics. Consumers should not build their own ServerSync-style
    /// queues around it; ConditionalConfigSync already defers outgoing changes safely while processing or sending.
    /// Domain-specific gates, such as waiting for texture-cache generation, may still be appropriate.
    /// </remarks>
    [Description("True while an incoming synchronization package is being applied. Usually diagnostic only.")]
    public static bool ProcessingServerUpdate = false;

    /// <summary>
    /// Stable synchronization identifier, normally the owning mod's BepInEx GUID.
    /// </summary>
    /// <remarks>It is used in RPC names and policy keys, so it must be unique and must not change between releases.</remarks>
    [Description("Stable unique synchronization identifier, normally the owning mod GUID.")]
    public readonly string Name;

    /// <summary>Human-readable mod name used in logs. Falls back to <see cref="Name"/> when omitted.</summary>
    [Description("Human-readable mod name used in logs.")]
    public string? DisplayName;

    /// <summary>Current version of the owning mod, for example <c>1.4.2</c>.</summary>
    [Description("Current version of the owning mod used for peer compatibility checks.")]
    public string? CurrentVersion;

    /// <summary>Oldest remote version accepted by this mod instance.</summary>
    /// <remarks>Set it independently from <see cref="CurrentVersion"/> when compatible mod releases span several versions.</remarks>
    [Description("Oldest compatible remote mod version accepted by this instance.")]
    public string? MinimumRequiredVersion;

    /// <summary>
    /// Gets or sets whether the owning mod must be installed and compatible on the remote peer.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="true"/> for mods that require matching code on both sides of the connection.
    /// On a client, the connected server must have a compatible copy of the mod. On a server, every connecting
    /// client must have a compatible copy. Missing or incompatible copies reject the connection during the normal
    /// peer version handshake.
    /// <para>
    /// Leave this <see langword="false"/> only when the mod can operate correctly while absent from the remote side,
    /// such as a genuinely client-only or otherwise optional integration. The local BepInEx hard dependency on
    /// Conditional Config Sync is separate: it requires CCS on the same machine as the owning mod, not automatically
    /// on the remote server or client.
    /// </para>
    /// <para>Set this before a connection is established, preferably in the object initializer.</para>
    /// </remarks>
    [Description("Whether the owning mod must be installed and compatible on the remote peer.")]
    public bool ModRequired { get; set; } = false;

    /// <summary>
    /// Allows an unlocked client to send config and custom-value changes to the server, matching ServerSync unlocked-client behavior.
    /// </summary>
    /// <remarks>Set to <see langword="false"/> for strictly server-originated synchronization even when the lock is disabled.</remarks>
    [Description("Allows unlocked clients to publish changes, matching ServerSync unlocked-client behavior.")]
    public bool AllowClientConfigUpdatesWhenUnlocked = true;

    private bool? forceConfigLocking;

    /// <summary>
    /// Gets whether non-admin clients are currently prevented from publishing server-controlled settings.
    /// </summary>
    /// <remarks>
    /// The getter combines the locking config entry, an optional programmatic override, and the local admin exemption.
    /// Assigning this property sets a programmatic override for the current process.
    /// </remarks>
    [Description("Whether non-admin clients are currently blocked from publishing server-controlled settings.")]
    public bool IsLocked
    {
        get => (forceConfigLocking ?? lockedConfig != null && ((IConvertible)lockedConfig.BaseConfig.BoxedValue).ToInt32(CultureInfo.InvariantCulture) != 0) && !lockExempt;
        set
        {
            bool oldValue = IsLocked;
            forceConfigLocking = value;
            if (oldValue != IsLocked)
            {
                ServerLockedSettingChanged();
            }
        }
    }

    /// <summary>Gets whether the local side is the source of truth or has received an admin exemption from the server.</summary>
    [Description("Whether the local side is the source of truth or has a server admin exemption.")]
    public bool IsAdmin => lockExempt || isSourceOfTruth;

    private bool isSourceOfTruth = true;

    /// <summary>
    /// Gets whether the local side currently owns and publishes synchronized values.
    /// </summary>
    /// <remarks>This is normally true on the server and in local worlds, and false on a connected client.</remarks>
    [Description("Whether the local side currently owns and publishes synchronized values.")]
    public bool IsSourceOfTruth
    {
        get => isSourceOfTruth;
        private set
        {
            if (value != isSourceOfTruth)
            {
                isSourceOfTruth = value;
                InvokeEventHandlers(SourceOfTruthChanged, value, nameof(SourceOfTruthChanged));
            }
        }
    }

    /// <summary>Gets whether the initial server synchronization has completed for this instance.</summary>
    [Description("Whether initial server synchronization has completed for this mod instance.")]
    public bool InitialSyncDone { get; private set; } = false;

    /// <summary>
    /// Raised when this process changes between source-of-truth and client-replica roles.
    /// </summary>
    /// <remarks>The argument is <see langword="true"/> when the local side becomes the source of truth.</remarks>
    [Description("Raised when this process changes between authoritative and client-replica roles.")]
    public event Action<bool>? SourceOfTruthChanged;

    /// <summary>
    /// Raised once when the first complete server synchronization package has been applied successfully.
    /// </summary>
    /// <remarks>
    /// Use this on clients when runtime systems must wait until all initial server-controlled config and custom values
    /// are available. It is not raised for later partial updates. A new server connection can raise it again after
    /// <see cref="ServerConnectionReset"/>.
    /// </remarks>
    [Description("Raised after the first complete server synchronization has been applied on a client.")]
    public event Action? InitialSyncCompleted;

    /// <summary>
    /// Raised after a server connection ends and server-owned values have been replaced with their saved local values.
    /// </summary>
    /// <remarks>
    /// Use this to discard caches derived from server settings or rebuild client-local state after disconnecting,
    /// returning to the menu, or shutting down the current network session.
    /// </remarks>
    [Description("Raised after server state is cleared and local fallback values are restored.")]
    public event Action? ServerConnectionReset;

    /// <summary>
    /// Raised when either the effective server-controlled state or hidden state changes for one config entry.
    /// </summary>
    /// <remarks>
    /// This is the combined policy event. It is useful for tools that need one notification for any policy transition.
    /// More focused consumers may subscribe to <see cref="ServerControlledChanged"/> or <see cref="HiddenStateChanged"/>.
    /// </remarks>
    [Description("Raised for any effective policy-state transition of a registered config entry.")]
    public event EventHandler<PolicyStateChangedEventArgs>? PolicyStateChanged;

    /// <summary>
    /// Raised when a config entry becomes server-controlled or client-controlled.
    /// </summary>
    /// <remarks>
    /// The first argument is the entry and the second is its new state. Use it when runtime behavior must switch
    /// between the received server value and the client's retained local fallback.
    /// </remarks>
    [Description("Raised when a config entry changes between server-controlled and client-controlled.")]
    public event Action<OwnConfigEntryBase, bool>? ServerControlledChanged;

    /// <summary>
    /// Raised when a config entry becomes hidden or visible through effective server policy.
    /// </summary>
    /// <remarks>
    /// The first argument is the entry and the second is its new hidden state. This can be used by custom config UIs
    /// that do not rely on Configuration Manager's Browsable metadata.
    /// </remarks>
    [Description("Raised when a config entry changes between hidden and visible.")]
    public event Action<OwnConfigEntryBase, bool>? HiddenStateChanged;

    /// <summary>
    /// Raised when the effective configuration lock is first evaluated and whenever it changes for this synchronization instance.
    /// </summary>
    /// <remarks>
    /// The argument is the new effective lock state after applying admin exemption and any programmatic override.
    /// The initial evaluation may therefore raise the event with <see langword="false"/>. Use it to refresh custom
    /// configuration UIs or explain why a client can no longer publish changes.
    /// </remarks>
    [Description("Raised when the effective configuration lock changes.")]
    public event Action<bool>? LockStateChanged;

    /// <summary>
    /// Raised when an incoming package or outgoing synchronization operation is rejected deliberately.
    /// </summary>
    /// <remarks>
    /// Useful for diagnostics and telemetry implemented by the owning mod. The library itself performs no external
    /// telemetry. The event is raised on the thread that detects the rejection; marshal to Unity's main thread before
    /// touching Unity objects. Do not retry blindly because permission and size rejections are persistent.
    /// </remarks>
    [Description("Raised when synchronization is rejected for permissions, safety limits, malformed data, or an exception.")]
    public event EventHandler<SyncRejectedEventArgs>? SyncRejected;

    private static readonly HashSet<ConditionalConfigSync> configSyncs = new();

    private readonly HashSet<OwnConfigEntryBase> allConfigs = new();

    private HashSet<CustomSyncedValueBase> allCustomValues = new();

    private static bool isServer;

    private static bool lockExempt = false;

    private OwnConfigEntryBase? lockedConfig = null;

    private bool? lastNotifiedLockState;

    /// <summary>Creates a synchronization instance for one mod.</summary>
    /// <param name="name">Stable unique identifier, normally the mod's BepInEx GUID.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the standalone ConditionalConfigSync plugin is not initialized or when an embedded copy is used.
    /// </exception>
    [Description("Creates one synchronization instance. Pass a stable mod GUID as the name.")]
    public ConditionalConfigSync(string name)
    {
        EnsureRuntimeReady();
        Name = name;
        configSyncs.Add(this);
        versionCheck = new VersionCheck(this);
        if (sessionActive)
        {
            QueueMainThread(RegisterForActiveSession);
        }
    }

    /// <summary>Registers a config as policy-controlled and server-controlled by default.</summary>
    /// <typeparam name="T">The config value type.</typeparam>
    /// <param name="configEntry">The entry returned by <c>Config.Bind</c>.</param>
    /// <returns>The existing or newly created typed synchronization wrapper.</returns>
    /// <remarks>Equivalent to <c>AddConfigEntry(configEntry, ConfigSyncMode.Conditional, true)</c>.</remarks>
    [Description("Registers a policy-controlled config that is server-controlled by default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry)
        => AddConfigEntry(configEntry, ConfigSyncMode.Conditional, serverControlledByDefault: true);

    /// <summary>Registers a policy-controlled config with an explicit default ownership.</summary>
    /// <param name="configEntry">The entry returned by <c>Config.Bind</c>.</param>
    /// <param name="synchronizedSetting"><see langword="true"/> for server-controlled by default; otherwise client-controlled.</param>
    [Description("Registers a Conditional config with an explicit server/client default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry, bool synchronizedSetting)
        => AddConfigEntry(configEntry, ConfigSyncMode.Conditional, synchronizedSetting);

    /// <summary>Registers a config with an explicit synchronization mode.</summary>
    /// <param name="configEntry">The entry returned by <c>Config.Bind</c>.</param>
    /// <param name="syncMode">Whether ownership is fixed or may be overridden by policy.</param>
    /// <remarks>
    /// <see cref="ConfigSyncMode.Conditional"/> is server-controlled by default in this overload. Choose
    /// <see cref="ConfigSyncMode.AlwaysServerControlled"/> for shared mechanics that require the same effective value
    /// on the server and every client.
    /// </remarks>
    [Description("Registers a config with an explicit synchronization mode.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry, ConfigSyncMode syncMode)
        => AddConfigEntry(configEntry, syncMode, serverControlledByDefault: true);

    /// <summary>Registers a config with a complete synchronization-mode definition.</summary>
    /// <param name="configEntry">The entry returned by <c>Config.Bind</c>.</param>
    /// <param name="syncMode">Whether ownership is fixed or may be overridden by policy.</param>
    /// <param name="serverControlledByDefault">Default ownership used only for <see cref="ConfigSyncMode.Conditional"/>.</param>
    /// <remarks>
    /// This overload configures the wrapper before registration, avoiding the temporary default state caused by assigning
    /// <see cref="OwnConfigEntryBase.SynchronizedConfig"/> after <c>AddConfigEntry</c> returns.
    /// </remarks>
    [Description("Registers a fully configured config without a temporary synchronization state.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry, ConfigSyncMode syncMode, bool serverControlledByDefault)
    {
        if (!Enum.IsDefined(typeof(ConfigSyncMode), syncMode))
        {
            throw new ArgumentOutOfRangeException(nameof(syncMode), syncMode, "Unknown config synchronization mode.");
        }

        bool normalizedDefault = syncMode switch
        {
            ConfigSyncMode.AlwaysServerControlled => true,
            ConfigSyncMode.AlwaysClientControlled => false,
            _ => serverControlledByDefault,
        };

        if (GetConfigData(configEntry) is SyncedConfigEntry<T> existingEntry)
        {
            return existingEntry;
        }

        ConfigDefinition definition = configEntry.Definition;
        if (allConfigs.Any(config => config.BaseConfig.Definition.Equals(definition)))
        {
            throw new InvalidOperationException($"Config entry '{definition.Section} -> {definition.Key}' is already registered in sync '{Name}'.");
        }

        SyncedConfigEntry<T> syncedEntry = new(configEntry)
        {
            SyncMode = syncMode,
            SynchronizedConfig = normalizedDefault,
        };

        AccessTools.DeclaredField(typeof(ConfigDescription), "<Tags>k__BackingField").SetValue(
            configEntry.Description,
            new object[] { new ConfigurationManagerAttributes() }
                .Concat(configEntry.Description.Tags ?? Array.Empty<object>())
                .Concat(new[] { syncedEntry })
                .ToArray());

        configEntry.SettingChanged += (_, _) => OnConfigEntryChanged(configEntry, syncedEntry);
        allConfigs.Add(syncedEntry);

        bool applyLoadedPolicy = IsSourceOfTruth && isServer && GameReflection.HasZNet && policySupportInitialized;
        syncedEntry.IsServerControlled = applyLoadedPolicy ? ComputeServerControlled(syncedEntry) : GetDefaultServerControlled(syncedEntry);
        syncedEntry.IsHidden = applyLoadedPolicy && ComputeHidden(syncedEntry);
        syncedEntry.PolicyStateInitialized = applyLoadedPolicy;

        ConfigurationManagerAttributes attributes = GetConfigAttribute<ConfigurationManagerAttributes>(configEntry);
        attributes.ReadOnly = !IsWritableConfig(syncedEntry);
        attributes.Browsable = !syncedEntry.IsHidden;

        DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Register",
            $"Added config {definition.Section}/{definition.Key}, type={configEntry.SettingType.Name}, mode={syncMode}, default={(normalizedDefault ? "ServerControlled" : "ClientControlled")}");
        ScheduleLateRegistrationSync(config: syncedEntry);
        return syncedEntry;
    }

    /// <summary>Binds and registers a Conditional config in one call using a text description and explicit default ownership.</summary>
    [Description("Binds and registers a Conditional config with an explicit server/client default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        string section,
        string key,
        T defaultValue,
        string description,
        bool synchronizedSetting)
        => AddConfigEntry(configFile.Bind(section, key, defaultValue, description), ConfigSyncMode.Conditional, synchronizedSetting);

    /// <summary>Binds and registers a config entry in one call using a text description.</summary>
    [Description("Binds and registers a config in one call using a text description.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        string section,
        string key,
        T defaultValue,
        string description,
        ConfigSyncMode syncMode = ConfigSyncMode.Conditional,
        bool serverControlledByDefault = true)
        => AddConfigEntry(configFile.Bind(section, key, defaultValue, description), syncMode, serverControlledByDefault);

    /// <summary>Binds and registers a Conditional config in one call using a full BepInEx description and explicit default ownership.</summary>
    [Description("Binds and registers a Conditional ConfigDescription entry with an explicit server/client default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        string section,
        string key,
        T defaultValue,
        ConfigDescription description,
        bool synchronizedSetting)
        => AddConfigEntry(configFile.Bind(section, key, defaultValue, description), ConfigSyncMode.Conditional, synchronizedSetting);

    /// <summary>Binds and registers a config entry in one call using a full BepInEx description.</summary>
    [Description("Binds and registers a config in one call using a ConfigDescription.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        string section,
        string key,
        T defaultValue,
        ConfigDescription description,
        ConfigSyncMode syncMode = ConfigSyncMode.Conditional,
        bool serverControlledByDefault = true)
        => AddConfigEntry(configFile.Bind(section, key, defaultValue, description), syncMode, serverControlledByDefault);

    /// <summary>Binds and registers a Conditional config in one call using a <see cref="ConfigDefinition"/>, text description, and explicit default ownership.</summary>
    [Description("Binds and registers a Conditional ConfigDefinition entry with a text description and explicit default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        ConfigDefinition definition,
        T defaultValue,
        string description,
        bool synchronizedSetting)
        => AddConfigEntry(configFile.Bind(definition.Section, definition.Key, defaultValue, description), ConfigSyncMode.Conditional, synchronizedSetting);

    /// <summary>Binds and registers a config entry in one call using a <see cref="ConfigDefinition"/> and text description.</summary>
    [Description("Binds and registers a ConfigDefinition entry with a text description.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        ConfigDefinition definition,
        T defaultValue,
        string description,
        ConfigSyncMode syncMode = ConfigSyncMode.Conditional,
        bool serverControlledByDefault = true)
        => AddConfigEntry(configFile.Bind(definition.Section, definition.Key, defaultValue, description), syncMode, serverControlledByDefault);

    /// <summary>Binds and registers a Conditional config in one call using a <see cref="ConfigDefinition"/> and explicit default ownership.</summary>
    [Description("Binds and registers a Conditional ConfigDefinition entry with an explicit server/client default.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        ConfigDefinition definition,
        T defaultValue,
        ConfigDescription description,
        bool synchronizedSetting)
        => AddConfigEntry(configFile.Bind(definition.Section, definition.Key, defaultValue, description), ConfigSyncMode.Conditional, synchronizedSetting);

    /// <summary>Binds and registers a config entry in one call using a <see cref="ConfigDefinition"/>.</summary>
    [Description("Binds and registers a config in one call using a ConfigDefinition.")]
    public SyncedConfigEntry<T> AddConfigEntry<T>(
        ConfigFile configFile,
        ConfigDefinition definition,
        T defaultValue,
        ConfigDescription description,
        ConfigSyncMode syncMode = ConfigSyncMode.Conditional,
        bool serverControlledByDefault = true)
        => AddConfigEntry(configFile.Bind(definition.Section, definition.Key, defaultValue, description), syncMode, serverControlledByDefault);

    /// <summary>Registers the single protected config entry that controls whether clients may edit synchronized settings.</summary>
    /// <typeparam name="T">A convertible value type, commonly <see cref="bool"/> or an integer.</typeparam>
    /// <param name="lockingConfig">The BepInEx entry used as the lock switch. Zero/false means unlocked.</param>
    /// <returns>The synchronization wrapper for the locking entry.</returns>
    /// <exception cref="Exception">Thrown when a second locking entry is registered for the same instance.</exception>
    /// <remarks>
    /// The locking entry is always server-controlled. A <c>ForceClientControlled</c> policy override is ignored,
    /// and the server rejects attempts by non-admin clients to change this entry even while the rest of the
    /// configuration is unlocked. This prevents a client from granting itself permission to edit server settings.
    /// </remarks>
    /// <example>
    /// <code>
    /// var lockEntry = Config.Bind("General", "Lock Configuration", true, "Server controls settings");
    /// configSync.AddLockingConfigEntry(lockEntry);
    /// </code>
    /// </example>
    [Description("Registers the protected server-controlled lock entry. Non-admin clients can never change it.")]
    public SyncedConfigEntry<T> AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible
    {
        if (lockedConfig != null)
        {
            throw new Exception("Cannot initialize locking ConfigEntry twice");
        }

        lockedConfig = AddConfigEntry(lockingConfig, ConfigSyncMode.AlwaysServerControlled);
        lockedConfig.IsServerControlled = true;
        lockedConfig.PolicyStateInitialized = false;
        lockingConfig.SettingChanged += (_, _) => LockedConfigChanged?.Invoke();
        LockedConfigChanged -= ServerLockedSettingChanged;
        LockedConfigChanged += ServerLockedSettingChanged;
        UpdateInvalidLockingPolicyWarning("locking config registration");

        return (SyncedConfigEntry<T>)lockedConfig;
    }

    internal void AddCustomValue(CustomSyncedValueBase customValue)
    {
        if (allCustomValues.Select(v => v.Identifier).Concat(new[] { "serverversion", "lockexempt" }).Contains(customValue.Identifier))
        {
            throw new Exception("Cannot have multiple settings with the same name or with a reserved name (serverversion, lockexempt)");
        }

        allCustomValues.Add(customValue);
        allCustomValues = new HashSet<CustomSyncedValueBase>(allCustomValues.OrderByDescending(v => v.Priority).ThenBy(v => v.RegistrationIndex));
        customValue.ValueChanged += () => OnCustomValueChanged(customValue);
        DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Register", $"Added custom value {customValue.Identifier}, type={customValue.Type.Name}, priority={customValue.Priority}, sequenced={customValue.PreserveUpdateSequence}");
        ScheduleLateRegistrationSync(customValue: customValue);
    }
}
