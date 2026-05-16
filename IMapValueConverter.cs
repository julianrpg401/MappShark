namespace MappShark;

/// <summary>
/// Converts a source value into a destination value for an indexed mapping property.
/// </summary>
/// <typeparam name="TSource">Source member type.</typeparam>
/// <typeparam name="TDestination">Destination member type.</typeparam>
public interface IMapValueConverter<in TSource, out TDestination>
{
    /// <summary>
    /// Converts a source value into the destination value.
    /// </summary>
    /// <param name="source">Source value.</param>
    /// <returns>Converted destination value.</returns>
    TDestination Convert(TSource source);
}
