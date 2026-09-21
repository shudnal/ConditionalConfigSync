using System.ComponentModel;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    /// <summary>
    /// Gets the compatibility state of a Conditional Config Sync consumer on a connected remote peer.
    /// </summary>
    /// <param name="consumerGuid">Stable consumer/mod GUID used to create its <see cref="ConfigSync"/> or <see cref="VersionCheck"/>.</param>
    /// <param name="peerUid">Valheim routed peer UID (<c>ZNetPeer.m_uid</c>).</param>
    /// <returns>
    /// The current <see cref="RemoteConsumerState"/>. <see cref="RemoteConsumerState.Unknown"/> is returned when the
    /// peer is not currently admitted, the local/server CCS runtime cannot authoritatively evaluate that consumer, or
    /// capability data has not been supplied by the connected server.
    /// </returns>
    /// <remarks>
    /// On the server, the result is derived directly from the existing version-handshake state for the requested peer.
    /// On a client, the connected server distributes compact per-peer capability snapshots derived from the same
    /// handshake state; raw remote versions and handshake payloads are not exposed through this API.
    ///
    /// For client-to-client queries the server compares the two peers' existing consumer handshakes. When the server
    /// also has that consumer registered, its ordinary validation result is enforced as well and absence can be reported
    /// as <see cref="RemoteConsumerState.Missing"/> when advertisement is guaranteed. Without a server-side consumer,
    /// two advertising peers can still be compared, while a silent target remains <see cref="RemoteConsumerState.Unknown"/>.
    /// </remarks>
    [Description("Gets the compatibility state of a CCS consumer on a connected routed peer UID.")]
    public static RemoteConsumerState GetRemoteConsumerState(string consumerGuid, long peerUid)
    {
        if (string.IsNullOrWhiteSpace(consumerGuid) || peerUid == 0)
        {
            return RemoteConsumerState.Unknown;
        }

        return VersionCheck.GetRemoteConsumerState(consumerGuid.Trim(), peerUid);
    }

    /// <summary>Returns whether a connected remote peer exposes a compatible CCS consumer.</summary>
    /// <param name="consumerGuid">Stable consumer/mod GUID.</param>
    /// <param name="peerUid">Valheim routed peer UID (<c>ZNetPeer.m_uid</c>).</param>
    [Description("Returns whether a connected routed peer has a compatible CCS consumer.")]
    public static bool HasCompatibleConsumer(string consumerGuid, long peerUid)
        => GetRemoteConsumerState(consumerGuid, peerUid) == RemoteConsumerState.Compatible;
}
