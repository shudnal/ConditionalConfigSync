using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private static readonly Regex assemblyVersionTypeIdentityPattern = new(
        @",\s*Version=[^,\]\[]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // ConfigEntry values use TOML strings. Custom values retain the existing count/element and
    // reflected-field formats, or use Valheim's ISerializableParameter hook for domain formats.
    private static void WriteValueWithTypeToZPackage(ZPackage package, Type type, object value)
    {
        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(ISerializableParameter).IsAssignableFrom(effectiveType))
        {
            RequireConstructibleSerializableParameter(effectiveType);
            RequireExactRuntimeType(effectiveType, value, "ISerializableParameter");
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
            Type? collectionType = GetGenericCollectionType(effectiveType);
            if (!effectiveType.IsArray && collectionType != null)
            {
                // Validate the declared type, not just the runtime collection. The matching reader
                // must be able to create an assignable instance without changing the wire layout.
                GetCollectionImplementationType(effectiveType, collectionType);
            }
            else if (effectiveType.IsInterface && value is ICollection)
            {
                throw new NotSupportedException($"Collection interface '{effectiveType.FullName}' has no supported materialization. Declare an ICollection<T>-based type or implement ISerializableParameter.");
            }

            if (value is ICollection collection)
            {
                GameReflection.PackageWrite(package, collection.Count);
                Type? elementType = effectiveType.IsArray ? effectiveType.GetElementType() : collectionType?.GenericTypeArguments[0];
                WriteCollectionElements(package, collection, elementType);
                return;
            }

            if (collectionType != null)
            {
                int count = (int)collectionType.GetProperty("Count")!.GetValue(value)!;
                GameReflection.PackageWrite(package, count);
                WriteCollectionElements(package, (IEnumerable)value, collectionType.GenericTypeArguments[0]);
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

        RequireExactRuntimeType(effectiveType, value, "custom value");
        int startPosition = GameReflection.PackageGetPos(package);
        GameReflection.Serialize(new[] { value }, ref package);
        if (GameReflection.PackageGetPos(package) == startPosition)
        {
            throw new NotSupportedException($"Custom value type '{effectiveType.FullName}' has no package serializer. Implement ISerializableParameter for this type.");
        }
    }

    private static void WriteCollectionElements(ZPackage package, IEnumerable collection, Type? declaredElementType)
    {
        foreach (object? item in collection)
        {
            if (item == null)
            {
                // Protocol 1 has no per-element null marker. Adding one would corrupt existing
                // list/array encodings. Nullable root values and reflected fields remain supported.
                throw new NotSupportedException("Null collection elements have no encoding in the current protocol. Use ISerializableParameter for a nullable collection format.");
            }

            if (declaredElementType == null)
            {
                WriteValueWithTypeToZPackage(package, item.GetType(), item);
                continue;
            }

            Type effectiveElementType = Nullable.GetUnderlyingType(declaredElementType) ?? declaredElementType;
            RequireExactRuntimeType(effectiveElementType, item, "collection element");
            RequireConstructibleSerializableParameter(effectiveElementType);
            // The receiver decodes with this same declared element type. Do not dispatch on a
            // polymorphic runtime type and produce bytes the matching reader interprets differently.
            WriteValueWithTypeToZPackage(package, effectiveElementType, item);
        }
    }

    private static void RequireExactRuntimeType(Type declaredType, object value, string context)
    {
        Type effectiveType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (value.GetType() != effectiveType)
        {
            throw new NotSupportedException(
                $"Polymorphic {context} '{value.GetType().FullName}' cannot be encoded as declared type '{effectiveType.FullName}' by the current protocol. " +
                "Use an exact declared element/value type or implement an explicit ISerializableParameter container format.");
        }
    }

    private static bool IsCompatibleZPackageTypeString(string receivedTypeName, Type expectedType)
    {
        string expectedTypeName = GetZPackageTypeString(expectedType);
        return string.Equals(receivedTypeName, expectedTypeName, StringComparison.Ordinal)
               || string.Equals(
                   RemoveAssemblyVersionFromTypeIdentity(receivedTypeName),
                   RemoveAssemblyVersionFromTypeIdentity(expectedTypeName),
                   StringComparison.Ordinal);
    }

    private static string RemoveAssemblyVersionFromTypeIdentity(string typeName)
        => assemblyVersionTypeIdentityPattern.Replace(typeName, string.Empty);

    private static void RequireConstructibleSerializableParameter(Type type)
    {
        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        if (!typeof(ISerializableParameter).IsAssignableFrom(effectiveType))
        {
            return;
        }

        // Mirror Activator.CreateInstance(Type) on the receiving side. Structs have a valid default
        // construction path even when reflection does not report an explicit parameterless constructor.
        if (effectiveType.IsInterface || effectiveType.IsAbstract || effectiveType.ContainsGenericParameters
            || !effectiveType.IsValueType && effectiveType.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new NotSupportedException($"ISerializableParameter type '{effectiveType.FullName}' cannot be constructed by the matching reader. Declare a closed concrete value type or a class with a public parameterless constructor.");
        }
    }

    private static Type? GetGenericCollectionType(Type type)
    {
        // Structs implementing only ICollection<T> already use the reflected-field layout.
        // Keep that successful encoding instead of changing them to a count/element format.
        if (type.IsValueType)
        {
            return null;
        }
        return type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(ICollection<>));
    }

    private static Type GetCollectionImplementationType(Type declaredType, Type collectionType)
    {
        if (!declaredType.IsInterface)
        {
            if (declaredType.IsAbstract || declaredType.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new NotSupportedException($"Collection type '{declaredType.FullName}' requires a concrete type with a public parameterless constructor or ISerializableParameter.");
            }
            return declaredType;
        }

        Type elementType = collectionType.GenericTypeArguments[0];
        Type listType = typeof(List<>).MakeGenericType(elementType);
        if (declaredType.IsAssignableFrom(listType))
        {
            return listType;
        }

        Type setType = typeof(HashSet<>).MakeGenericType(elementType);
        if (declaredType.IsAssignableFrom(setType))
        {
            return setType;
        }

        if (elementType.IsGenericType && elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(elementType.GenericTypeArguments);
            if (declaredType.IsAssignableFrom(dictionaryType))
            {
                return dictionaryType;
            }
        }

        throw new NotSupportedException($"Collection interface '{declaredType.FullName}' has no compatible built-in implementation. Declare a constructible concrete type or implement ISerializableParameter.");
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
            RequireConstructibleSerializableParameter(effectiveType);
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
                if (!IsCompatibleZPackageTypeString(typeName, field.FieldType))
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
            Type implementationType = GetCollectionImplementationType(effectiveType, collectionType);
            int entriesCount = GameReflection.PackageReadInt(package);
            object collection = Activator.CreateInstance(implementationType)!;
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
