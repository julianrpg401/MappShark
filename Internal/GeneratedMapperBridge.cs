using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace MappShark.Internal;

/// <summary>
/// Central mapper registry populated by generated module initializers.
/// Replaces the previous reflection-based assembly scanning (GeneratedMapperBridge).
/// Generated code calls <see cref="Register{TSource,TDest}"/> at module load time so that
/// <see cref="Mapper"/> can dispatch to the generated delegates without any reflection.
/// </summary>
public static class MapperRegistry
{
    // Typed mappers: stored as boxed Func<TSource, TDest>; retrieved and cast on lookup.
    private static readonly ConcurrentDictionary<TypePairKey, object> TypedMappers = new();

    // Untyped mappers: object?object delegates used for dynamic nested-object mapping.
    // Populated alongside TypedMappers to avoid MakeGenericMethod reflection on nested paths.
    private static readonly ConcurrentDictionary<TypePairKey, Func<object, object>> UntypedMappers = new();

    // Projection expressions: stored as boxed Expression<Func<TSource, TDest>>.
    private static readonly ConcurrentDictionary<TypePairKey, object> Projections = new();

    /// <summary>
    /// Registers a typed mapper delegate. Called by generated module initializers.
    /// Also registers an untyped (object to object) wrapper for dynamic nested-object scenarios.
    /// </summary>
    public static void Register<TSource, TDest>(Func<TSource, TDest> mapper)
    {
        var key = new TypePairKey(typeof(TSource), typeof(TDest));
        TypedMappers[key] = mapper;
        UntypedMappers[key] = src => (object)mapper((TSource)src)!;
    }

    /// <summary>
    /// Registers a projection expression. Called by generated module initializers.
    /// </summary>
    public static void RegisterProjection<TSource, TDest>(Expression<Func<TSource, TDest>> projection)
    {
        Projections[new TypePairKey(typeof(TSource), typeof(TDest))] = projection;
    }

    public static bool TryGetMapper<TSource, TDest>(out Func<TSource, TDest> mapper)
    {
        if (TypedMappers.TryGetValue(new TypePairKey(typeof(TSource), typeof(TDest)), out var obj)
            && obj is Func<TSource, TDest> typed)
        {
            mapper = typed;
            return true;
        }

        mapper = default!;
        return false;
    }

    public static bool TryGetUntypedMapper(Type source, Type dest, out Func<object, object>? mapper)
        => UntypedMappers.TryGetValue(new TypePairKey(source, dest), out mapper);

    public static bool TryGetProjection<TSource, TDest>(out Expression<Func<TSource, TDest>>? projection)
    {
        if (Projections.TryGetValue(new TypePairKey(typeof(TSource), typeof(TDest)), out var obj)
            && obj is Expression<Func<TSource, TDest>> typed)
        {
            projection = typed;
            return true;
        }

        projection = default;
        return false;
    }

    private readonly struct TypePairKey : IEquatable<TypePairKey>
    {
        private readonly Type _source;
        private readonly Type _dest;

        public TypePairKey(Type source, Type dest)
        {
            _source = source;
            _dest = dest;
        }

        public bool Equals(TypePairKey other) => _source == other._source && _dest == other._dest;

        public override bool Equals(object? obj) => obj is TypePairKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                return (_source.GetHashCode() * 397) ^ _dest.GetHashCode();
            }
        }
    }
}