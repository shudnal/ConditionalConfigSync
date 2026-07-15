using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private readonly struct ReceivedConfigState
    {
        public readonly bool ServerControlled;
        public readonly bool Hidden;

        public ReceivedConfigState(bool serverControlled, bool hidden)
        {
            ServerControlled = serverControlled;
            Hidden = hidden;
        }
    }

    private void ApplyParsedConfigs(ParsedConfigs configs, bool receivedFromServer)
    {
        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "Apply", $"Applying configs={configs.configValues.Count}, custom={configs.customValues.Count}, states={configs.configStates.Count}");
        Dictionary<ConfigFile, bool> saveOnConfigSet = new();
        List<PolicyStateChangedEventArgs> policyTransitions = new();

        void DisableSaveOnConfigSet(ConfigFile configFile)
        {
            if (!saveOnConfigSet.ContainsKey(configFile))
            {
                saveOnConfigSet[configFile] = configFile.SaveOnConfigSet;
                configFile.SaveOnConfigSet = false;
            }
        }

        bool SetConfigValue(OwnConfigEntryBase config, object? value)
        {
            ConfigFile configFile = config.BaseConfig.ConfigFile;
            DisableSaveOnConfigSet(configFile);
            configsBeingApplied.Add(config.BaseConfig);
            try
            {
                config.BaseConfig.BoxedValue = value;
                return true;
            }
            catch (Exception e)
            {
                ConfigDefinition definition = config.BaseConfig.Definition;
                DebugWarning("Apply", $"Failed to apply config {definition.Section} -> {definition.Key}; continuing with the remaining entries. Error: {e}");
                return false;
            }
            finally
            {
                configsBeingApplied.Remove(config.BaseConfig);
            }
        }

        void RestoreLocalValueIfNeeded(OwnConfigEntryBase config)
        {
            if (!config.HasLocalBaseValue)
            {
                return;
            }

            if (SetConfigValue(config, config.LocalBaseValue))
            {
                config.ClearLocalBaseValue();
            }
        }

        void ApplyServerValueIfAvailable(OwnConfigEntryBase config)
        {
            if (!config.HasServerValue)
            {
                return;
            }

            if (!config.HasLocalBaseValue)
            {
                config.StoreLocalBaseValue(config.BaseConfig.BoxedValue);
            }
            SetConfigValue(config, config.ServerValue);
        }

        try
        {
            if (receivedFromServer)
            {
                foreach (KeyValuePair<OwnConfigEntryBase, ReceivedConfigState> stateKv in configs.configStates)
                {
                    bool oldServerControlled = stateKv.Key.IsServerControlled;
                    bool oldHidden = stateKv.Key.IsHidden;
                    stateKv.Key.IsServerControlled = stateKv.Value.ServerControlled;
                    stateKv.Key.IsHidden = stateKv.Value.Hidden;
                    stateKv.Key.IsPolicyStateInitialized = true;
                    if (oldServerControlled != stateKv.Value.ServerControlled || oldHidden != stateKv.Value.Hidden)
                    {
                        policyTransitions.Add(new PolicyStateChangedEventArgs(
                            stateKv.Key,
                            oldServerControlled,
                            stateKv.Value.ServerControlled,
                            oldHidden,
                            stateKv.Value.Hidden,
                            "server package"));
                    }
                }
            }

            foreach (KeyValuePair<OwnConfigEntryBase, object?> configKv in configs.configValues)
            {
                if (receivedFromServer)
                {
                    configKv.Key.StoreServerValue(configKv.Value);
                    if (!configKv.Key.IsServerControlled)
                    {
                        RestoreLocalValueIfNeeded(configKv.Key);
                        continue;
                    }

                    if (!configKv.Key.HasLocalBaseValue)
                    {
                        configKv.Key.StoreLocalBaseValue(configKv.Key.BaseConfig.BoxedValue);
                    }
                }

                SetConfigValue(configKv.Key, configKv.Value);
            }

            if (receivedFromServer)
            {
                foreach (KeyValuePair<OwnConfigEntryBase, ReceivedConfigState> stateKv in configs.configStates)
                {
                    if (configs.configValues.ContainsKey(stateKv.Key))
                    {
                        continue;
                    }

                    if (stateKv.Key.IsServerControlled)
                    {
                        ApplyServerValueIfAvailable(stateKv.Key);
                    }
                    else
                    {
                        RestoreLocalValueIfNeeded(stateKv.Key);
                    }
                }
            }
        }
        finally
        {
            foreach (KeyValuePair<ConfigFile, bool> kv in saveOnConfigSet)
            {
                kv.Key.SaveOnConfigSet = kv.Value;
                try
                {
                    kv.Key.Save();
                }
                catch (Exception e)
                {
                    DebugWarning("Apply", $"Failed to save config file '{kv.Key.ConfigFilePath}' after applying synchronized values: {e}");
                }
            }
        }

        foreach (KeyValuePair<CustomSyncedValueBase, object?> configKv in configs.customValues)
        {
            if (!isServer && !configKv.Key.HasLocalBaseValue)
            {
                configKv.Key.StoreLocalBaseValue(configKv.Key.BoxedValue);
            }

            customValuesBeingApplied.Add(configKv.Key);
            try
            {
                configKv.Key.BoxedValue = configKv.Value;
            }
            catch (Exception e)
            {
                DebugWarning("Apply", $"Failed to apply custom value '{configKv.Key.Identifier}'; continuing with the remaining entries. Error: {e}");
            }
            finally
            {
                customValuesBeingApplied.Remove(configKv.Key);
            }
        }

        if (receivedFromServer)
        {
            if (configs.capabilities.HasValue)
            {
                remotePolicyChangeSupported = (configs.capabilities.Value & PolicyChangeCapability) != 0;
            }

            // lockExempt is process-wide. Update Configuration Manager metadata and LockStateChanged
            // for every registered synchronization instance before policy subscribers are invoked.
            foreach (ConditionalConfigSync configSync in configSyncs)
            {
                configSync.ServerLockedSettingChanged();
            }
        }

        // Policy subscribers observe the final active values and effective read-only/visibility metadata.
        foreach (PolicyStateChangedEventArgs transition in policyTransitions)
        {
            RaisePolicyStateEvents(transition);
        }
    }

    private class ParsedConfigs
    {
        public readonly Dictionary<OwnConfigEntryBase, object?> configValues = new();
        public readonly Dictionary<CustomSyncedValueBase, object?> customValues = new();
        public readonly Dictionary<OwnConfigEntryBase, ReceivedConfigState> configStates = new();
        public int? capabilities;
    }

    private static string GetSingleEntryReceiveDetails(ParsedConfigs configs)
    {
        List<string> details = new();

        if (configs.configValues.Count == 1)
        {
            ConfigDefinition definition = configs.configValues.Keys.First().BaseConfig.Definition;
            details.Add($"config={definition.Section} -> {definition.Key}");
        }

        if (configs.customValues.Count == 1)
        {
            details.Add($"custom={configs.customValues.Keys.First().Identifier}");
        }

        return details.Count == 0 ? "" : $" ({string.Join(", ", details)})";
    }

    private enum PackageEntryKind : byte
    {
        Config = 1,
        CustomValue = 2,
        ServerVersion = 3,
        LockExempt = 4,
        ConfigState = 5,
    }

    private ParsedConfigs ReadConfigsFromPackage(ZPackage package, bool receivedFromServer)
    {
        ParsedConfigs configs = new();
        Dictionary<string, OwnConfigEntryBase> configMap = allConfigs.ToDictionary(c => c.BaseConfig.Definition.Section + "\n" + c.BaseConfig.Definition.Key, c => c);
        Dictionary<string, CustomSyncedValueBase> customValueMap = allCustomValues.ToDictionary(c => c.Identifier, c => c);

        int valueCount = GameReflection.PackageReadInt(package);
        if (valueCount < 0 || valueCount > maxPackageEntries)
        {
            throw new InvalidDataException($"Invalid config entry count {valueCount}");
        }

        for (int i = 0; i < valueCount; ++i)
        {
            PackageEntryKind kind = (PackageEntryKind)GameReflection.PackageReadByte(package);
            byte[] payload = GameReflection.PackageReadByteArray(package, maxPayloadSize);
            if (payload.Length > maxPayloadSize)
            {
                DebugWarning("Read", $"Skipping too large entry payload ({payload.Length})");
                continue;
            }

            ZPackage entry = GameReflection.NewPackage(payload);

            switch (kind)
            {
                case PackageEntryKind.Config:
                    {
                        string section = GameReflection.PackageReadString(entry);
                        string key = GameReflection.PackageReadString(entry);
                        string serializedValue = GameReflection.PackageReadString(entry);

                        if (configMap.TryGetValue(section + "\n" + key, out OwnConfigEntryBase config))
                        {
                            try
                            {
                                configs.configValues[config] = TomlTypeConverter.ConvertToValue(serializedValue, config.BaseConfig.SettingType);
                            }
                            catch (Exception e)
                            {
                                DebugWarning("Read", $"Config value of setting \"{config.BaseConfig.Definition}\" could not be parsed and will be ignored. Reason: {e.Message}");
                            }
                        }
                        else
                        {
                            DebugWarning("Read", $"Received unknown config entry {section}/{key}. This may happen if client and server versions of the mod do not match.");
                        }
                        break;
                    }
                case PackageEntryKind.ConfigState:
                    {
                        string section = GameReflection.PackageReadString(entry);
                        string key = GameReflection.PackageReadString(entry);
                        bool serverControlled = GameReflection.PackageReadBool(entry);
                        bool hidden = GameReflection.PackageReadBool(entry);

                        if (configMap.TryGetValue(section + "\n" + key, out OwnConfigEntryBase config))
                        {
                            configs.configStates[config] = new ReceivedConfigState(serverControlled, hidden);
                        }
                        else
                        {
                            DebugWarning("Read", $"Received unknown config state {section}/{key}. This may happen if client and server versions of the mod do not match.");
                        }
                        break;
                    }
                case PackageEntryKind.CustomValue:
                    {
                        string identifier = GameReflection.PackageReadString(entry);
                        string typeName = GameReflection.PackageReadString(entry);

                        if (!customValueMap.TryGetValue(identifier, out CustomSyncedValueBase config))
                        {
                            DebugWarning("Read", $"Received unknown custom synced value {identifier}. This may happen if client and server versions of the mod do not match.");
                            break;
                        }

                        string expectedType = GetZPackageTypeString(config.Type);
                        if (typeName != expectedType)
                        {
                            DebugWarning("Read", $"Got unexpected type {typeName} for custom synced value {identifier}, expecting {expectedType}");
                            break;
                        }

                        try
                        {
                            configs.customValues[config] = ReadCustomValueFromPackage(entry, config.Type);
                        }
                        catch (InvalidDeserializationTypeException e)
                        {
                            DebugWarning("Read", $"Got unexpected struct internal type {e.received} for field {e.field} struct {typeName} for custom synced value {identifier}, expecting {e.expected}");
                        }
                        catch (Exception e)
                        {
                            DebugWarning("Read", $"Could not deserialize custom synced value {identifier}: {e}");
                        }
                        break;
                    }
                case PackageEntryKind.ServerVersion:
                    {
                        string serverVersion = GameReflection.PackageReadString(entry);
                        if (receivedFromServer && serverVersion != CurrentVersion)
                        {
                            DebugWarning("Version", $"Received server version is not equal: server version={serverVersion}, local version={CurrentVersion ?? "unknown"}");
                        }
                        break;
                    }
                case PackageEntryKind.LockExempt:
                    {
                        bool exempt = GameReflection.PackageReadBool(entry);
                        if (receivedFromServer)
                        {
                            lockExempt = exempt;
                            if (GameReflection.PackageSize(entry) - GameReflection.PackageGetPos(entry) >= sizeof(int))
                            {
                                configs.capabilities = GameReflection.PackageReadInt(entry);
                            }
                        }
                        break;
                    }
                default:
                    DebugWarning("Read", $"Received unknown config entry kind {(byte)kind}");
                    break;
            }
        }

        return configs;
    }

    private class PackageEntry
    {
        public PackageEntryKind kind;
        public string? section;
        public string? key;
        public Type? type;
        public object? value;
        public bool serverControlled;
        public bool hidden;
        public int? capabilities;

        public static PackageEntry ServerVersion(string version) => new() { kind = PackageEntryKind.ServerVersion, value = version };
        public static PackageEntry LockExempt(bool value, int? capabilities = null) => new() { kind = PackageEntryKind.LockExempt, value = value, capabilities = capabilities };
        public static PackageEntry ConfigState(ConfigEntryBase config, bool serverControlled, bool hidden) => new() { kind = PackageEntryKind.ConfigState, section = config.Definition.Section, key = config.Definition.Key, serverControlled = serverControlled, hidden = hidden };
    }

    private static ZPackage ConfigsToPackage(
        IEnumerable<ConfigEntryBase>? configs = null,
        IEnumerable<CustomSyncedValueBase>? customValues = null,
        IEnumerable<PackageEntry>? packageEntries = null,
        bool partial = true,
        bool includeConfigValues = true,
        bool includeAllProvidedConfigStates = false)
    {
        List<OwnConfigEntryBase> configList = new();
        if (configs != null)
        {
            foreach (ConfigEntryBase configEntry in configs)
            {
                OwnConfigEntryBase? data = GetConfigData(configEntry);
                if (data != null && (includeAllProvidedConfigStates || ShouldIncludeConfigInPackage(data)) && !configList.Contains(data))
                {
                    configList.Add(data);
                }
            }
        }
        List<OwnConfigEntryBase> valueConfigList = includeConfigValues
            ? configList.Where(GetPackageServerControlled).ToList()
            : new List<OwnConfigEntryBase>();
        List<CustomSyncedValueBase> customValueList = customValues?.ToList() ?? new List<CustomSyncedValueBase>();
        List<PackageEntry> packageEntryList = packageEntries?.ToList() ?? new List<PackageEntry>();

        ZPackage package = GameReflection.NewPackage();
        GameReflection.PackageWrite(package, (byte)((partial ? PARTIAL_CONFIGS : 0) | V2_PACKAGE));
        GameReflection.PackageWrite(package, valueConfigList.Count + configList.Count + customValueList.Count + packageEntryList.Count);

        foreach (PackageEntry packageEntry in packageEntryList)
        {
            AddEntryToPackage(package, packageEntry);
        }
        foreach (OwnConfigEntryBase config in configList)
        {
            AddEntryToPackage(package, PackageEntry.ConfigState(config.BaseConfig, GetPackageServerControlled(config), GetPackageHidden(config)));
        }
        foreach (CustomSyncedValueBase customValue in customValueList)
        {
            AddEntryToPackage(package, new PackageEntry { kind = PackageEntryKind.CustomValue, key = customValue.Identifier, type = customValue.Type, value = customValue.BoxedValue });
        }
        foreach (OwnConfigEntryBase config in valueConfigList)
        {
            AddEntryToPackage(package, new PackageEntry { kind = PackageEntryKind.Config, section = config.BaseConfig.Definition.Section, key = config.BaseConfig.Definition.Key, type = config.BaseConfig.SettingType, value = config.BaseConfig.BoxedValue });
        }

        return package;
    }

    private static void AddEntryToPackage(ZPackage package, PackageEntry entry)
    {
        ZPackage payload = GameReflection.NewPackage();
        switch (entry.kind)
        {
            case PackageEntryKind.Config:
                GameReflection.PackageWrite(payload, entry.section!);
                GameReflection.PackageWrite(payload, entry.key!);
                GameReflection.PackageWrite(payload, TomlTypeConverter.ConvertToString(entry.value, entry.type!));
                break;
            case PackageEntryKind.CustomValue:
                GameReflection.PackageWrite(payload, entry.key!);
                GameReflection.PackageWrite(payload, GetZPackageTypeString(entry.type!));
                WriteCustomValueToPackage(payload, entry.type!, entry.value);
                break;
            case PackageEntryKind.ServerVersion:
                GameReflection.PackageWrite(payload, entry.value?.ToString() ?? "");
                break;
            case PackageEntryKind.LockExempt:
                GameReflection.PackageWrite(payload, entry.value is bool b && b);
                if (entry.capabilities.HasValue)
                {
                    GameReflection.PackageWrite(payload, entry.capabilities.Value);
                }
                break;
            case PackageEntryKind.ConfigState:
                GameReflection.PackageWrite(payload, entry.section!);
                GameReflection.PackageWrite(payload, entry.key!);
                GameReflection.PackageWrite(payload, entry.serverControlled);
                GameReflection.PackageWrite(payload, entry.hidden);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry.kind), entry.kind, null);
        }

        GameReflection.PackageWrite(package, (byte)entry.kind);
        GameReflection.PackageWrite(package, GameReflection.PackageGetArray(payload));
    }

    private static string GetZPackageTypeString(Type type) => type.AssemblyQualifiedName!;

    private static void WriteCustomValueToPackage(ZPackage package, Type type, object? value)
    {
        GameReflection.PackageWrite(package, value != null);
        if (value == null)
        {
            return;
        }

        WriteValueWithTypeToZPackage(package, type, value);
    }

    private static object? ReadCustomValueFromPackage(ZPackage package, Type type)
    {
        bool hasValue = GameReflection.PackageReadBool(package);
        if (!hasValue)
        {
            return null;
        }

        return ReadValueWithTypeFromZPackage(package, type);
    }

    // ConfigEntry values use TOML strings. Custom values stay free-form and may use Valheim's own serializer hook.
    private static void WriteValueWithTypeToZPackage(ZPackage package, Type type, object value)
    {
        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        if (typeof(ISerializableParameter).IsAssignableFrom(effectiveType))
        {
            GameReflection.SerializeParameter(value, ref package);
            return;
        }

        if (effectiveType.IsEnum)
        {
            WriteValueWithTypeToZPackage(package, Enum.GetUnderlyingType(effectiveType), ((IConvertible)value).ToType(Enum.GetUnderlyingType(effectiveType), CultureInfo.InvariantCulture));
            return;
        }

        if (value is ICollection collection && effectiveType != typeof(string) && effectiveType != typeof(List<string>))
        {
            GameReflection.PackageWrite(package, collection.Count);
            foreach (object item in collection)
            {
                WriteValueWithTypeToZPackage(package, item.GetType(), item);
            }
            return;
        }

        if (effectiveType is { IsValueType: true, IsPrimitive: false })
        {
            FieldInfo[] fields = effectiveType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameReflection.PackageWrite(package, fields.Length);
            foreach (FieldInfo field in fields)
            {
                GameReflection.PackageWrite(package, GetZPackageTypeString(field.FieldType));
                object? fieldValue = field.GetValue(value);
                WriteCustomValueToPackage(package, field.FieldType, fieldValue);
            }
            return;
        }

        GameReflection.Serialize(new[] { value }, ref package);
    }

    private static object ReadValueWithTypeFromZPackage(ZPackage package, Type type)
    {
        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        if (typeof(ISerializableParameter).IsAssignableFrom(effectiveType))
        {
            object value = Activator.CreateInstance(effectiveType) ?? throw new MissingMethodException($"Cannot create {effectiveType.FullName} for ISerializableParameter deserialization");
            GameReflection.DeserializeParameter(value, ref package);
            return value;
        }

        if (effectiveType.IsEnum)
        {
            object underlying = ReadValueWithTypeFromZPackage(package, Enum.GetUnderlyingType(effectiveType));
            return Enum.ToObject(effectiveType, underlying);
        }

        if (effectiveType is { IsValueType: true, IsPrimitive: false })
        {
            FieldInfo[] fields = effectiveType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int fieldCount = GameReflection.PackageReadInt(package);
            if (fieldCount != fields.Length)
            {
                throw new InvalidDeserializationTypeException { received = $"(field count: {fieldCount})", expected = $"(field count: {fields.Length})" };
            }

            object value = FormatterServices.GetUninitializedObject(effectiveType);
            foreach (FieldInfo field in fields)
            {
                string typeName = GameReflection.PackageReadString(package);
                if (typeName != GetZPackageTypeString(field.FieldType))
                {
                    throw new InvalidDeserializationTypeException { received = typeName, expected = GetZPackageTypeString(field.FieldType), field = field.Name };
                }
                field.SetValue(value, ReadCustomValueFromPackage(package, field.FieldType));
            }
            return value;
        }
        if (effectiveType.IsGenericType && effectiveType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            int entriesCount = GameReflection.PackageReadInt(package);
            IDictionary dict = (IDictionary)Activator.CreateInstance(effectiveType)!;
            Type kvType = typeof(KeyValuePair<,>).MakeGenericType(effectiveType.GenericTypeArguments);
            FieldInfo keyField = kvType.GetField("key", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo valueField = kvType.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int i = 0; i < entriesCount; ++i)
            {
                object kv = ReadValueWithTypeFromZPackage(package, kvType);
                dict.Add(keyField.GetValue(kv), valueField.GetValue(kv));
            }
            return dict;
        }
        if (effectiveType != typeof(List<string>) && effectiveType.IsGenericType && typeof(ICollection<>).MakeGenericType(effectiveType.GenericTypeArguments[0]) is { } collectionType && collectionType.IsAssignableFrom(effectiveType))
        {
            int entriesCount = GameReflection.PackageReadInt(package);
            object list = Activator.CreateInstance(effectiveType)!;
            MethodInfo adder = collectionType.GetMethod("Add")!;
            for (int i = 0; i < entriesCount; ++i)
            {
                adder.Invoke(list, new[] { ReadValueWithTypeFromZPackage(package, effectiveType.GenericTypeArguments[0]) });
            }
            return list;
        }

        ParameterInfo param = (ParameterInfo)FormatterServices.GetUninitializedObject(typeof(ParameterInfo));
        AccessTools.DeclaredField(typeof(ParameterInfo), "ClassImpl").SetValue(param, effectiveType);
        List<object> data = new();
        GameReflection.Deserialize(new[] { null, param }, package, ref data);
        return data.First();
    }

    private static ArraySegment<byte> GetPackageArraySegment(ZPackage package)
    {
        if (GameReflection.PackageStream(package).TryGetBuffer(out ArraySegment<byte> segment))
        {
            return new ArraySegment<byte>(segment.Array!, segment.Offset, GameReflection.PackageSize(package));
        }

        byte[] array = GameReflection.PackageGetArray(package);
        return new ArraySegment<byte>(array, 0, array.Length);
    }

    private static void WriteByteArray(ZPackage package, ArraySegment<byte> data)
    {
        GameReflection.PackageWrite(package, data.Count);
        GameReflection.PackageStream(package).Write(data.Array!, data.Offset, data.Count);
    }

    private static byte[] DecompressLimited(byte[] data, int maxBytes)
    {
        using MemoryStream input = new(data);
        using DeflateStream deflateStream = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        for (; ; )
        {
            int read = deflateStream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }
            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException($"Decompressed package exceeds limit {maxBytes}");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private class InvalidDeserializationTypeException : Exception
    {
        public string expected = null!;
        public string received = null!;
        public string field = "";
    }
}
