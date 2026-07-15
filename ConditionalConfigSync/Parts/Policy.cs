using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private enum ConfigPolicyOverride
    {
        Default,
        ForceServerControlled,
        ForceClientControlled,
    }

    private sealed class SyncPolicyRecord
    {
        internal readonly string DisplayText;
        internal readonly string? Key;
        internal readonly ConfigPolicyOverride PolicyOverride;
        internal readonly string? Error;

        internal SyncPolicyRecord(string displayText, string? key, ConfigPolicyOverride policyOverride, string? error)
        {
            DisplayText = displayText;
            Key = key;
            PolicyOverride = policyOverride;
            Error = error;
        }
    }

    private sealed class HiddenPolicyRecord
    {
        internal readonly string DisplayText;
        internal readonly string? Key;
        internal readonly string? Error;

        internal HiddenPolicyRecord(string displayText, string? key, string? error)
        {
            DisplayText = displayText;
            Key = key;
            Error = error;
        }
    }

    private enum PolicyTargetFailure
    {
        None,
        Mod,
        Section,
        Config,
    }

    private sealed class PolicyTargetResolution
    {
        internal ConditionalConfigSync? ConfigSync;
        internal readonly List<OwnConfigEntryBase> Configs = new();
        internal PolicyTargetFailure Failure;
    }

    private const string SyncPolicyFileName = "ConditionalConfigSync.SyncPolicy.cfg";

    private const string HiddenConfigsFileName = "ConditionalConfigSync.HiddenConfigs.cfg";

    private const string PolicyDumpFileName = "ConditionalConfigSync.PolicyDump.txt";

    private static readonly object policyLock = new();

    private static bool policySupportInitialized;

    private static bool policyReloadScheduled;

    private static long policyReadGeneration;

    private static long policyAppliedGeneration;

    private static FileSystemWatcher? syncPolicyWatcher;

    private static FileSystemWatcher? hiddenConfigsWatcher;

    private static Dictionary<string, ConfigPolicyOverride> syncPolicy = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> hiddenConfigPolicy = new(StringComparer.OrdinalIgnoreCase);

    private static string SyncPolicyPath => Path.Combine(ConfigDirectoryPath, SyncPolicyFileName);

    private static string HiddenConfigsPath => Path.Combine(ConfigDirectoryPath, HiddenConfigsFileName);

    private static void EnsurePolicySupportInitialized(bool createIfMissing = false)
    {
        bool startWatchers;
        lock (policyLock)
        {
            startWatchers = !policySupportInitialized || syncPolicyWatcher == null || hiddenConfigsWatcher == null;
            policySupportInitialized = true;
        }

        // Always perform a synchronous startup read before relying on FileSystemWatcher events.
        // This also reloads policy when a new server session starts in the same process.
        LoadPolicyFiles(createIfMissing, quiet: true, source: "server startup");

        if (startWatchers)
        {
            StartPolicyWatchers();
        }
    }

    private static void StartPolicyWatchers()
    {
        try
        {
            EnsureConfigDirectory();
            syncPolicyWatcher ??= CreatePolicyWatcher(SyncPolicyFileName);
            hiddenConfigsWatcher ??= CreatePolicyWatcher(HiddenConfigsFileName);
        }
        catch (Exception e)
        {
            LogSource.LogWarning($"[Policy] Could not start policy watchers: {e.Message}");
        }
    }

    private static FileSystemWatcher CreatePolicyWatcher(string fileName)
    {
        FileSystemWatcher watcher = new(ConfigDirectoryPath, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => SchedulePolicyReload();
        watcher.Created += (_, _) => SchedulePolicyReload();
        watcher.Renamed += (_, _) => SchedulePolicyReload();
        watcher.Deleted += (_, _) => SchedulePolicyReload();
        return watcher;
    }

    private static void SchedulePolicyReload()
    {
        long generation;
        lock (policyLock)
        {
            if (policyReloadScheduled)
            {
                return;
            }
            policyReloadScheduled = true;
            generation = ++policyReadGeneration;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(500);
            if (!TryReadPolicyFiles(
                    createIfMissing: false,
                    out Dictionary<string, ConfigPolicyOverride> newSyncPolicy,
                    out HashSet<string> newHiddenPolicy,
                    out List<SyncPolicyRecord> syncRecords,
                    out List<HiddenPolicyRecord> hiddenRecords,
                    out string? error))
            {
                lock (policyLock)
                {
                    policyReloadScheduled = false;
                }
                LogSource.LogWarning($"[Policy] Failed to read policy files: {error}");
                return;
            }

            lock (policyLock)
            {
                policyReloadScheduled = false;
            }

            EnqueueMainThread(() =>
                ApplyPolicyFiles(newSyncPolicy, newHiddenPolicy, syncRecords, hiddenRecords, quiet: false, source: "policy file watcher", generation: generation));
        });
    }

    private static bool LoadPolicyFiles(bool createIfMissing, bool quiet, string source)
    {
        if (!TryReadPolicyFiles(
                createIfMissing,
                out Dictionary<string, ConfigPolicyOverride> newSyncPolicy,
                out HashSet<string> newHiddenPolicy,
                out List<SyncPolicyRecord> syncRecords,
                out List<HiddenPolicyRecord> hiddenRecords,
                out string? error))
        {
            LogSource.LogWarning($"[Policy] Failed to read policy files: {error}");
            return false;
        }

        long generation = Interlocked.Increment(ref policyReadGeneration);
        if (IsMainThread)
        {
            ApplyPolicyFiles(newSyncPolicy, newHiddenPolicy, syncRecords, hiddenRecords, quiet, source, generation);
        }
        else
        {
            EnqueueMainThread(() => ApplyPolicyFiles(newSyncPolicy, newHiddenPolicy, syncRecords, hiddenRecords, quiet, source, generation));
        }

        return true;
    }

    private static bool TryReadPolicyFiles(
        bool createIfMissing,
        out Dictionary<string, ConfigPolicyOverride> newSyncPolicy,
        out HashSet<string> newHiddenPolicy,
        out List<SyncPolicyRecord> syncRecords,
        out List<HiddenPolicyRecord> hiddenRecords,
        out string? error)
    {
        newSyncPolicy = new Dictionary<string, ConfigPolicyOverride>(StringComparer.OrdinalIgnoreCase);
        newHiddenPolicy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        syncRecords = new List<SyncPolicyRecord>();
        hiddenRecords = new List<HiddenPolicyRecord>();
        error = null;

        try
        {
            EnsureConfigDirectory();
            if (createIfMissing)
            {
                EnsurePolicyFilesExist();
            }

            // Missing policy files are accepted only after the same stability window as a changed file. This avoids
            // treating an editor's short atomic replace/rename gap as an intentional empty policy.
            newSyncPolicy = ReadSyncPolicyFile(SyncPolicyPath, out syncRecords);
            newHiddenPolicy = ReadHiddenConfigsFile(HiddenConfigsPath, out hiddenRecords);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static void ApplyPolicyFiles(
        Dictionary<string, ConfigPolicyOverride> newSyncPolicy,
        HashSet<string> newHiddenPolicy,
        List<SyncPolicyRecord> syncRecords,
        List<HiddenPolicyRecord> hiddenRecords,
        bool quiet,
        string source,
        long generation)
    {
        lock (policyLock)
        {
            // A policy snapshot can remain queued while ZNet is shutting down. Ignore it when a newer
            // snapshot was already read and applied during the next server initialization.
            if (generation < policyAppliedGeneration)
            {
                return;
            }

            policyAppliedGeneration = generation;
            syncPolicy = newSyncPolicy;
            hiddenConfigPolicy = newHiddenPolicy;
        }

        LogPolicyFileRecords(syncRecords, hiddenRecords, source);

        if (!quiet)
        {
            int forceServer = newSyncPolicy.Count(kv => kv.Value == ConfigPolicyOverride.ForceServerControlled);
            int forceClient = newSyncPolicy.Count(kv => kv.Value == ConfigPolicyOverride.ForceClientControlled);
            LogSource.LogInfo($"[SyncPolicy] Reloaded: forceServer={forceServer}, forceClient={forceClient}, hidden={newHiddenPolicy.Count}, source={source}");
        }

        RefreshPolicyStatesForAll(source, broadcast: !quiet);
    }

    private static void EnsurePolicyFilesExist()
    {
        if (!File.Exists(SyncPolicyPath))
        {
            File.WriteAllText(SyncPolicyPath,
                "# ConditionalConfigSync sync policy. Server-side only.\n" +
                "# Exact setting: + ModGuid.Section.Key or - ModGuid.Section.Key\n" +
                "# Whole section: + ModGuid.Section or - ModGuid.Section\n" +
                "# + forces server-controlled; - forces client-controlled.\n" +
                "# Exact-setting rules take precedence over section rules.\n" +
                "# Rules apply only to configs registered as Conditional.\n");
        }

        if (!File.Exists(HiddenConfigsPath))
        {
            File.WriteAllText(HiddenConfigsPath,
                "# ConditionalConfigSync hidden config policy. Server-side only.\n" +
                "# Exact setting: ModGuid.Section.Key\n" +
                "# Whole section: ModGuid.Section\n" +
                "# Exact and section entries may be combined.\n");
        }
    }

    private static Dictionary<string, ConfigPolicyOverride> ReadSyncPolicyFile(string path, out List<SyncPolicyRecord> records)
    {
        Dictionary<string, ConfigPolicyOverride> result = new(StringComparer.OrdinalIgnoreCase);
        records = new List<SyncPolicyRecord>();
        foreach (string rawLine in ReadAllLinesStable(path, missingIsEmpty: true))
        {
            string line = StripPolicyComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            char prefix = line[0];
            if (prefix != '+' && prefix != '-')
            {
                records.Add(new SyncPolicyRecord(line, null, ConfigPolicyOverride.Default, "expected '+' or '-'"));
                continue;
            }

            string key = line.Substring(1).Trim();
            if (key.Length == 0)
            {
                records.Add(new SyncPolicyRecord(line, null, ConfigPolicyOverride.Default, "config record is empty"));
                continue;
            }

            ConfigPolicyOverride policyOverride = prefix == '+'
                ? ConfigPolicyOverride.ForceServerControlled
                : ConfigPolicyOverride.ForceClientControlled;

            records.Add(new SyncPolicyRecord(key, key, policyOverride, null));
            result[key] = policyOverride;
        }
        return result;
    }

    private static HashSet<string> ReadHiddenConfigsFile(string path, out List<HiddenPolicyRecord> records)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        records = new List<HiddenPolicyRecord>();
        foreach (string rawLine in ReadAllLinesStable(path, missingIsEmpty: true))
        {
            string key = StripPolicyComment(rawLine).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            records.Add(new HiddenPolicyRecord(key, key, null));
            result.Add(key);
        }
        return result;
    }

    private static string StripPolicyComment(string line)
    {
        int hash = line.IndexOf('#');
        int semicolon = line.IndexOf(';');
        int comment = hash < 0 ? semicolon : semicolon < 0 ? hash : Math.Min(hash, semicolon);
        return comment < 0 ? line : line.Substring(0, comment);
    }

    private static void RefreshPolicyStatesForAll(string source, bool broadcast)
    {
        foreach (ConditionalConfigSync configSync in configSyncs)
        {
            configSync.RefreshPolicyStates(source, broadcast);
        }
    }

    private void RefreshPolicyStates(string source, bool broadcast)
    {
        if (!IsSourceOfTruth)
        {
            return;
        }

        List<OwnConfigEntryBase> changed = new();
        List<PolicyStateChangedEventArgs> policyTransitions = new();
        foreach (OwnConfigEntryBase config in allConfigs)
        {
            bool firstInitialization = !config.IsPolicyStateInitialized;

            // On the first pass compare against the fully configured mode/default established during registration.
            bool oldEffectiveServerControlled = firstInitialization ? GetDefaultServerControlled(config) : config.IsServerControlled;
            bool oldEffectiveHidden = !firstInitialization && config.IsHidden;
            bool oldStoredServerControlled = config.IsServerControlled;
            bool oldStoredHidden = config.IsHidden;

            bool newServerControlled = ComputeServerControlled(config);
            bool newHidden = ComputeHidden(config);

            config.IsServerControlled = newServerControlled;
            config.IsHidden = newHidden;
            config.IsPolicyStateInitialized = true;

            bool storedStateChanged = oldStoredServerControlled != newServerControlled || oldStoredHidden != newHidden;
            bool effectivePolicyChanged = oldEffectiveServerControlled != newServerControlled || oldEffectiveHidden != newHidden;
            if (!storedStateChanged && !effectivePolicyChanged)
            {
                continue;
            }

            changed.Add(config);
            if (effectivePolicyChanged)
            {
                policyTransitions.Add(new PolicyStateChangedEventArgs(
                    config,
                    oldEffectiveServerControlled,
                    newServerControlled,
                    oldEffectiveHidden,
                    newHidden,
                    source));
            }
        }

        if (changed.Count > 0)
        {
            ServerLockedSettingChanged();
        }

        // Subscribers observe the final policy metadata and read-only state, not an intermediate value.
        foreach (PolicyStateChangedEventArgs transition in policyTransitions)
        {
            RaisePolicyStateEvents(transition);
        }

        if (broadcast && isServer && GameReflection.HasZNet && changed.Count > 0)
        {
            StartBroadcastPackage(
                GameReflection.Everybody,
                ConfigsToPackage(changed.Select(c => c.BaseConfig), includeConfigValues: true, includeAllProvidedConfigStates: true));
        }
    }

    private static void LogPolicyFileRecords(
        IReadOnlyList<SyncPolicyRecord> syncRecords,
        IReadOnlyList<HiddenPolicyRecord> hiddenRecords,
        string source)
    {
        LogSyncPolicyRecords(syncRecords, source);
        LogHiddenPolicyRecords(hiddenRecords, source);
    }

    private static void LogSyncPolicyRecords(IReadOnlyList<SyncPolicyRecord> records, string source)
    {
        Dictionary<string, int> lastRecordIndexes = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < records.Count; ++index)
        {
            if (records[index].Key != null)
            {
                lastRecordIndexes[records[index].Key!] = index;
            }
        }

        for (int index = 0; index < records.Count; ++index)
        {
            SyncPolicyRecord record = records[index];
            string sourceSuffix = GetPolicySourceSuffix(source);
            if (record.Error != null || record.Key == null)
            {
                LogSource.LogWarning($"[SyncPolicy] {record.DisplayText}: {record.Error}{sourceSuffix}");
                continue;
            }

            PolicyTargetResolution resolution = ResolvePolicyTarget(record.Key);
            if (LogPolicyTargetFailure(resolution, record.DisplayText, "SyncPolicy", sourceSuffix))
            {
                continue;
            }

            ConditionalConfigSync configSync = resolution.ConfigSync!;
            string prefix = $"[SyncPolicy][{configSync.GetDebugModName()}]";
            string policyName = GetPolicyOverrideName(record.PolicyOverride);

            if (lastRecordIndexes[record.Key] != index)
            {
                LogSource.LogWarning($"{prefix} {record.DisplayText}: overridden by a later rule{sourceSuffix}");
                continue;
            }

            List<OwnConfigEntryBase> effectiveConfigs = resolution.Configs
                .Where(config => configSync.GetPolicyOverride(config, out string? matchedKey)
                                 != ConfigPolicyOverride.Default
                                 && string.Equals(matchedKey, record.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (effectiveConfigs.Count == 0)
            {
                LogSource.LogInfo($"{prefix} {record.DisplayText}: {policyName} has no effect on mod behavior{sourceSuffix}");
                continue;
            }

            List<OwnConfigEntryBase> conditionalConfigs = effectiveConfigs
                .Where(config => config.SyncMode == ConfigSyncMode.Conditional)
                .ToList();

            if (conditionalConfigs.Count == 0)
            {
                ConfigSyncMode[] modes = effectiveConfigs.Select(config => config.SyncMode).Distinct().ToArray();
                string reason = modes.Length == 1
                    ? $"ignored because its mode is {modes[0]}"
                    : "ignored because matching configs are not Conditional";
                LogSource.LogInfo($"{prefix} {record.DisplayText}: {reason}{sourceSuffix}");
                continue;
            }

            bool targetServerControlled = record.PolicyOverride == ConfigPolicyOverride.ForceServerControlled;
            bool changesBehavior = conditionalConfigs.Any(config => config.SynchronizedConfig != targetServerControlled);
            if (changesBehavior)
            {
                LogSource.LogWarning($"{prefix} {record.DisplayText}: {policyName} changes mod behavior{sourceSuffix}");
            }
            else
            {
                LogSource.LogInfo($"{prefix} {record.DisplayText}: {policyName} has no effect on mod behavior{sourceSuffix}");
            }
        }
    }

    private static void LogHiddenPolicyRecords(IReadOnlyList<HiddenPolicyRecord> records, string source)
    {
        foreach (HiddenPolicyRecord record in records)
        {
            string sourceSuffix = GetPolicySourceSuffix(source);
            if (record.Error != null || record.Key == null)
            {
                LogSource.LogWarning($"[HiddenConfigs] {record.DisplayText}: {record.Error}{sourceSuffix}");
                continue;
            }

            PolicyTargetResolution resolution = ResolvePolicyTarget(record.Key);
            if (LogPolicyTargetFailure(resolution, record.DisplayText, "HiddenConfigs", sourceSuffix))
            {
                continue;
            }

            ConditionalConfigSync configSync = resolution.ConfigSync!;
            LogSource.LogWarning(
                $"[HiddenConfigs][{configSync.GetDebugModName()}] {record.DisplayText}: " +
                $"is now hidden in configuration manager{sourceSuffix}");
        }
    }

    private static bool LogPolicyTargetFailure(
        PolicyTargetResolution resolution,
        string displayText,
        string area,
        string sourceSuffix)
    {
        if (resolution.Failure == PolicyTargetFailure.None)
        {
            return false;
        }

        if (resolution.Failure == PolicyTargetFailure.Mod)
        {
            LogSource.LogWarning($"[{area}] {displayText}: can not find mod by GUID{sourceSuffix}");
            return true;
        }

        string modName = resolution.ConfigSync!.GetDebugModName();
        string reason = resolution.Failure == PolicyTargetFailure.Section
            ? "can not find config section"
            : "can not find config name";
        LogSource.LogWarning($"[{area}][{modName}] {displayText}: {reason}{sourceSuffix}");
        return true;
    }

    private static PolicyTargetResolution ResolvePolicyTarget(string key)
    {
        PolicyTargetResolution result = new();
        ConditionalConfigSync? configSync = configSyncs
            .Where(sync => key.StartsWith(sync.Name + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sync => sync.Name.Length)
            .FirstOrDefault();

        if (configSync == null)
        {
            result.Failure = PolicyTargetFailure.Mod;
            return result;
        }

        result.ConfigSync = configSync;

        OwnConfigEntryBase? exactConfig = configSync.allConfigs.FirstOrDefault(config =>
            string.Equals(configSync.GetPolicyKey(config), key, StringComparison.OrdinalIgnoreCase));
        if (exactConfig != null)
        {
            result.Configs.Add(exactConfig);
            return result;
        }

        List<OwnConfigEntryBase> sectionConfigs = configSync.allConfigs
            .Where(config => string.Equals(configSync.GetPolicySectionKey(config), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sectionConfigs.Count > 0)
        {
            result.Configs.AddRange(sectionConfigs);
            return result;
        }

        string remainder = key.Substring(configSync.Name.Length + 1);
        bool sectionFound = configSync.allConfigs
            .Select(config => config.BaseConfig.Definition.Section)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(section => remainder.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase));

        result.Failure = sectionFound ? PolicyTargetFailure.Config : PolicyTargetFailure.Section;
        return result;
    }

    private static string GetPolicyOverrideName(ConfigPolicyOverride policyOverride)
    {
        return policyOverride == ConfigPolicyOverride.ForceServerControlled
            ? "ForceServerControlled"
            : "ForceClientControlled";
    }

    private static string GetPolicySourceSuffix(string source)
    {
        return IsDebugActive && EffectiveDebugLevel >= ConditionalConfigSyncDebugLevel.Verbose
            ? $", source={source}"
            : "";
    }

    private static List<string> ValidatePolicyFiles(out int syncRuleCount, out int hiddenRuleCount)
    {
        List<string> diagnostics = new();
        Dictionary<string, ConfigPolicyOverride> parsedSync = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> parsedHidden = new(StringComparer.OrdinalIgnoreCase);

        EnsureConfigDirectory();
        ValidateSyncPolicyFile(parsedSync, diagnostics);
        ValidateHiddenPolicyFile(parsedHidden, diagnostics);

        syncRuleCount = parsedSync.Count;
        hiddenRuleCount = parsedHidden.Count;

        HashSet<string> knownKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConditionalConfigSync configSync in configSyncs)
        {
            foreach (OwnConfigEntryBase config in configSync.allConfigs)
            {
                knownKeys.Add(configSync.GetPolicyKey(config));
                knownKeys.Add(configSync.GetPolicySectionKey(config));
            }
        }

        foreach (string key in parsedSync.Keys.Concat(parsedHidden).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!knownKeys.Contains(key))
            {
                diagnostics.Add($"Unknown identifier: {key}");
            }
        }

        foreach (ConditionalConfigSync configSync in configSyncs)
        {
            foreach (OwnConfigEntryBase config in configSync.allConfigs)
            {
                ConfigPolicyOverride policyOverride = ResolvePolicyOverride(parsedSync, configSync.GetPolicyKey(config), configSync.GetPolicySectionKey(config), out string? matchedKey);
                if (policyOverride == ConfigPolicyOverride.Default)
                {
                    continue;
                }

                if (config == configSync.lockedConfig && policyOverride == ConfigPolicyOverride.ForceClientControlled)
                {
                    diagnostics.Add($"ForceClientControlled is ignored for protected locking config {configSync.GetPolicyKey(config)} (matched rule: {matchedKey}).");
                }
                else if (config.SyncMode != ConfigSyncMode.Conditional)
                {
                    diagnostics.Add($"Rule {matchedKey} is ignored for {configSync.GetPolicyKey(config)} because its mode is {config.SyncMode}.");
                }
            }
        }

        return diagnostics;
    }

    private static void ValidateSyncPolicyFile(Dictionary<string, ConfigPolicyOverride> result, List<string> diagnostics)
    {
        if (!File.Exists(SyncPolicyPath))
        {
            diagnostics.Add($"File does not exist: {SyncPolicyPath}");
            return;
        }

        string[] lines = ReadAllLinesStable(SyncPolicyPath);
        for (int index = 0; index < lines.Length; ++index)
        {
            string rawLine = lines[index];
            string line = StripPolicyComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            char prefix = line[0];
            if (prefix != '+' && prefix != '-')
            {
                diagnostics.Add($"{SyncPolicyFileName}:{index + 1}: expected '+' or '-': {rawLine}");
                continue;
            }

            string key = line.Substring(1).Trim();
            if (key.Length == 0)
            {
                diagnostics.Add($"{SyncPolicyFileName}:{index + 1}: identifier is empty.");
                continue;
            }

            ConfigPolicyOverride policyOverride = prefix == '+'
                ? ConfigPolicyOverride.ForceServerControlled
                : ConfigPolicyOverride.ForceClientControlled;

            if (result.TryGetValue(key, out ConfigPolicyOverride previous))
            {
                diagnostics.Add(previous == policyOverride
                    ? $"{SyncPolicyFileName}:{index + 1}: duplicate rule: {key}"
                    : $"{SyncPolicyFileName}:{index + 1}: conflicting rule overrides an earlier entry: {key}");
            }

            result[key] = policyOverride;
        }
    }

    private static void ValidateHiddenPolicyFile(HashSet<string> result, List<string> diagnostics)
    {
        if (!File.Exists(HiddenConfigsPath))
        {
            diagnostics.Add($"File does not exist: {HiddenConfigsPath}");
            return;
        }

        string[] lines = ReadAllLinesStable(HiddenConfigsPath);
        for (int index = 0; index < lines.Length; ++index)
        {
            string key = StripPolicyComment(lines[index]).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!result.Add(key))
            {
                diagnostics.Add($"{HiddenConfigsFileName}:{index + 1}: duplicate rule: {key}");
            }
        }
    }

    private static ConfigPolicyOverride ResolvePolicyOverride(
        IReadOnlyDictionary<string, ConfigPolicyOverride> policy,
        string exactKey,
        string sectionKey,
        out string? matchedKey)
    {
        if (policy.TryGetValue(exactKey, out ConfigPolicyOverride exactOverride))
        {
            matchedKey = exactKey;
            return exactOverride;
        }

        if (policy.TryGetValue(sectionKey, out ConfigPolicyOverride sectionOverride))
        {
            matchedKey = sectionKey;
            return sectionOverride;
        }

        matchedKey = null;
        return ConfigPolicyOverride.Default;
    }

    private static string WritePolicyDumpFile()
    {
        EnsureConfigDirectory();
        string path = Path.Combine(ConfigDirectoryPath, PolicyDumpFileName);
        List<string> lines = new()
        {
            "# ConditionalConfigSync policy identifiers",
            "# Copy an identifier into SyncPolicy.cfg and prefix it with '+' or '-'.",
            "# Copy an identifier into HiddenConfigs.cfg without a prefix.",
            "# To target a whole section, copy the section identifier shown after '# Section:'.",
            "",
        };

        foreach (ConditionalConfigSync configSync in configSyncs.OrderBy(sync => sync.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? previousSection = null;
            foreach (OwnConfigEntryBase config in configSync.allConfigs
                         .OrderBy(entry => entry.BaseConfig.Definition.Section, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(entry => entry.BaseConfig.Definition.Key, StringComparer.OrdinalIgnoreCase))
            {
                string sectionKey = configSync.GetPolicySectionKey(config);
                if (!string.Equals(previousSection, sectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (previousSection != null)
                    {
                        lines.Add("");
                    }
                    lines.Add($"# Section: {sectionKey}");
                    previousSection = sectionKey;
                }

                lines.Add(configSync.GetPolicyKey(config));
                lines.Add($"Policy: {config.SyncMode}; Default: {(GetDefaultServerControlled(config) ? "ServerControlled" : "ClientControlled")}");
            }

            lines.Add("");
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    private static object HandlePolicyConsoleCommand(Terminal.ConsoleEventArgs args)
    {
        string command = GameReflection.ConsoleArg(args, 0).Trim().ToLowerInvariant();
        if (!GameReflection.HasZNet || !GameReflection.IsServer())
        {
            return "This command is available only on the server.";
        }

        switch (command)
        {
            case "conditionalconfigsync_status":
                WritePolicyStatus(args);
                return true;

            case "conditionalconfigsync_policy_reload":
                bool loaded = LoadPolicyFiles(createIfMissing: true, quiet: false, source: "console command");
                AddConsoleLine(args, loaded ? "ConditionalConfigSync policy reloaded." : "ConditionalConfigSync policy reload failed. See the server log.");
                return loaded;

            case "conditionalconfigsync_policy_validate":
                List<string> diagnostics = ValidatePolicyFiles(out int syncRules, out int hiddenRules);
                AddConsoleLine(args, $"ConditionalConfigSync policy validation: syncRules={syncRules}, hiddenRules={hiddenRules}, issues={diagnostics.Count}");
                foreach (string diagnostic in diagnostics)
                {
                    AddConsoleLine(args, diagnostic);
                }
                return true;

            case "conditionalconfigsync_policy_dump":
                try
                {
                    string path = WritePolicyDumpFile();
                    AddConsoleLine(args, $"ConditionalConfigSync policy dump written to: {path}");
                    return true;
                }
                catch (Exception e)
                {
                    LogSource.LogWarning($"[Policy] Failed to write policy dump: {e}");
                    return $"Failed to write policy dump: {e.Message}";
                }

            default:
                return $"Unknown ConditionalConfigSync policy command: {command}";
        }
    }

    private static void WritePolicyStatus(Terminal.ConsoleEventArgs args)
    {
        Dictionary<string, ConfigPolicyOverride> currentSync;
        HashSet<string> currentHidden;
        lock (policyLock)
        {
            currentSync = new Dictionary<string, ConfigPolicyOverride>(syncPolicy, StringComparer.OrdinalIgnoreCase);
            currentHidden = new HashSet<string>(hiddenConfigPolicy, StringComparer.OrdinalIgnoreCase);
        }

        AddConsoleLine(args,
            $"ConditionalConfigSync policy status: protocol={PluginInfoCCS.ProtocolVersion}, " +
            $"mods={configSyncs.Count}, syncRules={currentSync.Count}, hiddenRules={currentHidden.Count}, " +
            $"syncFile='{SyncPolicyPath}', hiddenFile='{HiddenConfigsPath}'");

        foreach (ConditionalConfigSync configSync in configSyncs.OrderBy(sync => sync.Name, StringComparer.OrdinalIgnoreCase))
        {
            int alwaysServer = configSync.allConfigs.Count(config => config.SyncMode == ConfigSyncMode.AlwaysServerControlled);
            int conditional = configSync.allConfigs.Count(config => config.SyncMode == ConfigSyncMode.Conditional);
            int alwaysClient = configSync.allConfigs.Count(config => config.SyncMode == ConfigSyncMode.AlwaysClientControlled);
            int effectiveServer = configSync.allConfigs.Count(config => config.IsServerControlled);
            int hidden = configSync.allConfigs.Count(config => config.IsHidden);
            AddConsoleLine(args,
                $"{configSync.GetDebugModName()} ({configSync.Name}): configs={configSync.allConfigs.Count}, " +
                $"modes={alwaysServer}/{conditional}/{alwaysClient} [AlwaysServer/Conditional/AlwaysClient], " +
                $"effectiveServer={effectiveServer}, hidden={hidden}, locked={configSync.IsLocked}");
        }
    }

    private string GetPolicyKey(OwnConfigEntryBase config)
    {
        ConfigDefinition definition = config.BaseConfig.Definition;
        return $"{Name}.{definition.Section}.{definition.Key}";
    }

    private string GetPolicySectionKey(OwnConfigEntryBase config)
    {
        ConfigDefinition definition = config.BaseConfig.Definition;
        return $"{Name}.{definition.Section}";
    }

    private static ConditionalConfigSync? GetOwningConfigSync(OwnConfigEntryBase config)
    {
        return configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config));
    }

    private ConfigPolicyOverride GetPolicyOverride(OwnConfigEntryBase config, out string? matchedKey)
    {
        string exactKey = GetPolicyKey(config);
        string sectionKey = GetPolicySectionKey(config);
        lock (policyLock)
        {
            if (syncPolicy.TryGetValue(exactKey, out ConfigPolicyOverride exactOverride))
            {
                matchedKey = exactKey;
                return exactOverride;
            }

            if (syncPolicy.TryGetValue(sectionKey, out ConfigPolicyOverride sectionOverride))
            {
                matchedKey = sectionKey;
                return sectionOverride;
            }
        }

        matchedKey = null;
        return ConfigPolicyOverride.Default;
    }

    private bool IsHiddenByPolicy(OwnConfigEntryBase config, out string? matchedKey)
    {
        string exactKey = GetPolicyKey(config);
        string sectionKey = GetPolicySectionKey(config);
        lock (policyLock)
        {
            if (hiddenConfigPolicy.Contains(exactKey))
            {
                matchedKey = exactKey;
                return true;
            }

            if (hiddenConfigPolicy.Contains(sectionKey))
            {
                matchedKey = sectionKey;
                return true;
            }
        }

        matchedKey = null;
        return false;
    }

    private static bool GetDefaultServerControlled(OwnConfigEntryBase config)
    {
        return config.ServerControlledByDefault;
    }

    private bool ComputeServerControlled(OwnConfigEntryBase config)
    {
        // The lock switch controls permissions, so letting clients own it would allow privilege escalation.
        if (config == lockedConfig || config.SyncMode == ConfigSyncMode.AlwaysServerControlled)
        {
            return true;
        }

        if (config.SyncMode == ConfigSyncMode.AlwaysClientControlled)
        {
            return false;
        }

        return GetPolicyOverride(config, out _) switch
        {
            ConfigPolicyOverride.ForceServerControlled => true,
            ConfigPolicyOverride.ForceClientControlled => false,
            _ => config.SynchronizedConfig,
        };
    }

    private bool ComputeHidden(OwnConfigEntryBase config) => IsHiddenByPolicy(config, out _);

    private static bool GetPackageServerControlled(OwnConfigEntryBase config)
    {
        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        return owner == null ? GetDefaultServerControlled(config) : owner.IsSourceOfTruth ? owner.ComputeServerControlled(config) : config.IsServerControlled;
    }

    private static bool GetPackageHidden(OwnConfigEntryBase config)
    {
        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        return owner != null && owner.IsSourceOfTruth ? owner.ComputeHidden(config) : config.IsHidden;
    }

    private static bool ShouldIncludeConfigInPackage(OwnConfigEntryBase config)
    {
        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        if (owner == null)
        {
            return GetDefaultServerControlled(config);
        }

        if (!owner.IsSourceOfTruth)
        {
            return config.IsServerControlled || config.IsHidden || config.IsServerControlled != GetDefaultServerControlled(config);
        }

        bool serverControlled = owner.ComputeServerControlled(config);
        return serverControlled || owner.ComputeHidden(config) || serverControlled != GetDefaultServerControlled(config);
    }
}
