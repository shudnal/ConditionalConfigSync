using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    /// <summary>Controls the amount of diagnostic information written by ConditionalConfigSync.</summary>
    [Description("Diagnostic logging verbosity for ConditionalConfigSync.")]
    public enum ConditionalConfigSyncDebugLevel
    {
        /// <summary>Lifecycle, registration, initial sync, and important queue activity.</summary>
        Basic = 1,
        /// <summary>Adds package preparation, compression, policy application, and value-flow details.</summary>
        Verbose = 2,
        /// <summary>Adds per-value and per-fragment tracing. Intended for short diagnostic sessions.</summary>
        Trace = 3,
    }

    /// <summary>Enables runtime debug logging programmatically for the shared standalone plugin.</summary>
    [Description("Enables shared ConditionalConfigSync diagnostic logging programmatically.")] public static bool DebugLoggingEnabled = false;

    /// <summary>Selects the runtime debug verbosity used when <see cref="DebugLoggingEnabled"/> is enabled.</summary>
    [Description("Selects diagnostic logging verbosity.")] public static ConditionalConfigSyncDebugLevel DebugLoggingLevel = ConditionalConfigSyncDebugLevel.Basic;

    /// <summary>
    /// Optional case-insensitive filter matched against a synchronization name or display name. Empty means all mods.
    /// </summary>
    [Description("Optional case-insensitive filter matched against mod sync names and display names.")] public static string DebugLoggingFilter = "";

    private static bool debugConfigEnabled;

    private static ConditionalConfigSyncDebugLevel debugConfigLevel = ConditionalConfigSyncDebugLevel.Basic;

    private static string debugConfigFilter = "";

    private static bool debugSupportInitialized;

    private static bool debugConfigReloadScheduled;

    private static bool debugCommandsRegistered;

    private static FileSystemWatcher? debugConfigWatcher;

    private static readonly object debugSupportLock = new();

    private static void EnsureDebugSupportInitialized()
    {
        lock (debugSupportLock)
        {
            if (debugSupportInitialized)
            {
                return;
            }

            debugSupportInitialized = true;
            LoadDebugConfig(createIfMissing: true, quiet: true);
            StartDebugConfigWatcher();
        }
    }

    private static void StartDebugConfigWatcher()
    {
        try
        {
            EnsureConfigDirectory();
            debugConfigWatcher = new FileSystemWatcher(ConfigDirectoryPath, DebugConfigFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            debugConfigWatcher.Changed += (_, _) => ScheduleDebugConfigReload();
            debugConfigWatcher.Created += (_, _) => ScheduleDebugConfigReload();
            debugConfigWatcher.Renamed += (_, _) => ScheduleDebugConfigReload();
            debugConfigWatcher.Deleted += (_, _) => ScheduleDebugConfigReload();
        }
        catch (Exception e)
        {
            LogSource.LogWarning($"[DebugConfig] Could not start watcher for {DebugConfigFileName}: {e.Message}");
        }
    }

    private static void ScheduleDebugConfigReload()
    {
        lock (debugSupportLock)
        {
            if (debugConfigReloadScheduled)
            {
                return;
            }
            debugConfigReloadScheduled = true;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(500);
            lock (debugSupportLock)
            {
                debugConfigReloadScheduled = false;
            }
            LoadDebugConfig(createIfMissing: false, quiet: false);
        });
    }

    private static void LoadDebugConfig(bool createIfMissing, bool quiet)
    {
        try
        {
            EnsureConfigDirectory();
            if (!File.Exists(DebugConfigPath))
            {
                if (createIfMissing)
                {
                    File.WriteAllText(DebugConfigPath,
                        "[Debug]\n" +
                        "# Local debug logging for ConditionalConfigSync. This file is not synchronized.\n" +
                        "Enabled = false\n" +
                        "Level = Basic\n" +
                        "Filter = \n");
                }
                else
                {
                    debugConfigEnabled = false;
                    debugConfigLevel = ConditionalConfigSyncDebugLevel.Basic;
                    debugConfigFilter = "";
                    if (!quiet && DebugLoggingEnabled)
                    {
                        LogSource.LogInfo($"[DebugConfig] {DebugConfigFileName} was deleted; file-based debug settings were reset.");
                    }
                }
                return;
            }

            Dictionary<string, string> values = ReadSimpleDebugConfig(DebugConfigPath);
            bool enabled = ReadBool(values, "Enabled", false);
            ConditionalConfigSyncDebugLevel level = ReadDebugLevel(values.TryGetValue("Level", out string? rawLevel) ? rawLevel : null, ConditionalConfigSyncDebugLevel.Basic);
            string filter = values.TryGetValue("Filter", out string? rawFilter) ? rawFilter.Trim() : "";

            debugConfigEnabled = enabled;
            debugConfigLevel = level;
            debugConfigFilter = filter;

            if (!quiet && (enabled || DebugLoggingEnabled))
            {
                LogSource.LogInfo($"[DebugConfig] Reloaded {DebugConfigFileName}: enabled={enabled}, level={level}, filter='{filter}'");
            }
        }
        catch (Exception e)
        {
            LogSource.LogWarning($"[DebugConfig] Failed to read {DebugConfigFileName}: {e.Message}");
        }
    }

    private static Dictionary<string, string> ReadSimpleDebugConfig(string path)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string rawLine in ReadAllLinesStable(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
            {
                continue;
            }
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }
            if (!section.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }
            values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
        }
        return values;
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? raw))
        {
            return fallback;
        }
        if (bool.TryParse(raw, out bool value))
        {
            return value;
        }
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
        {
            return intValue != 0;
        }
        return fallback;
    }

    private static ConditionalConfigSyncDebugLevel ReadDebugLevel(string? raw, ConditionalConfigSyncDebugLevel fallback)
    {
        return Enum.TryParse(raw, ignoreCase: true, out ConditionalConfigSyncDebugLevel level) ? level : fallback;
    }

    private static bool IsDebugActive => DebugLoggingEnabled || debugConfigEnabled;

    private static ConditionalConfigSyncDebugLevel EffectiveDebugLevel
    {
        get
        {
            int level = 0;
            if (DebugLoggingEnabled)
            {
                level = Math.Max(level, (int)DebugLoggingLevel);
            }
            if (debugConfigEnabled)
            {
                level = Math.Max(level, (int)debugConfigLevel);
            }
            return level <= 0 ? ConditionalConfigSyncDebugLevel.Basic : (ConditionalConfigSyncDebugLevel)level;
        }
    }

    private static string EffectiveDebugFilter => !string.IsNullOrWhiteSpace(DebugLoggingFilter) ? DebugLoggingFilter : debugConfigFilter;

    private bool ShouldDebugLog(ConditionalConfigSyncDebugLevel level)
    {
        if (!IsDebugActive || level > EffectiveDebugLevel)
        {
            return false;
        }

        string filter = EffectiveDebugFilter;
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string modName = DisplayName ?? Name;
        return modName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
               || Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DebugLog(ConditionalConfigSyncDebugLevel level, string area, string message)
    {
        if (!ShouldDebugLog(level))
        {
            return;
        }

        LogSource.LogInfo($"[{GetDebugModName()}][{GetDebugSide()}][{area}] {message}");
    }

    private void InfoLog(string message)
    {
        LogSource.LogInfo($"[{GetDebugModName()}][{GetDebugSide()}] {message}");
    }

    private void DebugWarning(string area, string message)
    {
        LogSource.LogWarning($"[{GetDebugModName()}][{GetDebugSide()}][{area}] {message}");
    }

    private string GetDebugModName()
    {
        return !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : Name;
    }

    private static string GetDebugSide()
    {
        if (!GameReflection.HasZNet)
        {
            return "NoZNet";
        }
        return GameReflection.IsServer() ? "Server" : "Client";
    }

    private void InvokeEventHandlers(Action? handlers, string eventName)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception e)
            {
                DebugWarning("Event", $"Subscriber of {eventName} failed; continuing. Error: {e}");
            }
        }
    }

    private void InvokeEventHandlers<T>(Action<T>? handlers, T value, string eventName)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)subscriber)(value);
            }
            catch (Exception e)
            {
                DebugWarning("Event", $"Subscriber of {eventName} failed; continuing. Error: {e}");
            }
        }
    }

    private void InvokeEventHandlers<T1, T2>(Action<T1, T2>? handlers, T1 value1, T2 value2, string eventName)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T1, T2>)subscriber)(value1, value2);
            }
            catch (Exception e)
            {
                DebugWarning("Event", $"Subscriber of {eventName} failed; continuing. Error: {e}");
            }
        }
    }

    private void InvokeEventHandlers<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args, string eventName)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEventArgs>)subscriber)(this, args);
            }
            catch (Exception e)
            {
                DebugWarning("Event", $"Subscriber of {eventName} failed; continuing. Error: {e}");
            }
        }
    }

    private void RaiseInitialSyncCompletedIfNeeded()
    {
        if (InitialSyncDone)
        {
            return;
        }

        InitialSyncDone = true;
        InvokeEventHandlers(InitialSyncCompleted, nameof(InitialSyncCompleted));
    }

    private void RaisePolicyStateEvents(
        OwnConfigEntryBase config,
        bool oldServerControlled,
        bool newServerControlled,
        bool oldHidden,
        bool newHidden,
        string source)
    {
        if (oldServerControlled == newServerControlled && oldHidden == newHidden)
        {
            return;
        }

        RaisePolicyStateEvents(new PolicyStateChangedEventArgs(
            config,
            oldServerControlled,
            newServerControlled,
            oldHidden,
            newHidden,
            source));
    }

    private void RaisePolicyStateEvents(PolicyStateChangedEventArgs args)
    {
        InvokeEventHandlers(PolicyStateChanged, args, nameof(PolicyStateChanged));

        if (args.OldServerControlled != args.NewServerControlled)
        {
            InvokeEventHandlers(ServerControlledChanged, args.Config, args.NewServerControlled, nameof(ServerControlledChanged));
        }
        if (args.OldHidden != args.NewHidden)
        {
            InvokeEventHandlers(HiddenStateChanged, args.Config, args.NewHidden, nameof(HiddenStateChanged));
        }
    }

    private void RaiseLockStateChangedIfNeeded()
    {
        bool current = IsLocked;
        if (lastNotifiedLockState == current)
        {
            return;
        }

        lastNotifiedLockState = current;
        InvokeEventHandlers(LockStateChanged, current, nameof(LockStateChanged));
    }

    private void RejectSync(string reason, long? senderUid, bool incoming, Exception? exception = null)
    {
        DebugWarning("Reject", exception == null ? reason : $"{reason}{Environment.NewLine}{exception}");
        InvokeEventHandlers(SyncRejected, new SyncRejectedEventArgs(reason, senderUid, incoming, exception), nameof(SyncRejected));
    }

    private static void RegisterDebugConsoleCommands()
    {
        if (debugCommandsRegistered)
        {
            return;
        }

        try
        {
            MethodInfo debugHandler = AccessTools.DeclaredMethod(typeof(ConditionalConfigSync), nameof(HandleDebugConsoleCommand))
                                      ?? throw new MissingMethodException(typeof(ConditionalConfigSync).FullName, nameof(HandleDebugConsoleCommand));
            MethodInfo policyHandler = AccessTools.DeclaredMethod(typeof(ConditionalConfigSync), nameof(HandlePolicyConsoleCommand))
                                       ?? throw new MissingMethodException(typeof(ConditionalConfigSync).FullName, nameof(HandlePolicyConsoleCommand));

            GameReflection.RegisterConsoleCommand(
                "conditionalconfigsync_debug",
                "ConditionalConfigSync debug logging: status/on/off/level/filter/clearfilter",
                debugHandler);

            GameReflection.RegisterConsoleCommand(
                "conditionalconfigsync_debug_server",
                "ConditionalConfigSync server debug logging: status/on/off/level/filter/clearfilter",
                debugHandler,
                isNetwork: true,
                onlyServer: true,
                remoteCommand: true,
                onlyAdmin: true);

            RegisterPolicyConsoleCommand("conditionalconfigsync_status", "Show registered mods, config modes, and effective policy counts", policyHandler);
            RegisterPolicyConsoleCommand("conditionalconfigsync_policy_reload", "Reload and apply ConditionalConfigSync policy files", policyHandler);
            RegisterPolicyConsoleCommand("conditionalconfigsync_policy_validate", "Validate policy syntax, identifiers, and ignored rules", policyHandler);
            RegisterPolicyConsoleCommand("conditionalconfigsync_policy_dump", "Write a copy-ready policy identifier file", policyHandler);

            debugCommandsRegistered = true;
        }
        catch (Exception e)
        {
            LogSource.LogWarning($"[Console] Could not register ConditionalConfigSync commands: {e.Message}");
        }
    }

    private static void RegisterPolicyConsoleCommand(string command, string description, MethodInfo handler)
    {
        GameReflection.RegisterConsoleCommand(
            command,
            description,
            handler,
            isNetwork: true,
            onlyServer: true,
            remoteCommand: true,
            onlyAdmin: true);
    }

    private static object HandleDebugConsoleCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureDebugSupportInitialized();

        bool status = GameReflection.ConsoleArgsLength(args) <= 1;
        bool? enabled = null;
        ConditionalConfigSyncDebugLevel? level = null;
        string? filter = null;

        for (int i = 1; i < GameReflection.ConsoleArgsLength(args); ++i)
        {
            string token = GameReflection.ConsoleArg(args, i).Trim();
            switch (token.ToLowerInvariant())
            {
                case "status":
                    status = true;
                    break;
                case "on":
                case "enable":
                case "enabled":
                    enabled = true;
                    status = false;
                    break;
                case "off":
                case "disable":
                case "disabled":
                    enabled = false;
                    status = false;
                    break;
                case "basic":
                case "verbose":
                case "trace":
                    level = ReadDebugLevel(token, DebugLoggingLevel);
                    status = false;
                    break;
                case "level":
                    if (i + 1 < GameReflection.ConsoleArgsLength(args))
                    {
                        level = ReadDebugLevel(GameReflection.ConsoleArg(args, ++i), DebugLoggingLevel);
                        status = false;
                    }
                    break;
                case "filter":
                    if (i + 1 < GameReflection.ConsoleArgsLength(args))
                    {
                        filter = GameReflection.ConsoleArg(args, ++i);
                        status = false;
                    }
                    break;
                case "clearfilter":
                case "nofilter":
                    filter = "";
                    status = false;
                    break;
                default:
                    return $"Unknown argument '{token}'";
            }
        }

        if (status)
        {
            WriteDebugStatus(args);
            return true;
        }

        DebugLoggingEnabled = enabled ?? DebugLoggingEnabled;
        DebugLoggingLevel = level ?? DebugLoggingLevel;
        DebugLoggingFilter = filter ?? DebugLoggingFilter;
        AddConsoleLine(args, $"ConditionalConfigSync debug: enabled={DebugLoggingEnabled}, level={DebugLoggingLevel}, filter='{DebugLoggingFilter}'");
        return true;
    }

    private static void WriteDebugStatus(Terminal.ConsoleEventArgs args)
    {
        AddConsoleLine(args, $"ConditionalConfigSync debug: enabled={DebugLoggingEnabled}, configEnabled={debugConfigEnabled}, level={EffectiveDebugLevel}, filter='{EffectiveDebugFilter}'");
    }

    private static void AddConsoleLine(Terminal.ConsoleEventArgs args, string text)
    {
        GameReflection.ConsoleAddString(args, text);
        LogSource.LogInfo($"[Console] {text}");
    }

    internal static void VersionDebugLog(string area, string modName, string message)
    {
        EnsureDebugSupportInitialized();
        if (!IsDebugActive || ConditionalConfigSyncDebugLevel.Basic > EffectiveDebugLevel)
        {
            return;
        }
        string filter = EffectiveDebugFilter;
        if (!string.IsNullOrWhiteSpace(filter)
            && modName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }
        LogSource.LogInfo($"[{modName}][{GetDebugSide()}][{area}] {message}");
    }

    internal static void VersionInfoLog(string area, string modName, string message)
    {
        LogSource.LogInfo($"[{modName}][{GetDebugSide()}][{area}] {message}");
    }

    internal static void VersionWarningLog(string area, string modName, string message)
    {
        LogSource.LogWarning($"[{modName}][{GetDebugSide()}][{area}] {message}");
    }
}
