using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private const string PolicyChangeRpcSuffix = " ConditionalConfigSync Policy Change";
    private const int MaxPolicyChangeRequestSize = 4096;
    private const int MaxPolicyIdentifierLength = 1024;
    private const int PolicyChangeCapability = 1;

    private bool remotePolicyChangeSupported;

    internal static bool CanChangePolicyFor(OwnConfigEntryBase config)
    {
        if (config == null || config.SyncMode != ConfigSyncMode.Conditional)
        {
            return false;
        }

        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        if (owner == null || config == owner.lockedConfig || !sessionActive || !GameReflection.HasZNet)
        {
            return false;
        }

        if (owner.IsSourceOfTruth)
        {
            return isServer;
        }

        return owner.InitialSyncDone
               && owner.remotePolicyChangeSupported
               && owner.IsAdmin
               && GameReflection.GetPeers().Any(GameReflection.IsPeerReady);
    }

    internal static bool TogglePolicyFor(OwnConfigEntryBase config)
    {
        if (!CanChangePolicyFor(config))
        {
            return false;
        }

        ConditionalConfigSync? owner = GetOwningConfigSync(config);
        if (owner == null)
        {
            return false;
        }

        bool serverControlled = !config.IsServerControlled;
        return owner.IsSourceOfTruth
            ? owner.TryApplySynchronizationPolicy(config, serverControlled, "configuration manager")
            : owner.RequestSynchronizationPolicyChange(config, serverControlled);
    }

    private bool RequestSynchronizationPolicyChange(OwnConfigEntryBase config, bool serverControlled)
    {
        try
        {
            ConfigDefinition definition = config.BaseConfig.Definition;
            ZPackage request = GameReflection.NewPackage();
            GameReflection.PackageWrite(request, PluginInfoCCS.ProtocolVersion);
            GameReflection.PackageWrite(request, definition.Section);
            GameReflection.PackageWrite(request, definition.Key);
            GameReflection.PackageWrite(request, serverControlled);

            int size = GameReflection.PackageSize(request);
            if (size > MaxPolicyChangeRequestSize)
            {
                RejectSync(
                    $"Policy change request for {definition.Section} -> {definition.Key} is too large: {size} bytes.",
                    null,
                    incoming: false);
                return false;
            }

            GameReflection.InvokeRoutedPackage(Name + PolicyChangeRpcSuffix, request);
            DebugLog(
                ConditionalConfigSyncDebugLevel.Basic,
                "Policy",
                $"Requested {(serverControlled ? "server-controlled" : "client-controlled")} policy for {definition.Section} -> {definition.Key}");
            return true;
        }
        catch (Exception e)
        {
            RejectSync($"Failed to request synchronization policy change: {e.Message}", null, incoming: false, e);
            return false;
        }
    }

    private void RPC_RequestSynchronizationPolicyChange(long sender, ZPackage request)
    {
        if (!isServer || !sessionActive)
        {
            return;
        }

        try
        {
            if (GameReflection.PackageSize(request) > MaxPolicyChangeRequestSize)
            {
                RejectSync(
                    $"Rejected oversized policy change request from {FormatClient(sender)}.",
                    sender,
                    incoming: true);
                return;
            }

            int requesterProtocol = GameReflection.PackageReadInt(request);
            if (!IsProtocolCompatible(requesterProtocol))
            {
                RejectSync(
                    $"Rejected policy change request from {FormatClient(sender)} using ConditionalConfigSync protocol {requesterProtocol}. " +
                    $"Required protocol is {PluginInfoCCS.ProtocolVersion}.",
                    sender,
                    incoming: true);
                return;
            }

            if (!IsSenderAdmin(sender))
            {
                RejectSync(
                    $"Rejected policy change request from {FormatClient(sender)} because server administrator access is required.",
                    sender,
                    incoming: true);
                return;
            }

            string section = GameReflection.PackageReadString(request);
            string key = GameReflection.PackageReadString(request);
            bool serverControlled = GameReflection.PackageReadBool(request);
            if (section.Length == 0 || key.Length == 0
                || section.Length > MaxPolicyIdentifierLength || key.Length > MaxPolicyIdentifierLength)
            {
                RejectSync(
                    $"Rejected policy change request from {FormatClient(sender)} with an invalid config identifier.",
                    sender,
                    incoming: true);
                return;
            }

            OwnConfigEntryBase? config = allConfigs.FirstOrDefault(entry =>
                string.Equals(entry.BaseConfig.Definition.Section, section, StringComparison.Ordinal)
                && string.Equals(entry.BaseConfig.Definition.Key, key, StringComparison.Ordinal));
            if (config == null)
            {
                RejectSync(
                    $"Rejected policy change request from {FormatClient(sender)} for unknown config {section} -> {key}.",
                    sender,
                    incoming: true);
                return;
            }

            if (config.SyncMode != ConfigSyncMode.Conditional || config == lockedConfig)
            {
                RejectSync(
                    $"Rejected policy change request from {FormatClient(sender)} for non-Conditional config {section} -> {key}.",
                    sender,
                    incoming: true);
                return;
            }

            if (!TryApplySynchronizationPolicy(
                    config,
                    serverControlled,
                    $"configuration manager request from {FormatClient(sender)}"))
            {
                RejectSync(
                    $"Failed to apply policy change requested by {FormatClient(sender)} for {section} -> {key}.",
                    sender,
                    incoming: true);
            }
        }
        catch (Exception e)
        {
            RejectSync(
                $"Failed to process policy change request from {FormatClient(sender)}: {e.Message}",
                sender,
                incoming: true,
                e);
        }
    }

    private bool TryApplySynchronizationPolicy(OwnConfigEntryBase config, bool serverControlled, string source)
    {
        if (!IsSourceOfTruth || !isServer || config.SyncMode != ConfigSyncMode.Conditional || config == lockedConfig)
        {
            return false;
        }

        try
        {
            EnsurePolicyFilesExist();

            string exactKey = GetPolicyKey(config);
            ConfigPolicyOverride exactOverride = UpdateExactPolicyRuleFile(
                exactKey,
                GetPolicySectionKey(config),
                config.ServerControlledByDefault,
                serverControlled);
            if (!LoadPolicyFiles(createIfMissing: true, quiet: false, source: source))
            {
                return false;
            }

            DebugLog(
                ConditionalConfigSyncDebugLevel.Basic,
                "Policy",
                $"Set {exactKey} to {(serverControlled ? "server-controlled" : "client-controlled")}; " +
                $"exactOverride={exactOverride}, source={source}");
            return true;
        }
        catch (Exception e)
        {
            DebugWarning("Policy", $"Failed to change synchronization policy for {GetPolicyKey(config)}: {e}");
            return false;
        }
    }

    private static ConfigPolicyOverride UpdateExactPolicyRuleFile(
        string exactKey,
        string sectionKey,
        bool serverControlledByDefault,
        bool requestedServerControlled)
    {
        string[] lines = ReadAllLinesStable(SyncPolicyPath, missingIsEmpty: true);
        ConfigPolicyOverride sectionOverride = ConfigPolicyOverride.Default;
        foreach (string line in lines)
        {
            if (TryParsePolicyRule(line, out string key, out ConfigPolicyOverride policyOverride)
                && string.Equals(key, sectionKey, StringComparison.OrdinalIgnoreCase))
            {
                sectionOverride = policyOverride;
            }
        }

        bool ownershipWithoutExactRule = sectionOverride switch
        {
            ConfigPolicyOverride.ForceServerControlled => true,
            ConfigPolicyOverride.ForceClientControlled => false,
            _ => serverControlledByDefault,
        };
        ConfigPolicyOverride exactOverride = requestedServerControlled == ownershipWithoutExactRule
            ? ConfigPolicyOverride.Default
            : requestedServerControlled
                ? ConfigPolicyOverride.ForceServerControlled
                : ConfigPolicyOverride.ForceClientControlled;

        List<string> output = new(lines.Length + 2);
        int insertionIndex = -1;
        foreach (string line in lines)
        {
            if (IsExactPolicyRule(line, exactKey))
            {
                if (insertionIndex < 0)
                {
                    insertionIndex = output.Count;
                }
                continue;
            }

            output.Add(line);
        }

        if (exactOverride != ConfigPolicyOverride.Default)
        {
            string rule = $"{(exactOverride == ConfigPolicyOverride.ForceServerControlled ? '+' : '-')} {exactKey}";
            if (insertionIndex >= 0)
            {
                output.Insert(insertionIndex, rule);
            }
            else
            {
                if (output.Count > 0 && output[output.Count - 1].Length != 0)
                {
                    output.Add(string.Empty);
                }
                output.Add(rule);
            }
        }

        File.WriteAllLines(SyncPolicyPath, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return exactOverride;
    }

    private static bool TryParsePolicyRule(
        string rawLine,
        out string key,
        out ConfigPolicyOverride policyOverride)
    {
        string line = StripPolicyComment(rawLine).Trim();
        if (line.Length < 2 || (line[0] != '+' && line[0] != '-'))
        {
            key = string.Empty;
            policyOverride = ConfigPolicyOverride.Default;
            return false;
        }

        key = line.Substring(1).Trim();
        if (key.Length == 0)
        {
            policyOverride = ConfigPolicyOverride.Default;
            return false;
        }

        policyOverride = line[0] == '+'
            ? ConfigPolicyOverride.ForceServerControlled
            : ConfigPolicyOverride.ForceClientControlled;
        return true;
    }

    private static bool IsExactPolicyRule(string rawLine, string exactKey)
    {
        string line = StripPolicyComment(rawLine).Trim();
        if (line.Length < 2 || (line[0] != '+' && line[0] != '-'))
        {
            return false;
        }

        return string.Equals(line.Substring(1).Trim(), exactKey, StringComparison.OrdinalIgnoreCase);
    }
}
