using System;

namespace MappShark;

/// <summary>
/// Specifies the name of the destination property to write to when this property is mapped.
/// Use this on a source property when the destination property has a different name.
/// Takes priority over name-based fallback. Cannot be combined with <see cref="MapIndexAttribute"/>.
/// </summary>
/// <example>
/// <code>
/// public class CreateOrderCommand
/// {
///     // Maps CreateOrderCommand.Amount → Order.TotalAmount
///     [MapTo("TotalAmount")]
///     public decimal Amount { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class MapToAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance specifying the destination property name to write to.
    /// </summary>
    /// <param name="propertyName">The name of the destination property to map to.</param>
    public MapToAttribute(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name must not be null or empty.", nameof(propertyName));

        PropertyName = propertyName;
    }

    /// <summary>Gets the name of the destination property to write to.</summary>
    public string PropertyName { get; }
}
