using System;
using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Describes one effective policy-state transition for a registered config entry.
/// </summary>
/// <remarks>
/// This event data is raised on the Unity main thread. It is useful when a mod must rebuild runtime state after an
/// administrator changes SyncPolicy or HiddenConfigs while the server is running, or when a client receives a new
/// effective state from the server.
/// </remarks>
[Description("Details an effective server-controlled or hidden-state transition for one config entry.")]
public sealed class PolicyStateChangedEventArgs : EventArgs
{
    internal PolicyStateChangedEventArgs(
        OwnConfigEntryBase config,
        bool oldServerControlled,
        bool newServerControlled,
        bool oldHidden,
        bool newHidden,
        string source)
    {
        Config = config;
        OldServerControlled = oldServerControlled;
        NewServerControlled = newServerControlled;
        OldHidden = oldHidden;
        NewHidden = newHidden;
        Source = source;
    }

    /// <summary>The config entry whose effective policy state changed.</summary>
    public OwnConfigEntryBase Config { get; }
    /// <summary>The previous effective server-controlled state.</summary>
    public bool OldServerControlled { get; }
    /// <summary>The new effective server-controlled state.</summary>
    public bool NewServerControlled { get; }
    /// <summary>The previous effective hidden state.</summary>
    public bool OldHidden { get; }
    /// <summary>The new effective hidden state.</summary>
    public bool NewHidden { get; }
    /// <summary>Human-readable origin such as a policy reload or an incoming server package.</summary>
    public string Source { get; }
}

/// <summary>
/// Describes a synchronization operation that was deliberately rejected before it could be applied or sent.
/// </summary>
/// <remarks>
/// Typical reasons include permission checks, malformed fragments, queue overflow, payloads larger than the configured
/// safety limit, or a package-level exception. This event is diagnostic; handlers should not retry automatically
/// without understanding the reason, because doing so can create a retry loop.
/// </remarks>
[Description("Diagnostic details for an incoming or outgoing synchronization operation that was rejected.")]
public sealed class SyncRejectedEventArgs : EventArgs
{
    internal SyncRejectedEventArgs(string reason, long? senderUid, bool incoming, Exception? exception = null)
    {
        Reason = reason;
        SenderUid = senderUid;
        Incoming = incoming;
        Exception = exception;
    }

    /// <summary>Human-readable rejection reason suitable for logs and diagnostics.</summary>
    public string Reason { get; }
    /// <summary>Remote sender UID for an incoming package, or <see langword="null"/> for a local outgoing rejection.</summary>
    public long? SenderUid { get; }
    /// <summary><see langword="true"/> for an incoming package; <see langword="false"/> for a local outgoing operation.</summary>
    public bool Incoming { get; }
    /// <summary>The originating exception when rejection followed an exception, otherwise <see langword="null"/>.</summary>
    public Exception? Exception { get; }
}
