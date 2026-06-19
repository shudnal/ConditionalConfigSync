using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private event Action? LockedConfigChanged;

    private static bool IsWritableConfig(OwnConfigEntryBase config)
    {
        if (configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config)) is not { } configSync)
        {
            return true;
        }

        return configSync.IsSourceOfTruth || !config.IsServerControlled || !config.HasLocalBaseValue || (!configSync.IsLocked && (config != configSync.lockedConfig || lockExempt));
    }

    private void ServerLockedSettingChanged()
    {
        RaiseLockStateChangedIfNeeded();
        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Lock", $"Re-evaluating read-only state: locked={IsLocked}, admin={IsAdmin}, sourceOfTruth={IsSourceOfTruth}");
        foreach (OwnConfigEntryBase configEntryBase in allConfigs)
        {
            ConfigurationManagerAttributes attributes = GetConfigAttribute<ConfigurationManagerAttributes>(configEntryBase.BaseConfig);
            attributes.ReadOnly = !IsWritableConfig(configEntryBase);
            attributes.Browsable = !configEntryBase.IsHidden;
        }
    }

    private void ResetConfigsFromServer()
    {
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
            config.PolicyStateInitialized = false;
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

    internal static class ConfigEntryGetSerializedValuePatch
    {
        internal static bool Prefix(ConfigEntryBase __instance, ref string __result)
        {
            if (GetConfigData(__instance) is not { } data || IsWritableConfig(data))
            {
                return true;
            }

            __result = TomlTypeConverter.ConvertToString(data.LocalBaseValue, __instance.SettingType);
            return false;
        }
    }

    internal static class ConfigEntrySetSerializedValuePatch
    {
        internal static bool Prefix(ConfigEntryBase __instance, string value)
        {
            if (GetConfigData(__instance) is not { } data || !data.HasLocalBaseValue)
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
