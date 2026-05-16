using System;
using System.Collections.Generic;

namespace MappShark;

/// <summary>
/// Base class for organizing mapping registrations into profiles.
/// Inherit from this class and call <see cref="CreateMap{TSource, TDestination}"/> in the constructor.
/// The source generator discovers profiles in the same compilation and generates mappers for all registered pairs.
/// </summary>
/// <example>
/// <code>
/// public class OrderProfile : MappSharkProfile
/// {
///     public OrderProfile()
///     {
///         CreateMap&lt;OrderEntity, OrderDto&gt;();
///         CreateMap&lt;OrderDto, OrderEntity&gt;();
///     }
/// }
/// </code>
/// </example>
public abstract class MappSharkProfile
{
    private readonly List<(Type Source, Type Destination)> _maps = new();

    /// <summary>
    /// Registers a mapping pair for source generator discovery.
    /// </summary>
    protected void CreateMap<TSource, TDestination>()
        where TDestination : new()
        => _maps.Add((typeof(TSource), typeof(TDestination)));

    /// <summary>
    /// Returns all registered mapping pairs (used by the runtime for reflection fallback registration).
    /// </summary>
    internal IReadOnlyList<(Type Source, Type Destination)> Maps => _maps;
}
