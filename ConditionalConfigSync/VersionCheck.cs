using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnityEngine;

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
    private static readonly HashSet<VersionCheck> versionChecks = new();
    private static readonly Dictionary<string, string> notProcessedNames = new();

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
        get => minimumRequiredVersion ?? (ModRequired ? CurrentVersion : "0.0.0");
        set => minimumRequiredVersion = value;
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

    private string? ReceivedCurrentVersion;

    private string? ReceivedMinimumRequiredVersion;
    private int receivedProtocolVersion;

    /// <summary>
    /// Protocol version reported by the connected server. Zero means that the version handshake has not completed.
    /// </summary>
    [Description("Protocol version reported by the connected server, or zero before the handshake completes.")]
    public static int RemoteServerProtocolVersion { get; private set; }

    /// <summary>Whether protocol metadata has already been received from the connected server.</summary>
    [Description("Whether the connected server's ConditionalConfigSync protocol is known.")]
    public static bool RemoteServerProtocolKnown { get; private set; }

    // Tracks which clients have passed the version check (only for servers).
    private readonly HashSet<ZRpc> ValidatedClients = new();

    // Optional backing field to use ConditionalConfigSync values (will override other fields).
    private readonly ConditionalConfigSync? configSync;

    // Version-check patches are also explicit for the same embedded-copy protection.
    internal static void ApplyRuntimePatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }),
            prefix: CreateHarmonyMethod(nameof(RPC_PeerInfo)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }),
            prefix: CreateHarmonyMethod(nameof(RegisterAndCheckVersion)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) }),
            prefix: CreateHarmonyMethod(nameof(RemoveDisconnected)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(FejdStartup), "ShowConnectError", new[] { typeof(ZNet.ConnectionStatus) }),
            postfix: CreateHarmonyMethod(nameof(ShowConnectionError)));
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
        ReceivedCurrentVersion = null;
        ReceivedMinimumRequiredVersion = null;
        receivedProtocolVersion = 0;
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
    private bool IsVersionOk()
    {
        if (ReceivedMinimumRequiredVersion == null || ReceivedCurrentVersion == null)
        {
            return !ModRequired;
        }
        bool myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
        bool otherVersionOk = new System.Version(ReceivedCurrentVersion) >= new System.Version(MinimumRequiredVersion);
        return myVersionOk && otherVersionOk && IsProtocolOk();
    }

    private bool IsProtocolOk()
    {
        return receivedProtocolVersion == PluginInfo.ProtocolVersion;
    }
    private string ErrorClient()
    {
        if (ReceivedMinimumRequiredVersion == null)
        {
            return $"{DisplayName} is not installed on the server.";
        }
        if (!IsProtocolOk())
        {
            string protocol = receivedProtocolVersion == 0 ? "missing" : receivedProtocolVersion.ToString();
            return $"{DisplayName} uses incompatible ConditionalConfigSync protocol {protocol}. " +
                   $"This client requires protocol {PluginInfo.ProtocolVersion}.";
        }
        bool myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
        return myVersionOk ? $"{DisplayName} may not be higher than version {ReceivedCurrentVersion}. You have version {CurrentVersion}." : $"{DisplayName} needs to be at least version {ReceivedMinimumRequiredVersion}. You have version {CurrentVersion}.";
    }

    private string ErrorServer(ZRpc rpc)
    {
        string client = GameReflection.SocketGetHostName(GameReflection.GetRpcSocket(rpc) ?? throw new InvalidOperationException("RPC socket is unavailable."));
        if (!IsProtocolOk())
        {
            string protocol = receivedProtocolVersion == 0 ? "missing" : receivedProtocolVersion.ToString();
            return $"Disconnect: The client ({client}) uses incompatible ConditionalConfigSync protocol {protocol}. " +
                   $"The server requires protocol {PluginInfo.ProtocolVersion}.";
        }
        return $"Disconnect: The client ({client}) doesn't have the correct {DisplayName} version {MinimumRequiredVersion}";
    }

    private string Error(ZRpc? rpc = null)
    {
        return rpc == null ? ErrorClient() : ErrorServer(rpc);
    }

    private static VersionCheck[] GetFailedClient()
    {
        return versionChecks.Where(check => !check.IsVersionOk()).ToArray();
    }

    private static VersionCheck[] GetFailedServer(ZRpc rpc)
    {
        return versionChecks.Where(check => check.ModRequired && !check.ValidatedClients.Contains(rpc)).ToArray();
    }

    private static void Logout()
    {
        GameReflection.Logout();
        GameReflection.SetConnectionStatus(ZNet.ConnectionStatus.ErrorVersion);
    }

    private static void DisconnectClient(ZRpc rpc)
    {
        GameReflection.InvokeRpc(rpc, "Error", (int)ZNet.ConnectionStatus.ErrorVersion);
    }

    private static void CheckVersion(ZRpc rpc, ZPackage pkg) => CheckVersion(rpc, pkg, null);

    private static void CheckVersion(ZRpc rpc, ZPackage pkg, Action<ZRpc, ZPackage>? original)
    {
        string guid = GameReflection.PackageReadString(pkg);
        string minimumRequiredVersion = GameReflection.PackageReadString(pkg);
        string currentVersion = GameReflection.PackageReadString(pkg);
        int protocolVersion = 0;
        if (GameReflection.PackageSize(pkg) - GameReflection.PackageGetPos(pkg) >= sizeof(int))
        {
            protocolVersion = GameReflection.PackageReadInt(pkg);
        }

        bool matched = false;

        foreach (VersionCheck check in versionChecks)
        {
            if (guid != check.Name)
            {
                continue;
            }

            ConditionalConfigSync.VersionInfoLog(
                "Version",
                check.DisplayName,
                $"Received {check.DisplayName} version {currentVersion}, minimum version {minimumRequiredVersion}, " +
                $"ConditionalConfigSync protocol {(protocolVersion == 0 ? "missing" : protocolVersion.ToString())} from the " +
                $"{(GameReflection.IsServer() ? "client" : "server")}.");

            check.ReceivedMinimumRequiredVersion = minimumRequiredVersion;
            check.ReceivedCurrentVersion = currentVersion;
            check.receivedProtocolVersion = protocolVersion;
            if (!GameReflection.IsServer())
            {
                RemoteServerProtocolVersion = protocolVersion;
                RemoteServerProtocolKnown = true;
            }
            if (GameReflection.IsServer() && check.IsVersionOk())
            {
                check.ValidatedClients.Add(rpc);
            }

            matched = true;
        }

        if (!matched)
        {
            GameReflection.PackageSetPos(pkg, 0);
            if (original is not null)
            {
                original(rpc, pkg);
                if (GameReflection.PackageGetPos(pkg) == 0)
                {
                    notProcessedNames[guid] = currentVersion;
                }
            }
        }
    }
    private static bool RPC_PeerInfo(ZRpc rpc, ZNet __instance)
    {
        VersionCheck[] failedChecks = GameReflection.IsServer(__instance) ? GetFailedServer(rpc) : GetFailedClient();
        if (failedChecks.Length == 0)
        {
            return true;
        }

        foreach (VersionCheck check in failedChecks)
        {
            ConditionalConfigSync.VersionWarningLog("Version", check.DisplayName, check.Error(rpc));
        }

        if (GameReflection.IsServer(__instance))
        {
            DisconnectClient(rpc);
        }
        else
        {
            Logout();
        }
        return false;
    }
    private static void RegisterAndCheckVersion(ZNetPeer peer, ZNet __instance)
    {
        notProcessedNames.Clear();

        ZRpc peerRpc = GameReflection.GetPeerRpc(peer);
        IDictionary rpcFunctions = GameReflection.GetRpcFunctions(peerRpc);
        if (rpcFunctions.Contains(GameReflection.StableHash("ConditionalConfigSync VersionCheck")))
        {
            object function = rpcFunctions[GameReflection.StableHash("ConditionalConfigSync VersionCheck")];
            Action<ZRpc, ZPackage> action = GameReflection.GetRpcPackageAction(function);
            GameReflection.RegisterRpcPackage(peerRpc, "ConditionalConfigSync VersionCheck", (rpc, pkg) => CheckVersion(rpc, pkg, action));
        }
        else
        {
            GameReflection.RegisterRpcPackage(peerRpc, "ConditionalConfigSync VersionCheck", CheckVersion);
        }

        foreach (VersionCheck check in versionChecks)
        {
            check.Initialize();
            // If the mod is not required, then it's enough for only one side to do the check.
            if (!check.ModRequired && !GameReflection.IsServer(__instance))
            {
                continue;
            }

            ConditionalConfigSync.VersionDebugLog(
                "Version",
                check.DisplayName,
                $"Sending version {check.CurrentVersion}, minimum {check.MinimumRequiredVersion}, " +
                $"ConditionalConfigSync protocol {PluginInfo.ProtocolVersion} to " +
                $"{(GameReflection.IsServer(__instance) ? "client" : "server")}");

            ZPackage zpackage = GameReflection.NewPackage();
            GameReflection.PackageWrite(zpackage, check.Name);
            GameReflection.PackageWrite(zpackage, check.MinimumRequiredVersion);
            GameReflection.PackageWrite(zpackage, check.CurrentVersion);
            GameReflection.PackageWrite(zpackage, PluginInfo.ProtocolVersion);
            GameReflection.InvokeRpc(peerRpc, "ConditionalConfigSync VersionCheck", zpackage);
        }
    }
    internal static void ResetSessionState()
    {
        RemoteServerProtocolVersion = 0;
        RemoteServerProtocolKnown = false;
        notProcessedNames.Clear();
        foreach (VersionCheck check in versionChecks)
        {
            check.ReceivedCurrentVersion = null;
            check.ReceivedMinimumRequiredVersion = null;
            check.receivedProtocolVersion = 0;
            check.ValidatedClients.Clear();
        }
    }

    internal static void ShutdownRuntime()
    {
        ResetSessionState();
        versionChecks.Clear();
    }

    private static void RemoveDisconnected(ZNetPeer peer, ZNet __instance)
    {
        if (!GameReflection.IsServer(__instance))
        {
            return;
        }
        foreach (VersionCheck check in versionChecks)
        {
            check.ValidatedClients.Remove(GameReflection.GetPeerRpc(peer));
        }
    }
    private static void ShowConnectionError(FejdStartup __instance)
    {
        GameObject? connectionFailedPanel = GameReflection.GetConnectionFailedPanel(__instance);
        TMPro.TMP_Text? connectionFailedError = GameReflection.GetConnectionFailedError(__instance);
        if (connectionFailedPanel == null || connectionFailedError == null
            || !connectionFailedPanel.activeSelf
            || !Equals(GameReflection.GetConnectionStatus(), ZNet.ConnectionStatus.ErrorVersion))
        {
            return;
        }
        bool failedCheck = false;
        VersionCheck[] failedChecks = GetFailedClient();
        if (failedChecks.Length > 0)
        {
            string error = string.Join("\n", failedChecks.Select(check => check.Error()));
            connectionFailedError.text += "\n" + error;
            failedCheck = true;
        }

        foreach (KeyValuePair<string, string> kv in notProcessedNames.OrderBy(kv => kv.Key))
        {
            if (!connectionFailedError.text.Contains(kv.Key))
            {
                connectionFailedError.text += $"\nServer expects you to have {kv.Key} (Version: {kv.Value}) installed.";
                failedCheck = true;
            }
        }

        if (failedCheck)
        {
            RectTransform panel = connectionFailedPanel.transform.Find("Image").GetComponent<RectTransform>();
            panel.sizeDelta = panel.sizeDelta with { x = 675 };
            connectionFailedError.ForceMeshUpdate();
            float newHeight = connectionFailedError.renderedHeight + 105;
            RectTransform button = panel.transform.Find("ButtonOk").GetComponent<RectTransform>();
            button.anchoredPosition = new Vector2(button.anchoredPosition.x, button.anchoredPosition.y - (newHeight - panel.sizeDelta.y) / 2);
            panel.sizeDelta = panel.sizeDelta with { y = newHeight };
        }
    }
}
