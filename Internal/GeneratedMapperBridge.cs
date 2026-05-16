using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace MappShark.Internal;

internal static class GeneratedMapperBridge
{
    private const string GeneratedResolverTypeName = "MappShark.Generated.IndexedMapResolver";

    private static readonly Lazy<IReadOnlyList<ResolverProvider>> ResolverProviders = new(LoadResolvers, isThreadSafe: true);
    private static readonly ConcurrentDictionary<TypePair, Func<object, object>?> UntypedCache = new();
    private static readonly ConcurrentDictionary<TypePair, TypedResolverCacheEntry> TypedCache = new();

    internal static bool TryResolve(Type sourceType, Type destinationType, out Func<object, object>? mapper)
    {
        var key = new TypePair(sourceType, destinationType);
        mapper = UntypedCache.GetOrAdd(key, ResolveUntypedMapper);
        return mapper is not null;
    }

    internal static bool TryResolveTyped<TSource, TDestination>(out Func<TSource, TDestination> mapper)
        where TDestination : new()
    {
        var key = new TypePair(typeof(TSource), typeof(TDestination));
        var cacheEntry = TypedCache.GetOrAdd(key, static _ => ResolveTypedMapper<TSource, TDestination>());

        if (!cacheEntry.HasMapper || cacheEntry.Mapper is not Func<TSource, TDestination> typedMapper)
        {
            mapper = default!;
            return false;
        }

        mapper = typedMapper;
        return true;
    }

    private static readonly ConcurrentDictionary<TypePair, object?> ProjectionCache = new();

    internal static bool TryResolveProjection<TSource, TDestination>(out Expression<Func<TSource, TDestination>>? projection)
        where TDestination : new()
    {
        var key = new TypePair(typeof(TSource), typeof(TDestination));
        var cached = ProjectionCache.GetOrAdd(key, static _ => ResolveProjection<TSource, TDestination>());
        projection = cached as Expression<Func<TSource, TDestination>>;
        return projection is not null;
    }

    private static object? ResolveProjection<TSource, TDestination>()
        where TDestination : new()
    {
        foreach (var resolver in ResolverProviders.Value)
        {
            if (resolver.TryResolveProjectionMethodDefinition is null)
                continue;

            try
            {
                var closedMethod = resolver.TryResolveProjectionMethodDefinition.MakeGenericMethod(typeof(TSource), typeof(TDestination));
                var args = new object?[] { null };
                var resolved = closedMethod.Invoke(obj: null, parameters: args);
                if (resolved is bool success && success && args[0] is Expression<Func<TSource, TDestination>> expr)
                    return expr;
            }
            catch
            {
                // Continue probing
            }
        }

        return null;
    }

    private static Func<object, object>? ResolveUntypedMapper(TypePair pair)
    {
        foreach (var resolver in ResolverProviders.Value)
        {
            if (resolver.TryResolveUntyped(pair.SourceType, pair.DestinationType, out var mapper))
            {
                return mapper;
            }
        }

        return null;
    }

    private static TypedResolverCacheEntry ResolveTypedMapper<TSource, TDestination>()
        where TDestination : new()
    {
        foreach (var resolver in ResolverProviders.Value)
        {
            if (resolver.TryResolveTypedMethodDefinition is null)
            {
                continue;
            }

            try
            {
                var closedMethod = resolver.TryResolveTypedMethodDefinition.MakeGenericMethod(typeof(TSource), typeof(TDestination));
                var args = new object?[] { null };
                var resolved = closedMethod.Invoke(obj: null, parameters: args);

                if (resolved is bool success
                    && success
                    && args[0] is Func<TSource, TDestination> typedMapper)
                {
                    return new TypedResolverCacheEntry(hasMapper: true, mapper: typedMapper);
                }
            }
            catch
            {
                // Ignore invalid resolver implementations and continue probing the remaining providers.
            }
        }

        return default;
    }

    private static IReadOnlyList<ResolverProvider> LoadResolvers()
    {
        var resolvers = new List<ResolverProvider>();
        var untypedSignature = new[]
        {
            typeof(Type),
            typeof(Type),
            typeof(Func<object, object>).MakeByRefType()
        };

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            Type? resolverType;
            try
            {
                resolverType = assembly.GetType(GeneratedResolverTypeName, throwOnError: false, ignoreCase: false);
            }
            catch
            {
                continue;
            }

            if (resolverType is null)
            {
                continue;
            }

            var untypedMethod = resolverType.GetMethod(
                name: "TryResolve",
                bindingAttr: BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: untypedSignature,
                modifiers: null);

            if (untypedMethod is null)
            {
                continue;
            }

            if (untypedMethod.CreateDelegate(typeof(TryResolveUntypedDelegate)) is not TryResolveUntypedDelegate untypedResolver)
            {
                continue;
            }

            var typedMethod = resolverType.GetMethod(name: "TryResolveTyped", bindingAttr: BindingFlags.Public | BindingFlags.Static);
            if (typedMethod is not null)
            {
                if (!typedMethod.IsGenericMethodDefinition
                    || typedMethod.GetGenericArguments().Length != 2
                    || typedMethod.ReturnType != typeof(bool))
                {
                    typedMethod = null;
                }
            }

            var projectionMethod = resolverType.GetMethod(name: "TryResolveProjection", bindingAttr: BindingFlags.Public | BindingFlags.Static);
            if (projectionMethod is not null)
            {
                if (!projectionMethod.IsGenericMethodDefinition
                    || projectionMethod.GetGenericArguments().Length != 2
                    || projectionMethod.ReturnType != typeof(bool))
                {
                    projectionMethod = null;
                }
            }

            resolvers.Add(new ResolverProvider(untypedResolver, typedMethod, projectionMethod));
        }

        return resolvers;
    }

    private delegate bool TryResolveUntypedDelegate(Type sourceType, Type destinationType, out Func<object, object> mapper);

    private readonly struct ResolverProvider
    {
        public ResolverProvider(TryResolveUntypedDelegate tryResolveUntyped, MethodInfo? tryResolveTypedMethodDefinition, MethodInfo? tryResolveProjectionMethodDefinition)
        {
            TryResolveUntyped = tryResolveUntyped;
            TryResolveTypedMethodDefinition = tryResolveTypedMethodDefinition;
            TryResolveProjectionMethodDefinition = tryResolveProjectionMethodDefinition;
        }

        public TryResolveUntypedDelegate TryResolveUntyped { get; }

        public MethodInfo? TryResolveTypedMethodDefinition { get; }

        public MethodInfo? TryResolveProjectionMethodDefinition { get; }
    }

    private readonly struct TypedResolverCacheEntry
    {
        public TypedResolverCacheEntry(bool hasMapper, Delegate? mapper)
        {
            HasMapper = hasMapper;
            Mapper = mapper;
        }

        public bool HasMapper { get; }

        public Delegate? Mapper { get; }
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
        {
            return SourceType == other.SourceType && DestinationType == other.DestinationType;
        }

        public override bool Equals(object? obj)
        {
            return obj is TypePair other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (SourceType.GetHashCode() * 397) ^ DestinationType.GetHashCode();
            }
        }
    }
}

internal static class GeneratedMapperBridge<TSource, TDestination>
    where TDestination : new()
{
    private static readonly Func<TSource, TDestination>? Mapper = ResolveMapper();
    private static readonly Expression<Func<TSource, TDestination>>? Projection = ResolveProjection();

    public static bool TryMap(TSource source, out TDestination destination)
    {
        if (Mapper is null)
        {
            destination = default!;
            return false;
        }

        destination = Mapper(source);
        return true;
    }

    public static bool TryGetProjection(out Expression<Func<TSource, TDestination>> projection)
    {
        projection = Projection!;
        return Projection is not null;
    }

    private static Func<TSource, TDestination>? ResolveMapper()
    {
        if (GeneratedMapperBridge.TryResolveTyped<TSource, TDestination>(out var typedMapper))
        {
            return typedMapper;
        }

        if (!GeneratedMapperBridge.TryResolve(typeof(TSource), typeof(TDestination), out var untypedMapper)
            || untypedMapper is null)
        {
            return null;
        }

        return source => (TDestination)untypedMapper(source!);
    }

    private static Expression<Func<TSource, TDestination>>? ResolveProjection()
    {
        GeneratedMapperBridge.TryResolveProjection<TSource, TDestination>(out var projection);
        return projection;
    }
}
