using System.Collections.Generic;
using System.Linq;
using MappShark;
using Xunit;

namespace MappShark.Tests;

/// <summary>Tests for name-based fallback, BothWays, MapMany, and MappSharkProfile features.</summary>
public sealed class NewFeaturesTests
{
    // ---------- Feature 1: Name-based fallback ----------

    [Fact]
    public void NameBasedFallback_MapsNonIndexedPropertiesWithMatchingNames()
    {
        var source = new PersonSource
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36
        };

        var destination = Mapper.Map<PersonSource, PersonDestination>(source);

        Assert.Equal("Ada", destination.FirstName);
        Assert.Equal("Lovelace", destination.LastName);
        Assert.Equal(36, destination.Age);
    }

    [Fact]
    public void NameBasedFallback_IgnoresIncompatibleTypes()
    {
        // Age in source is int, but NotANumber in destination is string — should be skipped silently
        var source = new MixedSource { Name = "Test", Age = 99 };
        var destination = Mapper.Map<MixedSource, MixedDestination>(source);

        Assert.Equal("Test", destination.Name);
        Assert.Null(destination.Age); // incompatible, skipped
    }

    // ---------- Feature 2: BothWays + MapMany ----------

    [Fact]
    public void BothWays_MapsForwardCorrectly()
    {
        var source = new PersonSource { FirstName = "Alan", LastName = "Turing", Age = 41 };
        var result = Mapper.BothWays<PersonSource, PersonDestination>(source);
        Assert.Equal("Alan", result.FirstName);
        Assert.Equal("Turing", result.LastName);
        Assert.Equal(41, result.Age);
    }

    [Fact]
    public void MapMany_MapsAllElementsInList()
    {
        var sources = new List<PersonSource>
        {
            new() { FirstName = "Grace", LastName = "Hopper", Age = 85 },
            new() { FirstName = "Dennis", LastName = "Ritchie", Age = 70 },
        };

        var destinations = Mapper.MapMany<PersonSource, PersonDestination>(sources);

        Assert.Equal(2, destinations.Count);
        Assert.Equal("Grace", destinations[0].FirstName);
        Assert.Equal("Hopper", destinations[0].LastName);
        Assert.Equal("Dennis", destinations[1].FirstName);
        Assert.Equal("Ritchie", destinations[1].LastName);
    }

    [Fact]
    public void MapMany_PreservesCollectionSize()
    {
        var sources = Enumerable.Range(1, 5).Select(i => new PersonSource { FirstName = $"Person{i}", LastName = "X", Age = i }).ToList();
        var result = Mapper.MapMany<PersonSource, PersonDestination>(sources);
        Assert.Equal(5, result.Count);
    }

    // ---------- Feature 4: MappSharkProfile (runtime behavior) ----------

    [Fact]
    public void Profile_CanBeInstantiated()
    {
        // Profile doesn't affect runtime directly — it drives code gen at compile time.
        // Verify instantiation works without exceptions.
        var profile = new SampleProfile();
        Assert.NotNull(profile);
    }

    // ---------- Feature 5: [MapFrom] and [MapTo] ----------

    [Fact]
    public void MapFrom_MapsSourcePropertyByName_ToDifferentNamedDestination()
    {
        var source = new OrderSource { Id = 1, TotalAmount = 99.50m };
        var dest = Mapper.Map<OrderSource, OrderDestination>(source);

        Assert.Equal(1, dest.Id);
        Assert.Equal(99.50m, dest.Total); // TotalAmount → Total via [MapFrom]
    }

    [Fact]
    public void MapTo_MapsSourcePropertyByName_ToDifferentNamedDestination()
    {
        var source = new CommandSource { Reference = "CMD-001", Amount = 42.00m };
        var dest = Mapper.Map<CommandSource, CommandDestination>(source);

        Assert.Equal("CMD-001", dest.Reference);
        Assert.Equal(42.00m, dest.Price); // Amount → Price via [MapTo]
    }

    [Fact]
    public void MapFrom_TakesPriorityOverNameFallback()
    {
        // OrderOverride has a Name property in source, but [MapFrom("Title")] on dest
        var source = new OverrideSource { Name = "should-be-ignored", Title = "correct-value" };
        var dest = Mapper.Map<OverrideSource, OverrideDestination>(source);

        Assert.Equal("correct-value", dest.Name); // [MapFrom("Title")] wins over same-name "Name"
    }

    [Fact]
    public void BothWays_MapFrom_WorksInBothDirections()
    {
        // Forward: ProductSource → ProductDto
        var source = new ProductSource { Code = "P001", UnitPrice = 19.99m };
        var dto = Mapper.BothWays<ProductSource, ProductDto>(source);
        Assert.Equal("P001", dto.Code);
        Assert.Equal(19.99m, dto.Price); // UnitPrice → Price via [MapFrom("UnitPrice")] on ProductDto

        // Reverse: ProductDto → ProductSource
        var backSource = Mapper.BothWays<ProductDto, ProductSource>(dto);
        Assert.Equal("P001", backSource.Code);
        Assert.Equal(19.99m, backSource.UnitPrice); // Price → UnitPrice via [MapFrom("Price")] on ProductSource
    }

    // ---------- Test types for [MapFrom]/[MapTo] ----------

    private sealed class OrderSource
    {
        public int Id { get; set; }
        public decimal TotalAmount { get; set; }
    }

    private sealed class OrderDestination
    {
        public int Id { get; set; }
        [MapFrom("TotalAmount")]
        public decimal Total { get; set; }
    }

    private sealed class CommandSource
    {
        public string Reference { get; set; } = string.Empty;
        [MapTo("Price")]
        public decimal Amount { get; set; }
    }

    private sealed class CommandDestination
    {
        public string Reference { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    private sealed class OverrideSource
    {
        public string Name { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    private sealed class OverrideDestination
    {
        [MapFrom("Title")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProductSource
    {
        public string Code { get; set; } = string.Empty;
        // When ProductSource is the destination (reverse BothWays), read from ProductDto.Price
        [MapFrom("Price")]
        public decimal UnitPrice { get; set; }
    }

    private sealed class ProductDto
    {
        public string Code { get; set; } = string.Empty;
        [MapFrom("UnitPrice")]
        public decimal Price { get; set; }
    }

    // ---------- Test types ----------

    private sealed class PersonSource
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class PersonDestination
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class MixedSource
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class MixedDestination
    {
        public string Name { get; set; } = string.Empty;
        public string? Age { get; set; } // incompatible — int vs string
    }

    private sealed class SampleProfile : MappSharkProfile
    {
        public SampleProfile()
        {
            CreateMap<PersonSource, PersonDestination>();
        }
    }
}
