using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TMPro;
using UnityEngine;

namespace ConditionalConfigSync;

/// <summary>
/// Cached reflection bridge for Valheim runtime APIs. The development references may be publicized,
/// while the actual game assembly can keep the same members non-public. All game member invocation
/// is centralized here so CLR accessibility changes do not surface as MethodAccessException in callers.
/// </summary>
internal static class GameReflection
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type ZNetType = typeof(ZNet);
    private static readonly Type ZRoutedRpcType = typeof(ZRoutedRpc);
    private static readonly Type ZNetPeerType = typeof(ZNetPeer);
    private static readonly Type ZRpcType = typeof(ZRpc);
    private static readonly Type ZPackageType = typeof(ZPackage);
    private static readonly Type ISocketType = typeof(ISocket);
    private static readonly Type GameType = typeof(Game);
    private static readonly Type TerminalType = typeof(Terminal);
    private static readonly Type FejdStartupType = typeof(FejdStartup);
    private static readonly Type ZPlayFabSocketType = typeof(ZPlayFabSocket);

    private static readonly PropertyInfo? ZNetInstanceProperty = ZNetType.GetProperty("instance", Any);
    private static readonly FieldInfo? ZNetInstanceField = ZNetType.GetField("s_instance", Any) ?? ZNetType.GetField("m_instance", Any);
    private static readonly MethodInfo ZNetIsServerMethod = RequiredMethod(ZNetType, "IsServer", Type.EmptyTypes);
    private static readonly MethodInfo ZNetGetPeersMethod = RequiredMethod(ZNetType, "GetPeers", Type.EmptyTypes);
    private static readonly MethodInfo ZNetIsAdminMethod = RequiredMethod(ZNetType, "IsAdmin", typeof(string));
    private static readonly MethodInfo ZNetDisconnectMethod = RequiredMethod(ZNetType, "Disconnect", ZNetPeerType);
    private static readonly MethodInfo ZNetGetPeerByRpcMethod = RequiredMethod(ZNetType, "GetPeer", ZRpcType);
    private static readonly MethodInfo ZNetGetConnectionStatusMethod = RequiredMethod(ZNetType, "GetConnectionStatus", Type.EmptyTypes);
    private static readonly FieldInfo ZNetAdminListField = RequiredField(ZNetType, "m_adminList");
    private static readonly FieldInfo ZNetOnlineBackendField = RequiredField(ZNetType, "m_onlineBackend");
    private static readonly FieldInfo ZNetConnectionStatusField = RequiredField(ZNetType, "m_connectionStatus");

    private static readonly PropertyInfo? ZRoutedRpcInstanceProperty = ZRoutedRpcType.GetProperty("instance", Any);
    private static readonly FieldInfo? ZRoutedRpcInstanceField = ZRoutedRpcType.GetField("s_instance", Any);
    private static readonly FieldInfo ZRoutedRpcEverybodyField = RequiredField(ZRoutedRpcType, "Everybody");
    private static readonly FieldInfo ZRoutedRpcPeersField = RequiredField(ZRoutedRpcType, "m_peers");
    private static readonly MethodInfo ZRoutedRpcGetPeerMethod = RequiredMethod(ZRoutedRpcType, "GetPeer", typeof(long));
    private static readonly MethodInfo ZRoutedRpcInvokePackageMethod = RequiredMethod(ZRoutedRpcType, "InvokeRoutedRPC", typeof(string), typeof(object[]));
    private static readonly MethodInfo ZRoutedRpcRegisterPackageMethod = ZRoutedRpcType.GetMethods(Any)
        .Single(m => m.Name == "Register" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2)
        .MakeGenericMethod(ZPackageType);

    private static readonly FieldInfo ZNetPeerRpcField = RequiredField(ZNetPeerType, "m_rpc");
    private static readonly FieldInfo ZNetPeerSocketField = RequiredField(ZNetPeerType, "m_socket");
    private static readonly FieldInfo ZNetPeerUidField = RequiredField(ZNetPeerType, "m_uid");
    private static readonly FieldInfo ZNetPeerPlayerNameField = RequiredField(ZNetPeerType, "m_playerName");
    private static readonly MethodInfo ZNetPeerIsReadyMethod = RequiredMethod(ZNetPeerType, "IsReady", Type.EmptyTypes);

    private static readonly FieldInfo ZRpcSocketField = RequiredField(ZRpcType, "m_socket");
    private static readonly FieldInfo ZRpcFunctionsField = RequiredField(ZRpcType, "m_functions");
    private static readonly MethodInfo ZRpcGetSocketMethod = RequiredMethod(ZRpcType, "GetSocket", Type.EmptyTypes);
    private static readonly MethodInfo ZRpcInvokeMethod = RequiredMethod(ZRpcType, "Invoke", typeof(string), typeof(object[]));
    private static readonly MethodInfo ZRpcRegisterPackageMethod = ZRpcType.GetMethods(Any)
        .Single(m => m.Name == "Register" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2)
        .MakeGenericMethod(ZPackageType);
    private static readonly MethodInfo ZRpcSerializeMethod = RequiredMethod(ZRpcType, "Serialize", typeof(object[]), ZPackageType.MakeByRefType());
    private static readonly MethodInfo ZRpcDeserializeMethod = ZRpcType.GetMethods(Any)
        .Single(m =>
        {
            if (m.Name != "Deserialize" || !m.IsStatic)
            {
                return false;
            }
            ParameterInfo[] parameters = m.GetParameters();
            return parameters.Length == 3
                   && parameters[0].ParameterType == typeof(ParameterInfo[])
                   && parameters[1].ParameterType == ZPackageType
                   && parameters[2].ParameterType == typeof(List<object>).MakeByRefType();
        });

    private static readonly ConstructorInfo ZPackageDefaultConstructor = RequiredConstructor(ZPackageType, Type.EmptyTypes);
    private static readonly ConstructorInfo ZPackageBytesConstructor = RequiredConstructor(ZPackageType, typeof(byte[]));
    private static readonly FieldInfo ZPackageStreamField = RequiredField(ZPackageType, "m_stream");
    private static readonly MethodInfo ZPackageSizeMethod = RequiredMethod(ZPackageType, "Size", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageGetArrayMethod = RequiredMethod(ZPackageType, "GetArray", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageSetPosMethod = RequiredMethod(ZPackageType, "SetPos", typeof(int));
    private static readonly MethodInfo ZPackageGetPosMethod = RequiredMethod(ZPackageType, "GetPos", Type.EmptyTypes);
    private static readonly Dictionary<Type, MethodInfo> ZPackageWriteMethods = ZPackageType.GetMethods(Any)
        .Where(m => m.Name == "Write" && m.GetParameters().Length == 1)
        .GroupBy(m => m.GetParameters()[0].ParameterType)
        .ToDictionary(g => g.Key, g => g.First());
    private static readonly MethodInfo ZPackageReadByteMethod = RequiredMethod(ZPackageType, "ReadByte", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageReadBoolMethod = RequiredMethod(ZPackageType, "ReadBool", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageReadIntMethod = RequiredMethod(ZPackageType, "ReadInt", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageReadLongMethod = RequiredMethod(ZPackageType, "ReadLong", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageReadStringMethod = RequiredMethod(ZPackageType, "ReadString", Type.EmptyTypes);
    private static readonly MethodInfo ZPackageReadByteArrayCountMethod = RequiredMethod(ZPackageType, "ReadByteArray", typeof(int));

    private static readonly Type SerializableParameterType = typeof(ISerializableParameter);
    private static readonly MethodInfo SerializableParameterSerializeMethod = RequiredMethod(SerializableParameterType, "Serialize", ZPackageType.MakeByRefType());
    private static readonly MethodInfo SerializableParameterDeserializeMethod = RequiredMethod(SerializableParameterType, "Deserialize", ZPackageType.MakeByRefType());

    private static readonly Dictionary<string, MethodInfo> SocketMethods = ISocketType.GetMethods(Any)
        .GroupBy(m => m.Name)
        .ToDictionary(g => g.Key, g => g.First());

    private static readonly PropertyInfo? GameInstanceProperty = GameType.GetProperty("instance", Any);
    private static readonly FieldInfo? GameInstanceField = GameType.GetField("m_instance", Any) ?? GameType.GetField("s_instance", Any);
    private static readonly MethodInfo GameLogoutMethod = RequiredMethod(GameType, "Logout", typeof(bool), typeof(bool));

    private static readonly FieldInfo FejdConnectionFailedPanelField = RequiredField(FejdStartupType, "m_connectionFailedPanel");
    private static readonly FieldInfo FejdConnectionFailedErrorField = RequiredField(FejdStartupType, "m_connectionFailedError");
    private static readonly FieldInfo PlayFabRemotePlayerIdField = RequiredField(ZPlayFabSocketType, "m_remotePlayerId");

    private static readonly MethodInfo MonoBehaviourStartCoroutineMethod = typeof(MonoBehaviour).GetMethods(Any)
        .Single(m => m.Name == "StartCoroutine" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(IEnumerator));

    private static readonly MethodInfo? SyncedListGetListMethod = typeof(SyncedList).GetMethod("GetList", Any, null, Type.EmptyTypes, null);

    private static readonly Type? TerminalConsoleCommandType = TerminalType.GetNestedType("ConsoleCommand", Any);
    private static readonly Type? TerminalConsoleEventFailableType = TerminalType.GetNestedType("ConsoleEventFailable", Any);

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameters)
    {
        return type.GetMethod(name, Any, null, parameters, null)
               ?? throw new MissingMethodException(type.FullName, name);
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        return type.GetField(name, Any)
               ?? throw new MissingFieldException(type.FullName, name);
    }

    private static ConstructorInfo RequiredConstructor(Type type, params Type[] parameters)
    {
        return type.GetConstructor(Any, null, parameters, null)
               ?? throw new MissingMethodException(type.FullName, ".ctor");
    }

    private static object? Invoke(MethodInfo method, object? instance, params object?[]? arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Forces eager validation of all cached Valheim reflection bindings.</summary>
    internal static void ValidateBindings()
    {
        // Static field initialization performs the validation. This method intentionally has no body.
    }

    internal static ZNet? ZNetInstance => (ZNet?)(ZNetInstanceProperty?.GetValue(null) ?? ZNetInstanceField?.GetValue(null));
    internal static ZRoutedRpc? ZRoutedRpcInstance => (ZRoutedRpc?)(ZRoutedRpcInstanceProperty?.GetValue(null) ?? ZRoutedRpcInstanceField?.GetValue(null));
    internal static long Everybody => Convert.ToInt64(ZRoutedRpcEverybodyField.GetValue(null));
    internal static bool HasZNet => ZNetInstance != null;
    internal static bool HasZRoutedRpc => ZRoutedRpcInstance != null;

    internal static bool IsServer(ZNet? znet = null)
    {
        ZNet? instance = znet ?? ZNetInstance;
        return instance != null && (bool)(Invoke(ZNetIsServerMethod, instance) ?? false);
    }

    internal static List<ZNetPeer> GetPeers(ZNet? znet = null)
        => (List<ZNetPeer>)(Invoke(ZNetGetPeersMethod, znet ?? ZNetInstance) ?? new List<ZNetPeer>());

    internal static bool IsAdmin(string hostName, ZNet? znet = null)
        => (bool)(Invoke(ZNetIsAdminMethod, znet ?? ZNetInstance, hostName) ?? false);

    internal static void Disconnect(ZNetPeer peer, ZNet? znet = null)
        => Invoke(ZNetDisconnectMethod, znet ?? ZNetInstance, peer);

    internal static ZNetPeer? GetPeer(ZRpc rpc, ZNet? znet = null)
        => (ZNetPeer?)Invoke(ZNetGetPeerByRpcMethod, znet ?? ZNetInstance, rpc);

    internal static object? GetConnectionStatus() => Invoke(ZNetGetConnectionStatusMethod, null);
    internal static void SetConnectionStatus(object value) => ZNetConnectionStatusField.SetValue(null, value);
    internal static object? GetOnlineBackend() => ZNetOnlineBackendField.GetValue(null);
    internal static SyncedList? GetAdminList(ZNet? znet = null) => (SyncedList?)ZNetAdminListField.GetValue(znet ?? ZNetInstance);
    internal static List<string> GetSyncedListValues(SyncedList list)
        => SyncedListGetListMethod == null ? new List<string>() : new List<string>((IEnumerable<string>)Invoke(SyncedListGetListMethod, list)!);

    internal static Coroutine? StartCoroutine(IEnumerator routine, ZNet? znet = null)
        => (Coroutine?)Invoke(MonoBehaviourStartCoroutineMethod, znet ?? ZNetInstance, routine);

    internal static ZNetPeer? GetRoutedPeer(long uid)
    {
        ZRoutedRpc? rpc = ZRoutedRpcInstance;
        return rpc == null ? null : (ZNetPeer?)Invoke(ZRoutedRpcGetPeerMethod, rpc, uid);
    }

    internal static List<ZNetPeer> GetRoutedPeers()
    {
        ZRoutedRpc? rpc = ZRoutedRpcInstance;
        return rpc == null ? new List<ZNetPeer>() : new List<ZNetPeer>((IEnumerable<ZNetPeer>)ZRoutedRpcPeersField.GetValue(rpc)!);
    }

    internal static void RegisterRoutedPackage(string name, Action<long, ZPackage> handler)
    {
        ZRoutedRpc rpc = ZRoutedRpcInstance ?? throw new InvalidOperationException("ZRoutedRpc is not initialized.");
        Invoke(ZRoutedRpcRegisterPackageMethod, rpc, name, handler);
    }

    internal static void InvokeRoutedPackage(string method, ZPackage package)
    {
        ZRoutedRpc rpc = ZRoutedRpcInstance ?? throw new InvalidOperationException("ZRoutedRpc is not initialized.");
        Invoke(ZRoutedRpcInvokePackageMethod, rpc, method, new object[] { package });
    }

    internal static ZRpc GetPeerRpc(ZNetPeer peer) => (ZRpc)ZNetPeerRpcField.GetValue(peer)!;
    internal static ISocket? GetPeerSocket(ZNetPeer peer) => (ISocket?)ZNetPeerSocketField.GetValue(peer);
    internal static void SetPeerSocket(ZNetPeer peer, ISocket socket) => ZNetPeerSocketField.SetValue(peer, socket);
    internal static long GetPeerUid(ZNetPeer peer) => Convert.ToInt64(ZNetPeerUidField.GetValue(peer));
    internal static string GetPeerPlayerName(ZNetPeer peer) => (string?)ZNetPeerPlayerNameField.GetValue(peer) ?? string.Empty;
    internal static bool IsPeerReady(ZNetPeer peer) => (bool)(Invoke(ZNetPeerIsReadyMethod, peer) ?? false);

    internal static ISocket? GetRpcSocket(ZRpc rpc) => (ISocket?)Invoke(ZRpcGetSocketMethod, rpc);
    internal static void SetRpcSocket(ZRpc rpc, ISocket socket) => ZRpcSocketField.SetValue(rpc, socket);
    internal static IDictionary GetRpcFunctions(ZRpc rpc) => (IDictionary)ZRpcFunctionsField.GetValue(rpc)!;

    internal static Action<ZRpc, ZPackage> GetRpcPackageAction(object rpcMethod)
    {
        FieldInfo actionField = rpcMethod.GetType().GetField("m_action", Any)
                                ?? throw new MissingFieldException(rpcMethod.GetType().FullName, "m_action");
        return (Action<ZRpc, ZPackage>)(actionField.GetValue(rpcMethod)
               ?? throw new InvalidOperationException("RPC package action is null."));
    }

    internal static void RegisterRpcPackage(ZRpc rpc, string name, Action<ZRpc, ZPackage> handler)
        => Invoke(ZRpcRegisterPackageMethod, rpc, name, handler);

    internal static void InvokeRpc(ZRpc rpc, string method, params object[] parameters)
        => Invoke(ZRpcInvokeMethod, rpc, method, parameters);

    internal static void Serialize(object[] parameters, ref ZPackage package)
    {
        object?[] args = { parameters, package };
        Invoke(ZRpcSerializeMethod, null, args);
        package = (ZPackage)args[1]!;
    }

    internal static void Deserialize(ParameterInfo[] parameters, ZPackage package, ref List<object> data)
    {
        object?[] args = { parameters, package, data };
        Invoke(ZRpcDeserializeMethod, null, args);
        data = (List<object>)args[2]!;
    }

    internal static void SerializeParameter(object value, ref ZPackage package)
    {
        object?[] args = { package };
        Invoke(SerializableParameterSerializeMethod, value, args);
        package = (ZPackage)args[0]!;
    }

    internal static void DeserializeParameter(object value, ref ZPackage package)
    {
        object?[] args = { package };
        Invoke(SerializableParameterDeserializeMethod, value, args);
        package = (ZPackage)args[0]!;
    }

    internal static ZPackage NewPackage() => (ZPackage)ZPackageDefaultConstructor.Invoke(Array.Empty<object>());
    internal static ZPackage NewPackage(byte[] data) => (ZPackage)ZPackageBytesConstructor.Invoke(new object[] { data });
    internal static int PackageSize(ZPackage package) => Convert.ToInt32(Invoke(ZPackageSizeMethod, package));
    internal static byte[] PackageGetArray(ZPackage package) => (byte[])Invoke(ZPackageGetArrayMethod, package)!;
    internal static void PackageSetPos(ZPackage package, int position) => Invoke(ZPackageSetPosMethod, package, position);
    internal static int PackageGetPos(ZPackage package) => Convert.ToInt32(Invoke(ZPackageGetPosMethod, package));
    internal static MemoryStream PackageStream(ZPackage package) => (MemoryStream)ZPackageStreamField.GetValue(package)!;

    internal static void PackageWrite<T>(ZPackage package, T value)
    {
        Type type = typeof(T);
        if (!ZPackageWriteMethods.TryGetValue(type, out MethodInfo? method))
        {
            Type runtimeType = value?.GetType() ?? type;
            if (!ZPackageWriteMethods.TryGetValue(runtimeType, out method))
            {
                throw new MissingMethodException(ZPackageType.FullName, $"Write({runtimeType.FullName})");
            }
        }
        Invoke(method, package, value);
    }

    internal static byte PackageReadByte(ZPackage package) => (byte)Invoke(ZPackageReadByteMethod, package)!;
    internal static bool PackageReadBool(ZPackage package) => (bool)Invoke(ZPackageReadBoolMethod, package)!;
    internal static int PackageReadInt(ZPackage package) => (int)Invoke(ZPackageReadIntMethod, package)!;
    internal static long PackageReadLong(ZPackage package) => (long)Invoke(ZPackageReadLongMethod, package)!;
    internal static string PackageReadString(ZPackage package) => (string)Invoke(ZPackageReadStringMethod, package)!;
    internal static byte[] PackageReadByteArray(ZPackage package, int maxLength)
    {
        int length = PackageReadInt(package);
        int remaining = PackageSize(package) - PackageGetPos(package);
        if (length < 0 || length > maxLength || length > remaining)
        {
            throw new InvalidDataException($"Invalid byte-array length {length}; limit={maxLength}, remaining={remaining}.");
        }

        byte[] data = (byte[])Invoke(ZPackageReadByteArrayCountMethod, package, length)!;
        if (data.Length != length)
        {
            throw new EndOfStreamException($"Expected {length} bytes, received {data.Length}.");
        }
        return data;
    }

    internal static bool SocketIsConnected(ISocket socket) => (bool)(Invoke(SocketMethods[nameof(ISocket.IsConnected)], socket) ?? false);
    internal static ZPackage? SocketRecv(ISocket socket) => (ZPackage?)Invoke(SocketMethods[nameof(ISocket.Recv)], socket);
    internal static int SocketGetSendQueueSize(ISocket socket) => Convert.ToInt32(Invoke(SocketMethods[nameof(ISocket.GetSendQueueSize)], socket));
    internal static int SocketGetCurrentSendRate(ISocket socket) => Convert.ToInt32(Invoke(SocketMethods[nameof(ISocket.GetCurrentSendRate)], socket));
    internal static bool SocketIsHost(ISocket socket) => (bool)(Invoke(SocketMethods[nameof(ISocket.IsHost)], socket) ?? false);
    internal static void SocketDispose(ISocket socket) => Invoke(SocketMethods[nameof(ISocket.Dispose)], socket);
    internal static bool SocketGotNewData(ISocket socket) => (bool)(Invoke(SocketMethods[nameof(ISocket.GotNewData)], socket) ?? false);
    internal static void SocketClose(ISocket socket) => Invoke(SocketMethods[nameof(ISocket.Close)], socket);
    internal static string SocketGetEndPointString(ISocket socket) => (string?)Invoke(SocketMethods[nameof(ISocket.GetEndPointString)], socket) ?? string.Empty;
    internal static ISocket? SocketAccept(ISocket socket) => (ISocket?)Invoke(SocketMethods[nameof(ISocket.Accept)], socket);
    internal static int SocketGetHostPort(ISocket socket) => Convert.ToInt32(Invoke(SocketMethods[nameof(ISocket.GetHostPort)], socket));
    internal static bool SocketFlush(ISocket socket) => (bool)(Invoke(SocketMethods[nameof(ISocket.Flush)], socket) ?? false);
    internal static string SocketGetHostName(ISocket socket) => (string?)Invoke(SocketMethods[nameof(ISocket.GetHostName)], socket) ?? string.Empty;
    internal static void SocketVersionMatch(ISocket socket) => Invoke(SocketMethods[nameof(ISocket.VersionMatch)], socket);
    internal static void SocketSend(ISocket socket, ZPackage package) => Invoke(SocketMethods[nameof(ISocket.Send)], socket, package);

    internal static void SocketGetAndResetStats(ISocket socket, out int totalSent, out int totalRecv)
    {
        object?[] args = { 0, 0 };
        Invoke(SocketMethods[nameof(ISocket.GetAndResetStats)], socket, args);
        totalSent = (int)args[0]!;
        totalRecv = (int)args[1]!;
    }

    internal static void SocketGetConnectionQuality(ISocket socket, out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
    {
        object?[] args = { 0f, 0f, 0, 0f, 0f };
        Invoke(SocketMethods[nameof(ISocket.GetConnectionQuality)], socket, args);
        localQuality = (float)args[0]!;
        remoteQuality = (float)args[1]!;
        ping = (int)args[2]!;
        outByteSec = (float)args[3]!;
        inByteSec = (float)args[4]!;
    }

    internal static void Logout()
    {
        object? instance = GameInstanceProperty?.GetValue(null) ?? GameInstanceField?.GetValue(null);
        if (instance != null)
        {
            Invoke(GameLogoutMethod, instance, true, true);
        }
    }

    internal static GameObject? GetConnectionFailedPanel(FejdStartup startup) => (GameObject?)FejdConnectionFailedPanelField.GetValue(startup);
    internal static TMP_Text? GetConnectionFailedError(FejdStartup startup) => (TMP_Text?)FejdConnectionFailedErrorField.GetValue(startup);
    internal static object? GetPlayFabRemotePlayerId(ZPlayFabSocket socket) => PlayFabRemotePlayerIdField.GetValue(socket);
    internal static void SetPlayFabRemotePlayerId(ZPlayFabSocket socket, object? value) => PlayFabRemotePlayerIdField.SetValue(socket, value);

    internal static void RegisterConsoleCommand(
        string command,
        string description,
        MethodInfo handler,
        bool isCheat = false,
        bool isNetwork = false,
        bool onlyServer = false,
        bool isSecret = false,
        bool allowInDevBuild = false,
        bool remoteCommand = false,
        bool onlyAdmin = false)
    {
        if (TerminalConsoleCommandType == null || TerminalConsoleEventFailableType == null)
        {
            throw new MissingMemberException("Terminal console command types were not found.");
        }

        Delegate action = Delegate.CreateDelegate(TerminalConsoleEventFailableType, handler);
        ConstructorInfo constructor = TerminalConsoleCommandType.GetConstructors(Any)
            .First(c => c.GetParameters().Length == 12 && c.GetParameters()[2].ParameterType == TerminalConsoleEventFailableType);
        constructor.Invoke(new object?[]
        {
            command, description, action, isCheat, isNetwork, onlyServer, isSecret,
            allowInDevBuild, null, false, remoteCommand, onlyAdmin,
        });
    }

    internal static int ConsoleArgsLength(Terminal.ConsoleEventArgs args)
        => Convert.ToInt32(args.GetType().GetProperty("Length", Any)?.GetValue(args) ?? 0);

    internal static string ConsoleArg(Terminal.ConsoleEventArgs args, int index)
    {
        PropertyInfo? indexer = args.GetType().GetProperty("Item", Any, null, typeof(string), new[] { typeof(int) }, null);
        return (string?)indexer?.GetValue(args, new object[] { index }) ?? string.Empty;
    }

    internal static void ConsoleAddString(Terminal.ConsoleEventArgs args, string text)
    {
        object? context = args.GetType().GetProperty("Context", Any)?.GetValue(args)
                          ?? args.GetType().GetField("Context", Any)?.GetValue(args);
        MethodInfo? addString = context?.GetType().GetMethod("AddString", Any, null, new[] { typeof(string) }, null);
        if (context != null && addString != null)
        {
            Invoke(addString, context, text);
        }
    }

    internal static int StableHash(string value)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int i = 0; i < value.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ value[i];
                if (i == value.Length - 1)
                {
                    break;
                }
                hash2 = ((hash2 << 5) + hash2) ^ value[i + 1];
            }
            return hash1 + hash2 * 1566083941;
        }
    }
}
