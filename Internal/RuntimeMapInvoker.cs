using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace MappShark.Internal;

internal static class RuntimeMapInvoker
{
    private static readonly ConcurrentDictionary<TypePair, Func<object, object?>> Cache = new();
    private static readonly MethodInfo MapGenericMethod = typeof(RuntimeMapInvoker)
        .GetMethod(nameof(MapGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static object? Map(Type sourceType, Type destinationType, object source)
    {
        var key = new TypePair(sourceType, destinationType);
        var mapper = Cache.GetOrAdd(key, CreateMapper);
        return mapper(source);
    }

    private static Func<object, object?> CreateMapper(TypePair pair)
    {
        // Fast path: use the registry's pre-built untyped delegate (no reflection).
        if (MapperRegistry.TryGetUntypedMapper(pair.SourceType, pair.DestinationType, out var untypedMapper)
            && untypedMapper is not null)
        {
            return untypedMapper;
        }

        // Slow path: close the generic method for types not yet registered (reflection fallback).
        try
        {
            var closedMethod = MapGenericMethod.MakeGenericMethod(pair.SourceType, pair.DestinationType);
            return (Func<object, object?>)Delegate.CreateDelegate(typeof(Func<object, object?>), closedMethod);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Unable to map from '{pair.SourceType.FullName}' to '{pair.DestinationType.FullName}'. Destination must satisfy Mapper.Map generic constraints.",
                exception);
        }
    }

    private static object? MapGeneric<TSource, TDestination>(object source)
        where TDestination : new()
    {
        return Mapper.Map<TSource, TDestination>((TSource)source);
    }

    private readonly struct TypePair : IEquatable<TypePair>
    {
        public TypePair(Type sourceType, Type destinationType)
        {
            SourceType = sourceType;
            DestinationType = destinationType;
        }

        public Type SourceType { get; }

        public Type DestinationType { get; }

        public bool Equals(TypePair other)
            => SourceType == other.SourceType && DestinationType == other.DestinationType;

        public override bool Equals(object? obj)
            => obj is TypePair other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (SourceType.GetHashCode() * 397) ^ DestinationType.GetHashCode();
            }
        }
    }
}
