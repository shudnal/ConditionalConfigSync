using System;
using System.Collections.Generic;
using System.Linq;

namespace ConditionalConfigSync;

public partial class VersionCheck
{
    internal const string RemoteConsumerSnapshotRpcName = "ConditionalConfigSync RemoteConsumerSnapshot";
    private const int RemoteConsumerSnapshotFormatVersion = 1;
    private const int MaxRemoteConsumerSnapshotEntries = 8192;
    private const int MaxRemoteConsumerSnapshotPackageLength = 2 * 1024 * 1024;

    private sealed class RemoteConsumerSnapshotEntry
    {
        internal long PeerUid;
        internal string ConsumerGuid = string.Empty;
        internal RemoteConsumerState State;
    }

    // Server-side index of the same parsed version handshakes already used by VersionCheck. It also retains consumer
    // GUIDs that are not registered on the server so two advertising clients can still be compared without a second
    // capability handshake. Raw version metadata never leaves the server.
    private static readonly Dictionary<ZRpc, Dictionary<string, VersionHandshakeState>> advertisedClientConsumers = new();

    // Client-side only. The server answers public queries directly from its authoritative per-ZRpc handshake state.
    private static readonly object remoteConsumerStatesLock = new();
    private static Dictionary<long, Dictionary<string, RemoteConsumerState>> remoteConsumerStates = new();

    internal static RemoteConsumerState GetRemoteConsumerState(string consumerGuid, long peerUid)
    {
        if (!GameReflection.HasZNet)
        {
            return RemoteConsumerState.Unknown;
        }

        if (GameReflection.IsServer())
        {
            ZNetPeer? peer = GameReflection.GetRoutedPeer(peerUid);
            if (peer == null || !GameReflection.IsPeerReady(peer))
            {
                return RemoteConsumerState.Unknown;
            }

            VersionCheck? check = FindVersionCheck(consumerGuid);
            return check == null
                ? RemoteConsumerState.Unknown
                : check.GetServerPeerConsumerState(GameReflection.GetPeerRpc(peer));
        }

        lock (remoteConsumerStatesLock)
        {
            if (remoteConsumerStates.TryGetValue(peerUid, out Dictionary<string, RemoteConsumerState>? states)
                && states.TryGetValue(consumerGuid, out RemoteConsumerState state))
            {
                return state;
            }
        }

        // A client already has the server's normal version handshake locally, so this direct server query remains
        // useful even when connected to an older CCS server that does not publish cross-client capability snapshots.
        ZNetPeer? directPeer = GameReflection.GetPeers().FirstOrDefault(peer =>
            GameReflection.IsPeerReady(peer) && GameReflection.GetPeerUid(peer) == peerUid);
        if (directPeer == null)
        {
            return RemoteConsumerState.Unknown;
        }

        VersionCheck? localCheck = FindVersionCheck(consumerGuid);
        if (localCheck == null)
        {
            return RemoteConsumerState.Unknown;
        }

        if (localCheck.receivedServerHandshake == null)
        {
            return RemoteConsumerState.Missing;
        }

        return GetFailure(
                   localCheck.receivedServerHandshake,
                   localCheck.CurrentVersion,
                   localCheck.MinimumRequiredVersion) == VersionFailureKind.None
            ? RemoteConsumerState.Compatible
            : RemoteConsumerState.Incompatible;
    }

    private static VersionCheck? FindVersionCheck(string consumerGuid)
    {
        return versionChecks.FirstOrDefault(check =>
            string.Equals(check.Name, consumerGuid, StringComparison.OrdinalIgnoreCase));
    }

    private RemoteConsumerState GetServerPeerConsumerState(ZRpc rpc)
    {
        if (receivedClientHandshakes.TryGetValue(rpc, out VersionHandshakeState? state))
        {
            return ValidatedClients.Contains(rpc) && IsVersionOk(state, rpc)
                ? RemoteConsumerState.Compatible
                : RemoteConsumerState.Incompatible;
        }

        // Conditional and required consumers advertise whenever they are installed, so an admitted peer with no
        // matching handshake is authoritatively missing that capability. Fixed optional consumers are intentionally
        // silent and therefore remain Unknown instead of being reported as absent.
        return ShouldSendClientHandshake()
            ? RemoteConsumerState.Missing
            : RemoteConsumerState.Unknown;
    }

    private static RemoteConsumerState ComparePeerConsumers(
        VersionCheck? serverCheck,
        ZRpc recipientRpc,
        ZRpc targetRpc,
        VersionHandshakeState recipientState,
        VersionHandshakeState targetState)
    {
        // When the server owns this consumer, both advertisements must also have passed the ordinary server-side check.
        // For a consumer absent from the server, apply the same version/protocol algorithm directly between the two
        // advertised handshakes so client-to-client capability discovery still works without inventing another handshake.
        if (serverCheck != null
            && (!serverCheck.ValidatedClients.Contains(recipientRpc)
                || !serverCheck.ValidatedClients.Contains(targetRpc)))
        {
            return RemoteConsumerState.Incompatible;
        }

        if (!recipientState.ProtocolFieldPresent
            || recipientState.ProtocolVersion != PluginInfoCCS.ProtocolVersion)
        {
            return RemoteConsumerState.Incompatible;
        }

        return GetFailure(
                   targetState,
                   recipientState.CurrentVersion,
                   recipientState.MinimumRequiredVersion) == VersionFailureKind.None
            ? RemoteConsumerState.Compatible
            : RemoteConsumerState.Incompatible;
    }

    private static void RecordAdvertisedClientConsumer(ZRpc rpc, string consumerGuid, VersionHandshakeState state)
    {
        if (!advertisedClientConsumers.TryGetValue(rpc, out Dictionary<string, VersionHandshakeState>? consumers))
        {
            consumers = new Dictionary<string, VersionHandshakeState>(StringComparer.OrdinalIgnoreCase);
            advertisedClientConsumers.Add(rpc, consumers);
        }

        consumers[consumerGuid] = state;
    }

    private static void ResetAdvertisedClientConsumers(ZRpc rpc)
    {
        advertisedClientConsumers.Remove(rpc);
    }

    private static void ClearAdvertisedClientConsumers()
    {
        advertisedClientConsumers.Clear();
    }

    private static void RegisterRemoteConsumerSnapshotHandler(ZRpc rpc, long connectionGeneration)
    {
        GameReflection.RegisterRpcPackage(
            rpc,
            RemoteConsumerSnapshotRpcName,
            (_, package) => ReceiveRemoteConsumerSnapshot(package, connectionGeneration));
    }

    private static void ReceiveRemoteConsumerSnapshot(ZPackage package, long connectionGeneration)
    {
        if (connectionGeneration == 0 || connectionGeneration != activeClientConnectionGeneration)
        {
            return;
        }

        try
        {
            if (GameReflection.PackageSize(package) > MaxRemoteConsumerSnapshotPackageLength)
            {
                throw new InvalidOperationException(
                    $"remote-consumer snapshot exceeds the {MaxRemoteConsumerSnapshotPackageLength}-byte package limit");
            }

            int formatVersion = GameReflection.PackageReadInt(package);
            if (formatVersion != RemoteConsumerSnapshotFormatVersion)
            {
                throw new InvalidOperationException($"unsupported remote-consumer snapshot format {formatVersion}");
            }

            int entryCount = GameReflection.PackageReadInt(package);
            if (entryCount < 0 || entryCount > MaxRemoteConsumerSnapshotEntries)
            {
                throw new InvalidOperationException(
                    $"remote-consumer snapshot entry count {entryCount} is outside 0..{MaxRemoteConsumerSnapshotEntries}");
            }

            Dictionary<long, Dictionary<string, RemoteConsumerState>> next = new();
            for (int index = 0; index < entryCount; ++index)
            {
                long peerUid = GameReflection.PackageReadLong(package);
                string consumerGuid = GameReflection.PackageReadString(package);
                byte rawState = GameReflection.PackageReadByte(package);
                if (peerUid == 0 || string.IsNullOrWhiteSpace(consumerGuid))
                {
                    throw new InvalidOperationException("remote-consumer snapshot contains an empty peer UID or consumer GUID");
                }

                RemoteConsumerState state = rawState switch
                {
                    (byte)RemoteConsumerState.Missing => RemoteConsumerState.Missing,
                    (byte)RemoteConsumerState.Incompatible => RemoteConsumerState.Incompatible,
                    (byte)RemoteConsumerState.Compatible => RemoteConsumerState.Compatible,
                    _ => throw new InvalidOperationException($"remote-consumer snapshot contains invalid state {rawState}"),
                };

                if (!next.TryGetValue(peerUid, out Dictionary<string, RemoteConsumerState>? states))
                {
                    states = new Dictionary<string, RemoteConsumerState>(StringComparer.OrdinalIgnoreCase);
                    next.Add(peerUid, states);
                }

                if (states.ContainsKey(consumerGuid))
                {
                    throw new InvalidOperationException(
                        $"remote-consumer snapshot contains duplicate entry for peer {peerUid}, consumer '{consumerGuid}'");
                }
                states.Add(consumerGuid, state);
            }

            int trailingBytes = GameReflection.PackageSize(package) - GameReflection.PackageGetPos(package);
            if (trailingBytes != 0)
            {
                throw new InvalidOperationException(
                    $"remote-consumer snapshot contains {trailingBytes} unexpected trailing byte(s)");
            }

            lock (remoteConsumerStatesLock)
            {
                remoteConsumerStates = next;
            }
        }
        catch (Exception e)
        {
            ClearRemoteConsumerStates();
            ConditionalConfigSync.VersionErrorLog(
                "RemoteConsumer",
                PluginInfoCCS.PluginName,
                $"Ignored invalid remote-consumer capability snapshot and cleared cached capability state: {e.Message}");
        }
    }

    private static void BroadcastRemoteConsumerSnapshots(ZNet znet, ZNetPeer? excludedPeer = null)
    {
        if (!GameReflection.IsServer(znet))
        {
            return;
        }

        foreach (ZNetPeer recipient in GameReflection.GetPeers(znet).ToArray())
        {
            if (ReferenceEquals(recipient, excludedPeer) || !GameReflection.IsPeerReady(recipient))
            {
                continue;
            }

            SendRemoteConsumerSnapshot(recipient, znet, excludedPeer);
        }
    }

    private static void SendRemoteConsumerSnapshot(ZNetPeer recipient, ZNet znet, ZNetPeer? excludedPeer)
    {
        ZRpc recipientRpc = GameReflection.GetPeerRpc(recipient);
        List<RemoteConsumerSnapshotEntry> entries = new();
        long serverUid = GameReflection.GetServerRoutedUid();

        if (advertisedClientConsumers.TryGetValue(
                recipientRpc,
                out Dictionary<string, VersionHandshakeState>? recipientConsumers))
        {
            foreach (KeyValuePair<string, VersionHandshakeState> advertised in recipientConsumers)
            {
                string consumerGuid = advertised.Key;
                VersionHandshakeState recipientState = advertised.Value;
                VersionCheck? serverCheck = FindVersionCheck(consumerGuid);

                if (serverUid != 0 && serverCheck != null)
                {
                    RemoteConsumerState serverState = serverCheck.ValidatedClients.Contains(recipientRpc)
                        ? RemoteConsumerState.Compatible
                        : RemoteConsumerState.Incompatible;
                    entries.Add(new RemoteConsumerSnapshotEntry
                    {
                        PeerUid = serverUid,
                        ConsumerGuid = consumerGuid,
                        State = serverState,
                    });
                }

                foreach (ZNetPeer target in GameReflection.GetPeers(znet))
                {
                    if (ReferenceEquals(target, recipient)
                        || ReferenceEquals(target, excludedPeer)
                        || !GameReflection.IsPeerReady(target))
                    {
                        continue;
                    }

                    long targetUid = GameReflection.GetPeerUid(target);
                    if (targetUid == 0)
                    {
                        continue;
                    }

                    ZRpc targetRpc = GameReflection.GetPeerRpc(target);
                    RemoteConsumerState state;
                    if (advertisedClientConsumers.TryGetValue(
                            targetRpc,
                            out Dictionary<string, VersionHandshakeState>? targetConsumers)
                        && targetConsumers.TryGetValue(consumerGuid, out VersionHandshakeState? targetState))
                    {
                        state = ComparePeerConsumers(
                            serverCheck, recipientRpc, targetRpc, recipientState, targetState);
                    }
                    else
                    {
                        // Without a matching server check, silence is ambiguous: the target may be vanilla/missing or
                        // may run an older fixed-optional consumer that intentionally does not advertise. Returning
                        // Unknown is conservative; HasCompatibleConsumer remains false either way.
                        state = serverCheck != null && serverCheck.ShouldSendClientHandshake()
                            ? RemoteConsumerState.Missing
                            : RemoteConsumerState.Unknown;
                    }

                    if (state != RemoteConsumerState.Unknown)
                    {
                        entries.Add(new RemoteConsumerSnapshotEntry
                        {
                            PeerUid = targetUid,
                            ConsumerGuid = consumerGuid,
                            State = state,
                        });
                    }
                }
            }
        }

        if (entries.Count > MaxRemoteConsumerSnapshotEntries)
        {
            ConditionalConfigSync.VersionErrorLog(
                "RemoteConsumer",
                PluginInfoCCS.PluginName,
                $"Remote-consumer capability snapshot for peer {GameReflection.GetPeerUid(recipient)} contains " +
                $"{entries.Count} entries and exceeds the {MaxRemoteConsumerSnapshotEntries}-entry limit. " +
                "Clearing that client's capability cache instead of leaving stale state active.");
            SendEmptyRemoteConsumerSnapshot(recipientRpc);
            return;
        }

        ZPackage package = GameReflection.NewPackage();
        GameReflection.PackageWrite(package, RemoteConsumerSnapshotFormatVersion);
        GameReflection.PackageWrite(package, entries.Count);
        foreach (RemoteConsumerSnapshotEntry entry in entries)
        {
            GameReflection.PackageWrite(package, entry.PeerUid);
            GameReflection.PackageWrite(package, entry.ConsumerGuid);
            GameReflection.PackageWrite(package, (byte)entry.State);
        }

        if (GameReflection.PackageSize(package) > MaxRemoteConsumerSnapshotPackageLength)
        {
            ConditionalConfigSync.VersionErrorLog(
                "RemoteConsumer",
                PluginInfoCCS.PluginName,
                $"Remote-consumer capability snapshot for peer {GameReflection.GetPeerUid(recipient)} exceeds the " +
                $"{MaxRemoteConsumerSnapshotPackageLength}-byte package limit. Clearing that client's capability " +
                "cache instead of leaving stale state active.");
            SendEmptyRemoteConsumerSnapshot(recipientRpc);
            return;
        }

        GameReflection.InvokeRpc(recipientRpc, RemoteConsumerSnapshotRpcName, package);
    }

    private static void SendEmptyRemoteConsumerSnapshot(ZRpc recipientRpc)
    {
        ZPackage package = GameReflection.NewPackage();
        GameReflection.PackageWrite(package, RemoteConsumerSnapshotFormatVersion);
        GameReflection.PackageWrite(package, 0);
        GameReflection.InvokeRpc(recipientRpc, RemoteConsumerSnapshotRpcName, package);
    }

    private static void ClearRemoteConsumerStates()
    {
        lock (remoteConsumerStatesLock)
        {
            remoteConsumerStates.Clear();
        }
    }
}
