using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    // ConfigEntry values use TOML strings. Custom values retain the existing count/element and
    // reflected-field formats, or use Valheim's ISerializableParameter hook for domain formats.
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
            Type underlyingType = Enum.GetUnderlyingType(effectiveType);
            WriteValueWithTypeToZPackage(package, underlyingType, ((IConvertible)value).ToType(underlyingType, CultureInfo.InvariantCulture));
            return;
        }

        if (effectiveType.IsPrimitive || effectiveType == typeof(string))
        {
            // ZRpc.Serialize silently skips several primitives that ZPackage itself supports,
            // including byte, short, ushort, sbyte, ulong and char.
            GameReflection.PackageWrite(package, value);
            return;
        }

        if (effectiveType.IsArray)
        {
            RequireVectorArray(effectiveType);
            if (value is byte[] bytes)
            {
                GameReflection.PackageWrite(package, bytes);
                return;
            }
        }

        if (effectiveType != typeof(List<string>))
        {
            if (value is ICollection collection)
            {
                GameReflection.PackageWrite(package, collection.Count);
                WriteCollectionElements(package, collection);
                return;
            }

            if (GetGenericCollectionType(effectiveType) is { } collectionType)
            {
                int count = (int)collectionType.GetProperty("Count")!.GetValue(value)!;
                GameReflection.PackageWrite(package, count);
                WriteCollectionElements(package, (IEnumerable)value);
                return;
            }
        }

        if (effectiveType is { IsValueType: true, IsPrimitive: false })
        {
            FieldInfo[] fields = effectiveType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameReflection.PackageWrite(package, fields.Length);
            foreach (FieldInfo field in fields)
            {
                GameReflection.PackageWrite(package, GetZPackageTypeString(field.FieldType));
                WriteCustomValueToPackage(package, field.FieldType, field.GetValue(value));
            }
            return;
        }

        int startPosition = GameReflection.PackageGetPos(package);
        GameReflection.Serialize(new[] { value }, ref package);
        if (GameReflection.PackageGetPos(package) == startPosition)
        {
            throw new NotSupportedException($"Custom value type '{effectiveType.FullName}' has no package serializer. Implement ISerializableParameter for this type.");
        }
    }

    private static void WriteCollectionElements(ZPackage package, IEnumerable collection)
    {
        foreach (object? item in collection)
        {
            if (item == null)
            {
                // Protocol 1 has no per-element null marker. Adding one would corrupt existing
                // list/array encodings. Nullable root values and reflected fields remain supported.
                throw new NotSupportedException("Null collection elements have no encoding in the current protocol. Use ISerializableParameter for a nullable collection format.");
            }
            WriteValueWithTypeToZPackage(package, item.GetType(), item);
        }
    }

    private static Type? GetGenericCollectionType(Type type)
    {
        return type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(ICollection<>));
    }

    private static void RequireVectorArray(Type type)
    {
        if (type != type.GetElementType()!.MakeArrayType())
        {
            throw new NotSupportedException($"Array type '{type.FullName}' requires shape metadata that is not present in the current protocol. Use a one-dimensional zero-based array or ISerializableParameter.");
        }
    }

    private static object ReadPrimitiveFromPackage(ZPackage package, Type type)
    {
        // ZPackage uses a BinaryReader/BinaryWriter over this same MemoryStream. Leave the stream
        // open and retain the native primitive widths, endianness, and UTF-8 character encoding.
        using BinaryReader reader = new(GameReflection.PackageStream(package), Encoding.UTF8, leaveOpen: true);
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => reader.ReadBoolean(),
            TypeCode.Byte => reader.ReadByte(),
            TypeCode.SByte => reader.ReadSByte(),
            TypeCode.Char => reader.ReadChar(),
            TypeCode.Int16 => reader.ReadInt16(),
            TypeCode.UInt16 => reader.ReadUInt16(),
            TypeCode.Int32 => reader.ReadInt32(),
            TypeCode.UInt32 => reader.ReadUInt32(),
            TypeCode.Int64 => reader.ReadInt64(),
            TypeCode.UInt64 => reader.ReadUInt64(),
            TypeCode.Single => reader.ReadSingle(),
            TypeCode.Double => reader.ReadDouble(),
            _ => throw new NotSupportedException($"Primitive custom value type '{type.FullName}' has no package encoding."),
        };
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

        if (effectiveType.IsPrimitive)
        {
            return ReadPrimitiveFromPackage(package, effectiveType);
        }
        if (effectiveType == typeof(string))
        {
            return GameReflection.PackageReadString(package);
        }

        if (effectiveType.IsArray)
        {
            RequireVectorArray(effectiveType);
            if (effectiveType == typeof(byte[]))
            {
                return GameReflection.PackageReadByteArray(package, maxPayloadSize);
            }

            int count = GameReflection.PackageReadInt(package);
            Type elementType = effectiveType.GetElementType()!;
            Array array = Array.CreateInstance(elementType, count);
            for (int index = 0; index < count; ++index)
            {
                array.SetValue(ReadValueWithTypeFromZPackage(package, elementType), index);
            }
            return array;
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

        if (effectiveType != typeof(List<string>) && GetGenericCollectionType(effectiveType) is { } collectionType)
        {
            int entriesCount = GameReflection.PackageReadInt(package);
            object collection = Activator.CreateInstance(effectiveType)!;
            Type elementType = collectionType.GenericTypeArguments[0];
            MethodInfo adder = collectionType.GetMethod("Add")!;
            for (int i = 0; i < entriesCount; ++i)
            {
                adder.Invoke(collection, new[] { ReadValueWithTypeFromZPackage(package, elementType) });
            }
            return collection;
        }

        ParameterInfo param = (ParameterInfo)FormatterServices.GetUninitializedObject(typeof(ParameterInfo));
        AccessTools.DeclaredField(typeof(ParameterInfo), "ClassImpl").SetValue(param, effectiveType);
        List<object> data = new();
        GameReflection.Deserialize(new[] { null, param }, package, ref data);
        if (data.Count != 1)
        {
            throw new NotSupportedException($"Custom value type '{effectiveType.FullName}' has no package deserializer. Implement ISerializableParameter for this type.");
        }
        return data[0];
    }
}
