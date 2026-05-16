using System;

namespace MappShark;

/// <summary>
/// Configures a custom converter to map a destination property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class MapConverterAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the converter type.
    /// </summary>
    /// <param name="converterType">Converter implementation type.</param>
    public MapConverterAttribute(Type converterType)
    {
        ConverterType = converterType ?? throw new ArgumentNullException(nameof(converterType));
    }

    /// <summary>
    /// Gets the converter implementation type.
    /// </summary>
    public Type ConverterType { get; }
}
