using System;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private const string ResyncRpcSuffix = " ConditionalConfigSync Resync";
    private const int StableReadAttempts = 5;
    private const int StableReadDelayMilliseconds = 50;

    private readonly VersionCheck versionCheck;
    private bool serverRpcsRegistered;
    private readonly HashSet<ZRpc> registeredClientRpcs = new();
    private readonly HashSet<OwnConfigEntryBase> lateRegisteredConfigs = new();
    private readonly HashSet<CustomSyncedValueBase> lateRegisteredCustomValues = new();
    private bool lateRegistrationSyncScheduled;

    private sealed class FullSyncSnapshot
    {
        internal long Revision;
        internal bool Admin;
        internal string? ServerVersion;
        internal int ConfigCount;
        internal int CustomValueCount;
        internal int RawSize;
        internal int WireSize;
        internal bool Compressed;
        internal double SerializationMilliseconds;
        internal double CompressionMilliseconds;
        internal byte[] WireBytes = Array.Empty<byte>();
    }

    private long fullSyncStateRevision = 1;
    private FullSyncSnapshot? fullSyncAdminSnapshot;
    private FullSyncSnapshot? fullSyncNonAdminSnapshot;

    private static bool sessionActive;
    private static Harmony? runtimeHarmony;
    private static AssemblyLoadEventHandler? assemblyLoadHandler;

    /// <summary>
    /// Requests a complete synchronization package from the currently connected server.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the request was dispatched; otherwise <see langword="false"/> when no remote server
    /// is connected.
    /// </returns>
    /// <remarks>
    /// ConditionalConfigSync invokes this automatically when a config or custom value is registered after the initial
    /// client synchronization. Mods may also call it after rebuilding a dynamic registration set. The request is ignored
    /// on the server and in local worlds. Resynchronization does not repeat the completed peer admission or mod version
    /// check, so mods that require connection rejection must create their ConfigSync before connecting.
    /// </remarks>
    [Description("Requests a complete server synchronization, primarily for late-registered configs and custom values.")]
    public bool RequestFullSync()
    {
        if (!sessionActive || !GameReflection.HasZNet || isServer
            || !GameReflection.GetPeers().Any(GameReflection.IsPeerReady))
        {
            return false;
        }

        try
        {
            ZPackage request = GameReflection.NewPackage();
            GameReflection.PackageWrite(request, PluginInfoCCS.ProtocolVersion);
            GameReflection.InvokeRoutedPackage(Name + ResyncRpcSuffix, request);
            DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Resync", "Requested a complete synchronization package from the server");
            return true;
        }
        catch (Exception e)
        {
            RejectSync($"Failed to request complete synchronization: {e.Message}", null, incoming: false, e);
            return false;
        }
    }

    private static void QueueMainThread(Action action)
    {
        // Unlike EnqueueMainThread, this helper always defers until the next ZNet.Update pass. Late registrations use
        // it to batch every config/custom value created in the current frame into one synchronization operation.
        lock (mainThreadQueueLock)
        {
            mainThreadQueue.Enqueue(action);
        }
    }

    private void RegisterForActiveSession()
    {
        if (!sessionActive || !GameReflection.HasZNet)
        {
            return;
        }

        if (isServer)
        {
            ResetFullSyncSnapshotCache(advanceRevision: false);
            RegisterServerRpcHandlers();
            InitialSyncDone = true;
        }
        else
        {
            IsSourceOfTruth = false;
            ServerLockedSettingChanged();
            bool connectedPeerFound = false;
            foreach (ZNetPeer peer in GameReflection.GetPeers())
            {
                RegisterClientRpcHandler(peer);
                connectedPeerFound = true;
            }

            if (!InitialSyncDone && connectedPeerFound)
            {
                // A ConfigSync instance created after the connection's normal initial-sync window must explicitly
                // request its first complete package. Existing instances are registered before a peer is present and
                // continue to receive the normal startup synchronization without this extra request.
                RequestFullSync();
            }
        }
    }

    internal void UseLocalStateForMissingOptionalServer()
    {
        if (isServer || IsSourceOfTruth || ModRequired)
        {
            return;
        }

        bool wasProcessingServerUpdate = ProcessingServerUpdate;
        ProcessingServerUpdate = true;
        try
        {
            // The owning mod is optional and the successful peer handshake proved that the server did not
            // provide a matching ConfigSync instance. Return this instance to its normal local role instead
            // of leaving every registered setting in the fail-closed pre-sync replica state.
            InitialSyncDone = false;
            ResetConfigsFromServer();
            IsSourceOfTruth = true;
            ServerLockedSettingChanged();
            pendingConfigBroadcasts.Clear();
            pendingCustomValueBroadcasts.Clear();
            pendingSequencedCustomValuePackages.Clear();
            foreach (string cacheKey in configValueCache.Keys.ToArray())
            {
                RemoveFragmentAssembly(cacheKey);
            }

            LogSource.LogInfo(
                $"[{GetDebugModName()}][Client][Version] No server handshake was received for this optional mod; using local config ownership for this session.");
        }
        catch (Exception e)
        {
            DebugWarning("Version", $"Failed to restore local ownership for an optional mod without a server handshake: {e}");
        }
        finally
        {
            ProcessingServerUpdate = wasProcessingServerUpdate;
        }
    }

    private void RegisterServerRpcHandlers()
    {
        if (serverRpcsRegistered || !GameReflection.HasZRoutedRpc)
        {
            return;
        }

        GameReflection.RegisterRoutedPackage(Name + " ConditionalConfigSync", RPC_FromOtherClientConfigSync);
        GameReflection.RegisterRoutedPackage(Name + ResyncRpcSuffix, RPC_RequestFullSync);
        GameReflection.RegisterRoutedPackage(Name + PolicyChangeRpcSuffix, RPC_RequestSynchronizationPolicyChange);
        serverRpcsRegistered = true;
        DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Register", $"Registered server RPCs for '{Name}'");
    }

    private void RegisterClientRpcHandler(ZNetPeer peer)
    {
        ZRpc rpc = GameReflection.GetPeerRpc(peer);
        if (!registeredClientRpcs.Add(rpc))
        {
            return;
        }

        GameReflection.RegisterRpcPackage(rpc, Name + " ConditionalConfigSync", RPC_FromServerConfigSync);
        DebugLog(ConditionalConfigSyncDebugLevel.Basic, "Register", $"Registered client RPC for '{Name}'");
    }

    private void RPC_RequestFullSync(long sender, ZPackage request)
    {
        if (!isServer || !sessionActive)
        {
            return;
        }

        try
        {
            int requesterProtocol = GameReflection.PackageSize(request) - GameReflection.PackageGetPos(request) >= sizeof(int)
                ? GameReflection.PackageReadInt(request)
                : 0;

            if (!IsProtocolCompatible(requesterProtocol))
            {
                RejectSync(
                    $"Rejected resync request from {FormatClient(sender)} using ConditionalConfigSync protocol " +
                    $"{(requesterProtocol == 0 ? "missing" : requesterProtocol.ToString())}. " +
                    $"Required protocol is {PluginInfoCCS.ProtocolVersion}.",
                    sender,
                    incoming: true);
                return;
            }

            ZNetPeer? peer = GameReflection.GetRoutedPeer(sender);
            if (peer == null || !GameReflection.IsPeerReady(peer))
            {
                RejectSync($"Could not serve resync request from {FormatClient(sender)} because the peer is unavailable.", sender, incoming: true);
                return;
            }

            StartFullSyncPackage(peer, "Resync", "Sending complete resync to");
        }
        catch (Exception e)
        {
            RejectSync($"Failed to process resync request from {FormatClient(sender)}: {e.Message}", sender, incoming: true, e);
        }
    }

    private ZPackage CreateFullSyncPackageUncached(bool admin)
    {
        List<PackageEntry> entries = new();
        if (CurrentVersion != null)
        {
            entries.Add(PackageEntry.ServerVersion(CurrentVersion));
        }

        entries.Add(PackageEntry.LockExempt(admin, PolicyChangeCapability));
        return ConfigsToPackage(allConfigs.Select(c => c.BaseConfig), allCustomValues, entries, partial: false);
    }

    private void InvalidateFullSyncSnapshot(string reason)
    {
        if (!isServer)
        {
            return;
        }

        if (fullSyncStateRevision == long.MaxValue)
        {
            fullSyncStateRevision = 1;
        }
        else
        {
            ++fullSyncStateRevision;
        }

        fullSyncAdminSnapshot = null;
        fullSyncNonAdminSnapshot = null;
        DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Snapshot", $"Invalidated full-sync snapshot: revision={fullSyncStateRevision}, reason={reason}");
    }

    private void ResetFullSyncSnapshotCache(bool advanceRevision = true)
    {
        if (advanceRevision)
        {
            if (fullSyncStateRevision == long.MaxValue)
            {
                fullSyncStateRevision = 1;
            }
            else
            {
                ++fullSyncStateRevision;
            }
        }

        fullSyncAdminSnapshot = null;
        fullSyncNonAdminSnapshot = null;
    }

    private FullSyncSnapshot GetOrCreateFullSyncSnapshot(ZNetPeer peer, out bool cacheHit)
    {
        bool admin = IsPeerAdmin(peer);
        FullSyncSnapshot? cached = admin ? fullSyncAdminSnapshot : fullSyncNonAdminSnapshot;
        if (cached != null
            && cached.Revision == fullSyncStateRevision
            && cached.Admin == admin
            && string.Equals(cached.ServerVersion, CurrentVersion, StringComparison.Ordinal)
            && cached.ConfigCount == allConfigs.Count
            && cached.CustomValueCount == allCustomValues.Count)
        {
            cacheHit = true;
            DebugLog(
                ConditionalConfigSyncDebugLevel.Verbose,
                "Snapshot",
                $"Reusing full-sync snapshot: revision={cached.Revision}, admin={admin}, raw={FormatByteCount(cached.RawSize)}, " +
                $"wire={FormatByteCount(cached.WireSize)}, compressed={cached.Compressed}, " +
                $"buildSerialize={FormatMilliseconds(cached.SerializationMilliseconds)}, buildCompress={FormatMilliseconds(cached.CompressionMilliseconds)}");
            return cached;
        }

        cacheHit = false;
        bool measureSnapshot = ShouldDebugLog(ConditionalConfigSyncDebugLevel.Verbose);
        long serializationStarted = measureSnapshot ? Stopwatch.GetTimestamp() : 0;
        ZPackage rawPackage = CreateFullSyncPackageUncached(admin);
        double serializationMilliseconds = measureSnapshot ? ElapsedMilliseconds(serializationStarted) : 0d;
        int rawSize = GameReflection.PackageSize(rawPackage);

        ZPackage wirePackage = rawPackage;
        bool compressed = false;
        double compressionMilliseconds = 0d;
        if (rawSize <= maxPayloadSize && rawSize > compressMinSize)
        {
            long compressionStarted = measureSnapshot ? Stopwatch.GetTimestamp() : 0;
            wirePackage = CompressPackage(rawPackage);
            compressionMilliseconds = measureSnapshot ? ElapsedMilliseconds(compressionStarted) : 0d;
            compressed = true;
        }

        int wireSize = GameReflection.PackageSize(wirePackage);
        bool validPayload = rawSize <= maxPayloadSize && wireSize <= maxPayloadSize;
        FullSyncSnapshot snapshot = new()
        {
            Revision = fullSyncStateRevision,
            Admin = admin,
            ServerVersion = CurrentVersion,
            ConfigCount = allConfigs.Count,
            CustomValueCount = allCustomValues.Count,
            RawSize = rawSize,
            WireSize = wireSize,
            Compressed = compressed,
            SerializationMilliseconds = serializationMilliseconds,
            CompressionMilliseconds = compressionMilliseconds,
            // Avoid a second large allocation for a snapshot that will be rejected by the existing payload limits.
            WireBytes = validPayload ? GameReflection.PackageGetArray(wirePackage) : Array.Empty<byte>(),
        };

        string compressionDetails = compressed
            ? FormatCompressionStats(rawSize, wireSize)
            : $"raw={FormatByteCount(rawSize)}, wire={FormatByteCount(wireSize)}, compression=not-used";
        DebugLog(
            ConditionalConfigSyncDebugLevel.Verbose,
            "Snapshot",
            $"Built full-sync snapshot: revision={snapshot.Revision}, admin={admin}, configs={snapshot.ConfigCount}, custom={snapshot.CustomValueCount}, " +
            $"{compressionDetails}, serialize={FormatMilliseconds(serializationMilliseconds)}, " +
            $"compress={FormatMilliseconds(compressionMilliseconds)}");

        if (validPayload)
        {
            if (admin)
            {
                fullSyncAdminSnapshot = snapshot;
            }
            else
            {
                fullSyncNonAdminSnapshot = snapshot;
            }
        }

        return snapshot;
    }

    private IEnumerator SendFullSyncPackage(ZNetPeer peer, string area, string action)
    {
        FullSyncSnapshot snapshot = GetOrCreateFullSyncSnapshot(peer, out bool cacheHit);
        DebugLog(
            ConditionalConfigSyncDebugLevel.Basic,
            area,
            $"{action} {FormatPeer(peer)}, admin={snapshot.Admin}, configs={snapshot.ConfigCount}, custom={snapshot.CustomValueCount}, " +
            $"raw={FormatByteCount(snapshot.RawSize)}, wire={FormatByteCount(snapshot.WireSize)}, " +
            $"snapshot={(cacheHit ? "reused" : "built")}, revision={snapshot.Revision}");

        if (snapshot.RawSize > maxPayloadSize)
        {
            return RejectFullSyncSnapshot(
                $"Rejected outgoing synchronization package: serialized payload is {snapshot.RawSize} bytes, limit is {maxPayloadSize} bytes.");
        }
        if (snapshot.WireSize > maxPayloadSize)
        {
            return RejectFullSyncSnapshot(
                $"Rejected outgoing prepared package: payload is {snapshot.WireSize} bytes, limit is {maxPayloadSize} bytes.");
        }

        return SendZPackage(
            new List<ZNetPeer> { peer },
            GameReflection.NewPackage(snapshot.WireBytes),
            packagePrepared: true,
            rawSizeHint: snapshot.RawSize);
    }

    private IEnumerator RejectFullSyncSnapshot(string reason)
    {
        RejectSync(reason, null, incoming: false);
        yield break;
    }

    private void StartFullSyncPackage(ZNetPeer peer, string area, string action)
    {
        GameReflection.StartCoroutine(SendFullSyncPackage(peer, area, action));
    }

    private void ScheduleLateRegistrationSync(OwnConfigEntryBase? config = null, CustomSyncedValueBase? customValue = null)
    {
        // Registrations completed before a network session are part of the normal initial package and must not remain
        // in the late-registration batch. A client that has not completed its first full sync also needs no separate
        // batch because that package (or the explicit first resync for a late-created ConfigSync) includes all values.
        if (!sessionActive || !GameReflection.HasZNet || (!isServer && !InitialSyncDone))
        {
            return;
        }

        if (config != null)
        {
            lateRegisteredConfigs.Add(config);
        }
        if (customValue != null)
        {
            lateRegisteredCustomValues.Add(customValue);
        }

        if (lateRegistrationSyncScheduled)
        {
            return;
        }

        lateRegistrationSyncScheduled = true;
        QueueMainThread(FlushLateRegistrationSync);
    }

    private void FlushLateRegistrationSync()
    {
        lateRegistrationSyncScheduled = false;
        if (!sessionActive || !GameReflection.HasZNet)
        {
            lateRegisteredConfigs.Clear();
            lateRegisteredCustomValues.Clear();
            return;
        }

        if (isServer)
        {
            ConfigEntryBase[] configs = lateRegisteredConfigs.Select(config => config.BaseConfig).ToArray();
            CustomSyncedValueBase[] customValues = lateRegisteredCustomValues
                .OrderByDescending(value => value.Priority)
                .ThenBy(value => value.RegistrationIndex)
                .ToArray();
            lateRegisteredConfigs.Clear();
            lateRegisteredCustomValues.Clear();

            if (configs.Length == 0 && customValues.Length == 0)
            {
                return;
            }

            ZPackage package = ConfigsToPackage(
                configs: configs,
                customValues: customValues,
                partial: true,
                includeConfigValues: true,
                includeAllProvidedConfigStates: true);
            DebugLog(
                ConditionalConfigSyncDebugLevel.Basic,
                "LateRegistration",
                $"Broadcasting late registrations: configs={configs.Length}, custom={customValues.Length}");
            StartBroadcastPackage(GameReflection.Everybody, package);
        }
        else
        {
            lateRegisteredConfigs.Clear();
            lateRegisteredCustomValues.Clear();
            RequestFullSync();
        }
    }

    private void ResetSessionRegistrationState()
    {
        serverRpcsRegistered = false;
        registeredClientRpcs.Clear();
        lateRegisteredConfigs.Clear();
        lateRegisteredCustomValues.Clear();
        lateRegistrationSyncScheduled = false;
        authoritativeCorrectionTimes.Clear();
        largeStringCustomValueWarnings.Clear();
        ResetFullSyncSnapshotCache();
    }

    private static bool IsProtocolCompatible(int remoteProtocol)
    {
        return remoteProtocol == PluginInfoCCS.ProtocolVersion;
    }

    private static string[] ReadAllLinesStable(string path, bool missingIsEmpty = false)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= StableReadAttempts; ++attempt)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (missingIsEmpty && attempt == StableReadAttempts)
                    {
                        return Array.Empty<string>();
                    }

                    lastError = new FileNotFoundException(
                        $"File is temporarily unavailable (attempt {attempt}/{StableReadAttempts}).",
                        path);
                    throw lastError;
                }

                FileInfo before = new(path);
                long beforeLength = before.Length;
                DateTime beforeWrite = before.LastWriteTimeUtc;
                string text;

                using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true))
                {
                    text = reader.ReadToEnd();
                }

                Thread.Sleep(StableReadDelayMilliseconds);
                if (!File.Exists(path))
                {
                    if (missingIsEmpty && attempt == StableReadAttempts)
                    {
                        return Array.Empty<string>();
                    }

                    lastError = new FileNotFoundException(
                        $"File disappeared while it was being read (attempt {attempt}/{StableReadAttempts}).",
                        path);
                    throw lastError;
                }

                FileInfo after = new(path);
                if (beforeLength == after.Length && beforeWrite == after.LastWriteTimeUtc)
                {
                    return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                }

                lastError = new IOException($"File changed while it was being read (attempt {attempt}/{StableReadAttempts}).");
            }
            catch (IOException e)
            {
                lastError = e;
            }
            catch (UnauthorizedAccessException e)
            {
                lastError = e;
            }

            if (attempt < StableReadAttempts)
            {
                Thread.Sleep(StableReadDelayMilliseconds * attempt);
            }
        }

        throw new IOException(
            $"Could not read stable contents from '{path}' after {StableReadAttempts} attempts.",
            lastError);
    }

    private static void StopPolicySupport()
    {
        lock (policyLock)
        {
            syncPolicyWatcher?.Dispose();
            hiddenConfigsWatcher?.Dispose();
            syncPolicyWatcher = null;
            hiddenConfigsWatcher = null;
            policySupportInitialized = false;
            policyReloadScheduled = false;
            syncPolicy = new Dictionary<string, ConfigPolicyOverride>(StringComparer.OrdinalIgnoreCase);
            hiddenConfigPolicy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Interlocked.Increment(ref policyReadGeneration);
            policyAppliedGeneration = policyReadGeneration;
        }
    }

    private static void StopDebugSupport()
    {
        lock (debugSupportLock)
        {
            debugConfigWatcher?.Dispose();
            debugConfigWatcher = null;
            debugConfigReloadScheduled = false;
            debugSupportInitialized = false;
            debugConfigEnabled = false;
            debugConfigLevel = ConditionalConfigSyncDebugLevel.Basic;
            debugConfigFilter = "";
        }
    }

    private static void ResetNetworkSessionState()
    {
        sessionActive = false;
        foreach (ConditionalConfigSync sync in configSyncs)
        {
            sync.ResetSessionRegistrationState();
        }

        VersionCheck.ResetSessionState(VersionCheck.HasPendingConnectionError());
        StopPolicySupport();

        lock (mainThreadQueueLock)
        {
            mainThreadQueue.Clear();
        }
    }

    /// <summary>
    /// Releases process-wide watchers, Harmony patches, queued work, and network-session state.
    /// </summary>
    /// <remarks>
    /// Called by the standalone BepInEx bootstrap during plugin destruction. Dependent mods normally never call this.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static void ShutdownRuntime()
    {
        lock (runtimeLock)
        {
            if (!runtimeInitialized)
            {
                return;
            }

            ResetNetworkSessionState();
            StopDebugSupport();

            if (assemblyLoadHandler != null)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= assemblyLoadHandler;
                assemblyLoadHandler = null;
            }

            try
            {
                if (runtimeHarmony != null)
                {
                    MethodInfo? unpatchSelf = AccessTools.DeclaredMethod(typeof(Harmony), "UnpatchSelf");
                    if (unpatchSelf != null)
                    {
                        unpatchSelf.Invoke(runtimeHarmony, null);
                    }
                    else
                    {
                        LogSource.LogWarning("[Cleanup] Harmony.UnpatchSelf was not found; runtime patches will remain until process exit.");
                    }
                }
            }
            catch (Exception e)
            {
                LogSource.LogWarning($"[Cleanup] Failed to remove Harmony patches: {e.Message}");
            }

            runtimeHarmony = null;
            VersionCheck.ShutdownRuntime();
            configSyncs.Clear();
            runtimeInitialized = false;
            mainThreadId = 0;
            isServer = false;
            lockExempt = false;
        }
    }
}
