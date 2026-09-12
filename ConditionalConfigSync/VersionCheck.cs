using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ConditionalConfigSync;

/// <summary>
/// Performs peer version compatibility checks for a mod.
/// </summary>
/// <remarks>
/// A <see cref="ConditionalConfigSync"/> instance creates and maintains its own version check automatically. Construct
/// this class directly only for a mod that needs version validation without registering synchronized values.
/// </remarks>
[Description("Performs peer version compatibility checks for a mod.")]
public partial class VersionCheck
{
    private const string VersionCheckRpcName = "ConditionalConfigSync VersionCheck";
    private const string DisconnectReasonRpcName = "ConditionalConfigSync DisconnectReason";
    private const string JotunnHarmonyId = "com.jotunn.jotunn";
    private const string ServerSyncHarmonyId = "org.bepinex.helpers.ServerSync";
    private const string ConnectionErrorHeader = "Conditional Config Sync rejected this connection.";
    private const int DisconnectReportFormatVersion = 1;
    private const int MaxDisconnectReasonItems = 32;
    private const int MaxDisconnectReasonLength = 8192;
    private const int MaxDisconnectReasonTextLength = 2048;
    private const int MaxDisconnectModNameLength = 256;
    private const int MaxDisconnectReportPackageLength = 512 * 1024;
    private static readonly TimeSpan DisconnectReasonLifetime = TimeSpan.FromSeconds(30);

    private static readonly HashSet<VersionCheck> versionChecks = new();
    private static readonly Dictionary<string, string> notProcessedNames = new();
    private static readonly Dictionary<ZRpc, string> malformedHandshakeErrors = new();
    private static readonly Dictionary<ZRpc, long> connectionStartedAt = new();

    private static long nextClientConnectionGeneration;
    private static long activeClientConnectionGeneration;
    private static long expiredClientConnectionGeneration;
    private static long injectedClientConnectionGeneration;
    private static PendingDisconnectReport? pendingDisconnectReport;
    private static PendingConnectionDiagnostic? pendingMalformedServerHandshakeError;

    private sealed class VersionHandshakeState
    {
        internal string MinimumRequiredVersion = string.Empty;
        internal string CurrentVersion = string.Empty;
        internal bool ProtocolFieldPresent;
        internal int ProtocolVersion;
        internal bool PackageVersionFieldPresent;
        internal string PackageVersion = string.Empty;
        internal int TrailingBytes;
    }

    private enum DisconnectReasonCode : byte
    {
        Unknown = 0,
        HandshakeMissing = 1,
        ProtocolNotReported = 2,
        ProtocolMismatch = 3,
        RemoteVersionInvalid = 4,
        LocalVersionInvalid = 5,
        RemoteVersionTooOld = 6,
        LocalVersionTooOld = 7,
        MalformedHandshake = 8,
        MissingConsumerRegistration = 9,
    }

    private sealed class DisconnectReasonItem
    {
        internal DisconnectReasonCode Code;
        internal string ModName = string.Empty;
        internal string Message = string.Empty;
    }

    private sealed class PendingDisconnectReport
    {
        internal long ConnectionGeneration;
        internal string ReportId = string.Empty;
        internal DateTime ReceivedAtUtc;
        internal string Message = string.Empty;
    }

    private sealed class PendingConnectionDiagnostic
    {
        internal long ConnectionGeneration;
        internal string Message = string.Empty;
    }

    private enum VersionFailureKind
    {
        None,
        HandshakeMissing,
        ProtocolMissing,
        ProtocolMismatch,
        RemoteVersionInvalid,
        LocalVersionInvalid,
        RemoteVersionTooOld,
        LocalVersionTooOld,
    }

    /// <summary>Stable mod identifier used by the version-check RPC.</summary>
    [Description("Stable mod identifier used by the version-check RPC.")]
    public string Name;

    private string? displayName;

    /// <summary>Human-readable mod name used in connection errors and logs.</summary>
    [Description("Human-readable mod name used in version errors and logs.")]
    public string DisplayName
    {
        get => displayName ?? Name;
        set => displayName = value;
    }

    private string? currentVersion;

    /// <summary>Current local mod version.</summary>
    [Description("Current local mod version.")]
    public string CurrentVersion
    {
        get => currentVersion ?? "0.0.0";
        set => currentVersion = value;
    }

    private string? minimumRequiredVersion;

    /// <summary>Oldest compatible version accepted from the remote peer.</summary>
    [Description("Oldest compatible version accepted from the remote peer.")]
    public string MinimumRequiredVersion
    {
        get => GetMinimumRequiredVersion(ModRequired);
        set => minimumRequiredVersion = value;
    }

    private string GetMinimumRequiredVersion(bool required)
    {
        return minimumRequiredVersion ?? (required ? CurrentVersion : "0.0.0");
    }

    /// <summary>
    /// Gets or sets whether the checked mod must be installed and compatible on the remote peer.
    /// </summary>
    /// <remarks>
    /// When enabled on a client, the server must have a compatible copy of the mod. When enabled on a server, every
    /// connecting client must have a compatible copy. A standalone <see cref="VersionCheck"/> defaults to
    /// <see langword="true"/>. A version check created for <see cref="ConditionalConfigSync"/> mirrors that instance's
    /// <see cref="ConditionalConfigSync.ModRequired"/> value. Configure it before the connection handshake.
    /// </remarks>
    [Description("Whether the checked mod must be installed and compatible on the remote peer.")]
    public bool ModRequired { get; set; } = true;

    private VersionHandshakeState? receivedServerHandshake;

    /// <summary>
    /// Protocol version reported by the connected server. Zero means that the version handshake has not completed or
    /// that the server did not provide protocol metadata.
    /// </summary>
    [Description("Protocol version reported by the connected server, or zero before the handshake completes.")]
    public static int RemoteServerProtocolVersion { get; private set; }

    /// <summary>Whether protocol metadata has already been received from the connected server.</summary>
    [Description("Whether the connected server's ConditionalConfigSync protocol is known.")]
    public static bool RemoteServerProtocolKnown { get; private set; }

    // Tracks which clients have passed the version check and preserves per-peer admission state and diagnostics.
    private readonly HashSet<ZRpc> ValidatedClients = new();
    private readonly Dictionary<ZRpc, VersionHandshakeState> receivedClientHandshakes = new();
    private readonly Dictionary<ZRpc, bool> requiredClients = new();

    // Optional backing field to use ConditionalConfigSync values (will override other fields).
    private readonly ConditionalConfigSync? configSync;

    // Version-check patches are also explicit for the same embedded-copy protection.
    internal static void ApplyRuntimePatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }),
            prefix: CreateHarmonyMethod(nameof(RPC_PeerInfo)),
            postfix: CreateHarmonyMethod(nameof(RPC_PeerInfoCompleted)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }),
            prefix: CreateHarmonyMethod(nameof(RegisterAndCheckVersion)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) }),
            prefix: CreateHarmonyMethod(nameof(RemoveDisconnected)));

        HarmonyMethod showConnectionErrorPostfix = CreateHarmonyMethod(nameof(ShowConnectionError));
        showConnectionErrorPostfix.priority = Priority.First;
        showConnectionErrorPostfix.before = new[] { ServerSyncHarmonyId, JotunnHarmonyId };
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(FejdStartup), "ShowConnectError", new[] { typeof(ZNet.ConnectionStatus) }),
            postfix: showConnectionErrorPostfix);
    }

    private static HarmonyMethod CreateHarmonyMethod(string methodName)
    {
        MethodInfo method = AccessTools.DeclaredMethod(typeof(VersionCheck), methodName)
                            ?? throw new MissingMethodException(typeof(VersionCheck).FullName, methodName);
        return new HarmonyMethod(method);
    }

    /// <summary>Creates a standalone version check not backed by a synchronization instance.</summary>
    /// <param name="name">Stable unique mod identifier.</param>
    [Description("Creates a standalone version check without synchronized values.")]
    public VersionCheck(string name)
    {
        ConditionalConfigSync.EnsureRuntimeReady();
        Name = name;
        ModRequired = true;
        versionChecks.Add(this);
    }

    /// <summary>Creates a version check backed by a <see cref="ConditionalConfigSync"/> instance.</summary>
    /// <param name="configSync">The synchronization instance whose version fields should be used.</param>
    [Description("Creates a version check backed by a ConditionalConfigSync instance.")]
    public VersionCheck(ConditionalConfigSync configSync)
    {
        ConditionalConfigSync.EnsureRuntimeReady();
        this.configSync = configSync;
        Name = configSync.Name;
        versionChecks.Add(this);
    }

    /// <summary>
    /// Clears received peer-version state and refreshes fields from the backing synchronization instance.
    /// </summary>
    /// <remarks>ConditionalConfigSync calls this as part of its connection lifecycle; most mods do not call it directly.</remarks>
    [Description("Clears received peer state and refreshes fields from the backing sync instance.")]
    public void Initialize()
    {
        receivedServerHandshake = null;
        RefreshFromConfigSync();
    }

    private void RefreshFromConfigSync()
    {
        if (configSync == null)
        {
            return;
        }

        Name = configSync.Name;
        DisplayName = configSync.DisplayName!;
        CurrentVersion = configSync.CurrentVersion!;
        MinimumRequiredVersion = configSync.MinimumRequiredVersion!;
        ModRequired = configSync.ModRequired;
    }

    private void ResetPeerState(ZRpc rpc)
    {
        receivedClientHandshakes.Remove(rpc);
        ValidatedClients.Remove(rpc);
        requiredClients.Remove(rpc);
    }

    private void SnapshotRequirementForPeer(ZRpc rpc)
    {
        requiredClients[rpc] = configSync?.ComputeEffectiveModRequired() ?? ModRequired;
    }

    private bool IsRequiredForPeer(ZRpc rpc)
    {
        return requiredClients.TryGetValue(rpc, out bool required)
            ? required
            : configSync?.ComputeEffectiveModRequired() ?? ModRequired;
    }

    private string GetMinimumRequiredVersionForPeer(ZRpc rpc)
    {
        // Relaxing presence must not also relax the author's compatibility range for peers that do advertise the mod.
        // Strengthening an author-default optional consumer to required gets the same CurrentVersion fallback as a
        // fixed required consumer when no explicit MinimumRequiredVersion was supplied.
        return GetMinimumRequiredVersion(ModRequired || IsRequiredForPeer(rpc));
    }

    private bool ShouldSendClientHandshake()
    {
        return ModRequired || configSync?.ModRequirementMode == ModRequirementMode.Conditional;
    }

    private bool ShouldValidateAdvertisedClient(ZRpc rpc)
    {
        return configSync?.ModRequirementMode == ModRequirementMode.Conditional
               && receivedClientHandshakes.ContainsKey(rpc);
    }

    private static VersionFailureKind GetFailure(
        VersionHandshakeState? state,
        string localCurrentVersion,
        string localMinimumRequiredVersion)
    {
        if (state == null)
        {
            return VersionFailureKind.HandshakeMissing;
        }

        if (!state.ProtocolFieldPresent)
        {
            return VersionFailureKind.ProtocolMissing;
        }

        if (state.ProtocolVersion != PluginInfoCCS.ProtocolVersion)
        {
            return VersionFailureKind.ProtocolMismatch;
        }

        if (!System.Version.TryParse(state.CurrentVersion, out System.Version? remoteCurrent)
            || !System.Version.TryParse(state.MinimumRequiredVersion, out System.Version? remoteMinimum))
        {
            return VersionFailureKind.RemoteVersionInvalid;
        }

        if (!System.Version.TryParse(localCurrentVersion, out System.Version? localCurrent)
            || !System.Version.TryParse(localMinimumRequiredVersion, out System.Version? localMinimum))
        {
            return VersionFailureKind.LocalVersionInvalid;
        }

        if (remoteCurrent < localMinimum)
        {
            return VersionFailureKind.RemoteVersionTooOld;
        }

        if (localCurrent < remoteMinimum)
        {
            return VersionFailureKind.LocalVersionTooOld;
        }

        return VersionFailureKind.None;
    }

    private bool IsVersionOk()
    {
        if (receivedServerHandshake == null)
        {
            return !ModRequired;
        }

        return GetFailure(receivedServerHandshake, CurrentVersion, MinimumRequiredVersion) == VersionFailureKind.None;
    }

    private bool IsVersionOk(VersionHandshakeState state, ZRpc rpc)
    {
        return GetFailure(state, CurrentVersion, GetMinimumRequiredVersionForPeer(rpc)) == VersionFailureKind.None;
    }

    private void ResolveMissingOptionalServer()
    {
        RefreshFromConfigSync();
        if (configSync == null || ModRequired || receivedServerHandshake != null)
        {
            return;
        }

        configSync.UseLocalStateForMissingOptionalServer();
    }

    private string ErrorClient()
    {
        VersionHandshakeState? state = receivedServerHandshake;
        VersionFailureKind failure = GetFailure(state, CurrentVersion, MinimumRequiredVersion);
        switch (failure)
        {
            case VersionFailureKind.HandshakeMissing:
                return $"{DisplayName}: no version handshake was received from the server.";
            case VersionFailureKind.ProtocolMissing:
                return $"{DisplayName}: the server sent a legacy or incomplete ConditionalConfigSync handshake without protocol metadata. " +
                       $"This client requires protocol {PluginInfoCCS.ProtocolVersion}.";
            case VersionFailureKind.ProtocolMismatch:
                return $"{DisplayName}: the server uses ConditionalConfigSync protocol {state!.ProtocolVersion}, but this client requires protocol {PluginInfoCCS.ProtocolVersion}.";
            case VersionFailureKind.RemoteVersionInvalid:
                return $"{DisplayName}: the server reported an invalid mod version or minimum version " +
                       $"(version '{state!.CurrentVersion}', minimum '{state.MinimumRequiredVersion}').";
            case VersionFailureKind.LocalVersionInvalid:
                return $"{DisplayName}: this client has an invalid local version requirement " +
                       $"(version '{CurrentVersion}', minimum '{MinimumRequiredVersion}').";
            case VersionFailureKind.RemoteVersionTooOld:
                return $"{DisplayName}: the server has version {state!.CurrentVersion}, but this client requires at least {MinimumRequiredVersion}.";
            case VersionFailureKind.LocalVersionTooOld:
                return $"{DisplayName}: the server requires at least version {state!.MinimumRequiredVersion}, but this client has {CurrentVersion}.";
            default:
                return $"{DisplayName}: the version handshake was rejected for an unknown compatibility reason.";
        }
    }

    private string ErrorServer(ZRpc rpc)
    {
        receivedClientHandshakes.TryGetValue(rpc, out VersionHandshakeState? state);
        string localMinimumRequiredVersion = GetMinimumRequiredVersionForPeer(rpc);
        VersionFailureKind failure = GetFailure(state, CurrentVersion, localMinimumRequiredVersion);
        string client = FormatRemotePeer(rpc, "client");
        string elapsed = GetHandshakeElapsed(rpc);
        string malformed = malformedHandshakeErrors.TryGetValue(rpc, out string? malformedError)
            ? $" A malformed version package was also observed: {malformedError}."
            : string.Empty;

        switch (failure)
        {
            case VersionFailureKind.HandshakeMissing:
                return $"Disconnect: The {client} did not send a matching version handshake for {DisplayName} before PeerInfo " +
                       $"({elapsed}; expected GUID '{Name}', client version >= {localMinimumRequiredVersion}, server version {CurrentVersion}, " +
                       $"server ConditionalConfigSync {PluginInfoCCS.PluginVersion}, protocol {PluginInfoCCS.ProtocolVersion}). " +
                       "No version payload for this mod was received. Possible causes include a missing or disabled client mod, an older pre-CCS mod build, " +
                       "Conditional Config Sync missing or failing to load, or duplicate or outdated DLL files." + malformed;
            case VersionFailureKind.ProtocolMissing:
                return $"Disconnect: The {client} sent an incomplete legacy version handshake for {DisplayName} without ConditionalConfigSync protocol metadata " +
                       $"({elapsed}; client mod version {state!.CurrentVersion}, client minimum {state.MinimumRequiredVersion}, " +
                       $"client ConditionalConfigSync {FormatPackageVersion(state)}, server ConditionalConfigSync {PluginInfoCCS.PluginVersion}, required protocol {PluginInfoCCS.ProtocolVersion}).";
            case VersionFailureKind.ProtocolMismatch:
                return $"Disconnect: The {client} uses incompatible ConditionalConfigSync protocol {state!.ProtocolVersion} for {DisplayName} " +
                       $"({elapsed}; client ConditionalConfigSync {FormatPackageVersion(state)}, server ConditionalConfigSync {PluginInfoCCS.PluginVersion}, " +
                       $"required protocol {PluginInfoCCS.ProtocolVersion}).";
            case VersionFailureKind.RemoteVersionInvalid:
                return $"Disconnect: The {client} reported an invalid {DisplayName} version requirement " +
                       $"({elapsed}; client version '{state!.CurrentVersion}', client minimum '{state.MinimumRequiredVersion}', " +
                       $"client ConditionalConfigSync {FormatPackageVersion(state)}, protocol {FormatProtocol(state)}).";
            case VersionFailureKind.LocalVersionInvalid:
                return $"Disconnect: The server has an invalid local {DisplayName} version requirement " +
                       $"(server version '{CurrentVersion}', server minimum '{localMinimumRequiredVersion}'). The {client} cannot be validated.";
            case VersionFailureKind.RemoteVersionTooOld:
                return $"Disconnect: The {client} has {DisplayName} version {state!.CurrentVersion}, but the server requires at least {localMinimumRequiredVersion} " +
                       $"({elapsed}; client ConditionalConfigSync {FormatPackageVersion(state)}, protocol {FormatProtocol(state)}).";
            case VersionFailureKind.LocalVersionTooOld:
                return $"Disconnect: The {client} requires {DisplayName} version {state!.MinimumRequiredVersion}, but the server has {CurrentVersion} " +
                       $"({elapsed}; client ConditionalConfigSync {FormatPackageVersion(state)}, protocol {FormatProtocol(state)}).";
            default:
                return $"Disconnect: The {client} failed the {DisplayName} version check for an unknown compatibility reason ({elapsed}).";
        }
    }

    private DisconnectReasonItem ClientVisibleServerError(ZRpc rpc)
    {
        receivedClientHandshakes.TryGetValue(rpc, out VersionHandshakeState? state);
        string localMinimumRequiredVersion = GetMinimumRequiredVersionForPeer(rpc);
        VersionFailureKind failure = GetFailure(state, CurrentVersion, localMinimumRequiredVersion);
        return CreateDisconnectReasonItem(failure, state, localMinimumRequiredVersion, remoteIsClient: true);
    }

    private DisconnectReasonItem ClientVisibleClientError()
    {
        VersionHandshakeState? state = receivedServerHandshake;
        VersionFailureKind failure = GetFailure(state, CurrentVersion, MinimumRequiredVersion);
        return CreateDisconnectReasonItem(failure, state, MinimumRequiredVersion, remoteIsClient: false);
    }

    private DisconnectReasonItem CreateDisconnectReasonItem(
        VersionFailureKind failure,
        VersionHandshakeState? state,
        string localMinimumRequiredVersion,
        bool remoteIsClient)
    {
        string remoteSide = remoteIsClient ? "client" : "server";
        string localSide = remoteIsClient ? "server" : "client";
        DisconnectReasonCode code;
        string message;

        switch (failure)
        {
            case VersionFailureKind.HandshakeMissing:
                code = DisconnectReasonCode.HandshakeMissing;
                message = remoteIsClient
                    ? $"The server did not receive the required synchronization handshake. The server requires {DisplayName} {localMinimumRequiredVersion} or newer and Conditional Config Sync protocol {PluginInfoCCS.ProtocolVersion}."
                    : "No version handshake was received from the server.";
                break;
            case VersionFailureKind.ProtocolMissing:
                code = DisconnectReasonCode.ProtocolNotReported;
                message = $"The {remoteSide} sent a legacy or incomplete handshake without Conditional Config Sync protocol metadata. " +
                          $"The {localSide} requires protocol {PluginInfoCCS.ProtocolVersion}.";
                break;
            case VersionFailureKind.ProtocolMismatch:
                code = DisconnectReasonCode.ProtocolMismatch;
                message = $"The {remoteSide} reported Conditional Config Sync protocol {state!.ProtocolVersion}, but the {localSide} requires protocol {PluginInfoCCS.ProtocolVersion}.";
                break;
            case VersionFailureKind.RemoteVersionInvalid:
                code = DisconnectReasonCode.RemoteVersionInvalid;
                message = $"The {remoteSide} reported an invalid mod version or minimum version.";
                break;
            case VersionFailureKind.LocalVersionInvalid:
                code = DisconnectReasonCode.LocalVersionInvalid;
                message = $"The {localSide} has an invalid local version requirement and could not validate the connection.";
                break;
            case VersionFailureKind.RemoteVersionTooOld:
                code = DisconnectReasonCode.RemoteVersionTooOld;
                message = $"The {remoteSide} has version {state!.CurrentVersion}, but the {localSide} requires at least {localMinimumRequiredVersion}.";
                break;
            case VersionFailureKind.LocalVersionTooOld:
                code = DisconnectReasonCode.LocalVersionTooOld;
                message = $"The {remoteSide} requires at least version {state!.MinimumRequiredVersion}, but the {localSide} has {CurrentVersion}.";
                break;
            default:
                code = DisconnectReasonCode.Unknown;
                message = "The version handshake was rejected for an unknown compatibility reason.";
                break;
        }

        return new DisconnectReasonItem
        {
            Code = code,
            ModName = DisplayName,
            Message = message,
        };
    }

    private static string FormatProtocol(VersionHandshakeState state)
    {
        return state.ProtocolFieldPresent ? state.ProtocolVersion.ToString() : "missing";
    }

    private static string FormatPackageVersion(VersionHandshakeState state)
    {
        return state.PackageVersionFieldPresent && !string.IsNullOrWhiteSpace(state.PackageVersion)
            ? state.PackageVersion
            : "not reported";
    }

    private static string FormatRemotePeer(ZRpc rpc, string role)
    {
        ISocket? socket = GameReflection.GetRpcSocket(rpc);
        if (socket == null)
        {
            return role;
        }

        string identifier = GameReflection.SocketGetHostName(socket).Trim();
        if (string.IsNullOrEmpty(identifier))
        {
            identifier = GameReflection.SocketGetEndPointString(socket).Trim();
        }
        return string.IsNullOrEmpty(identifier) ? role : $"{role} ({identifier})";
    }

    private static string GetHandshakeElapsed(ZRpc rpc)
    {
        if (!connectionStartedAt.TryGetValue(rpc, out long startedAt))
        {
            return "elapsed time unavailable";
        }

        long elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - startedAt);
        return $"elapsed {TimeSpan.FromTicks(elapsedTicks).TotalMilliseconds:0} ms";
    }

    private static VersionCheck[] GetFailedClient()
    {
        return versionChecks.Where(check => !check.IsVersionOk()).ToArray();
    }

    private static VersionCheck[] GetFailedServer(ZRpc rpc)
    {
        return versionChecks
            .Where(check => !check.ValidatedClients.Contains(rpc)
                            && (check.IsRequiredForPeer(rpc) || check.ShouldValidateAdvertisedClient(rpc)))
            .ToArray();
    }

    private static void Logout()
    {
        GameReflection.Logout();
        GameReflection.SetConnectionStatus(ZNet.ConnectionStatus.ErrorVersion);
    }

    private static void DisconnectClient(ZRpc rpc, IEnumerable<DisconnectReasonItem> reasons)
    {
        DisconnectReasonItem[] normalizedReasons = NormalizeDisconnectReasons(reasons);
        string reportId = Guid.NewGuid().ToString("N");
        ConditionalConfigSync.VersionErrorLog(
            "Version",
            PluginInfoCCS.PluginName,
            $"Disconnect report {reportId}: sending {normalizedReasons.Length} CCS rejection reason(s) to the {FormatRemotePeer(rpc, "client")} before ErrorVersion.");
        try
        {
            ZPackage reasonPackage = GameReflection.NewPackage();
            GameReflection.PackageWrite(reasonPackage, DisconnectReportFormatVersion);
            GameReflection.PackageWrite(reasonPackage, reportId);
            GameReflection.PackageWrite(reasonPackage, PluginInfoCCS.PluginVersion);
            GameReflection.PackageWrite(reasonPackage, PluginInfoCCS.ProtocolVersion);
            GameReflection.PackageWrite(reasonPackage, normalizedReasons.Length);
            foreach (DisconnectReasonItem reason in normalizedReasons)
            {
                GameReflection.PackageWrite(reasonPackage, (byte)reason.Code);
                GameReflection.PackageWrite(reasonPackage, reason.ModName);
                GameReflection.PackageWrite(reasonPackage, reason.Message);
            }
            if (GameReflection.PackageSize(reasonPackage) > MaxDisconnectReportPackageLength)
            {
                throw new InvalidOperationException(
                    $"Disconnect report exceeds the {MaxDisconnectReportPackageLength}-byte package limit.");
            }
            GameReflection.InvokeRpc(rpc, DisconnectReasonRpcName, reasonPackage);
        }
        catch (Exception e)
        {
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"Failed to send disconnect report {reportId} to the {FormatRemotePeer(rpc, "client")}: {e.Message}");
        }

        GameReflection.InvokeRpc(rpc, "Error", (int)ZNet.ConnectionStatus.ErrorVersion);
    }

    private static DisconnectReasonItem[] NormalizeDisconnectReasons(IEnumerable<DisconnectReasonItem> reasons)
    {
        return reasons
            .Where(reason => reason != null)
            .Select(reason => new DisconnectReasonItem
            {
                Code = Enum.IsDefined(typeof(DisconnectReasonCode), reason.Code)
                    ? reason.Code
                    : DisconnectReasonCode.Unknown,
                ModName = NormalizeSingleLine(reason.ModName, MaxDisconnectModNameLength),
                Message = NormalizeSingleLine(reason.Message, MaxDisconnectReasonTextLength),
            })
            .Where(reason => !string.IsNullOrWhiteSpace(reason.Message))
            .GroupBy(
                reason => $"{(byte)reason.Code}\n{reason.ModName}\n{reason.Message}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaxDisconnectReasonItems)
            .ToArray();
    }

    private static string BuildConnectionFailureMessage(IEnumerable<DisconnectReasonItem> reasons)
    {
        DisconnectReasonItem[] uniqueReasons = NormalizeDisconnectReasons(reasons);
        string message = ConnectionErrorHeader;

        if (uniqueReasons.Length > 0)
        {
            message += "\n\nThe following synchronization checks failed:\n";
            message += string.Join(
                "\n",
                uniqueReasons.Select(reason =>
                {
                    string prefix = string.IsNullOrWhiteSpace(reason.ModName)
                        ? string.Empty
                        : reason.ModName + ": ";
                    return "- " + prefix + reason.Message;
                }));
        }

        if (uniqueReasons.Any(reason =>
                reason.Code == DisconnectReasonCode.HandshakeMissing
                || reason.Code == DisconnectReasonCode.MissingConsumerRegistration))
        {
            message += "\n\nA missing handshake can mean that the mod is missing or disabled, an older pre-CCS build is installed, " +
                       "Conditional Config Sync did not load, or duplicate or outdated DLL files are present.";
        }

        message += "\n\nCheck that the listed mods and Conditional Config Sync are installed, enabled, and up to date. " +
                   "Remove duplicate or outdated DLL files, then fully restart the game.";

        return Truncate(message, MaxDisconnectReasonLength);
    }

    private static string NormalizeSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        char[] normalized = value
            .Trim()
            .Select(character => character switch
            {
                '<' => '‹',
                '>' => '›',
                _ when char.IsControl(character) => ' ',
                _ => character,
            })
            .ToArray();
        string compact = string.Join(
            " ",
            new string(normalized).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return Truncate(compact, maxLength);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return maxLength <= 3
            ? value.Substring(0, maxLength)
            : value.Substring(0, maxLength - 3) + "...";
    }

    private static void SetPendingDisconnectReport(
        long connectionGeneration,
        string reportId,
        IEnumerable<DisconnectReasonItem> reasons)
    {
        if (connectionGeneration <= 0 || connectionGeneration != activeClientConnectionGeneration
            || expiredClientConnectionGeneration == connectionGeneration)
        {
            return;
        }

        pendingDisconnectReport = new PendingDisconnectReport
        {
            ConnectionGeneration = connectionGeneration,
            ReportId = NormalizeSingleLine(reportId, 128),
            ReceivedAtUtc = DateTime.UtcNow,
            Message = BuildConnectionFailureMessage(reasons),
        };
    }

    private static void SetPendingLegacyDisconnectReport(
        long connectionGeneration,
        string reportId,
        string message)
    {
        if (connectionGeneration <= 0 || connectionGeneration != activeClientConnectionGeneration
            || expiredClientConnectionGeneration == connectionGeneration)
        {
            return;
        }

        string normalized = Truncate(
            string.Join(
                "\n",
                message.Replace("\r", string.Empty)
                    .Split('\n')
                    .Select(line => NormalizeSingleLine(line, MaxDisconnectReasonTextLength))
                    .Where(line => !string.IsNullOrWhiteSpace(line))),
            MaxDisconnectReasonLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ConnectionErrorHeader +
                         "\n\nThe server rejected the connection because a required synchronization check failed, but no detailed reason was provided.";
        }

        pendingDisconnectReport = new PendingDisconnectReport
        {
            ConnectionGeneration = connectionGeneration,
            ReportId = NormalizeSingleLine(reportId, 128),
            ReceivedAtUtc = DateTime.UtcNow,
            Message = normalized,
        };
    }

    private static void ReceiveDisconnectReason(ZRpc rpc, ZPackage package, long connectionGeneration)
    {
        if (GameReflection.IsServer())
        {
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"Ignored an unexpected disconnect-reason RPC from the {FormatRemotePeer(rpc, "client")}.");
            return;
        }

        int initialPosition = GameReflection.PackageGetPos(package);
        try
        {
            if (GameReflection.PackageSize(package) > MaxDisconnectReportPackageLength)
            {
                throw new InvalidOperationException(
                    $"Disconnect report exceeds the {MaxDisconnectReportPackageLength}-byte package limit.");
            }

            int formatVersion = GameReflection.PackageReadInt(package);
            if (formatVersion != DisconnectReportFormatVersion)
            {
                // A future structured format uses a small positive version number. The early unreleased 1.0.4
                // plain-string format starts with the string byte length, which is expected to be much larger.
                if (formatVersion > 0 && formatVersion < 32)
                {
                    throw new InvalidOperationException($"Unsupported disconnect report format {formatVersion}.");
                }

                GameReflection.PackageSetPos(package, initialPosition);
                string legacyReason = GameReflection.PackageReadString(package);
                SetPendingLegacyDisconnectReport(connectionGeneration, "legacy", legacyReason);
                ConditionalConfigSync.VersionErrorLog(
                    "Version",
                    PluginInfoCCS.PluginName,
                    $"The server rejected the connection with a legacy disconnect report: {NormalizeSingleLine(legacyReason, 1024)}");
                return;
            }

            string reportId = NormalizeSingleLine(GameReflection.PackageReadString(package), 128);
            string serverPackageVersion = NormalizeSingleLine(GameReflection.PackageReadString(package), 128);
            int serverProtocolVersion = GameReflection.PackageReadInt(package);
            int itemCount = GameReflection.PackageReadInt(package);
            if (itemCount < 0 || itemCount > MaxDisconnectReasonItems)
            {
                throw new InvalidOperationException(
                    $"Disconnect report item count {itemCount} is outside the allowed range 0..{MaxDisconnectReasonItems}.");
            }

            List<DisconnectReasonItem> reasons = new(itemCount);
            for (int i = 0; i < itemCount; ++i)
            {
                byte rawCode = GameReflection.PackageReadByte(package);
                DisconnectReasonCode code = Enum.IsDefined(typeof(DisconnectReasonCode), rawCode)
                    ? (DisconnectReasonCode)rawCode
                    : DisconnectReasonCode.Unknown;
                reasons.Add(new DisconnectReasonItem
                {
                    Code = code,
                    ModName = NormalizeSingleLine(GameReflection.PackageReadString(package), MaxDisconnectModNameLength),
                    Message = NormalizeSingleLine(GameReflection.PackageReadString(package), MaxDisconnectReasonTextLength),
                });
            }

            int trailingBytes = GameReflection.PackageSize(package) - GameReflection.PackageGetPos(package);
            if (trailingBytes != 0)
            {
                throw new InvalidOperationException($"Disconnect report contains {trailingBytes} unexpected trailing byte(s).");
            }

            SetPendingDisconnectReport(connectionGeneration, reportId, reasons);
            string reasonCodes = string.Join(", ", reasons.Select(reason => reason.Code.ToString()).Distinct());
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"The server rejected the connection with CCS report {reportId}; server CCS {serverPackageVersion}, " +
                $"protocol {serverProtocolVersion}, reasons [{reasonCodes}].");
        }
        catch (Exception e)
        {
            SetPendingDisconnectReport(
                connectionGeneration,
                "unreadable",
                new[]
                {
                    new DisconnectReasonItem
                    {
                        Code = DisconnectReasonCode.MalformedHandshake,
                        ModName = PluginInfoCCS.PluginName,
                        Message = "The server rejected the connection, but the detailed Conditional Config Sync disconnect report could not be read.",
                    },
                });
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"Failed to read the server's disconnect report: {e.Message}");
        }
    }

    private static void CheckVersion(ZRpc rpc, ZPackage pkg) => CheckVersion(rpc, pkg, null);

    private static void CheckVersion(ZRpc rpc, ZPackage pkg, Action<ZRpc, ZPackage>? original)
    {
        string? guid = null;
        try
        {
            guid = GameReflection.PackageReadString(pkg);
            string minimumRequiredVersion = GameReflection.PackageReadString(pkg);
            string currentVersion = GameReflection.PackageReadString(pkg);

            VersionHandshakeState state = new()
            {
                MinimumRequiredVersion = minimumRequiredVersion,
                CurrentVersion = currentVersion,
            };

            int remaining = GameReflection.PackageSize(pkg) - GameReflection.PackageGetPos(pkg);
            if (remaining >= sizeof(int))
            {
                state.ProtocolFieldPresent = true;
                state.ProtocolVersion = GameReflection.PackageReadInt(pkg);
            }

            remaining = GameReflection.PackageSize(pkg) - GameReflection.PackageGetPos(pkg);
            if (remaining > 0)
            {
                state.PackageVersion = GameReflection.PackageReadString(pkg);
                state.PackageVersionFieldPresent = true;
            }

            state.TrailingBytes = GameReflection.PackageSize(pkg) - GameReflection.PackageGetPos(pkg);

            bool matched = false;
            bool server = GameReflection.IsServer();
            string remote = FormatRemotePeer(rpc, server ? "client" : "server");

            foreach (VersionCheck check in versionChecks)
            {
                if (guid != check.Name)
                {
                    continue;
                }

                string trailingData = state.TrailingBytes > 0
                    ? $"; trailing bytes {state.TrailingBytes}"
                    : string.Empty;
                ConditionalConfigSync.VersionInfoLog(
                    "Version",
                    check.DisplayName,
                    $"Received {check.DisplayName} version {currentVersion}, minimum version {minimumRequiredVersion}, " +
                    $"ConditionalConfigSync protocol {FormatProtocol(state)} from the {remote}. " +
                    $"Remote ConditionalConfigSync version {FormatPackageVersion(state)}; {GetHandshakeElapsed(rpc)}{trailingData}.");

                if (server)
                {
                    check.receivedClientHandshakes[rpc] = state;
                    if (check.IsVersionOk(state, rpc))
                    {
                        check.ValidatedClients.Add(rpc);
                    }
                    else
                    {
                        check.ValidatedClients.Remove(rpc);
                    }
                }
                else
                {
                    check.receivedServerHandshake = state;
                    RemoteServerProtocolVersion = state.ProtocolFieldPresent ? state.ProtocolVersion : 0;
                    RemoteServerProtocolKnown = state.ProtocolFieldPresent;
                }

                matched = true;
            }

            if (!matched)
            {
                bool processedByPreviousHandler = false;
                GameReflection.PackageSetPos(pkg, 0);
                if (original is not null)
                {
                    original(rpc, pkg);
                    processedByPreviousHandler = GameReflection.PackageGetPos(pkg) != 0;
                }

                if (!server && !processedByPreviousHandler)
                {
                    notProcessedNames[guid] = currentVersion;
                }
            }
        }
        catch (Exception e)
        {
            string remote = FormatRemotePeer(rpc, GameReflection.IsServer() ? "client" : "server");
            string description = string.IsNullOrEmpty(guid)
                ? $"could not read the package header: {e.Message}"
                : $"could not read the package for GUID '{guid}': {e.Message}";
            malformedHandshakeErrors[rpc] = description;
            if (!GameReflection.IsServer())
            {
                pendingMalformedServerHandshakeError = new PendingConnectionDiagnostic
                {
                    ConnectionGeneration = activeClientConnectionGeneration,
                    Message = description,
                };
            }
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"Malformed version handshake from the {remote}: {description}");
        }
    }

    private static bool RPC_PeerInfo(ZRpc rpc, ZNet __instance)
    {
        bool server = GameReflection.IsServer(__instance);
        VersionCheck[] failedChecks = server ? GetFailedServer(rpc) : GetFailedClient();
        if (failedChecks.Length == 0)
        {
            if (!server)
            {
                ClearPendingConnectionErrorState(clearGeneration: false);
            }
            return true;
        }

        if (server)
        {
            List<DisconnectReasonItem> clientReasons = new();
            foreach (VersionCheck check in failedChecks)
            {
                ConditionalConfigSync.VersionErrorLog("Version", check.DisplayName, check.ErrorServer(rpc));
                clientReasons.Add(check.ClientVisibleServerError(rpc));
            }

            if (malformedHandshakeErrors.TryGetValue(rpc, out string? malformedError))
            {
                clientReasons.Add(new DisconnectReasonItem
                {
                    Code = DisconnectReasonCode.MalformedHandshake,
                    ModName = PluginInfoCCS.PluginName,
                    Message = "A malformed version-handshake package was received from this client: " +
                              NormalizeSingleLine(malformedError, MaxDisconnectReasonTextLength),
                });
            }

            DisconnectClient(rpc, clientReasons);
        }
        else
        {
            List<DisconnectReasonItem> localReasons = new();
            foreach (VersionCheck check in failedChecks)
            {
                ConditionalConfigSync.VersionErrorLog("Version", check.DisplayName, "Disconnect: " + check.ErrorClient());
                localReasons.Add(check.ClientVisibleClientError());
            }

            AddPendingClientDiagnostics(localReasons);
            SetPendingDisconnectReport(
                activeClientConnectionGeneration,
                "local-" + activeClientConnectionGeneration,
                localReasons);
            Logout();
        }
        return false;
    }

    private static void RPC_PeerInfoCompleted(ZNet __instance)
    {
        if (GameReflection.IsServer(__instance)
            || Equals(GameReflection.GetConnectionStatus(), ZNet.ConnectionStatus.ErrorVersion))
        {
            return;
        }

        foreach (VersionCheck check in versionChecks.ToArray())
        {
            check.ResolveMissingOptionalServer();
        }

        notProcessedNames.Clear();
        ClearPendingConnectionErrorState(clearGeneration: false);
    }

    private static void RegisterAndCheckVersion(ZNetPeer peer, ZNet __instance)
    {
        bool server = GameReflection.IsServer(__instance);
        ZRpc peerRpc = GameReflection.GetPeerRpc(peer);
        connectionStartedAt[peerRpc] = DateTime.UtcNow.Ticks;
        malformedHandshakeErrors.Remove(peerRpc);

        if (!server)
        {
            RemoteServerProtocolVersion = 0;
            RemoteServerProtocolKnown = false;
            notProcessedNames.Clear();
            ClearPendingConnectionErrorState(clearGeneration: true);
            long connectionGeneration = ++nextClientConnectionGeneration;
            activeClientConnectionGeneration = connectionGeneration;
            expiredClientConnectionGeneration = 0;
            GameReflection.RegisterRpcPackage(
                peerRpc,
                DisconnectReasonRpcName,
                (rpc, package) => ReceiveDisconnectReason(rpc, package, connectionGeneration));
        }

        IDictionary rpcFunctions = GameReflection.GetRpcFunctions(peerRpc);
        int versionCheckHash = GameReflection.StableHash(VersionCheckRpcName);
        if (rpcFunctions.Contains(versionCheckHash))
        {
            object function = rpcFunctions[versionCheckHash];
            Action<ZRpc, ZPackage> action = GameReflection.GetRpcPackageAction(function);
            GameReflection.RegisterRpcPackage(peerRpc, VersionCheckRpcName, (rpc, pkg) => CheckVersion(rpc, pkg, action));
        }
        else
        {
            GameReflection.RegisterRpcPackage(peerRpc, VersionCheckRpcName, CheckVersion);
        }

        foreach (VersionCheck check in versionChecks)
        {
            if (server)
            {
                check.RefreshFromConfigSync();
                check.ResetPeerState(peerRpc);
                // Admission policy is snapshotted per connection so a policy reload cannot change the result halfway
                // through an already-started handshake. Reloaded requirements apply to later connection attempts.
                check.SnapshotRequirementForPeer(peerRpc);
            }
            else
            {
                check.Initialize();
            }

            // Conditional consumers always advertise their presence from the client. The server may have overridden an
            // author-default optional requirement to required and must be able to distinguish an installed consumer from
            // a genuinely missing one. Fixed optional consumers retain the original one-sided handshake behavior.
            if (!server && !check.ShouldSendClientHandshake())
            {
                continue;
            }

            string minimumRequiredVersion = server
                ? check.GetMinimumRequiredVersionForPeer(peerRpc)
                : check.MinimumRequiredVersion;
            ConditionalConfigSync.VersionDebugLog(
                "Version",
                check.DisplayName,
                $"Sending version {check.CurrentVersion}, minimum {minimumRequiredVersion}, " +
                $"ConditionalConfigSync {PluginInfoCCS.PluginVersion} protocol {PluginInfoCCS.ProtocolVersion} to the " +
                $"{FormatRemotePeer(peerRpc, server ? "client" : "server")}");

            ZPackage zpackage = GameReflection.NewPackage();
            GameReflection.PackageWrite(zpackage, check.Name);
            GameReflection.PackageWrite(zpackage, minimumRequiredVersion);
            GameReflection.PackageWrite(zpackage, check.CurrentVersion);
            GameReflection.PackageWrite(zpackage, PluginInfoCCS.ProtocolVersion);
            GameReflection.PackageWrite(zpackage, PluginInfoCCS.PluginVersion);
            GameReflection.InvokeRpc(peerRpc, VersionCheckRpcName, zpackage);
        }
    }

    private static void AddPendingClientDiagnostics(List<DisconnectReasonItem> reasons)
    {
        if (pendingMalformedServerHandshakeError != null
            && pendingMalformedServerHandshakeError.ConnectionGeneration == activeClientConnectionGeneration)
        {
            reasons.Add(new DisconnectReasonItem
            {
                Code = DisconnectReasonCode.MalformedHandshake,
                ModName = PluginInfoCCS.PluginName,
                Message = "The server sent a malformed version-handshake package: " +
                          NormalizeSingleLine(
                              pendingMalformedServerHandshakeError.Message,
                              MaxDisconnectReasonTextLength),
            });
        }

        foreach (KeyValuePair<string, string> kv in notProcessedNames.OrderBy(kv => kv.Key))
        {
            reasons.Add(new DisconnectReasonItem
            {
                Code = DisconnectReasonCode.MissingConsumerRegistration,
                ModName = kv.Key,
                Message = $"The server reported version {kv.Value}, but this client did not process that mod's Conditional Config Sync handshake.",
            });
        }
    }

    private static PendingDisconnectReport? GetCurrentPendingDisconnectReport()
    {
        if (pendingDisconnectReport == null)
        {
            return null;
        }

        if (pendingDisconnectReport.ConnectionGeneration != activeClientConnectionGeneration)
        {
            pendingDisconnectReport = null;
            return null;
        }

        if (DateTime.UtcNow - pendingDisconnectReport.ReceivedAtUtc > DisconnectReasonLifetime)
        {
            expiredClientConnectionGeneration = activeClientConnectionGeneration;
            pendingDisconnectReport = null;
            return null;
        }

        return pendingDisconnectReport;
    }

    private static void ClearPendingConnectionErrorState(bool clearGeneration)
    {
        pendingDisconnectReport = null;
        pendingMalformedServerHandshakeError = null;
        injectedClientConnectionGeneration = 0;
        if (clearGeneration)
        {
            activeClientConnectionGeneration = 0;
            expiredClientConnectionGeneration = 0;
        }
    }

    internal static bool HasPendingConnectionError()
    {
        return GetCurrentPendingDisconnectReport() != null;
    }

    internal static void ResetSessionState(bool preserveConnectionError)
    {
        RemoteServerProtocolVersion = 0;
        RemoteServerProtocolKnown = false;
        notProcessedNames.Clear();
        malformedHandshakeErrors.Clear();
        connectionStartedAt.Clear();
        foreach (VersionCheck check in versionChecks)
        {
            check.receivedServerHandshake = null;
            check.receivedClientHandshakes.Clear();
            check.ValidatedClients.Clear();
            check.requiredClients.Clear();
        }

        if (preserveConnectionError && GetCurrentPendingDisconnectReport() != null)
        {
            pendingMalformedServerHandshakeError = null;
        }
        else
        {
            ClearPendingConnectionErrorState(clearGeneration: true);
        }
    }

    internal static void ShutdownRuntime()
    {
        ResetSessionState(preserveConnectionError: false);
        nextClientConnectionGeneration = 0;
        versionChecks.Clear();
    }

    private static void RemoveDisconnected(ZNetPeer peer, ZNet __instance)
    {
        if (!GameReflection.IsServer(__instance))
        {
            return;
        }

        ZRpc rpc = GameReflection.GetPeerRpc(peer);
        malformedHandshakeErrors.Remove(rpc);
        connectionStartedAt.Remove(rpc);
        foreach (VersionCheck check in versionChecks)
        {
            check.ValidatedClients.Remove(rpc);
            check.receivedClientHandshakes.Remove(rpc);
            check.requiredClients.Remove(rpc);
        }
    }

    private static void ShowConnectionError(FejdStartup __instance)
    {
        GameObject? connectionFailedPanel = GameReflection.GetConnectionFailedPanel(__instance);
        TMP_Text? connectionFailedError = GameReflection.GetConnectionFailedError(__instance);
        if (connectionFailedPanel == null || connectionFailedError == null
            || !connectionFailedPanel.activeSelf
            || !Equals(GameReflection.GetConnectionStatus(), ZNet.ConnectionStatus.ErrorVersion)
            || injectedClientConnectionGeneration == activeClientConnectionGeneration)
        {
            return;
        }

        PendingDisconnectReport? report = GetCurrentPendingDisconnectReport();
        if (report == null)
        {
            return;
        }

        if (!connectionFailedError.text.Contains(report.Message))
        {
            connectionFailedError.text += "\n\n" + report.Message;
        }

        injectedClientConnectionGeneration = activeClientConnectionGeneration;
        pendingDisconnectReport = null;
        pendingMalformedServerHandshakeError = null;

        try
        {
            GameReflection.StartCoroutine(
                NormalizeConnectionErrorLayout(__instance, connectionFailedPanel, connectionFailedError),
                __instance);
        }
        catch (Exception e)
        {
            ConditionalConfigSync.VersionErrorLog(
                "Version",
                PluginInfoCCS.PluginName,
                $"Failed to schedule connection-error layout normalization for report {report.ReportId}: {e.Message}");
        }
    }

    private static IEnumerator NormalizeConnectionErrorLayout(
        FejdStartup startup,
        GameObject connectionFailedPanel,
        TMP_Text connectionFailedError)
    {
        // Let ServerSync and other ShowConnectError postfixes finish appending text and adjusting the vanilla panel.
        yield return null;

        if (startup == null || connectionFailedPanel == null || connectionFailedError == null
            || !connectionFailedPanel.activeSelf
            || !Equals(GameReflection.GetConnectionStatus(), ZNet.ConnectionStatus.ErrorVersion))
        {
            yield break;
        }

        // Jotunn hides the vanilla panel and presents its own compatibility window. If that happened, the early return
        // above leaves the Jotunn window untouched while it keeps the CCS-enriched failed-connection text.
        RectTransform? panel = connectionFailedPanel.transform.Find("Image")?.GetComponent<RectTransform>();
        if (panel == null)
        {
            yield break;
        }

        connectionFailedError.ForceMeshUpdate();
        LayoutRebuilder.ForceRebuildLayoutImmediate(connectionFailedError.rectTransform);

        float currentHeight = panel.sizeDelta.y;
        float targetHeight = Math.Max(currentHeight, connectionFailedError.renderedHeight + 125f);
        float targetWidth = Math.Max(panel.sizeDelta.x, 760f);
        float heightDelta = targetHeight - currentHeight;

        if (heightDelta > 0.5f)
        {
            RectTransform? button = panel.transform.Find("ButtonOk")?.GetComponent<RectTransform>();
            if (button != null)
            {
                button.anchoredPosition = new Vector2(
                    button.anchoredPosition.x,
                    button.anchoredPosition.y - heightDelta / 2f);
            }
        }

        panel.sizeDelta = new Vector2(targetWidth, targetHeight);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
    }
}
