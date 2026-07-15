using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private short sendCount;

    private short processingCount;

    private bool lastHandledPackageWasFull;

    private bool IsSending => sendCount > 0;

    private bool IsProcessing => processingCount > 0;

    // Changes can happen while a server package is being applied. Queue them instead of dropping them.
    private readonly HashSet<ConfigEntryBase> pendingConfigBroadcasts = new();

    private readonly HashSet<CustomSyncedValueBase> pendingCustomValueBroadcasts = new();

    private readonly System.Collections.Generic.Queue<ZPackage> pendingSequencedCustomValuePackages = new();

    private bool flushingPendingBroadcasts;

    private readonly HashSet<ConfigEntryBase> configsBeingApplied = new();

    private readonly HashSet<CustomSyncedValueBase> customValuesBeingApplied = new();

    private readonly Dictionary<long, long> authoritativeCorrectionTimes = new();

    private static readonly long AuthoritativeCorrectionIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    private bool ShouldBroadcastConfigChange(OwnConfigEntryBase syncedEntry)
    {
        return GetPackageServerControlled(syncedEntry) && ShouldIncludeConfigInPackage(syncedEntry);
    }

    private bool CanBroadcastFromThisSide()
    {
        if (!GameReflection.HasZNet)
        {
            return false;
        }

        if (isServer)
        {
            return true;
        }

        return InitialSyncDone
               && (IsAdmin || !ServerLockEnabled && AllowClientConfigUpdatesWhenUnlocked);
    }

    private bool ShouldDeferOutgoingBroadcasts => ProcessingServerUpdate || IsProcessing || IsSending || flushingPendingBroadcasts;

    private void OnConfigEntryChanged(ConfigEntryBase configEntry, OwnConfigEntryBase syncedEntry)
    {
        if (configsBeingApplied.Contains(configEntry))
        {
            return;
        }

        if (!IsWritableConfig(syncedEntry))
        {
            RestoreRejectedConfigChange(configEntry, syncedEntry, GetWriteRejectionReason(syncedEntry));
            return;
        }

        syncedEntry.StoreLastAcceptedValue(configEntry.BoxedValue);

        if (!ShouldBroadcastConfigChange(syncedEntry) || !CanBroadcastFromThisSide())
        {
            DebugLog(
                ConditionalConfigSyncDebugLevel.Trace,
                "ConfigChanged",
                $"Kept local {configEntry.Definition.Section}/{configEntry.Definition.Key}: mode={syncedEntry.SyncMode}, defaultServer={syncedEntry.SynchronizedConfig}, serverControlled={syncedEntry.IsServerControlled}, canBroadcast={CanBroadcastFromThisSide()}");
            return;
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            pendingConfigBroadcasts.Add(configEntry);
            DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "ConfigChanged", $"Queued {configEntry.Definition.Section}/{configEntry.Definition.Key}, processing={IsProcessing}, sending={IsSending}");
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "ConfigChanged", $"Broadcast {configEntry.Definition.Section}/{configEntry.Definition.Key}");
        StartBroadcastPackage(
            GameReflection.Everybody,
            ConfigsToPackage(configs: new[] { configEntry }, includeConfigStates: isServer));
    }

    private void OnCustomValueChanged(CustomSyncedValueBase customValue)
    {
        if (customValuesBeingApplied.Contains(customValue))
        {
            return;
        }

        if (IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(customValue.BoxedValue);
        }

        if (!CanBroadcastFromThisSide())
        {
            if (!IsSourceOfTruth
                && customValue.HasLastAcceptedValue
                && !customValue.BoxedValuesEqual(customValue.BoxedValue, customValue.LastAcceptedValue))
            {
                customValuesBeingApplied.Add(customValue);
                try
                {
                    customValue.BoxedValue = customValue.LastAcceptedValue;
                }
                finally
                {
                    customValuesBeingApplied.Remove(customValue);
                }
                DebugWarning("CustomValue", $"Restored protected custom value {customValue.Identifier} after a rejected local change");
            }

            DebugLog(ConditionalConfigSyncDebugLevel.Trace, "CustomValue", $"Ignored {customValue.Identifier}: canBroadcast={CanBroadcastFromThisSide()}");
            return;
        }

        if (!IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(customValue.BoxedValue);
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            if (customValue.PreserveUpdateSequence)
            {
                if (TryEnqueueSequencedPackage(ConfigsToPackage(customValues: new[] { customValue }), customValue.Identifier))
                {
                    DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued sequenced {customValue.Identifier}, priority={customValue.Priority}");
                }
            }
            else
            {
                pendingCustomValueBroadcasts.Add(customValue);
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued latest-state {customValue.Identifier}, priority={customValue.Priority}");
            }
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Broadcast {customValue.Identifier}, priority={customValue.Priority}");
        StartBroadcastPackage(GameReflection.Everybody, ConfigsToPackage(customValues: new[] { customValue }));
    }

    internal static class ZNetUpdatePatch
    {
        internal static void Postfix() => DrainMainThreadQueue();
    }

    internal static class ZNetAwakePatch
    {
        internal static void Postfix(ZNet __instance)
        {
            EnsureDebugSupportInitialized();
            isServer = GameReflection.IsServer(__instance);
            sessionActive = true;
            if (isServer)
            {
                // Policy files are server-side only. Clients receive the effective state in sync packages.
                EnsurePolicySupportInitialized(createIfMissing: true);
            }
            foreach (ConditionalConfigSync configSync in configSyncs)
            {
                configSync.RegisterForActiveSession();
            }

            IEnumerator WatchAdminListChanges()
            {
                SyncedList adminList = GameReflection.GetAdminList(__instance) ?? throw new InvalidOperationException("ZNet admin list is unavailable.");
                List<string> currentList = GameReflection.GetSyncedListValues(adminList);
                for (; ; )
                {
                    yield return new WaitForSeconds(30);
                    if (!GameReflection.GetSyncedListValues(adminList).SequenceEqual(currentList))
                    {
                        currentList = GameReflection.GetSyncedListValues(adminList);

                        void SendAdmin(List<ZNetPeer> peers, bool isAdmin)
                        {
                            ZPackage package = ConfigsToPackage(packageEntries: new[]
                            {
                                PackageEntry.LockExempt(isAdmin),
                            });

                            if (configSyncs.FirstOrDefault() is { } configSync)
                            {
                                GameReflection.StartCoroutine(configSync.SendZPackage(peers, package), __instance);
                            }
                        }

                        List<ZNetPeer> peers = GameReflection.GetPeers(__instance);
                        List<ZNetPeer> adminPeer = peers.Where(IsPeerAdmin).ToList();
                        List<ZNetPeer> nonAdminPeer = peers.Except(adminPeer).ToList();
                        foreach (ConditionalConfigSync sync in configSyncs)
                        {
                            sync.DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Admin", $"Admin list changed, admins={adminPeer.Count}, nonAdmins={nonAdminPeer.Count}");
                        }
                        SendAdmin(nonAdminPeer, false);
                        SendAdmin(adminPeer, true);
                    }
                }
            }

            if (isServer)
            {
                GameReflection.StartCoroutine(WatchAdminListChanges(), __instance);
            }
        }
    }

    internal static class ZNetOnNewConnectionPatch
    {
        internal static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!GameReflection.IsServer(__instance))
            {
                foreach (ConditionalConfigSync configSync in configSyncs)
                {
                    configSync.RegisterClientRpcHandler(peer);
                }
            }
        }
    }

    private static bool IsSenderAdmin(long sender)
    {
        if (!GameReflection.HasZNet || !GameReflection.HasZRoutedRpc)
        {
            return false;
        }

        ZNetPeer? peer = GameReflection.GetRoutedPeer(sender);
        return IsPeerAdmin(peer);
    }

    private static bool IsPeerAdmin(ZNetPeer? peer)
    {
        if (!GameReflection.HasZNet || peer == null || GameReflection.GetPeerSocket(peer) == null)
        {
            return false;
        }

        string hostName = GameReflection.SocketGetHostName(GameReflection.GetPeerSocket(peer)!);
        return !string.IsNullOrEmpty(hostName) && GameReflection.IsAdmin(hostName);
    }

    private static string FormatClient(long uid)
    {
        ZNetPeer? peer = GameReflection.GetRoutedPeer(uid);
        string playerName = peer == null ? string.Empty : GameReflection.GetPeerPlayerName(peer).Trim();
        return string.IsNullOrEmpty(playerName) ? $"client {uid}" : $"client {uid} ({playerName})";
    }

    private static string FormatPeer(ZNetPeer peer)
    {
        string playerName = GameReflection.GetPeerPlayerName(peer).Trim();
        long uid = GameReflection.GetPeerUid(peer);
        return string.IsNullOrEmpty(playerName) ? $"client {uid}" : $"client {uid} ({playerName})";
    }

    // V2 packages use small length-prefixed entries. That keeps the format less fragile than the old positional layout.
    private const byte PARTIAL_CONFIGS = 1;

    private const byte FRAGMENTED_CONFIG = 2;

    private const byte COMPRESSED_CONFIG = 4;

    private const byte V2_PACKAGE = 8;

    private const int packageSliceSize = 250000;

    private const int maximumSendQueueSize = 20000;

    private const int compressMinSize = 10000;

    private const int maxPackageEntries = 8192;

    // Hard network safety limits. Oversized operations are rejected explicitly and raise SyncRejected.
    private const int maxPayloadSize = 20 * 1024 * 1024; // 20 MiB before or after compression.

    private const int maxFragments = 128;

    private const int maxFragmentSize = 300000;

    private const int maxFragmentAssembliesPerSender = 4;

    private const int maxFragmentCacheBytesPerSender = maxPayloadSize;

    private const int maxFragmentCacheBytesGlobal = 64 * 1024 * 1024;

    // Sequenced values preserve every event, so silent coalescing is not allowed. Reject newest on overflow.
    private const int maxPendingSequencedUpdates = 100;

    private readonly Dictionary<string, SortedDictionary<int, byte[]>> configValueCache = new();

    private readonly Dictionary<string, int> configValueCacheExpectedFragments = new();

    private readonly Dictionary<string, int> configValueCacheBytes = new();

    private readonly Dictionary<string, long> configValueCacheSenders = new();

    private readonly List<KeyValuePair<long, string>> cacheExpirations = new(); // avoid leaking memory

    private void RemoveFragmentAssembly(string cacheKey)
    {
        configValueCache.Remove(cacheKey);
        configValueCacheExpectedFragments.Remove(cacheKey);
        configValueCacheBytes.Remove(cacheKey);
        configValueCacheSenders.Remove(cacheKey);
        cacheExpirations.RemoveAll(kv => kv.Value == cacheKey);
    }

    private int GetFragmentCacheBytesForSender(long sender)
    {
        return configValueCacheBytes
            .Where(kv => configValueCacheSenders.TryGetValue(kv.Key, out long owner) && owner == sender)
            .Sum(kv => kv.Value);
    }

    private int GetFragmentAssemblyCountForSender(long sender)
    {
        return configValueCacheSenders.Values.Count(owner => owner == sender);
    }

    private int GetFragmentCacheBytesGlobal() => configValueCacheBytes.Values.Sum();

    private bool TryEnqueueSequencedPackage(ZPackage package, string identifier)
    {
        int size = GameReflection.PackageSize(package);
        if (size > maxPayloadSize)
        {
            RejectSync($"Rejected sequenced custom value '{identifier}': serialized payload is {size} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
            return false;
        }

        if (pendingSequencedCustomValuePackages.Count >= maxPendingSequencedUpdates)
        {
            RejectSync($"Rejected newest sequenced custom value '{identifier}': pending queue already contains {maxPendingSequencedUpdates} events.", null, incoming: false);
            return false;
        }

        pendingSequencedCustomValuePackages.Enqueue(package);
        return true;
    }

    private bool ValidateOutgoingPayload(ZPackage package, string context)
    {
        int size = GameReflection.PackageSize(package);
        if (size <= maxPayloadSize)
        {
            return true;
        }

        RejectSync($"Rejected outgoing {context}: serialized payload is {size} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
        return false;
    }

    private void RPC_FromServerConfigSync(ZRpc rpc, ZPackage package)
    {
        LockedConfigChanged -= ServerLockedSettingChanged;
        LockedConfigChanged += ServerLockedSettingChanged;
        IsSourceOfTruth = false;

        if (HandleConfigSyncRPC(0, package, false) && lastHandledPackageWasFull)
        {
            RaiseInitialSyncCompletedIfNeeded();
        }
    }

    private void RPC_FromOtherClientConfigSync(long sender, ZPackage package) => HandleConfigSyncRPC(sender, package, true);

    private bool HandleConfigSyncRPC(long sender, ZPackage package, bool clientUpdate)
    {
        bool receivedFromServer = !isServer && !clientUpdate;
        string? activeFragmentCacheKey = null;
        lastHandledPackageWasFull = false;
        bool processingStarted = false;
        ParsedConfigs? parsedClientUpdate = null;

        try
        {
            bool senderIsAdmin = !isServer || !clientUpdate || IsSenderAdmin(sender);

            foreach (string expiredKey in cacheExpirations.Where(kv => kv.Key < DateTimeOffset.Now.Ticks).Select(kv => kv.Value).Distinct().ToArray())
            {
                RemoveFragmentAssembly(expiredKey);
            }

            byte packageFlags = GameReflection.PackageReadByte(package);
            string senderDescription = receivedFromServer ? "server" : FormatClient(sender);
            int receivedPackageSize = GameReflection.PackageSize(package);
            DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Receive", $"Start package from {senderDescription}, flags={packageFlags}, size={receivedPackageSize}");

            if ((packageFlags & FRAGMENTED_CONFIG) == 0 && receivedPackageSize > maxPayloadSize)
            {
                RejectSync($"Package from {senderDescription} is too large: {receivedPackageSize} bytes, limit is {maxPayloadSize} bytes.", receivedFromServer ? null : sender, incoming: true);
                return false;
            }

            if ((packageFlags & FRAGMENTED_CONFIG) != 0)
            {
                long uniqueIdentifier = GameReflection.PackageReadLong(package);
                string cacheKey = sender + ":" + uniqueIdentifier;
                activeFragmentCacheKey = cacheKey;

                int fragment = GameReflection.PackageReadInt(package);
                int fragments = GameReflection.PackageReadInt(package);
                byte[] fragmentData = GameReflection.PackageReadByteArray(package, maxFragmentSize);

                if (fragments <= 0 || fragments > maxFragments || fragment < 0 || fragment >= fragments || fragmentData.Length > maxFragmentSize)
                {
                    RejectSync($"Invalid fragmented package from {senderDescription}: fragment {fragment}/{fragments}, size={fragmentData.Length}.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                if (!configValueCache.TryGetValue(cacheKey, out SortedDictionary<int, byte[]> dataFragments))
                {
                    if (GetFragmentAssemblyCountForSender(sender) >= maxFragmentAssembliesPerSender)
                    {
                        RejectSync($"Rejected fragmented package from {senderDescription}: more than {maxFragmentAssembliesPerSender} incomplete packages are already cached for this sender.", receivedFromServer ? null : sender, incoming: true);
                        return false;
                    }

                    if (GetFragmentCacheBytesForSender(sender) + fragmentData.Length > maxFragmentCacheBytesPerSender
                        || GetFragmentCacheBytesGlobal() + fragmentData.Length > maxFragmentCacheBytesGlobal)
                    {
                        RejectSync($"Rejected fragmented package from {senderDescription}: fragment cache memory limit would be exceeded.", receivedFromServer ? null : sender, incoming: true);
                        return false;
                    }

                    dataFragments = new SortedDictionary<int, byte[]>();
                    configValueCache[cacheKey] = dataFragments;
                    configValueCacheExpectedFragments[cacheKey] = fragments;
                    configValueCacheBytes[cacheKey] = 0;
                    configValueCacheSenders[cacheKey] = sender;
                    cacheExpirations.Add(new KeyValuePair<long, string>(DateTimeOffset.Now.AddSeconds(60).Ticks, cacheKey));
                }
                else if (!configValueCacheExpectedFragments.TryGetValue(cacheKey, out int expectedFragments) || expectedFragments != fragments)
                {
                    RemoveFragmentAssembly(cacheKey);
                    RejectSync($"Rejected fragmented package from {senderDescription}: fragment count changed while assembling the package.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                if (dataFragments.ContainsKey(fragment))
                {
                    RemoveFragmentAssembly(cacheKey);
                    RejectSync($"Duplicate package fragment {fragment}/{fragments} from {senderDescription}; the incomplete package was discarded.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                int senderBytes = GetFragmentCacheBytesForSender(sender);
                int globalBytes = GetFragmentCacheBytesGlobal();
                int assemblyBytes = configValueCacheBytes[cacheKey];
                if (assemblyBytes + fragmentData.Length > maxPayloadSize
                    || senderBytes + fragmentData.Length > maxFragmentCacheBytesPerSender
                    || globalBytes + fragmentData.Length > maxFragmentCacheBytesGlobal)
                {
                    RemoveFragmentAssembly(cacheKey);
                    RejectSync($"Rejected fragmented package from {senderDescription}: fragment cache or {maxPayloadSize}-byte payload limit was exceeded.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                dataFragments[fragment] = fragmentData;
                configValueCacheBytes[cacheKey] = assemblyBytes + fragmentData.Length;

                if (dataFragments.Count < fragments)
                {
                    return false;
                }

                int combinedSize = configValueCacheBytes[cacheKey];
                RemoveFragmentAssembly(cacheKey);
                if (combinedSize > maxPayloadSize)
                {
                    RejectSync($"Fragmented package from {senderDescription} is too large: {combinedSize} bytes, limit is {maxPayloadSize} bytes.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                byte[] combined = new byte[combinedSize];
                int offset = 0;
                foreach (byte[] data in dataFragments.Values)
                {
                    Buffer.BlockCopy(data, 0, combined, offset, data.Length);
                    offset += data.Length;
                }

                package = GameReflection.NewPackage(combined);
                packageFlags = GameReflection.PackageReadByte(package);
            }

            ProcessingServerUpdate = true;
            ++processingCount;
            processingStarted = true;

            if ((packageFlags & COMPRESSED_CONFIG) != 0)
            {
                byte[] data = GameReflection.PackageReadByteArray(package, maxPayloadSize);
                if (data.Length > maxPayloadSize)
                {
                    RejectSync($"Compressed package from {senderDescription} is too large: {data.Length} bytes, limit is {maxPayloadSize} bytes.", receivedFromServer ? null : sender, incoming: true);
                    return false;
                }

                package = GameReflection.NewPackage(DecompressLimited(data, maxPayloadSize));
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Network", $"Decompressed package: compressed={data.Length}, raw={GameReflection.PackageSize(package)}");
                packageFlags = GameReflection.PackageReadByte(package);
            }

            if ((packageFlags & V2_PACKAGE) == 0)
            {
                RejectSync("Received an unsupported package format. Client and server must use the same ConditionalConfigSync version.", receivedFromServer ? null : sender, incoming: true);
                return false;
            }

            byte allowedFlags = (byte)(V2_PACKAGE | PARTIAL_CONFIGS);
            if (isServer && clientUpdate && ((packageFlags & PARTIAL_CONFIGS) == 0 || (packageFlags & ~allowedFlags) != 0))
            {
                RejectClientUpdate(
                    sender,
                    senderIsAdmin,
                    $"client updates must be partial packages without server transport flags; received flags={packageFlags}",
                    null);
                return false;
            }

            GameReflection.PackageSetPos(package, 0);
            packageFlags = GameReflection.PackageReadByte(package);

            lastHandledPackageWasFull = (packageFlags & PARTIAL_CONFIGS) == 0;
            if (lastHandledPackageWasFull && receivedFromServer)
            {
                ResetConfigsFromServer();
            }

            ParsedConfigs configs = ReadConfigsFromPackage(package, receivedFromServer, strictClientUpdate: isServer && clientUpdate);
            if (isServer && clientUpdate)
            {
                parsedClientUpdate = configs;
                if (!TryAuthorizeClientUpdate(configs, sender, senderIsAdmin, out string rejectionReason))
                {
                    RejectClientUpdate(sender, senderIsAdmin, rejectionReason, configs);
                    return false;
                }
            }

            ApplyParsedConfigs(configs, receivedFromServer);

            string source = isServer || clientUpdate ? FormatClient(sender) : "the server";
            InfoLog($"Received {configs.configValues.Count} configs and {configs.customValues.Count} custom values from {source}{GetSingleEntryReceiveDetails(configs)}");

            if (isServer && clientUpdate)
            {
                string authorization = senderIsAdmin ? "administrator" : "configuration is unlocked and client updates are enabled";
                LogAcceptedClientUpdate(sender, configs, authorization);

                ZPackage canonicalPackage = ConfigsToPackage(
                    configs.configValues.Keys.Select(config => config.BaseConfig),
                    configs.customValues.Keys,
                    partial: true,
                    includeConfigValues: true,
                    includeAllProvidedConfigStates: true,
                    includeConfigStates: true);
                StartBroadcastPackage(GameReflection.Everybody, canonicalPackage);
            }

            return true;
        }
        catch (Exception e)
        {
            if (activeFragmentCacheKey != null)
            {
                RemoveFragmentAssembly(activeFragmentCacheKey);
            }

            if (isServer && clientUpdate)
            {
                bool senderIsAdmin = IsSenderAdmin(sender);
                RejectClientUpdate(sender, senderIsAdmin, $"malformed or unprocessable package: {e.Message}", parsedClientUpdate, e);
            }
            else
            {
                RejectSync($"Error while applying config package: {e.Message}", receivedFromServer ? null : sender, incoming: true, e);
            }
            return false;
        }
        finally
        {
            if (processingStarted)
            {
                if (processingCount > 0)
                {
                    --processingCount;
                }
                ProcessingServerUpdate = false;
                FlushPendingBroadcastsIfIdle();
            }
        }
    }

    private bool TryAuthorizeClientUpdate(ParsedConfigs configs, long sender, bool senderIsAdmin, out string rejectionReason)
    {
        ZNetPeer? senderPeer = GameReflection.GetRoutedPeer(sender);
        if (senderPeer == null || !GameReflection.IsPeerReady(senderPeer))
        {
            rejectionReason = "the sender is not a ready connected peer";
            return false;
        }

        if (!string.IsNullOrEmpty(configs.rejectionReason))
        {
            rejectionReason = configs.rejectionReason!;
            return false;
        }

        if (configs.entryCount <= 0 || configs.configValues.Count == 0 && configs.customValues.Count == 0)
        {
            rejectionReason = "client update contains no applicable values";
            return false;
        }

        foreach (OwnConfigEntryBase config in configs.configValues.Keys)
        {
            ConfigDefinition definition = config.BaseConfig.Definition;
            if (config == lockedConfig && !senderIsAdmin)
            {
                rejectionReason = $"protected locking config {definition.Section} -> {definition.Key} requires administrator access";
                return false;
            }

            if (!ComputeServerControlled(config))
            {
                rejectionReason = $"config {definition.Section} -> {definition.Key} is client-controlled by the server's effective policy";
                return false;
            }
        }

        if (senderIsAdmin)
        {
            rejectionReason = string.Empty;
            return true;
        }

        if (ServerLockEnabled)
        {
            rejectionReason = "the server configuration is locked";
            return false;
        }

        if (!AllowClientConfigUpdatesWhenUnlocked)
        {
            rejectionReason = "the mod does not allow non-admin client updates while the configuration is unlocked";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private void RejectClientUpdate(long sender, bool senderIsAdmin, string reason, ParsedConfigs? configs, Exception? exception = null)
    {
        string state = $"admin={senderIsAdmin}, serverLock={ServerLockEnabled}, unlockedClientUpdates={AllowClientConfigUpdatesWhenUnlocked}";
        RejectSync($"Rejected config update from {FormatClient(sender)}: {reason} ({state}).", sender, incoming: true, exception);

        if (configs != null)
        {
            foreach (OwnConfigEntryBase config in configs.configValues.Keys)
            {
                ConfigDefinition definition = config.BaseConfig.Definition;
                DebugWarning("ConfigUpdate", $"Rejected {definition.Section} -> {definition.Key} from {FormatClient(sender)}: {reason} ({state})");
            }
            foreach (CustomSyncedValueBase customValue in configs.customValues.Keys)
            {
                DebugWarning("ConfigUpdate", $"Rejected custom value {customValue.Identifier} from {FormatClient(sender)}: {reason} ({state})");
            }
        }

        SendAuthoritativeCorrection(sender, configs);
    }

    private void LogAcceptedClientUpdate(long sender, ParsedConfigs configs, string authorization)
    {
        foreach (OwnConfigEntryBase config in configs.configValues.Keys)
        {
            ConfigDefinition definition = config.BaseConfig.Definition;
            LogSource.LogInfo($"[{GetDebugModName()}][Server][ConfigUpdate] Accepted {definition.Section} -> {definition.Key} from {FormatClient(sender)}: {authorization}");
        }
        foreach (CustomSyncedValueBase customValue in configs.customValues.Keys)
        {
            LogSource.LogInfo($"[{GetDebugModName()}][Server][ConfigUpdate] Accepted custom value {customValue.Identifier} from {FormatClient(sender)}: {authorization}");
        }
    }

    private void SendAuthoritativeCorrection(long sender, ParsedConfigs? configs)
    {
        long now = DateTimeOffset.UtcNow.Ticks;
        if (authoritativeCorrectionTimes.TryGetValue(sender, out long previous)
            && now - previous < AuthoritativeCorrectionIntervalTicks)
        {
            return;
        }

        ZNetPeer? peer = GameReflection.GetRoutedPeer(sender);
        if (peer == null || !GameReflection.IsPeerReady(peer))
        {
            return;
        }

        authoritativeCorrectionTimes[sender] = now;

        bool requireFullSync = configs == null
                               || !string.IsNullOrEmpty(configs.rejectionReason)
                               || configs.configValues.Count == 0 && configs.customValues.Count == 0;
        ZPackage correction = requireFullSync
            ? CreateFullSyncPackage(peer)
            : ConfigsToPackage(
                configs.configValues.Keys.Select(config => config.BaseConfig),
                configs.customValues.Keys,
                partial: true,
                includeConfigValues: true,
                includeAllProvidedConfigStates: true,
                includeConfigStates: true);
        StartBroadcastPackage(new List<ZNetPeer> { peer }, correction);
    }

    internal static class ZNetShutdownPatch
    {
        internal static void Postfix()
        {
            ConditionalConfigSync[] instances = configSyncs.ToArray();
            ProcessingServerUpdate = true;
            lockExempt = false;
            try
            {
                foreach (ConditionalConfigSync serverSync in instances)
                {
                    try
                    {
                        serverSync.DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Shutdown", "Reset local values and source-of-truth state");
                        serverSync.ResetConfigsFromServer();
                        serverSync.IsSourceOfTruth = true;
                        serverSync.InitialSyncDone = false;
                        serverSync.pendingConfigBroadcasts.Clear();
                        serverSync.pendingCustomValueBroadcasts.Clear();
                        serverSync.pendingSequencedCustomValuePackages.Clear();
                        foreach (string cacheKey in serverSync.configValueCache.Keys.ToArray())
                        {
                            serverSync.RemoveFragmentAssembly(cacheKey);
                        }
                    }
                    catch (Exception e)
                    {
                        serverSync.DebugWarning("Shutdown", $"Failed to reset one synchronization instance; continuing. Error: {e}");
                    }
                }
            }
            finally
            {
                ProcessingServerUpdate = false;
            }

            // Subscribers see the fully restored local state and are free to rebuild local caches.
            foreach (ConditionalConfigSync serverSync in instances)
            {
                serverSync.InvokeEventHandlers(serverSync.ServerConnectionReset, nameof(ServerConnectionReset));
            }

            ResetNetworkSessionState();
            isServer = false;
        }
    }

    private static long packageCounter = 0;

    private IEnumerator<bool> DistributeConfigToPeer(ZNetPeer peer, ZPackage package)
    {
        if (!GameReflection.HasZRoutedRpc)
        {
            yield break;
        }

        IEnumerable<bool> waitForQueue()
        {
            float timeout = Time.time + 30;
            while (GameReflection.GetPeerSocket(peer) is { } peerSocket && GameReflection.SocketGetSendQueueSize(peerSocket) > maximumSendQueueSize)
            {
                if (Time.time > timeout)
                {
                    DebugWarning("Network", $"Disconnecting {FormatPeer(peer)} after 30 seconds config sending timeout");
                    GameReflection.InvokeRpc(GameReflection.GetPeerRpc(peer), "Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                    GameReflection.Disconnect(peer);
                    yield break;
                }

                yield return false;
            }
        }

        void SendPackage(ZPackage pkg)
        {
            string method = Name + " ConditionalConfigSync";
            if (isServer)
            {
                GameReflection.InvokeRpc(GameReflection.GetPeerRpc(peer), method, pkg);
            }
            else
            {
                // Send directly to the server peer. Target 0 means everybody and also invokes the RPC locally.
                GameReflection.InvokeRoutedPackage(method, pkg);
            }
        }

        if (GameReflection.PackageSize(package) > packageSliceSize)
        {
            DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Network", $"Fragmenting package for {FormatPeer(peer)}, size={GameReflection.PackageSize(package)}, slice={packageSliceSize}");
            ArraySegment<byte> data = GetPackageArraySegment(package);
            int len = GameReflection.PackageSize(package);
            int fragments = (len + packageSliceSize - 1) / packageSliceSize;
            long packageIdentifier = ++packageCounter;

            for (int fragment = 0; fragment < fragments; ++fragment)
            {
                foreach (bool wait in waitForQueue())
                {
                    yield return wait;
                }
                if (GameReflection.GetPeerSocket(peer) is not { } connectedSocket || !GameReflection.SocketIsConnected(connectedSocket))
                {
                    yield break;
                }

                int offset = fragment * packageSliceSize;
                int count = Math.Min(packageSliceSize, len - offset);

                ZPackage fragmentedPackage = GameReflection.NewPackage();
                GameReflection.PackageWrite(fragmentedPackage, FRAGMENTED_CONFIG);
                GameReflection.PackageWrite(fragmentedPackage, packageIdentifier);
                GameReflection.PackageWrite(fragmentedPackage, fragment);
                GameReflection.PackageWrite(fragmentedPackage, fragments);
                WriteByteArray(fragmentedPackage, new ArraySegment<byte>(data.Array!, data.Offset + offset, count));
                SendPackage(fragmentedPackage);
                DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Network", $"Sent fragment {fragment + 1}/{fragments} to {FormatPeer(peer)}, size={count}");

                if (fragment != fragments - 1)
                {
                    yield return true;
                }
            }
        }
        else
        {
            foreach (bool wait in waitForQueue())
            {
                yield return wait;
            }

            SendPackage(package);
        }
    }

    private IEnumerator SendZPackage(long target, ZPackage package)
    {
        if (!GameReflection.HasZNet)
        {
            yield break;
        }

        List<ZNetPeer> peers = GameReflection.GetRoutedPeers();
        if (target != GameReflection.Everybody)
        {
            peers = peers.Where(p => GameReflection.GetPeerUid(p) == target).ToList();
        }

        yield return SendZPackage(peers, package);
    }

    private IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
    {
        if (!GameReflection.HasZNet || peers.Count == 0)
        {
            yield break;
        }

        try
        {
            ++sendCount;
            int rawSize = GameReflection.PackageSize(package);
            if (rawSize > maxPayloadSize)
            {
                RejectSync($"Rejected outgoing synchronization package: serialized payload is {rawSize} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                yield break;
            }

            if (rawSize > compressMinSize)
            {
                package = CompressPackage(package);
                int compressedSize = GameReflection.PackageSize(package);
                if (compressedSize > maxPayloadSize)
                {
                    RejectSync($"Rejected outgoing compressed package: payload is {compressedSize} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                    yield break;
                }
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Network", $"Compressed outgoing package: raw={rawSize}, compressed={compressedSize}, peers={peers.Count}");
            }
            else
            {
                DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Network", $"Sending package: size={rawSize}, peers={peers.Count}");
            }

            List<IEnumerator<bool>> writers = peers.Where(GameReflection.IsPeerReady).Select(p => DistributeConfigToPeer(p, package)).ToList();
            writers.RemoveAll(writer => !writer.MoveNext());
            while (writers.Count > 0)
            {
                yield return null;
                writers.RemoveAll(writer => !writer.MoveNext());
            }
        }
        finally
        {
            if (sendCount > 0)
            {
                --sendCount;
            }
            FlushPendingBroadcastsIfIdle();
        }
    }

    private static ZPackage CompressPackage(ZPackage package)
    {
        ArraySegment<byte> rawData = GetPackageArraySegment(package);
        ZPackage compressedPackage = GameReflection.NewPackage();
        GameReflection.PackageWrite(compressedPackage, COMPRESSED_CONFIG);
        using MemoryStream output = new();
        using (DeflateStream deflateStream = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflateStream.Write(rawData.Array!, rawData.Offset, GameReflection.PackageSize(package));
        }
        GameReflection.PackageWrite(compressedPackage, output.ToArray());
        return compressedPackage;
    }

    internal static class ZNetRpcPeerInfoSyncPatch
    {
        internal class BufferingSocket : ZPlayFabSocket, ISocket
        {
            public volatile bool finished = false;
            public volatile int versionMatchQueued = -1;
            public readonly List<ZPackage> Package = new();
            public readonly ISocket Original;

            internal BufferingSocket(ISocket original)
            {
                Original = original;
            }

            public new bool IsConnected() => GameReflection.SocketIsConnected(Original);
            public new ZPackage Recv() => GameReflection.SocketRecv(Original)!;
            public new int GetSendQueueSize() => GameReflection.SocketGetSendQueueSize(Original);
            public new int GetCurrentSendRate() => GameReflection.SocketGetCurrentSendRate(Original);
            public new bool IsHost() => GameReflection.SocketIsHost(Original);
            public new void Dispose() => GameReflection.SocketDispose(Original);
            public new bool GotNewData() => GameReflection.SocketGotNewData(Original);
            public new void Close() => GameReflection.SocketClose(Original);
            public new string GetEndPointString() => GameReflection.SocketGetEndPointString(Original);
            public new void GetAndResetStats(out int totalSent, out int totalRecv) => GameReflection.SocketGetAndResetStats(Original, out totalSent, out totalRecv);
            public new void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) => GameReflection.SocketGetConnectionQuality(Original, out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
            public new ISocket Accept() => GameReflection.SocketAccept(Original)!;
            public new int GetHostPort() => GameReflection.SocketGetHostPort(Original);
            public new bool Flush() => GameReflection.SocketFlush(Original);
            public new string GetHostName() => GameReflection.SocketGetHostName(Original);

            public new void VersionMatch()
            {
                if (finished)
                {
                    GameReflection.SocketVersionMatch(Original);
                }
                else
                {
                    versionMatchQueued = Package.Count;
                }
            }

            public new void Send(ZPackage pkg)
            {
                int oldPos = GameReflection.PackageGetPos(pkg);
                GameReflection.PackageSetPos(pkg, 0);
                int methodHash = GameReflection.PackageReadInt(pkg);
                if ((methodHash == GameReflection.StableHash("PeerInfo") || methodHash == GameReflection.StableHash("RoutedRPC") || methodHash == GameReflection.StableHash("ZDOData")) && !finished)
                {
                    ZPackage newPkg = GameReflection.NewPackage(GameReflection.PackageGetArray(pkg));
                    GameReflection.PackageSetPos(newPkg, oldPos);
                    Package.Add(newPkg); // the original ZPackage gets reused, create a new one
                }
                else
                {
                    GameReflection.PackageSetPos(pkg, oldPos);
                    GameReflection.SocketSend(Original, pkg);
                }
            }
        }
        internal static void Prefix(ref BufferingSocket? __state, ZNet __instance, ZRpc rpc)
        {
            if (GameReflection.IsServer(__instance))
            {
                BufferingSocket bufferingSocket = new(GameReflection.GetRpcSocket(rpc) ?? throw new InvalidOperationException("RPC socket is unavailable."));
                GameReflection.SetRpcSocket(rpc, bufferingSocket);
                // Don't replace on steam sockets, RPC_PeerInfo does peer.m_socket as ZSteamSocket - which will cause a nullref when replaced
                if (GameReflection.GetPeer(rpc, __instance) is ZNetPeer peer && !Equals(GameReflection.GetOnlineBackend(), OnlineBackendType.Steamworks))
                {
                    if (GameReflection.GetPeerSocket(peer) is ZPlayFabSocket playFabSocket)
                    {
                        GameReflection.SetPlayFabRemotePlayerId(bufferingSocket, GameReflection.GetPlayFabRemotePlayerId(playFabSocket));
                    }
                    GameReflection.SetPeerSocket(peer, bufferingSocket);
                }

                __state = bufferingSocket;
            }
        }
        internal static void Postfix(BufferingSocket? __state, ZNet __instance, ZRpc rpc)
        {
            if (!GameReflection.IsServer(__instance) || __state == null)
            {
                return;
            }

            void SendBufferedData()
            {
                BufferingSocket bufferingSocket;
                if (GameReflection.GetRpcSocket(rpc) is BufferingSocket currentBufferingSocket)
                {
                    GameReflection.SetRpcSocket(rpc, currentBufferingSocket.Original);
                    if (GameReflection.GetPeer(rpc, __instance) is ZNetPeer currentPeer)
                    {
                        GameReflection.SetPeerSocket(currentPeer, currentBufferingSocket.Original);
                    }
                    bufferingSocket = currentBufferingSocket;
                }
                else
                {
                    bufferingSocket = __state;
                }

                if (bufferingSocket.finished)
                {
                    return;
                }

                bufferingSocket.finished = true;

                for (int i = 0; i < bufferingSocket.Package.Count; ++i)
                {
                    if (i == bufferingSocket.versionMatchQueued)
                    {
                        GameReflection.SocketVersionMatch(bufferingSocket.Original);
                    }
                    GameReflection.SocketSend(bufferingSocket.Original, bufferingSocket.Package[i]);
                }
                if (bufferingSocket.Package.Count == bufferingSocket.versionMatchQueued)
                {
                    GameReflection.SocketVersionMatch(bufferingSocket.Original);
                }
            }

            if (GameReflection.GetPeer(rpc, __instance) is not ZNetPeer peer)
            {
                SendBufferedData();
                return;
            }

            IEnumerator sendAsync()
            {
                try
                {
                    foreach (ConditionalConfigSync configSync in configSyncs)
                    {
                        ZPackage package = configSync.CreateFullSyncPackage(peer);
                        configSync.DebugLog(ConditionalConfigSyncDebugLevel.Basic, "InitialSync", $"Sending full sync to {FormatPeer(peer)}, admin={IsPeerAdmin(peer)}, configs={configSync.allConfigs.Count}, custom={configSync.allCustomValues.Count}, size={GameReflection.PackageSize(package)}");

                        yield return GameReflection.StartCoroutine(configSync.SendZPackage(new List<ZNetPeer> { peer }, package), __instance);
                    }
                }
                finally
                {
                    SendBufferedData();
                }
            }

            GameReflection.StartCoroutine(sendAsync(), __instance);
        }
    }

    private void Broadcast(long target, params ConfigEntryBase[] configs)
    {
        if (!CanBroadcastFromThisSide())
        {
            DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Broadcast", $"Ignored config broadcast, target={target}, locked={IsLocked}, server={isServer}");
            return;
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            foreach (ConfigEntryBase config in configs)
            {
                if (GetConfigData(config) is { } data && ShouldBroadcastConfigChange(data))
                {
                    pendingConfigBroadcasts.Add(config);
                }
            }
            return;
        }

        StartBroadcastPackage(target, ConfigsToPackage(configs: configs, includeConfigStates: isServer));
    }

    private void Broadcast(long target, params CustomSyncedValueBase[] customValues)
    {
        if (!CanBroadcastFromThisSide())
        {
            DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Broadcast", $"Ignored custom broadcast, target={target}, locked={IsLocked}, server={isServer}");
            return;
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            foreach (CustomSyncedValueBase customValue in customValues)
            {
                if (customValue.PreserveUpdateSequence)
                {
                    TryEnqueueSequencedPackage(ConfigsToPackage(customValues: new[] { customValue }), customValue.Identifier);
                }
                else
                {
                    pendingCustomValueBroadcasts.Add(customValue);
                }
            }
            return;
        }

        StartBroadcastPackage(target, ConfigsToPackage(customValues: customValues));
    }

    private void StartBroadcastPackage(long target, ZPackage package)
    {
        if (ValidateOutgoingPayload(package, "synchronization package"))
        {
            GameReflection.StartCoroutine(SendZPackage(target, package));
        }
    }

    private void StartBroadcastPackage(List<ZNetPeer> peers, ZPackage package)
    {
        if (ValidateOutgoingPayload(package, "synchronization package"))
        {
            GameReflection.StartCoroutine(SendZPackage(peers, package));
        }
    }

    private void FlushPendingBroadcastsIfIdle()
    {
        if (ProcessingServerUpdate || IsProcessing || IsSending || flushingPendingBroadcasts || !GameReflection.HasZNet || !CanBroadcastFromThisSide())
        {
            return;
        }

        if (pendingSequencedCustomValuePackages.Count == 0 && pendingConfigBroadcasts.Count == 0 && pendingCustomValueBroadcasts.Count == 0)
        {
            return;
        }

        flushingPendingBroadcasts = true;
        DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Pending", $"Flushing configs={pendingConfigBroadcasts.Count}, custom={pendingCustomValueBroadcasts.Count}, sequenced={pendingSequencedCustomValuePackages.Count}");
        try
        {
            while (pendingSequencedCustomValuePackages.Count > 0)
            {
                StartBroadcastPackage(GameReflection.Everybody, pendingSequencedCustomValuePackages.Dequeue());
            }

            if (pendingConfigBroadcasts.Count > 0)
            {
                ConfigEntryBase[] configs = pendingConfigBroadcasts.Where(config => GetConfigData(config) is { } data && ShouldBroadcastConfigChange(data)).ToArray();
                pendingConfigBroadcasts.Clear();
                if (configs.Length > 0)
                {
                    StartBroadcastPackage(GameReflection.Everybody, ConfigsToPackage(configs: configs, includeConfigStates: isServer));
                }
            }

            if (pendingCustomValueBroadcasts.Count > 0)
            {
                CustomSyncedValueBase[] values = pendingCustomValueBroadcasts.OrderByDescending(v => v.Priority).ThenBy(v => v.RegistrationIndex).ToArray();
                pendingCustomValueBroadcasts.Clear();
                if (values.Length > 0)
                {
                    StartBroadcastPackage(GameReflection.Everybody, ConfigsToPackage(customValues: values));
                }
            }
        }
        finally
        {
            flushingPendingBroadcasts = false;
        }
    }
}
