using System;

namespace MappShark;

/// <summary>
/// Specifies the name of the source property to read from when mapping into this property.
/// Use this on a destination property when the source property has a different name.
/// Takes priority over name-based fallback. Cannot be combined with <see cref="MapIndexAttribute"/>.
/// </summary>
/// <example>
/// <code>
/// public class OrderDto
/// {
///     // Maps from Order.TotalAmount → OrderDto.Total
///     [MapFrom("TotalAmount")]
///     public decimal Total { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class MapFromAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance specifying the source property name to read from.
    /// </summary>
    /// <param name="propertyName">The name of the source property to map from.</param>
    public MapFromAttribute(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name must not be null or empty.", nameof(propertyName));

        PropertyName = propertyName;
    }

    /// <summary>Gets the name of the source property to read from.</summary>
    public string PropertyName { get; }
}
