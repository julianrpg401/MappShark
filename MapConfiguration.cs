using System;
using System.Linq.Expressions;

namespace MappShark;

/// <summary>
/// Fluent configuration builder returned by <see cref="MappSharkProfile.CreateMap{TSource,TDestination}"/>.
/// Use <see cref="ForMember{TMember}"/> to define custom property mappings with lambda expressions.
/// </summary>
/// <typeparam name="TSource">Source type.</typeparam>
/// <typeparam name="TDestination">Destination type.</typeparam>
public interface IMapConfiguration<TSource, TDestination>
{
    /// <summary>
    /// Defines a custom mapping for a single destination property using a source resolver delegate.
    /// The resolver receives the full source object and returns the value to assign.
    /// </summary>
    /// <typeparam name="TMember">The destination member type.</typeparam>
    /// <param name="destinationMember">
    /// A simple property-selector expression that identifies the destination property to map into,
    /// e.g. <c>dto => dto.AuthorFullName</c>.
    /// </param>
    /// <param name="resolver">
    /// A delegate that receives the source instance and returns the value for the destination property,
    /// e.g. <c>src => src.User.UserName</c> or <c>src => src.PostVotes!.Count(v => v.IsRelevant)</c>.
    /// </param>
    /// <returns>The same configuration builder for continued chaining.</returns>
    IMapConfiguration<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Func<TSource, TMember> resolver);
}

internal sealed class MapConfiguration<TSource, TDestination> : IMapConfiguration<TSource, TDestination>
{
    /// <inheritdoc/>
    public IMapConfiguration<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Func<TSource, TMember> resolver)
    {
        if (destinationMember is null)
            throw new ArgumentNullException(nameof(destinationMember));

        if (resolver is null)
            throw new ArgumentNullException(nameof(resolver));

        if (destinationMember.Body is not MemberExpression memberExpr)
            throw new ArgumentException(
                "The destination selector must be a simple property access expression, e.g. dto => dto.PropertyName.",
                nameof(destinationMember));

        var propertyName = memberExpr.Member.Name;
        Internal.ProfileMappingRegistry.Register<TSource, TDestination, TMember>(propertyName, resolver);
        return this;
    }
}
