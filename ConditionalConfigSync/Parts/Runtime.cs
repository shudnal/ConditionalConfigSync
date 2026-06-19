using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private static ManualLogSource? logSource;

    private static ManualLogSource LogSource => logSource ??= Logger.CreateLogSource("ConditionalConfigSync");

    private static readonly object runtimeLock = new();

    private static bool runtimeInitialized;

    private static int mainThreadId;

    private static readonly object mainThreadQueueLock = new();

    private static readonly Queue<Action> mainThreadQueue = new();

    private const string ConfigDirectoryName = "shudnal.ConditionalConfigSync";

    private const string DebugConfigFileName = "ConditionalConfigSync.Debug.cfg";

    private static readonly object configDirectoryLock = new();

    private static bool configDirectoryInitialized;

    private static string ConfigDirectoryPath => Path.Combine(Paths.ConfigPath, ConfigDirectoryName);

    private static string DebugConfigPath => Path.Combine(ConfigDirectoryPath, DebugConfigFileName);

    private static void EnsureConfigDirectory()
    {
        lock (configDirectoryLock)
        {
            if (configDirectoryInitialized)
            {
                return;
            }

            Directory.CreateDirectory(ConfigDirectoryPath);
            configDirectoryInitialized = true;
        }
    }

    private static bool IsMainThread => mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == mainThreadId;

    private static void EnqueueMainThread(Action action)
    {
        if (action == null)
        {
            return;
        }

        if (IsMainThread)
        {
            action();
            return;
        }

        lock (mainThreadQueueLock)
        {
            mainThreadQueue.Enqueue(action);
        }
    }

    private static void DrainMainThreadQueue()
    {
        if (mainThreadId == 0)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        for (;;)
        {
            Action? action;
            lock (mainThreadQueueLock)
            {
                if (mainThreadQueue.Count == 0)
                {
                    return;
                }
                action = mainThreadQueue.Dequeue();
            }

            try
            {
                action();
            }
            catch (Exception e)
            {
                LogSource.LogWarning($"[MainThread] Queued action failed: {e}");
            }
        }
    }

    internal static void InitializeRuntime()
    {
        RuntimeGuard.ThrowIfEmbedded();

        lock (runtimeLock)
        {
            if (runtimeInitialized)
            {
                return;
            }

            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureDebugSupportInitialized();
            try
            {
                GameReflection.ValidateBindings();
            }
            catch (Exception e)
            {
                LogSource.LogFatal($"[Reflection] Failed to bind required Valheim runtime members: {e}");
                throw;
            }

            runtimeHarmony = new Harmony(RuntimeGuard.HarmonyId);
            ApplyRuntimePatches(runtimeHarmony);
            VersionCheck.ApplyRuntimePatches(runtimeHarmony);

            assemblyLoadHandler = (_, args) => WarnIfEmbeddedAssembly(args.LoadedAssembly);
            AppDomain.CurrentDomain.AssemblyLoad += assemblyLoadHandler;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                WarnIfEmbeddedAssembly(assembly);
            }

            runtimeInitialized = true;
        }
    }

    // Patches are installed explicitly so an ILRepack copy cannot be discovered by another mod's PatchAll().
    private static void ApplyRuntimePatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "Awake", Type.EmptyTypes),
            postfix: CreateHarmonyMethod(typeof(ZNetAwakePatch), nameof(ZNetAwakePatch.Postfix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "Update", Type.EmptyTypes),
            postfix: CreateHarmonyMethod(typeof(ZNetUpdatePatch), nameof(ZNetUpdatePatch.Postfix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }),
            postfix: CreateHarmonyMethod(typeof(ZNetOnNewConnectionPatch), nameof(ZNetOnNewConnectionPatch.Postfix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(Terminal), nameof(Terminal.InitTerminal), Type.EmptyTypes),
            postfix: CreateHarmonyMethod(typeof(TerminalInitPatch), nameof(TerminalInitPatch.Postfix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "Shutdown", new[] { typeof(bool) }),
            postfix: CreateHarmonyMethod(typeof(ZNetShutdownPatch), nameof(ZNetShutdownPatch.Postfix)));

        HarmonyMethod peerInfoPrefix = CreateHarmonyMethod(typeof(ZNetRpcPeerInfoSyncPatch), nameof(ZNetRpcPeerInfoSyncPatch.Prefix));
        peerInfoPrefix.priority = Priority.First;
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }),
            prefix: peerInfoPrefix,
            postfix: CreateHarmonyMethod(typeof(ZNetRpcPeerInfoSyncPatch), nameof(ZNetRpcPeerInfoSyncPatch.Postfix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ConfigEntryBase), nameof(ConfigEntryBase.GetSerializedValue), Type.EmptyTypes),
            prefix: CreateHarmonyMethod(typeof(ConfigEntryGetSerializedValuePatch), nameof(ConfigEntryGetSerializedValuePatch.Prefix)));

        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(ConfigEntryBase), nameof(ConfigEntryBase.SetSerializedValue), new[] { typeof(string) }),
            prefix: CreateHarmonyMethod(typeof(ConfigEntrySetSerializedValuePatch), nameof(ConfigEntrySetSerializedValuePatch.Prefix)));
    }

    private static HarmonyMethod CreateHarmonyMethod(Type type, string methodName)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, methodName)
                            ?? throw new MissingMethodException(type.FullName, methodName);
        return new HarmonyMethod(method);
    }

    private static void WarnIfEmbeddedAssembly(Assembly assembly)
    {
        if (assembly == typeof(ConditionalConfigSync).Assembly)
        {
            return;
        }

        if (assembly.GetType("ConditionalConfigSync.ConditionalConfigSync", throwOnError: false) == null
            && assembly.GetType("ConditionalConfigSync.ConfigSync", throwOnError: false) == null)
        {
            return;
        }

        LogSource.LogError(
            $"Embedded ConditionalConfigSync copy detected in assembly '{assembly.GetName().Name}'. " +
            "Embedded copies are unsupported and will reject ConfigSync creation. " +
            "Reference ConditionalConfigSync.dll and declare the BepInEx hard dependency '_shudnal.ConditionalConfigSync'.");
    }

    internal static void EnsureRuntimeReady()
    {
        RuntimeGuard.ThrowIfEmbedded();
        if (!runtimeInitialized)
        {
            throw new InvalidOperationException(
                "ConditionalConfigSync runtime is not initialized. Install the standalone ConditionalConfigSync mod " +
                "and add the BepInEx hard dependency '{PluginInfo.PluginGuid}'.");
        }
    }

    internal static class TerminalInitPatch
    {
        internal static void Postfix() => RegisterDebugConsoleCommands();
    }
}
