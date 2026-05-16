using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MappShark.Internal;

internal static class ReflectionMapperFactory<TSource, TDestination>
    where TDestination : new()
{
    private static readonly Type GenericConverterContract = typeof(IMapValueConverter<,>);

    public static readonly Func<TSource, TDestination> Map = BuildMapper();

    private static Func<TSource, TDestination> BuildMapper()
    {
        var propertyPairs = BuildPropertyPairs();

        return source =>
        {
            var destination = new TDestination();

            foreach (var pair in propertyPairs)
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

        // Name-based fallback pairs (non-indexed, same name)
        var sourceByName = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic && !sourceMappedByIndex.Contains(p.Name))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var destProp in destinationType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!destProp.CanWrite || destProp.SetMethod is null || !destProp.SetMethod.IsPublic)
                continue;
            if (destinationMappedByIndex.Contains(destProp.Name))
                continue;
            if (!sourceByName.TryGetValue(destProp.Name, out var srcProp))
                continue;

            // Check type compatibility (direct, nullable, collection, or nested)
            Func<object?, object?> converter;
            if (destProp.PropertyType.IsAssignableFrom(srcProp.PropertyType))
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
            var index = property.GetCustomAttribute<MapIndexAttribute>(inherit: true)?.Index;
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
            var index = property.GetCustomAttribute<MapIndexAttribute>(inherit: true)?.Index;
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

            var converterType = property.GetCustomAttribute<MapConverterAttribute>(inherit: true)?.ConverterType;
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
}
