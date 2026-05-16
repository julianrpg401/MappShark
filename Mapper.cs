using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace MappShark;

/// <summary>
/// Provides object mapping based on <see cref="MapIndexAttribute"/>.
/// </summary>
public static class Mapper
{
    /// <summary>
    /// Maps an object from <typeparamref name="TSource"/> to <typeparamref name="TDestination"/>.
    /// Only properties annotated with <see cref="MapIndexAttribute"/> are considered (non-indexed properties
    /// with matching names are also mapped as a fallback).
    /// </summary>
    /// <typeparam name="TSource">Source type.</typeparam>
    /// <typeparam name="TDestination">Destination type.</typeparam>
    /// <param name="source">Source instance.</param>
    /// <returns>A mapped destination instance.</returns>
    public static TDestination Map<TSource, TDestination>(TSource source)
        where TDestination : new()
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (Internal.GeneratedMapperBridge<TSource, TDestination>.TryMap(source, out var generated))
        {
            return generated;
        }

        if (AppContext.TryGetSwitch("MappShark.StrictGeneratedMode", out var strictGeneratedMode)
            && strictGeneratedMode)
        {
            throw new InvalidOperationException(
                $"No generated mapper was found for '{typeof(TSource).FullName}' -> '{typeof(TDestination).FullName}'. " +
                "Disable switch 'MappShark.StrictGeneratedMode' to allow reflection fallback.");
        }

        return Internal.ReflectionMapperFactory<TSource, TDestination>.Map(source);
    }

    /// <summary>
    /// Maps <paramref name="source"/> from <typeparamref name="TSource"/> to <typeparamref name="TDestination"/>.
    /// Using this method signals the source generator to also produce a reverse mapper for
    /// <typeparamref name="TDestination"/> → <typeparamref name="TSource"/>.
    /// </summary>
    public static TDestination BothWays<TSource, TDestination>(TSource source)
        where TDestination : new()
        => Map<TSource, TDestination>(source);

    /// <summary>
    /// Maps each element of <paramref name="source"/> from <typeparamref name="TSource"/> to <typeparamref name="TDestination"/>.
    /// </summary>
    public static List<TDestination> MapMany<TSource, TDestination>(IEnumerable<TSource> source)
        where TDestination : new()
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var result = source is ICollection<TSource> col ? new List<TDestination>(col.Count) : new List<TDestination>();
        foreach (var item in source)
        {
            result.Add(Map<TSource, TDestination>(item));
        }

        return result;
    }

    /// <summary>
    /// Returns a projection expression suitable for use with <c>IQueryable.Select()</c> (e.g. EF Core).
    /// The expression is generated at compile-time by the MappShark source generator.
    /// Properties with <see cref="MapConverterAttribute"/> are excluded from projections.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no generated projection is available for the pair and strict mode is enabled,
    /// or when strict mode is off but no generated projection exists.
    /// </exception>
    public static Expression<Func<TSource, TDestination>> Projection<TSource, TDestination>()
        where TDestination : new()
    {
        if (Internal.GeneratedMapperBridge<TSource, TDestination>.TryGetProjection(out var projection))
        {
            return projection;
        }

        throw new InvalidOperationException(
            $"No generated projection was found for '{typeof(TSource).FullName}' -> '{typeof(TDestination).FullName}'. " +
            "Ensure Mapper.Map<TSource, TDestination>() or Mapper.BothWays<TSource, TDestination>() is called somewhere in the project so the source generator produces a projection.");
    }
}
