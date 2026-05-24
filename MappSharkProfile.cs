using System;
using System.Collections.Generic;

namespace MappShark;

/// <summary>
/// Base class for organizing mapping registrations into profiles.
/// Inherit from this class and call <see cref="CreateMap{TSource, TDestination}"/> in the constructor.
/// The source generator discovers profiles in the same compilation and generates mappers for all registered pairs.
/// Chain <see cref="IMapConfiguration{TSource,TDestination}.ForMember{TMember}"/> to define custom property
/// mappings using lambda expressions — including nested paths and computed values.
/// </summary>
/// <example>
/// <code>
/// public class PostProfile : MappSharkProfile
/// {
///     public PostProfile()
///     {
///         CreateMap&lt;PostEntity, PostDto&gt;()
///             .ForMember(dto => dto.AuthorUserName, src => src.User.UserName)
///             .ForMember(dto => dto.VoteCount,      src => src.PostVotes!.Count(v => v.IsRelevant));
///     }
/// }
/// </code>
/// </example>
public abstract class MappSharkProfile
{
    private readonly List<(Type Source, Type Destination)> _maps = new();

    /// <summary>
    /// Registers a mapping pair for source generator discovery and returns a fluent configuration
    /// builder for chaining <see cref="IMapConfiguration{TSource,TDestination}.ForMember{TMember}"/> overrides.
    /// </summary>
    protected IMapConfiguration<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        _maps.Add((typeof(TSource), typeof(TDestination)));
        return new MapConfiguration<TSource, TDestination>();
    }

    /// <summary>
    /// Returns all registered mapping pairs (used by the runtime for reflection fallback registration).
    /// </summary>
    internal IReadOnlyList<(Type Source, Type Destination)> Maps => _maps;
}
