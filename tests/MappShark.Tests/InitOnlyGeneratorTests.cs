using MappShark;
using Xunit;

namespace MappShark.Tests;

// Public types so the source generator can reference them in IndexedMapResolver.g.cs
// and produce optimized object-initializer code instead of the reflection fallback.

public sealed class ProductInfo
{
    public string Code { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public sealed record ProductSummaryDto
{
    public string Code { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public int Stock { get; init; }
}

public sealed class OrderInfo
{
    public int Id { get; set; }
    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
}

public sealed record OrderSummaryDto
{
    public int Id { get; init; }

    [MapFrom("TotalAmount")]
    public decimal Total { get; init; }

    [MapFrom("CustomerName")]
    public string Customer { get; init; } = string.Empty;
}

/// <summary>
/// Tests for init-only / record support via the generated code path.
/// Public types allow the source generator to emit an optimized mapper using
/// object-initializer syntax instead of falling back to reflection.
/// </summary>
public sealed class InitOnlyGeneratorTests
{
    [Fact]
    public void Map_PublicRecord_SameName_MapsAllProperties()
    {
        var source = new ProductInfo { Code = "PRD-01", Price = 12.50m, Stock = 100 };
        var result = Mapper.Map<ProductInfo, ProductSummaryDto>(source);

        Assert.Equal("PRD-01", result.Code);
        Assert.Equal(12.50m, result.Price);
        Assert.Equal(100, result.Stock);
    }

    [Fact]
    public void Map_PublicRecord_MapFrom_RemapsProperties()
    {
        var source = new OrderInfo { Id = 7, TotalAmount = 250m, CustomerName = "Alice" };
        var result = Mapper.Map<OrderInfo, OrderSummaryDto>(source);

        Assert.Equal(7, result.Id);
        Assert.Equal(250m, result.Total);       // [MapFrom("TotalAmount")]
        Assert.Equal("Alice", result.Customer); // [MapFrom("CustomerName")]
    }

    [Fact]
    public void MapMany_PublicRecord_MapsCollection()
    {
        var sources = new[]
        {
            new ProductInfo { Code = "A", Price = 1m, Stock = 10 },
            new ProductInfo { Code = "B", Price = 2m, Stock = 20 },
        };

        var results = Mapper.MapMany<ProductInfo, ProductSummaryDto>(sources);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Code);
        Assert.Equal("B", results[1].Code);
    }
}
