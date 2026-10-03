using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

/// <summary>
/// Reflection-friendly bridge for mods that treat Conditional Config Sync as an optional BepInEx dependency.
/// </summary>
/// <remarks>
/// Consumer mods should not reference this type at compile time. Use the source-only helper shipped with this
/// repository, which resolves this bridge through the already active Conditional Config Sync BepInEx plugin.
/// </remarks>
public static class SoftDependencyBridge
{
    /// <summary>Version of the reflection bridge contract.</summary>
    public static int ApiVersion => 1;

    private static readonly MethodInfo AddConfigEntryDefinition =
        typeof(global::ConditionalConfigSync.ConditionalConfigSync)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
            {
                if (method.Name != nameof(global::ConditionalConfigSync.ConditionalConfigSync.AddConfigEntry)
                    || !method.IsGenericMethodDefinition)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType.IsGenericType
                    && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(ConfigEntry<>)
                    && parameters[1].ParameterType == typeof(ConfigSyncMode)
                    && parameters[2].ParameterType == typeof(bool);
            });

    private static readonly MethodInfo AddLockingConfigEntryDefinition =
        typeof(global::ConditionalConfigSync.ConditionalConfigSync)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
            {
                if (method.Name != nameof(global::ConditionalConfigSync.ConditionalConfigSync.AddLockingConfigEntry)
                    || !method.IsGenericMethodDefinition)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType.IsGenericType
                    && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(ConfigEntry<>);
            });

    private static readonly object MethodCacheLock = new();
    private static readonly Dictionary<Type, MethodInfo> AddConfigEntryMethods = new();
    private static readonly Dictionary<Type, MethodInfo> AddLockingConfigEntryMethods = new();

    /// <summary>
    /// Gets whether the standalone CCS core referenced by the active bootstrap is initialized and owns the shared runtime.
    /// </summary>
    public static bool IsRuntimeReady
    {
        get
        {
            try
            {
                global::ConditionalConfigSync.ConditionalConfigSync.EnsureRuntimeReady();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Creates one CCS consumer instance. This method is intended for the source-only soft dependency helper.
    /// </summary>
    public static object CreateConfigSync(
        string name,
        string displayName,
        string currentVersion,
        string minimumRequiredVersion,
        bool modRequired,
        string modRequirementMode,
        bool allowClientConfigUpdatesWhenUnlocked)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A stable consumer GUID is required.", nameof(name));
        }

        global::ConditionalConfigSync.ConditionalConfigSync.EnsureRuntimeReady();

        if (!Enum.TryParse(modRequirementMode, ignoreCase: false, out ModRequirementMode parsedRequirementMode)
            || !Enum.IsDefined(typeof(ModRequirementMode), parsedRequirementMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(modRequirementMode),
                modRequirementMode,
                "Unknown Conditional Config Sync mod requirement mode.");
        }

        ConfigSync sync = new(name)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
            CurrentVersion = currentVersion,
            MinimumRequiredVersion = minimumRequiredVersion,
            ModRequired = modRequired,
            ModRequirementMode = parsedRequirementMode,
            AllowClientConfigUpdatesWhenUnlocked = allowClientConfigUpdatesWhenUnlocked,
        };

        return sync;
    }

    /// <summary>
    /// Registers an existing BepInEx config entry with one soft-dependency consumer instance.
    /// </summary>
    public static void RegisterConfigEntry(
        object configSyncInstance,
        ConfigEntryBase configEntry,
        string syncMode,
        bool serverControlledByDefault)
    {
        global::ConditionalConfigSync.ConditionalConfigSync.EnsureRuntimeReady();

        if (configSyncInstance is not global::ConditionalConfigSync.ConditionalConfigSync sync)
        {
            throw new ArgumentException(
                "The supplied context was not created by Conditional Config Sync.",
                nameof(configSyncInstance));
        }

        if (configEntry == null)
        {
            throw new ArgumentNullException(nameof(configEntry));
        }

        if (!Enum.TryParse(syncMode, ignoreCase: false, out ConfigSyncMode parsedSyncMode)
            || !Enum.IsDefined(typeof(ConfigSyncMode), parsedSyncMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(syncMode),
                syncMode,
                "Unknown Conditional Config Sync config mode.");
        }

        MethodInfo method = GetClosedMethod(AddConfigEntryDefinition, AddConfigEntryMethods, configEntry.SettingType);
        Invoke(method, sync, new object[] { configEntry, parsedSyncMode, serverControlledByDefault });
    }

    /// <summary>
    /// Registers an existing BepInEx config entry as the protected CCS locking entry.
    /// </summary>
    public static void RegisterLockingConfigEntry(object configSyncInstance, ConfigEntryBase configEntry)
    {
        global::ConditionalConfigSync.ConditionalConfigSync.EnsureRuntimeReady();

        if (configSyncInstance is not global::ConditionalConfigSync.ConditionalConfigSync sync)
        {
            throw new ArgumentException(
                "The supplied context was not created by Conditional Config Sync.",
                nameof(configSyncInstance));
        }

        if (configEntry == null)
        {
            throw new ArgumentNullException(nameof(configEntry));
        }

        Type settingType = configEntry.SettingType;
        if (!typeof(IConvertible).IsAssignableFrom(settingType))
        {
            throw new ArgumentException(
                $"Locking config type '{settingType.FullName}' must implement IConvertible.",
                nameof(configEntry));
        }

        MethodInfo method = GetClosedMethod(
            AddLockingConfigEntryDefinition,
            AddLockingConfigEntryMethods,
            settingType);
        Invoke(method, sync, new object[] { configEntry });
    }

    private static MethodInfo GetClosedMethod(
        MethodInfo definition,
        Dictionary<Type, MethodInfo> cache,
        Type settingType)
    {
        lock (MethodCacheLock)
        {
            if (!cache.TryGetValue(settingType, out MethodInfo method))
            {
                method = definition.MakeGenericMethod(settingType);
                cache[settingType] = method;
            }

            return method;
        }
    }

    private static void Invoke(MethodInfo method, object target, object[] arguments)
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
