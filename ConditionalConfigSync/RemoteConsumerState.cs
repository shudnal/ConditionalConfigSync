using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>Describes whether a specific remote peer exposes a compatible Conditional Config Sync consumer.</summary>
[Description("Compatibility state of a Conditional Config Sync consumer on a specific remote peer.")]
public enum RemoteConsumerState
{
    /// <summary>
    /// The state cannot currently be determined. This is returned before peer admission completes, for disconnected
    /// peers, when the server cannot authoritatively evaluate the consumer, or when an older deployment does not
    /// provide peer capability information.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Peer admission has completed and the remote peer did not advertise a consumer that is expected to advertise
    /// whenever it is installed.
    /// </summary>
    Missing = 1,

    /// <summary>The remote peer advertised the consumer, but it did not satisfy the applicable CCS compatibility check.</summary>
    Incompatible = 2,

    /// <summary>The remote peer advertised the consumer and passed the applicable CCS version/protocol compatibility check.</summary>
    Compatible = 3,
}
