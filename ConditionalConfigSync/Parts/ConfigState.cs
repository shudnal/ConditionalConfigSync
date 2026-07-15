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
        RaiseLockStateChangedIfNeeded();
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

    private void ResetConfigsFromServer()
    {
        remotePolicyChangeSupported = false;
        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Reset", "Restoring local values after server sync/session end");
        Dictionary<ConfigFile, bool> saveOnConfigSet = new();
        List<PolicyStateChangedEventArgs> policyTransitions = new();
        try
        {
            foreach (OwnConfigEntryBase config in allConfigs.Where(config => config.HasLocalBaseValue))
            {
                ConfigFile configFile = config.BaseConfig.ConfigFile;
                if (!saveOnConfigSet.ContainsKey(configFile))
                {
                    saveOnConfigSet[configFile] = configFile.SaveOnConfigSet;
                    configFile.SaveOnConfigSet = false;
                }

                configsBeingApplied.Add(config.BaseConfig);
                try
                {
                    config.BaseConfig.BoxedValue = config.LocalBaseValue;
                    config.StoreLastAcceptedValue(config.BaseConfig.BoxedValue);
                    config.ClearLocalBaseValue();
                    config.ClearServerValue();
                }
                catch (Exception e)
                {
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

        foreach (OwnConfigEntryBase config in allConfigs)
        {
            bool oldServerControlled = config.IsServerControlled;
            bool oldHidden = config.IsHidden;
            bool newServerControlled = GetDefaultServerControlled(config);
            config.ClearServerValue();
            config.IsServerControlled = newServerControlled;
            config.IsHidden = false;
            config.IsPolicyStateInitialized = false;
            config.StoreLastAcceptedValue(config.BaseConfig.BoxedValue);
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

        foreach (CustomSyncedValueBase config in allCustomValues.Where(config => config.HasLocalBaseValue))
        {
            customValuesBeingApplied.Add(config);
            try
            {
                config.BoxedValue = config.LocalBaseValue;
                config.StoreLastAcceptedValue(config.BoxedValue);
                config.ClearLocalBaseValue();
            }
            catch (Exception e)
            {
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
    }

    private static OwnConfigEntryBase? GetConfigData(ConfigEntryBase config)
    {
        return config.Description.Tags?.OfType<OwnConfigEntryBase>().SingleOrDefault();
    }

    /// <summary>Returns the synchronization wrapper attached to a registered BepInEx config entry.</summary>
    /// <typeparam name="T">The config value type.</typeparam>
    /// <param name="config">The BepInEx config entry to inspect.</param>
    /// <returns>The wrapper, or <see langword="null"/> when the entry was not registered.</returns>
    [Description("Returns the synchronization wrapper attached to a registered BepInEx config entry.")]
    public static SyncedConfigEntry<T>? ConfigData<T>(ConfigEntry<T> config)
    {
        return config.Description.Tags?.OfType<SyncedConfigEntry<T>>().SingleOrDefault();
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
            if (GetConfigData(__instance) is not { } data || IsWritableConfig(data))
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
