using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MappShark.Internal;

internal static class ReflectionMapperFactory<TSource, TDestination>
{
    private static readonly Type GenericConverterContract = typeof(IMapValueConverter<,>);

    public static readonly Func<TSource, TDestination> Map = BuildMapper();

    private static Func<TSource, TDestination> BuildMapper()
    {
        var destinationType = typeof(TDestination);

        // Positional records and other types without a parameterless constructor:
        // build the instance by invoking the primary constructor with resolved argument values.
        var parameterlessCtor = destinationType.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor is null)
        {
            return BuildConstructorBasedMapper(destinationType);
        }

        var propertyPairs = BuildPropertyPairs();

        return source =>
        {
            var destination = (TDestination)parameterlessCtor.Invoke(null);

            foreach (var pair in propertyPairs)
            {
                var value = pair.Source.GetValue(source);
                var converted = pair.Converter(value);
                pair.Destination.SetValue(destination, converted);
            }

            return destination;
        };
    }

    private static Func<TSource, TDestination> BuildConstructorBasedMapper(Type destinationType)
    {
        // Find the primary constructor: public, not the record copy constructor (single param of same type)
        var primaryCtor = destinationType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Where(c =>
            {
                var p = c.GetParameters();
                return p.Length > 0 && !(p.Length == 1 && p[0].ParameterType == destinationType);
            })
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No suitable public constructor found for type '{destinationType.FullName}'. " +
                "Positional records require a public primary constructor.");

        var ctorParams = primaryCtor.GetParameters();

        // BuildPropertyPairs already resolves [MapFrom], [MapTo], name fallback and type conversion.
        // For positional records, [MapFrom] / [MapIndex] placed on positional parameters (without the
        // [property: ...] specifier) are read from the constructor parameter via GetPositionalParameterAttribute.
        var allPairs = BuildPropertyPairs();
        var pairByDestName = allPairs.ToDictionary(p => p.Destination.Name, StringComparer.OrdinalIgnoreCase);

        // Positional args: one per constructor parameter, resolved by parameter name.
        // Parameters with no matching source property are treated as "orphan" — their constructor slot
        // receives the CLR default for that type (null for reference types, default(T) for value types).
        // This mirrors the behaviour of the parameterless-constructor path, which simply skips properties
        // that have no source counterpart.
        var positionalPairs = ctorParams.Select(param =>
        {
            if (!pairByDestName.TryGetValue(param.Name!, out var pair))
                return (PropertyPair?)null; // orphan parameter — will receive default value
            pairByDestName.Remove(param.Name!); // remove so it's not set again via SetValue
            return (PropertyPair?)pair;
        }).ToArray();

        // Remaining pairs: non-positional init properties that can be set via SetValue after construction.
        var remainingPairs = pairByDestName.Values.ToArray();

        return source =>
        {
            var args = new object?[ctorParams.Length];
            for (var i = 0; i < ctorParams.Length; i++)
            {
                if (positionalPairs[i] is { } pair)
                {
                    var value = pair.Source.GetValue(source);
                    args[i] = pair.Converter(value);
                }
                else
                {
                    // Orphan parameter: no source mapping. Use CLR default (null / default(T)).
                    var paramType = ctorParams[i].ParameterType;
                    args[i] = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
                }
            }

            var destination = (TDestination)primaryCtor.Invoke(args);

            foreach (var pair in remainingPairs)
            {
                var value = pair.Source.GetValue(source);
                var converted = pair.Converter(value);
                pair.Destination.SetValue(destination, converted);
            }

            return destination;
        };
    }

    private static IReadOnlyList<PropertyPair> BuildPropertyPairs()
    {
        var sourceType = typeof(TSource);
        var destinationType = typeof(TDestination);

        var sourceByIndex = CollectIndexedSourceProperties(sourceType);
        var destinationByIndex = CollectIndexedDestinationProperties(destinationType);

        // Collect name-based fallback pairs (non-indexed properties with matching names)
        var sourceMappedByIndex = new HashSet<string>(sourceByIndex.Values.Select(p => p.Name), StringComparer.Ordinal);
        var destinationMappedByIndex = new HashSet<string>(destinationByIndex.Values.Select(m => m.Property.Name), StringComparer.Ordinal);

        var pairs = new List<PropertyPair>(destinationByIndex.Count);

        // Indexed pairs (high priority)
        foreach (var entry in destinationByIndex)
        {
            var index = entry.Key;
            var destinationProperty = entry.Value;

            if (!sourceByIndex.TryGetValue(index, out var sourceProperty))
            {
                throw new InvalidOperationException(
                    $"No source property with [MapIndex({index})] was found in '{sourceType.FullName}' required by destination '{destinationType.FullName}.{destinationProperty.Property.Name}'.");
            }

            var converter = BuildPropertyConverter(
                sourceType,
                destinationType,
                index,
                sourceProperty,
                destinationProperty.Property,
                destinationProperty.ConverterType);

            pairs.Add(new PropertyPair(sourceProperty, destinationProperty.Property, converter));
        }

        // Name-based fallback + [MapFrom]/[MapTo] overrides
        var sourceByName = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic && !sourceMappedByIndex.Contains(p.Name))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        // [MapFrom("X")] on destination property → destPropName → sourcePropName.
        // For positional records, also check the corresponding constructor parameter
        // because [MapFrom] placed directly on the parameter (without [property: ...]) is
        // not forwarded to the synthesized property by the C# compiler.
        var mapFromOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var destProp in destinationType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var mapFrom = destProp.GetCustomAttribute<MapFromAttribute>(inherit: true)
                ?? GetPositionalParameterAttribute<MapFromAttribute>(destinationType, destProp.Name);
            if (mapFrom is not null && !destinationMappedByIndex.Contains(destProp.Name))
                mapFromOverrides[destProp.Name] = mapFrom.PropertyName;
        }

        // [MapTo("Y")] on source property → destPropName → (sourceProp, optional converter)
        var mapToOverrides = new Dictionary<string, (PropertyInfo Property, Type? ConverterType)>(StringComparer.Ordinal);
        foreach (var srcProp in sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic && !sourceMappedByIndex.Contains(p.Name)))
        {
            var mapTo = srcProp.GetCustomAttribute<MapToAttribute>(inherit: true)
                ?? GetPositionalParameterAttribute<MapToAttribute>(sourceType, srcProp.Name);
            if (mapTo is not null)
            {
                var srcConverterType = (srcProp.GetCustomAttribute<MapConverterAttribute>(inherit: true)
                    ?? GetPositionalParameterAttribute<MapConverterAttribute>(sourceType, srcProp.Name))?.ConverterType;
                mapToOverrides[mapTo.PropertyName] = (srcProp, srcConverterType);
            }
        }

        foreach (var destProp in destinationType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!destProp.CanWrite || destProp.SetMethod is null || !destProp.SetMethod.IsPublic)
                continue;
            if (destinationMappedByIndex.Contains(destProp.Name))
                continue;

            // Priority: [MapFrom] > [MapTo] > same-name fallback
            PropertyInfo? srcProp;
            Type? mapToConverterType = null;
            if (mapFromOverrides.TryGetValue(destProp.Name, out var fromSourceName))
            {
                if (!sourceByName.TryGetValue(fromSourceName, out srcProp))
                    continue; // [MapFrom] source not found — skip (generator would have reported MSP014)
            }
            else if (mapToOverrides.TryGetValue(destProp.Name, out var mapToEntry))
            {
                srcProp = mapToEntry.Property;
                mapToConverterType = mapToEntry.ConverterType;
            }
            else if (!sourceByName.TryGetValue(destProp.Name, out srcProp))
            {
                continue;
            }

            // Check type compatibility (direct, nullable, collection, or nested)
            Func<object?, object?> converter;
            if (mapToConverterType is not null)
            {
                converter = BuildCustomConverter(
                    sourceType,
                    destinationType,
                    index: -1,
                    srcProp!,
                    destProp,
                    mapToConverterType,
                    srcProp!.PropertyType,
                    destProp.PropertyType);
            }
            else if (destProp.PropertyType.IsAssignableFrom(srcProp.PropertyType))
            {
                converter = static v => v;
            }
            else
            {
                var srcType = srcProp.PropertyType;
                var dstType = destProp.PropertyType;
                if (TryBuildCollectionConverter(srcType, dstType, out var colConv))
                {
                    converter = colConv;
                }
                else if (CanUseNestedMap(srcType, dstType))
                {
                    converter = value =>
                    {
                        if (value is null) return null;
                        return RuntimeMapInvoker.Map(srcType, dstType, value);
                    };
                }
                else
                {
                    continue; // incompatible types, skip silently
                }
            }

            pairs.Add(new PropertyPair(srcProp, destProp, converter));
        }

        return pairs;
    }

    private static Dictionary<int, PropertyInfo> CollectIndexedSourceProperties(Type type)
    {
        var result = new Dictionary<int, PropertyInfo>();

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            // Also check the corresponding constructor parameter for positional records.
            var index = (property.GetCustomAttribute<MapIndexAttribute>(inherit: true)
                ?? GetPositionalParameterAttribute<MapIndexAttribute>(type, property.Name))?.Index;
            if (index is null)
            {
                continue;
            }

            if (property.GetMethod is null || !property.GetMethod.IsPublic)
            {
                throw new InvalidOperationException(
                    $"Source property '{type.FullName}.{property.Name}' with [MapIndex({index.Value})] must have a public getter.");
            }

            if (result.ContainsKey(index.Value))
            {
                throw new InvalidOperationException(
                    $"Duplicate [MapIndex({index.Value})] found in source type '{type.FullName}'.");
            }

            result.Add(index.Value, property);
        }

        return result;
    }

    private static Dictionary<int, DestinationPropertyMetadata> CollectIndexedDestinationProperties(Type type)
    {
        var result = new Dictionary<int, DestinationPropertyMetadata>();

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            // Also check the corresponding constructor parameter for positional records:
            // [MapIndex] placed directly on the parameter is not forwarded to the synthesized property.
            var index = (property.GetCustomAttribute<MapIndexAttribute>(inherit: true)
                ?? GetPositionalParameterAttribute<MapIndexAttribute>(type, property.Name))?.Index;
            if (index is null)
            {
                continue;
            }

            if (property.SetMethod is null || !property.SetMethod.IsPublic)
            {
                throw new InvalidOperationException(
                    $"Destination property '{type.FullName}.{property.Name}' with [MapIndex({index.Value})] must have a public setter.");
            }

            if (result.ContainsKey(index.Value))
            {
                throw new InvalidOperationException(
                    $"Duplicate [MapIndex({index.Value})] found in destination type '{type.FullName}'.");
            }

            var converterType = (property.GetCustomAttribute<MapConverterAttribute>(inherit: true)
                ?? GetPositionalParameterAttribute<MapConverterAttribute>(type, property.Name))?.ConverterType;
            result.Add(index.Value, new DestinationPropertyMetadata(property, converterType));
        }

        return result;
    }

    private static Func<object?, object?> BuildPropertyConverter(
        Type sourceOwnerType,
        Type destinationOwnerType,
        int index,
        PropertyInfo sourceProperty,
        PropertyInfo destinationProperty,
        Type? converterType)
    {
        var sourceMemberType = sourceProperty.PropertyType;
        var destinationMemberType = destinationProperty.PropertyType;

        if (converterType is not null)
        {
            return BuildCustomConverter(
                sourceOwnerType,
                destinationOwnerType,
                index,
                sourceProperty,
                destinationProperty,
                converterType,
                sourceMemberType,
                destinationMemberType);
        }

        if (destinationMemberType.IsAssignableFrom(sourceMemberType))
        {
            return static value => value;
        }

        var destinationUnderlying = Nullable.GetUnderlyingType(destinationMemberType);
        if (destinationUnderlying is not null && destinationUnderlying == sourceMemberType)
        {
            return static value => value;
        }

        if (TryBuildCollectionConverter(sourceMemberType, destinationMemberType, out var collectionConverter))
        {
            return collectionConverter;
        }

        if (CanUseNestedMap(sourceMemberType, destinationMemberType))
        {
            return value =>
            {
                if (value is null)
                {
                    return null;
                }

                return RuntimeMapInvoker.Map(sourceMemberType, destinationMemberType, value);
            };
        }

        throw new InvalidOperationException(
            $"Property type mismatch for [MapIndex({index})]: source '{sourceOwnerType.FullName}.{sourceProperty.Name}' ({sourceMemberType.FullName}) cannot map to destination '{destinationOwnerType.FullName}.{destinationProperty.Name}' ({destinationMemberType.FullName}).");
    }

    private static Func<object?, object?> BuildCustomConverter(
        Type sourceOwnerType,
        Type destinationOwnerType,
        int index,
        PropertyInfo sourceProperty,
        PropertyInfo destinationProperty,
        Type converterType,
        Type sourceMemberType,
        Type destinationMemberType)
    {
        if (converterType.IsAbstract || converterType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Converter '{converterType.FullName}' configured in '{destinationOwnerType.FullName}.{destinationProperty.Name}' must be a concrete type.");
        }

        var converterContract = converterType
            .GetInterfaces()
            .FirstOrDefault(type =>
                type.IsGenericType
                && type.GetGenericTypeDefinition() == GenericConverterContract
                && type.GetGenericArguments()[0] == sourceMemberType
                && type.GetGenericArguments()[1] == destinationMemberType);

        if (converterContract is null)
        {
            throw new InvalidOperationException(
                $"Converter '{converterType.FullName}' for [MapIndex({index})] must implement IMapValueConverter<{sourceMemberType.FullName}, {destinationMemberType.FullName}>.");
        }

        var converterInstance = Activator.CreateInstance(converterType)
            ?? throw new InvalidOperationException(
                $"Converter '{converterType.FullName}' for [MapIndex({index})] could not be created.");

        var createInvokerMethod = typeof(ReflectionMapperFactory<TSource, TDestination>)
            .GetMethod(nameof(CreateCustomConverterInvoker), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(sourceMemberType, destinationMemberType);

        return (Func<object?, object?>)createInvokerMethod.Invoke(obj: null, parameters: new[] { converterInstance })!;
    }

    private static Func<object?, object?> CreateCustomConverterInvoker<TSourceMember, TDestinationMember>(object converter)
    {
        var typedConverter = (IMapValueConverter<TSourceMember, TDestinationMember>)converter;

        return value =>
        {
            var sourceValue = value is null ? default! : (TSourceMember)value;
            return typedConverter.Convert(sourceValue);
        };
    }

    private static bool TryBuildCollectionConverter(Type sourceMemberType, Type destinationMemberType, out Func<object?, object?> converter)
    {
        converter = null!;

        // Dictionary<K,V> path — must precede IEnumerable detection because Dictionary<K,V>
        // implements IEnumerable<KeyValuePair<K,V>>, which causes incorrect list-mapping attempts.
        if (TryGetDictionaryKeyValueTypes(sourceMemberType, out var srcKeyType, out var srcValueType)
            && TryGetDictionaryKeyValueTypes(destinationMemberType, out _, out var dstValueType))
        {
            var mapDictMethod = typeof(ReflectionMapperFactory<TSource, TDestination>)
                .GetMethod(nameof(MapDictionaryCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(srcKeyType, srcValueType, dstValueType);
            converter = value => mapDictMethod.Invoke(obj: null, parameters: new object?[] { value })!;
            return true;
        }

        if (!TryGetEnumerableElementType(sourceMemberType, out var sourceElementType)
            || !TryGetEnumerableElementType(destinationMemberType, out var destinationElementType))
        {
            return false;
        }

        var mapCollectionMethod = typeof(ReflectionMapperFactory<TSource, TDestination>)
            .GetMethod(nameof(MapCollectionCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(sourceElementType, destinationElementType);

        converter = value => mapCollectionMethod.Invoke(obj: null, parameters: new object?[] { value, destinationMemberType })!;
        return true;
    }

    private static object? MapCollectionCore<TSourceElement, TDestinationElement>(object? sourceCollection, Type destinationCollectionType)
    {
        if (sourceCollection is null)
        {
            return null;
        }

        if (sourceCollection is not IEnumerable<TSourceElement> sourceEnumerable)
        {
            throw new InvalidOperationException(
                $"Source collection value of type '{sourceCollection.GetType().FullName}' is not assignable to IEnumerable<{typeof(TSourceElement).FullName}>.");
        }

        var mappedItems = new List<TDestinationElement>();
        foreach (var item in sourceEnumerable)
        {
            mappedItems.Add(MapCollectionElement<TSourceElement, TDestinationElement>(item));
        }

        if (destinationCollectionType.IsArray)
        {
            return mappedItems.ToArray();
        }

        var listType = typeof(List<>).MakeGenericType(typeof(TDestinationElement));
        if (destinationCollectionType.IsAssignableFrom(listType))
        {
            return mappedItems;
        }

        var collectionContract = typeof(ICollection<>).MakeGenericType(typeof(TDestinationElement));
        if (!collectionContract.IsAssignableFrom(destinationCollectionType)
            || destinationCollectionType.IsInterface
            || destinationCollectionType.IsAbstract)
        {
            throw new InvalidOperationException(
                $"Destination collection type '{destinationCollectionType.FullName}' is not supported for collection mapping.");
        }

        var destinationInstance = Activator.CreateInstance(destinationCollectionType)
            ?? throw new InvalidOperationException(
                $"Destination collection type '{destinationCollectionType.FullName}' could not be instantiated.");

        var addMethod = collectionContract.GetMethod("Add")
            ?? throw new InvalidOperationException(
                $"Destination collection type '{destinationCollectionType.FullName}' does not expose ICollection.Add.");

        foreach (var item in mappedItems)
        {
            addMethod.Invoke(destinationInstance, new object?[] { item });
        }

        return destinationInstance;
    }

    private static TDestinationElement MapCollectionElement<TSourceElement, TDestinationElement>(TSourceElement sourceElement)
    {
        var sourceType = typeof(TSourceElement);
        var destinationType = typeof(TDestinationElement);

        if (sourceElement is null)
        {
            return default!;
        }

        if (destinationType.IsAssignableFrom(sourceType))
        {
            return (TDestinationElement)(object)sourceElement;
        }

        var mapped = RuntimeMapInvoker.Map(sourceType, destinationType, sourceElement!);
        if (mapped is null)
        {
            return default!;
        }

        return (TDestinationElement)mapped;
    }

    private static bool CanUseNestedMap(Type sourceType, Type destinationType)
    {
        if (sourceType == typeof(string) || destinationType == typeof(string))
        {
            return false;
        }

        if (TryGetEnumerableElementType(sourceType, out _) || TryGetEnumerableElementType(destinationType, out _))
        {
            return false;
        }

        return sourceType.IsClass || sourceType.IsValueType;
    }

    private static bool TryGetDictionaryKeyValueTypes(Type type, out Type keyType, out Type valueType)
    {
        keyType = null!;
        valueType = null!;

        // Check the type itself (handles IDictionary<K,V>, IReadOnlyDictionary<K,V> as property types)
        if (type.IsGenericType && type.GetGenericArguments().Length == 2)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        // Check interfaces (handles Dictionary<K,V> and other implementing types)
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType || iface.GetGenericArguments().Length != 2)
                continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var args = iface.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        return false;
    }

    private static object? MapDictionaryCore<TKey, TSourceValue, TDestValue>(object? source) where TKey : notnull
    {
        if (source is null) return null;

        if (source is not IEnumerable<KeyValuePair<TKey, TSourceValue>> srcDict)
            throw new InvalidOperationException(
                $"Cannot map dictionary: source type '{source.GetType().FullName}' does not implement IEnumerable<KeyValuePair<{typeof(TKey).FullName}, {typeof(TSourceValue).FullName}>>.");

        var result = source is ICollection<KeyValuePair<TKey, TSourceValue>> col
            ? new Dictionary<TKey, TDestValue>(col.Count)
            : new Dictionary<TKey, TDestValue>();

        foreach (var kvp in srcDict)
            result[kvp.Key] = MapCollectionElement<TSourceValue, TDestValue>(kvp.Value);

        return result;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        elementType = null!;

        if (type == typeof(string))
        {
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType && type.GetGenericArguments().Length == 1)
        {
            var candidateElementType = type.GetGenericArguments()[0];
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(candidateElementType);
            if (enumerableType.IsAssignableFrom(type))
            {
                elementType = candidateElementType;
                return true;
            }
        }

        var interfaceMatch = type
            .GetInterfaces()
            .FirstOrDefault(interfaceType =>
                interfaceType.IsGenericType
                && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (interfaceMatch is null)
        {
            return false;
        }

        elementType = interfaceMatch.GetGenericArguments()[0];
        return true;
    }

    private readonly struct DestinationPropertyMetadata
    {
        public DestinationPropertyMetadata(PropertyInfo property, Type? converterType)
        {
            Property = property;
            ConverterType = converterType;
        }

        public PropertyInfo Property { get; }

        public Type? ConverterType { get; }
    }

    private readonly struct PropertyPair
    {
        public PropertyPair(PropertyInfo source, PropertyInfo destination, Func<object?, object?> converter)
        {
            Source = source;
            Destination = destination;
            Converter = converter;
        }

        public PropertyInfo Source { get; }

        public PropertyInfo Destination { get; }

        public Func<object?, object?> Converter { get; }
    }

    /// <summary>
    /// Returns the attribute of type <typeparamref name="T"/> from the primary constructor parameter
    /// that corresponds to the named property. Used to support attributes placed directly on
    /// positional record parameters (e.g. <c>[MapFrom("X")]</c>) without the <c>[property: ...]</c> specifier.
    /// Returns null if the type has no suitable primary constructor or no matching parameter.
    /// </summary>
    private static T? GetPositionalParameterAttribute<T>(Type type, string propertyName) where T : Attribute
    {
        var primaryCtor = GetReflectionPrimaryConstructor(type);
        return primaryCtor?
            .GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.GetCustomAttribute<T>(inherit: true);
    }

    /// <summary>
    /// Returns the primary constructor of a type: the public constructor with more than zero parameters
    /// that is not the record copy constructor (a single parameter of the same type).
    /// Returns null if no such constructor exists.
    /// </summary>
    private static ConstructorInfo? GetReflectionPrimaryConstructor(Type type)
    {
        return type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Where(c =>
            {
                var p = c.GetParameters();
                return p.Length > 0 && !(p.Length == 1 && p[0].ParameterType == type);
            })
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
    }
}
