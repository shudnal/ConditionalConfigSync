using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private event Action? LockedConfigChanged;

    internal static bool IsWritableConfig(OwnConfigEntryBase config)
    {
        if (configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config)) is not { } configSync)
        {
            return true;
        }

        if (configSync.IsSourceOfTruth || !config.IsServerControlled)
        {
            return true;
        }

        // A connected client must fail closed until the complete server state, including lock exemption,
        // ownership policy, and authoritative values, has been received.
        if (!configSync.InitialSyncDone)
        {
            return false;
        }

        if (config == configSync.lockedConfig)
        {
            return configSync.IsAdmin;
        }

        if (configSync.IsAdmin)
        {
            return true;
        }

        if (configSync.ServerLockEnabled)
        {
            return false;
        }

        return configSync.AllowClientConfigUpdatesWhenUnlocked;
    }

    internal static bool ShouldStoreLocalConfigValue(OwnConfigEntryBase config)
    {
        // An old fallback may still exist while a policy/reset callback is running. Only current
        // ownership decides whether AssignLocalValue targets the fallback or the active local value.
        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        return owner != null && !owner.IsSourceOfTruth && config.IsServerControlled;
    }

    private string GetWriteRejectionReason(OwnConfigEntryBase config)
    {
        if (!InitialSyncDone)
        {
            return "initial server synchronization is not complete";
        }

        if (config == lockedConfig)
        {
            return "the protected locking config requires administrator access";
        }

        if (ServerLockEnabled)
        {
            return "the server configuration is locked";
        }

        if (!AllowClientConfigUpdatesWhenUnlocked)
        {
            return "unlocked client updates are disabled by the mod";
        }

        return "the local side is not authorized to change this server-controlled config";
    }

    private void ServerLockedSettingChanged()
    {
        DebugLog(
            ConditionalConfigSyncDebugLevel.Verbose,
            "Lock",
            $"Re-evaluating read-only state: serverLock={ServerLockEnabled}, effectiveLock={IsLocked}, admin={IsAdmin}, sourceOfTruth={IsSourceOfTruth}, initialSync={InitialSyncDone}, unlockedClientUpdates={AllowClientConfigUpdatesWhenUnlocked}");

        foreach (OwnConfigEntryBase configEntryBase in allConfigs)
        {
            ConfigurationManagerAttributes attributes = GetConfigAttribute<ConfigurationManagerAttributes>(configEntryBase.BaseConfig);
            attributes.ReadOnly = !IsWritableConfig(configEntryBase);
            attributes.Browsable = !configEntryBase.IsHidden;
        }

        // A lock subscriber may inspect the configuration manager or register another setting.
        // Publish the event only after every existing entry has its final metadata.
        RaiseLockStateChangedIfNeeded();
    }

    private void RestoreRejectedConfigChange(ConfigEntryBase configEntry, OwnConfigEntryBase syncedEntry, string reason)
    {
        object? authoritativeValue = syncedEntry.HasServerValue
            ? syncedEntry.ServerValue
            : syncedEntry.HasLastAcceptedValue
                ? syncedEntry.LastAcceptedValue
                : syncedEntry.HasLocalBaseValue
                    ? syncedEntry.LocalBaseValue
                    : configEntry.DefaultValue;

        ConfigFile configFile = configEntry.ConfigFile;
        bool saveOnConfigSet = configFile.SaveOnConfigSet;
        configFile.SaveOnConfigSet = false;
        configsBeingApplied.Add(configEntry);
        try
        {
            configEntry.BoxedValue = authoritativeValue;
            syncedEntry.StoreLastAcceptedValue(configEntry.BoxedValue);
        }
        catch (Exception e)
        {
            ConfigDefinition definition = configEntry.Definition;
            DebugWarning(
                "ConfigUpdate",
                $"Failed to restore protected config {definition.Section} -> {definition.Key} after a rejected local change. Error: {e}");
            return;
        }
        finally
        {
            configsBeingApplied.Remove(configEntry);
            configFile.SaveOnConfigSet = saveOnConfigSet;
        }

        try
        {
            // GetSerializedValue is patched to persist the client's local fallback rather than the active server value.
            configFile.Save();
        }
        catch (Exception e)
        {
            DebugWarning("ConfigUpdate", $"Failed to save local fallback after restoring {configEntry.Definition}: {e.Message}");
        }

        DebugWarning(
            "ConfigUpdate",
            $"Restored protected config {configEntry.Definition.Section} -> {configEntry.Definition.Key} after a rejected local change: {reason}");
    }

    private void ResetConfigsFromServer(ParsedConfigs? retainedSnapshot = null)
    {
        remotePolicyChangeSupported = false;
        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Reset", "Restoring local values after server sync/session end");
        Dictionary<ConfigFile, bool> saveOnConfigSet = new();
        List<PolicyStateChangedEventArgs> policyTransitions = new();

        // Value callbacks must already see local ownership, but SourceOfTruthChanged must still run
        // after restoration. Both shutdown and optional-server fallback use this reset path.
        bool restoredLocalOwnership = retainedSnapshot == null && !isSourceOfTruth;
        if (restoredLocalOwnership)
        {
            isSourceOfTruth = true;
            foreach (CustomSyncedValueBase customValue in allCustomValues)
            {
                customValue.SetLocalOwnership(true);
            }
        }

        // During a full resync, entries present in the new snapshot keep their active values and
        // local fallbacks until ApplyParsedConfigs applies the replacement. Only absent entries reset.
        // Snapshot the registration sets before invoking consumer callbacks, which may add entries.
        OwnConfigEntryBase[] resetConfigs = allConfigs.Where(config => retainedSnapshot == null
            || (!retainedSnapshot.configValues.ContainsKey(config) && !retainedSnapshot.configStates.ContainsKey(config))).ToArray();
        CustomSyncedValueBase[] resetCustomValues = allCustomValues.Where(config => retainedSnapshot == null
            || !retainedSnapshot.customValues.ContainsKey(config)).ToArray();

        foreach (OwnConfigEntryBase config in resetConfigs)
        {
            bool oldServerControlled = config.IsServerControlled;
            bool oldHidden = config.IsHidden;
            bool newServerControlled = GetDefaultServerControlled(config);
            config.ClearServerValue();
            config.IsServerControlled = newServerControlled;
            config.IsHidden = false;
            config.IsPolicyStateInitialized = false;
            if (oldServerControlled != newServerControlled || oldHidden)
            {
                policyTransitions.Add(new PolicyStateChangedEventArgs(
                    config,
                    oldServerControlled,
                    newServerControlled,
                    oldHidden,
                    false,
                    "server connection reset"));
            }
        }

        try
        {
            foreach (OwnConfigEntryBase config in resetConfigs.Where(config => config.HasLocalBaseValue))
            {
                ConfigFile configFile = config.BaseConfig.ConfigFile;
                if (!saveOnConfigSet.ContainsKey(configFile))
                {
                    saveOnConfigSet[configFile] = configFile.SaveOnConfigSet;
                    configFile.SaveOnConfigSet = false;
                }

                // Detach only the fallback being restored. A callback may establish a new one.
                object? localValue = config.LocalBaseValue;
                config.ClearLocalBaseValue();
                configsBeingApplied.Add(config.BaseConfig);
                try
                {
                    config.BaseConfig.BoxedValue = localValue;
                    config.StoreLastAcceptedValue(config.BaseConfig.BoxedValue);
                }
                catch (Exception e)
                {
                    if (!config.HasLocalBaseValue)
                    {
                        config.StoreLocalBaseValue(localValue);
                    }
                    ConfigDefinition definition = config.BaseConfig.Definition;
                    DebugWarning("Reset", $"Failed to restore local config {definition.Section} -> {definition.Key}; continuing. Error: {e}");
                }
                finally
                {
                    configsBeingApplied.Remove(config.BaseConfig);
                }
            }
        }
        finally
        {
            foreach (KeyValuePair<ConfigFile, bool> kv in saveOnConfigSet)
            {
                kv.Key.SaveOnConfigSet = kv.Value;
            }
        }

        foreach (OwnConfigEntryBase config in resetConfigs)
        {
            config.StoreLastAcceptedValue(config.BaseConfig.BoxedValue);
        }

        foreach (CustomSyncedValueBase config in resetCustomValues.Where(config => config.HasLocalBaseValue))
        {
            object? localValue = config.LocalBaseValue;
            config.ClearLocalBaseValue();
            customValuesBeingApplied.Add(config);
            try
            {
                config.BoxedValue = localValue;
                config.StoreLastAcceptedValue(config.BoxedValue);
            }
            catch (Exception e)
            {
                if (!config.HasLocalBaseValue)
                {
                    config.StoreLocalBaseValue(localValue);
                }
                DebugWarning("Reset", $"Failed to restore local custom value '{config.Identifier}'; continuing. Error: {e}");
            }
            finally
            {
                customValuesBeingApplied.Remove(config);
            }
        }

        ServerLockedSettingChanged();
        foreach (PolicyStateChangedEventArgs transition in policyTransitions)
        {
            RaisePolicyStateEvents(transition);
        }
        if (restoredLocalOwnership)
        {
            InvokeEventHandlers(SourceOfTruthChanged, true, nameof(SourceOfTruthChanged));
        }
    }

    private static OwnConfigEntryBase? GetConfigData(ConfigEntryBase config)
    {
        return config.Description.Tags?.OfType<OwnConfigEntryBase>()
            .SingleOrDefault(entry => ReferenceEquals(entry.BaseConfig, config));
    }

    /// <summary>Returns the synchronization wrapper attached to a registered BepInEx config entry.</summary>
    /// <typeparam name="T">The config value type.</typeparam>
    /// <param name="config">The BepInEx config entry to inspect.</param>
    /// <returns>The wrapper, or <see langword="null"/> when the entry was not registered.</returns>
    [Description("Returns the synchronization wrapper attached to a registered BepInEx config entry.")]
    public static SyncedConfigEntry<T>? ConfigData<T>(ConfigEntry<T> config)
    {
        return GetConfigData(config) as SyncedConfigEntry<T>;
    }

    private static T GetConfigAttribute<T>(ConfigEntryBase config)
    {
        return config.Description.Tags.OfType<T>().First();
    }

    internal static class ConfigEntryOnSettingChangedPatch
    {
        internal static bool Prefix(ConfigEntryBase __instance)
        {
            if (GetConfigData(__instance) is not { } data
                || GetOwningConfigSync(data) is not { } owner
                || owner.configsBeingApplied.Contains(__instance)
                || IsWritableConfig(data))
            {
                return true;
            }

            owner.RestoreRejectedConfigChange(__instance, data, owner.GetWriteRejectionReason(data));
            return false;
        }
    }

    internal static class ConfigEntryGetSerializedValuePatch
    {
        internal static bool Prefix(ConfigEntryBase __instance, ref string __result)
        {
            // Edit permission does not transfer ownership of a server value to an administrator's
            // local cfg file. Persist an existing local fallback for every server-controlled replica.
            if (GetConfigData(__instance) is not { } data
                || IsWritableConfig(data) && !(data.HasLocalBaseValue && ShouldStoreLocalConfigValue(data)))
            {
                return true;
            }

            object? valueToPersist = data.HasLocalBaseValue
                ? data.LocalBaseValue
                : data.HasLastAcceptedValue
                    ? data.LastAcceptedValue
                    : __instance.BoxedValue;
            __result = TomlTypeConverter.ConvertToString(valueToPersist, __instance.SettingType);
            return false;
        }
    }

    internal static class ConfigEntrySetSerializedValuePatch
    {
        internal static bool Prefix(ConfigEntryBase __instance, string value)
        {
            if (GetConfigData(__instance) is not { } data || IsWritableConfig(data))
            {
                return true;
            }

            try
            {
                data.StoreLocalBaseValue(TomlTypeConverter.ConvertToValue(value, __instance.SettingType));
            }
            catch (Exception e)
            {
                LogSource.LogWarning($"[ConfigFile] Config value of setting \"{__instance.Definition}\" could not be parsed and will be ignored. Reason: {e.Message}");
            }
            return false;
        }
    }
}
