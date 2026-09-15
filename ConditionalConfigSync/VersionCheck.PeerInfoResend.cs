using System;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class VersionCheck
{
    internal static void ApplyPeerInfoHandshakeResendPatch(Harmony harmony)
    {
        HarmonyMethod prefix = CreateHarmonyMethod(nameof(ResendVersionHandshakesBeforePeerInfo));
        prefix.priority = Priority.First;
        harmony.Patch(
            AccessTools.DeclaredMethod(
                typeof(ZNet),
                nameof(ZNet.SendPeerInfo),
                new[] { typeof(ZRpc), typeof(string) })
            ?? throw new MissingMethodException(typeof(ZNet).FullName, nameof(ZNet.SendPeerInfo)),
            prefix: prefix);
    }

    private static void ResendVersionHandshakesBeforePeerInfo(ZRpc rpc, ZNet __instance)
    {
        bool server = GameReflection.IsServer(__instance);

        // Re-advertise immediately before the vanilla PeerInfo packet. This preserves the existing admission timing
        // while preventing a transient transport recovery after OnNewConnection from turning the early one-shot
        // version advertisement into a false HandshakeMissing result.
        foreach (VersionCheck check in versionChecks)
        {
            if (!server && !check.ShouldSendClientHandshake())
            {
                continue;
            }

            string minimumRequiredVersion = server
                ? check.GetMinimumRequiredVersionForPeer(rpc)
                : check.MinimumRequiredVersion;
            ConditionalConfigSync.VersionDebugLog(
                "Version",
                check.DisplayName,
                $"Resending version {check.CurrentVersion}, minimum {minimumRequiredVersion}, " +
                $"ConditionalConfigSync {PluginInfoCCS.PluginVersion} protocol {PluginInfoCCS.ProtocolVersion} to the " +
                $"{FormatRemotePeer(rpc, server ? "client" : "server")} before PeerInfo.");

            ZPackage package = GameReflection.NewPackage();
            GameReflection.PackageWrite(package, check.Name);
            GameReflection.PackageWrite(package, minimumRequiredVersion);
            GameReflection.PackageWrite(package, check.CurrentVersion);
            GameReflection.PackageWrite(package, PluginInfoCCS.ProtocolVersion);
            GameReflection.PackageWrite(package, PluginInfoCCS.PluginVersion);
            GameReflection.InvokeRpc(rpc, VersionCheckRpcName, package);
        }
    }
}
