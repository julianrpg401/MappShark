using System;
using System.Collections.Generic;
using System.Reflection;

namespace MappShark.Internal;

/// <summary>
/// Stores ForMember delegate overrides registered by <see cref="MappSharkProfile"/> subclasses.
/// Populated at startup via <see cref="MappShark.Mapper.UseProfile{TProfile}()"/>.
/// Queried by <see cref="ReflectionMapperFactory{TSource,TDestination}"/> on each reflection-based mapping.
/// </summary>
internal static class ProfileMappingRegistry
{
    private static readonly object Lock = new();

    // Key: (TSource, TDest). Value: list of (property name, untyped resolver) in registration order.
    private static readonly Dictionary<TypePairKey, List<ProfileOverride>> Overrides = new();

    private static volatile int _version;

    /// <summary>
    /// Incremented on every <see cref="Register{TSource,TDest,TMember}"/> call.
    /// Used by <see cref="ReflectionMapperFactory{TSource,TDestination}"/> to detect stale caches.
    /// </summary>
    internal static int Version => _version;

    /// <summary>
    /// Registers a ForMember override. Called by <see cref="MapConfiguration{TSource,TDestination}.ForMember{TMember}"/>.
    /// </summary>
    internal static void Register<TSource, TDest, TMember>(string propertyName, Func<TSource, TMember> resolver)
    {
        if (string.IsNullOrEmpty(propertyName))
            throw new ArgumentException("Property name must not be null or empty.", nameof(propertyName));

        var key = new TypePairKey(typeof(TSource), typeof(TDest));
        Func<object, object?> untyped = src => resolver((TSource)src);

        lock (Lock)
        {
            if (!Overrides.TryGetValue(key, out var list))
            {
                list = new List<ProfileOverride>();
                Overrides[key] = list;
            }

            list.Add(new ProfileOverride(propertyName, untyped));
        }

        // Increment after releasing the lock so readers see a consistent version bump.
        System.Threading.Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Returns the registered ForMember overrides for the given pair, or <c>null</c> if none.
    /// </summary>
    internal static IReadOnlyList<ProfileOverride>? GetOverrides(Type source, Type dest)
    {
        lock (Lock)
        {
            return Overrides.TryGetValue(new TypePairKey(source, dest), out var list) ? list : null;
        }
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

        public bool Equals(TypePairKey other) =>
            _source == other._source && _dest == other._dest;

        public override bool Equals(object? obj) =>
            obj is TypePairKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (_source.GetHashCode() * 397) ^ _dest.GetHashCode();
            }
        }
    }
}

/// <summary>A single ForMember override entry: destination property name + untyped resolver.</summary>
internal readonly struct ProfileOverride
{
    public ProfileOverride(string propertyName, Func<object, object?> resolver)
    {
        PropertyName = propertyName;
        Resolver = resolver;
    }

    /// <summary>The destination property name this override targets.</summary>
    public string PropertyName { get; }

    /// <summary>Delegate that receives the source object (as <see cref="object"/>) and returns the mapped value.</summary>
    public Func<object, object?> Resolver { get; }
}
