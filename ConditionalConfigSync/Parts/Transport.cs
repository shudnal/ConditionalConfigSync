using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private int processingCount;

    private bool lastHandledPackageWasFull;

    private bool IsSending => sendQueue.Count > 0;

    private bool IsProcessing => processingCount > 0;

    // Changes can happen while a server package is being applied. Queue them instead of dropping them.
    private readonly HashSet<ConfigEntryBase> pendingConfigBroadcasts = new();

    private readonly HashSet<CustomSyncedValueBase> pendingCustomValueBroadcasts = new();

    private sealed class PendingSequencedPackage
    {
        internal readonly SendSlot Reservation;
        internal ZPackage? Package;

        internal PendingSequencedPackage(SendSlot reservation)
        {
            Reservation = reservation;
        }
    }

    private readonly LinkedList<PendingSequencedPackage> pendingSequencedCustomValuePackages = new();

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
        if (!sessionActive || !GameReflection.HasZNet)
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

    private bool ShouldDeferOutgoingBroadcasts => ProcessingServerUpdate || packagePreparationDepth > 0
        || IsProcessing || IsSending || flushingPendingBroadcasts;

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
        if (isServer && IsSourceOfTruth && ShouldBroadcastConfigChange(syncedEntry))
        {
            InvalidateFullSyncSnapshot($"config changed: {configEntry.Definition.Section} -> {configEntry.Definition.Key}");
        }

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
            QueuePendingConfigBroadcast(configEntry);
            DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "ConfigChanged", $"Queued {configEntry.Definition.Section}/{configEntry.Definition.Key}, processing={IsProcessing}, sending={IsSending}");
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "ConfigChanged", $"Broadcast {configEntry.Definition.Section}/{configEntry.Definition.Key}");
        StartBroadcastPackage(
            GameReflection.Everybody,
            () => ConfigsToPackage(configs: new[] { configEntry }, includeConfigStates: isServer));
    }

    internal void PublishCustomValueChange(CustomSyncedValueBase customValue, object? publicationValue)
    {
        if (customValuesBeingApplied.Contains(customValue))
        {
            return;
        }

        if (IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(publicationValue);
            if (isServer)
            {
                InvalidateFullSyncSnapshot($"custom value changed: {customValue.Identifier}");
            }
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
            customValue.StoreLastAcceptedValue(publicationValue);
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            if (customValue.PreserveUpdateSequence)
            {
                if (TryEnqueueSequencedPackage(
                    () => ConfigsToPackage(packageEntries: new[] { PackageEntry.CustomValue(customValue, publicationValue) }),
                    customValue.Identifier))
                {
                    DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued sequenced {customValue.Identifier}, priority={customValue.Priority}");
                }
                FlushPendingBroadcastsForAllIfIdle();
            }
            else
            {
                QueuePendingCustomValueBroadcast(customValue);
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued latest-state {customValue.Identifier}, priority={customValue.Priority}");
            }
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Broadcast {customValue.Identifier}, priority={customValue.Priority}");
        StartBroadcastPackage(
            GameReflection.Everybody,
            () => ConfigsToPackage(packageEntries: new[] { PackageEntry.CustomValue(customValue, publicationValue) }),
            customValue.PreserveUpdateSequence);
    }

    internal static class ZNetUpdatePatch
    {
        internal static void Postfix()
        {
            DrainMainThreadQueue();
            FlushPendingBroadcastsForAllIfIdle();
        }
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
            foreach (ConditionalConfigSync configSync in configSyncs.ToArray())
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
                    if (!sessionActive || !ReferenceEquals(GameReflection.ZNetInstance, __instance))
                    {
                        yield break;
                    }
                    if (!GameReflection.GetSyncedListValues(adminList).SequenceEqual(currentList))
                    {
                        currentList = GameReflection.GetSyncedListValues(adminList);

                        void SendAdmin(List<ZNetPeer> peers, bool isAdmin)
                        {
                            // An optional first consumer may not exist on this client. Every registered
                            // consumer carries the same process-wide exemption through its own RPC.
                            foreach (ConditionalConfigSync configSync in configSyncs.ToArray())
                            {
                                configSync.StartBroadcastPackage(peers, () => configSync.ConfigsToPackage(packageEntries: new[]
                                {
                                    PackageEntry.LockExempt(isAdmin),
                                }));
                            }
                        }

                        List<ZNetPeer> peers = GameReflection.GetPeers(__instance);
                        List<ZNetPeer> adminPeer = peers.Where(IsPeerAdmin).ToList();
                        List<ZNetPeer> nonAdminPeer = peers.Except(adminPeer).ToList();
                        foreach (ConditionalConfigSync sync in configSyncs.ToArray())
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
                foreach (ConditionalConfigSync configSync in configSyncs.ToArray())
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

    private bool TryEnqueueSequencedPackage(Func<ZPackage> createPackage, string identifier)
    {
        if (waitingSequencedSendCount >= maxPendingSequencedUpdates)
        {
            RejectSync($"Rejected newest sequenced custom value '{identifier}': pending queue already contains {maxPendingSequencedUpdates} events.", null, incoming: false);
            return false;
        }

        // Reserve in the shared FIFO now, not at flush time. An earlier state marker stays ahead
        // of this event, and reentrant notifications remain behind it during serialization.
        SendSlot? reservation = ReserveSendSlot(sequenced: true);
        if (reservation == null)
        {
            return false;
        }
        LinkedListNode<PendingSequencedPackage> node = pendingSequencedCustomValuePackages.AddLast(new PendingSequencedPackage(reservation));
        ++packagePreparationDepth;
        try
        {
            ZPackage package = createPackage();
            int size = GameReflection.PackageSize(package);
            if (!IsCurrentSend(reservation))
            {
                return false;
            }
            if (size > maxPayloadSize)
            {
                RejectSync($"Rejected sequenced custom value '{identifier}': serialized payload is {size} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                return false;
            }

            node.Value.Package = package;
            return true;
        }
        catch (Exception e)
        {
            RejectSync($"Failed to serialize sequenced custom value '{identifier}': {e.Message}", null, incoming: false, e);
            return false;
        }
        finally
        {
            --packagePreparationDepth;
            if (node.Value.Package == null)
            {
                if (node.List == pendingSequencedCustomValuePackages)
                {
                    pendingSequencedCustomValuePackages.Remove(node);
                }
                ReleaseSendSlot(reservation);
            }
        }
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

    private void QueueInitialSyncRepair()
    {
        if (InitialSyncDone || initialSyncRepairRequested || !sessionActive || isServer)
        {
            return;
        }

        initialSyncRepairRequested = true;
        long generation = transportGeneration;
        QueueMainThread(() =>
        {
            if (generation != transportGeneration || !sessionActive || InitialSyncDone)
            {
                return;
            }
            if (!GameReflection.GetPeers().Any(GameReflection.IsPeerReady))
            {
                initialSyncRepairRequested = false;
                QueueInitialSyncRepair();
                return;
            }
            initialSyncRepairRequested = RequestFullSync();
        });
    }

    private void RPC_FromServerConfigSync(ZRpc rpc, ZPackage package)
    {
        LockedConfigChanged -= ServerLockedSettingChanged;
        LockedConfigChanged += ServerLockedSettingChanged;
        IsSourceOfTruth = false;

        if (HandleConfigSyncRPC(0, package, false))
        {
            if (lastHandledPackageWasFull)
            {
                initialSyncRepairRequested = false;
                RaiseInitialSyncCompletedIfNeeded();
            }
            else if (!InitialSyncDone)
            {
                // A provider can appear after an optional consumer completed admission locally.
                // Its first partial registration update is not a complete initial synchronization.
                QueueInitialSyncRepair();
            }
        }
    }

    private void RPC_FromOtherClientConfigSync(long sender, ZPackage package) => HandleConfigSyncRPC(sender, package, true);

    private bool HandleConfigSyncRPC(long sender, ZPackage package, bool clientUpdate)
    {
        bool receivedFromServer = !isServer && !clientUpdate;
        string? activeFragmentCacheKey = null;
        lastHandledPackageWasFull = false;
        bool processingStarted = false;
        bool wasProcessingServerUpdate = ProcessingServerUpdate;
        long generation = transportGeneration;
        ParsedConfigs? parsedClientUpdate = null;
        SendSlot? canonicalSlot = null;

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
            ParsedConfigs configs = ReadConfigsFromPackage(package, receivedFromServer, strictClientUpdate: isServer && clientUpdate);
            if (generation != transportGeneration || !sessionActive)
            {
                return false;
            }
            if (isServer && clientUpdate)
            {
                parsedClientUpdate = configs;
                if (!TryAuthorizeClientUpdate(configs, sender, senderIsAdmin, out string rejectionReason))
                {
                    RejectClientUpdate(sender, senderIsAdmin, rejectionReason, configs);
                    return false;
                }
                // Reserve the canonical update before callbacks enqueue derived changes.
                canonicalSlot = ReserveSendSlot();
            }
            if (lastHandledPackageWasFull && receivedFromServer)
            {
                ResetConfigsFromServer(configs);
            }

            ApplyParsedConfigs(configs, receivedFromServer);
            if (generation != transportGeneration || !sessionActive)
            {
                return false;
            }

            string source = isServer || clientUpdate ? FormatClient(sender) : "the server";
            InfoLog($"Received {configs.configValues.Count} configs and {configs.customValues.Count} custom values from {source}{GetSingleEntryReceiveDetails(configs)}");

            if (isServer && clientUpdate)
            {
                string authorization = senderIsAdmin ? "administrator" : "configuration is unlocked and client updates are enabled";
                LogAcceptedClientUpdate(sender, configs, authorization);
                InvalidateFullSyncSnapshot($"accepted client update from {FormatClient(sender)}");

                StartBroadcastPackage(GameReflection.Everybody, () => ConfigsToPackage(
                    configs.configValues.Keys.Select(config => config.BaseConfig),
                    configs.customValues.Keys,
                    partial: true,
                    includeConfigValues: true,
                    includeAllProvidedConfigStates: true,
                    includeConfigStates: true), reservation: canonicalSlot);
                canonicalSlot = null;
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
            if (canonicalSlot != null)
            {
                ReleaseSendSlot(canonicalSlot);
            }
            if (processingStarted && generation == transportGeneration)
            {
                if (processingCount > 0)
                {
                    --processingCount;
                }
                ProcessingServerUpdate = wasProcessingServerUpdate;
                FlushPendingBroadcastsForAllIfIdle();
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
        if (requireFullSync)
        {
            StartFullSyncPackage(peer, "Correction", "Sending authoritative full correction to");
            return;
        }

        StartBroadcastPackage(new List<ZNetPeer> { peer }, () => ConfigsToPackage(
            configs.configValues.Keys.Select(config => config.BaseConfig),
            configs.customValues.Keys,
            partial: true,
            includeConfigValues: true,
            includeAllProvidedConfigStates: true,
            includeConfigStates: true));
    }

    internal static class ZNetShutdownPatch
    {
        internal static void Postfix()
        {
            ConditionalConfigSync[] instances = configSyncs.ToArray();
            sessionActive = false;
            ProcessingServerUpdate = true;
            lockExempt = false;
            foreach (ConditionalConfigSync configSync in instances)
            {
                configSync.ResetTransportState();
            }
            try
            {
                foreach (ConditionalConfigSync serverSync in instances)
                {
                    try
                    {
                        serverSync.DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Shutdown", "Reset local values and source-of-truth state");
                        serverSync.InitialSyncDone = false;
                        serverSync.ResetConfigsFromServer();
                        serverSync.IsSourceOfTruth = true;
                        serverSync.ServerLockedSettingChanged();
                    }
                    catch (Exception e)
                    {
                        serverSync.DebugWarning("Shutdown", $"Failed to reset one synchronization instance; continuing. Error: {e}");
                    }
                }
            }
            finally
            {
                ResetNetworkSessionState();
                isServer = false;
                ProcessingServerUpdate = false;
            }

            // Subscribers see restored values, local ownership, and an inactive network session.
            foreach (ConditionalConfigSync serverSync in instances)
            {
                serverSync.InvokeEventHandlers(serverSync.ServerConnectionReset, nameof(ServerConnectionReset));
            }
        }
    }

    private static long packageCounter = 0;

    private bool IsCurrentPeerSend(ZNetPeer peer, ZRpc rpc, SendSlot slot)
    {
        return IsCurrentSend(slot)
            && ReferenceEquals(GameReflection.GetPeer(rpc, slot.Session), peer)
            && ReferenceEquals(GameReflection.GetPeerRpc(peer), rpc)
            && GameReflection.GetPeerSocket(peer) is { } socket && GameReflection.SocketIsConnected(socket);
    }

    private IEnumerator<bool> DistributeConfigToPeer(ZNetPeer peer, ZPackage package, SendSlot slot)
    {
        if (!GameReflection.HasZRoutedRpc || !IsCurrentSend(slot))
        {
            yield break;
        }

        ZRpc rpc = GameReflection.GetPeerRpc(peer);
        bool server = GameReflection.IsServer(slot.Session);

        IEnumerable<bool> waitForQueue()
        {
            float timeout = Time.realtimeSinceStartup + 30;
            while (IsCurrentPeerSend(peer, rpc, slot)
                && GameReflection.GetPeerSocket(peer) is { } peerSocket
                && GameReflection.SocketGetSendQueueSize(peerSocket) > maximumSendQueueSize)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    DebugWarning("Network", $"Disconnecting {FormatPeer(peer)} after 30 seconds config sending timeout");
                    GameReflection.InvokeRpc(rpc, "Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                    GameReflection.Disconnect(peer, slot.Session);
                    yield break;
                }

                yield return false;
            }
        }

        void SendPackage(ZPackage pkg)
        {
            string method = Name + " ConditionalConfigSync";
            if (server)
            {
                GameReflection.InvokeRpc(rpc, method, pkg);
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
                if (!IsCurrentPeerSend(peer, rpc, slot))
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

            if (IsCurrentPeerSend(peer, rpc, slot))
            {
                SendPackage(package);
            }
        }
    }

    private IEnumerator SendZPackage(long target, ZPackage package, bool sequenced = false, SendSlot? reservation = null)
    {
        IEnumerator? sender = null;
        try
        {
            if (!sessionActive || !GameReflection.HasZNet)
            {
                yield break;
            }

            List<ZNetPeer> peers = GameReflection.GetRoutedPeers();
            if (target != GameReflection.Everybody)
            {
                peers = peers.Where(p => GameReflection.GetPeerUid(p) == target).ToList();
            }

            sender = SendZPackage(peers, package, sequenced: sequenced, reservation: reservation);
            while (sender.MoveNext())
            {
                yield return sender.Current;
            }
        }
        finally
        {
            (sender as IDisposable)?.Dispose();
            if (reservation != null)
            {
                ReleaseSendSlot(reservation);
            }
        }
    }

    private void DisposeSendWriter(IEnumerator<bool> writer)
    {
        try
        {
            writer.Dispose();
        }
        catch (Exception e)
        {
            DebugWarning("Network", $"Failed to dispose a completed peer sender: {e}");
        }
    }

    private void AdvanceSendWriters(List<KeyValuePair<ZNetPeer, IEnumerator<bool>>> writers, SendSlot slot)
    {
        for (int index = writers.Count - 1; index >= 0; --index)
        {
            if (!IsCurrentSend(slot))
            {
                return;
            }

            KeyValuePair<ZNetPeer, IEnumerator<bool>> entry = writers[index];
            bool active = false;
            try
            {
                active = entry.Value.MoveNext();
            }
            catch (Exception e)
            {
                RejectSync($"Failed to send synchronization to {FormatPeer(entry.Key)}: {e.Message}", null, incoming: false, e);
                try
                {
                    // Do not continue later ordered events after a failed fragmented transfer.
                    if (IsCurrentSend(slot))
                    {
                        GameReflection.Disconnect(entry.Key, slot.Session);
                    }
                }
                catch (Exception disconnectError)
                {
                    DebugWarning("Network", $"Failed to disconnect a peer after a send failure: {disconnectError}");
                }
            }
            if (!active)
            {
                DisposeSendWriter(entry.Value);
                writers.RemoveAt(index);
            }
        }
    }

    private IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package, bool packagePrepared = false,
        int rawSizeHint = -1, bool sequenced = false, SendSlot? reservation = null)
    {
        SendSlot? slot = reservation;
        List<KeyValuePair<ZNetPeer, IEnumerator<bool>>> writers = new();
        try
        {
            if (!sessionActive || !GameReflection.HasZNet || peers.Count == 0)
            {
                yield break;
            }

            slot ??= ReserveSendSlot(sequenced);
            if (slot == null)
            {
                yield break;
            }
            while (IsCurrentSend(slot) && (sendQueue.First != slot.Node || ProcessingServerUpdate || IsProcessing || packagePreparationDepth > 0))
            {
                yield return null;
            }
            if (!IsCurrentSend(slot) || !isServer && !CanBroadcastFromThisSide())
            {
                yield break;
            }
            StartSendSlot(slot);

            int rawSize = packagePrepared && rawSizeHint >= 0 ? rawSizeHint : GameReflection.PackageSize(package);
            if (rawSize > maxPayloadSize)
            {
                RejectSync($"Rejected outgoing synchronization package: serialized payload is {rawSize} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                yield break;
            }

            if (packagePrepared)
            {
                int wireSize = GameReflection.PackageSize(package);
                if (wireSize > maxPayloadSize)
                {
                    RejectSync($"Rejected outgoing prepared package: payload is {wireSize} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                    yield break;
                }

                DebugLog(
                    ConditionalConfigSyncDebugLevel.Verbose,
                    "Network",
                    $"Sending prepared package: raw={FormatByteCount(rawSize)}, wire={FormatByteCount(wireSize)}, peers={peers.Count}");
            }
            else if (rawSize > compressMinSize)
            {
                bool measureCompression = ShouldDebugLog(ConditionalConfigSyncDebugLevel.Verbose);
                long compressionStarted = measureCompression ? Stopwatch.GetTimestamp() : 0;
                package = CompressPackage(package);
                double compressionMilliseconds = measureCompression ? ElapsedMilliseconds(compressionStarted) : 0d;
                int compressedSize = GameReflection.PackageSize(package);
                if (compressedSize > maxPayloadSize)
                {
                    RejectSync($"Rejected outgoing compressed package: payload is {compressedSize} bytes, limit is {maxPayloadSize} bytes.", null, incoming: false);
                    yield break;
                }
                DebugLog(
                    ConditionalConfigSyncDebugLevel.Verbose,
                    "Network",
                    $"Compressed outgoing package: {FormatCompressionStats(rawSize, compressedSize)}, " +
                    $"compress={FormatMilliseconds(compressionMilliseconds)}, peers={peers.Count}");
            }
            else
            {
                DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Network", $"Sending package: size={FormatByteCount(rawSize)}, peers={peers.Count}");
            }

            foreach (ZNetPeer peer in peers.Where(GameReflection.IsPeerReady).ToArray())
            {
                writers.Add(new KeyValuePair<ZNetPeer, IEnumerator<bool>>(peer, DistributeConfigToPeer(peer, package, slot)));
            }
            while (writers.Count > 0 && IsCurrentSend(slot))
            {
                AdvanceSendWriters(writers, slot);
                if (writers.Count > 0)
                {
                    yield return null;
                }
            }
        }
        finally
        {
            foreach (KeyValuePair<ZNetPeer, IEnumerator<bool>> writer in writers)
            {
                DisposeSendWriter(writer.Value);
            }
            if (slot != null)
            {
                ReleaseSendSlot(slot);
            }
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

            // The client registers PlayerList and AdminList handlers while processing PeerInfo,
            // so these vanilla follow-up RPCs must not overtake the buffered PeerInfo package.
            private static bool ShouldBufferInitialPackage(int methodHash)
            {
                return methodHash == GameReflection.StableHash("PeerInfo")
                       || methodHash == GameReflection.StableHash("PlayerList")
                       || methodHash == GameReflection.StableHash("AdminList")
                       || methodHash == GameReflection.StableHash("RoutedRPC")
                       || methodHash == GameReflection.StableHash("ZDOData");
            }

            public new void Send(ZPackage pkg)
            {
                int oldPos = GameReflection.PackageGetPos(pkg);
                GameReflection.PackageSetPos(pkg, 0);
                int methodHash = GameReflection.PackageReadInt(pkg);
                GameReflection.PackageSetPos(pkg, oldPos);

                if (!finished && ShouldBufferInitialPackage(methodHash))
                {
                    ZPackage newPkg = GameReflection.NewPackage(GameReflection.PackageGetArray(pkg));
                    GameReflection.PackageSetPos(newPkg, oldPos);
                    Package.Add(newPkg); // the original ZPackage gets reused, create a new one
                }
                else
                {
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
                BufferingSocket bufferingSocket = __state;
                if (ReferenceEquals(GameReflection.GetRpcSocket(rpc), bufferingSocket))
                {
                    GameReflection.SetRpcSocket(rpc, bufferingSocket.Original);
                }
                if (GameReflection.GetPeer(rpc, __instance) is ZNetPeer currentPeer
                    && ReferenceEquals(GameReflection.GetPeerSocket(currentPeer), bufferingSocket))
                {
                    GameReflection.SetPeerSocket(currentPeer, bufferingSocket.Original);
                }

                if (bufferingSocket.finished)
                {
                    return;
                }

                bufferingSocket.finished = true;
                try
                {
                    if (!sessionActive || !ReferenceEquals(GameReflection.ZNetInstance, __instance)
                        || !GameReflection.SocketIsConnected(bufferingSocket.Original))
                    {
                        return;
                    }
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
                finally
                {
                    bufferingSocket.Package.Clear();
                    bufferingSocket.versionMatchQueued = -1;
                }
            }

            if (GameReflection.GetPeer(rpc, __instance) is not ZNetPeer peer)
            {
                SendBufferedData();
                return;
            }

            // Reserve every consumer before the first coroutine can yield. A later consumer's
            // partial update must not arrive before its initial full snapshot.
            KeyValuePair<ConditionalConfigSync, SendSlot?>[] initialSends = configSyncs.ToArray()
                .Select(sync => new KeyValuePair<ConditionalConfigSync, SendSlot?>(sync, sync.ReserveSendSlot()))
                .ToArray();

            void ReleaseInitialSends()
            {
                foreach (KeyValuePair<ConditionalConfigSync, SendSlot?> entry in initialSends)
                {
                    if (entry.Value != null)
                    {
                        entry.Key.ReleaseSendSlot(entry.Value);
                    }
                }
            }

            IEnumerator sendAsync()
            {
                try
                {
                    foreach (KeyValuePair<ConditionalConfigSync, SendSlot?> entry in initialSends)
                    {
                        if (entry.Value == null || !entry.Key.IsCurrentSend(entry.Value))
                        {
                            continue;
                        }
                        yield return GameReflection.StartCoroutine(
                            entry.Key.SendFullSyncPackage(peer, "InitialSync", "Sending full sync to", entry.Value),
                            __instance);
                    }
                }
                finally
                {
                    ReleaseInitialSends();
                    SendBufferedData();
                }
            }

            try
            {
                if (GameReflection.StartCoroutine(sendAsync(), __instance) == null)
                {
                    ReleaseInitialSends();
                    SendBufferedData();
                }
            }
            catch
            {
                ReleaseInitialSends();
                SendBufferedData();
                throw;
            }
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
                    QueuePendingConfigBroadcast(config);
                }
            }
            return;
        }

        StartBroadcastPackage(target, () => ConfigsToPackage(configs: configs, includeConfigStates: isServer));
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
                    TryEnqueueSequencedPackage(() => ConfigsToPackage(customValues: new[] { customValue }), customValue.Identifier);
                }
                else
                {
                    QueuePendingCustomValueBroadcast(customValue);
                }
            }
            FlushPendingBroadcastsForAllIfIdle();
            return;
        }

        StartBroadcastPackage(target, () => ConfigsToPackage(customValues: customValues), customValues.Any(value => value.PreserveUpdateSequence));
    }

    private bool StartBroadcastPackage(long target, ZPackage package, bool sequenced = false)
        => StartBroadcastPackage(target, () => package, sequenced);

    private bool StartBroadcastPackage(List<ZNetPeer> peers, ZPackage package)
        => StartBroadcastPackage(peers, () => package);

    private bool StartBroadcastPackage(long target, Func<ZPackage> createPackage, bool sequenced = false, SendSlot? reservation = null)
    {
        List<ZNetPeer> peers = GameReflection.GetRoutedPeers();
        if (target != GameReflection.Everybody)
        {
            peers = peers.Where(peer => GameReflection.GetPeerUid(peer) == target).ToList();
        }
        if (peers.Count == 0)
        {
            if (reservation != null)
            {
                ReleaseSendSlot(reservation);
            }
            return true;
        }

        return StartBroadcastPackageCore(
            createPackage,
            (package, slot) => SendZPackage(target, package, sequenced, slot),
            sequenced,
            reservation);
    }

    private bool StartBroadcastPackage(List<ZNetPeer> peers, Func<ZPackage> createPackage, bool sequenced = false, SendSlot? reservation = null)
    {
        if (peers.Count == 0)
        {
            if (reservation != null)
            {
                ReleaseSendSlot(reservation);
            }
            return true;
        }

        return StartBroadcastPackageCore(
            createPackage,
            (package, slot) => SendZPackage(peers, package, sequenced: sequenced, reservation: slot),
            sequenced,
            reservation);
    }

    private bool StartBroadcastPackageCore(Func<ZPackage> createPackage, Func<ZPackage, SendSlot, IEnumerator> createSender,
        bool sequenced = false, SendSlot? reservation = null)
    {
        SendSlot? slot = reservation ?? ReserveSendSlot(sequenced);
        if (slot == null)
        {
            return false;
        }

        bool scheduled = false;
        try
        {
            if (!IsCurrentSend(slot))
            {
                return false;
            }
            ZPackage package;
            ++packagePreparationDepth;
            try
            {
                package = createPackage();
            }
            finally
            {
                --packagePreparationDepth;
            }
            if (!IsCurrentSend(slot) || !ValidateOutgoingPayload(package, "synchronization package"))
            {
                return false;
            }

            IEnumerator sender = createSender(package, slot);
            bool hasPendingWork;
            try
            {
                hasPendingWork = sender.MoveNext();
            }
            catch
            {
                (sender as IDisposable)?.Dispose();
                throw;
            }

            if (!hasPendingWork)
            {
                // Unity may return null when an iterator finishes before its first yield. That is a
                // successful synchronous/no-recipient completion, not a failed sender startup.
                (sender as IDisposable)?.Dispose();
                scheduled = true;
                return true;
            }

            object? firstYield = sender.Current;
            IEnumerator ContinueSender(IEnumerator activeSender, object? initialYield)
            {
                try
                {
                    yield return initialYield;
                    while (activeSender.MoveNext())
                    {
                        yield return activeSender.Current;
                    }
                }
                finally
                {
                    (activeSender as IDisposable)?.Dispose();
                }
            }

            scheduled = GameReflection.StartCoroutine(ContinueSender(sender, firstYield), slot.Session) != null;
            if (!scheduled)
            {
                (sender as IDisposable)?.Dispose();
                RejectSync("Could not start the synchronization sender for the active session.", null, incoming: false);
            }
            return scheduled;
        }
        catch (Exception e)
        {
            RejectSync($"Failed to prepare or schedule synchronization: {e.Message}", null, incoming: false, e);
            return false;
        }
        finally
        {
            if (!scheduled)
            {
                ReleaseSendSlot(slot);
            }
        }
    }

    private void FlushPendingBroadcastsIfIdle()
    {
        if (ProcessingServerUpdate || packagePreparationDepth > 0 || IsProcessing || flushingPendingBroadcasts || !sessionActive || !GameReflection.HasZNet)
        {
            return;
        }
        if (!CanBroadcastFromThisSide())
        {
            if (!IsSourceOfTruth && InitialSyncDone)
            {
                ClearPendingBroadcasts();
            }
            return;
        }

        if (pendingSequencedCustomValuePackages.Count == 0
            && (pendingStateSendSlot == null || sendQueue.First != pendingStateSendSlot.Node))
        {
            return;
        }

        flushingPendingBroadcasts = true;
        DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Pending", $"Flushing configs={pendingConfigBroadcasts.Count}, custom={pendingCustomValueBroadcasts.Count}, sequenced={pendingSequencedCustomValuePackages.Count}");
        try
        {
            // State and events already own their FIFO positions. Materializing one cannot move it
            // behind a later reservation, even while an earlier fragmented send is still active.
            FlushPendingStateBroadcastIfReady();
            int sequenceCount = pendingSequencedCustomValuePackages.Count;
            for (int index = 0; index < sequenceCount; ++index)
            {
                if (pendingSequencedCustomValuePackages.First is not { } node || node.Value.Package is not { } package)
                {
                    break;
                }
                pendingSequencedCustomValuePackages.RemoveFirst();
                StartBroadcastPackage(GameReflection.Everybody, () => package,
                    sequenced: true, reservation: node.Value.Reservation);
            }

            // A short earlier event may have completed synchronously and exposed the state marker.
            FlushPendingStateBroadcastIfReady();
        }
        finally
        {
            flushingPendingBroadcasts = false;
        }
    }
}
