using System;

namespace MappShark;

/// <summary>
/// Marks a property with an explicit mapping index.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class MapIndexAttribute : Attribute
{
    /// <summary>
    /// Initializes a new attribute instance with the provided index.
    /// </summary>
    /// <param name="index">Index used to match source and destination properties.</param>
    public MapIndexAttribute(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The map index must be zero or greater.");
        }

        Index = index;
    }

    /// <summary>
    /// Gets the index used for property matching.
    /// </summary>
    public int Index { get; }
}
