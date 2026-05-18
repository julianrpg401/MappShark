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
    public void MapTo_WithConverter_TransformsScalarValue()
    {
        var source = new TaggedSource { Label = "hello" };
        var dest = Mapper.Map<TaggedSource, TaggedDestination>(source);

        Assert.Equal("HELLO", dest.Tag); // Label → Tag via [MapTo] + [MapConverter]
    }

    [Fact]
    public void MapTo_WithConverter_TransformsCollection()
    {
        var source = new PostCommandSource
        {
            Content = "My post",
            ImageUrls = new List<string> { "https://a.com/1.jpg", "https://b.com/2.jpg" }
        };

        var dest = Mapper.Map<PostCommandSource, PostEntityDestination>(source);

        Assert.Equal("My post", dest.Content);
        Assert.NotNull(dest.Images);
        Assert.Equal(2, dest.Images!.Count);
        Assert.Equal("https://a.com/1.jpg", dest.Images[0].Url);
        Assert.Equal("https://b.com/2.jpg", dest.Images[1].Url);
    }

    [Fact]
    public void MapTo_WithConverter_OnPositionalRecord_TransformsCollection()
    {
        var command = new PostRecordCommand("My record post", new List<string> { "https://c.com/3.jpg" });
        var dest = Mapper.Map<PostRecordCommand, PostEntityDestination>(command);

        Assert.Equal("My record post", dest.Content);
        Assert.NotNull(dest.Images);
        Assert.Single(dest.Images!);
        Assert.Equal("https://c.com/3.jpg", dest.Images[0].Url);
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

    // ---------- Feature 5: init-only properties (records with explicit init setters) ----------

    [Fact]
    public void InitOnly_Record_MapsViaReflectionFallback()
    {
        // Private record → generator skips it → reflection fallback handles init-only via SetValue
        var source = new InitOnlySource { TotalAmount = 49.99m, Label = "Widget" };
        var result = Mapper.Map<InitOnlySource, InitOnlyRecord>(source);
        Assert.Equal(49.99m, result.TotalAmount);
        Assert.Equal("Widget", result.Label);
    }

    [Fact]
    public void InitOnly_Record_MapFrom_WorksViaReflectionFallback()
    {
        var source = new InitOnlySource { TotalAmount = 99.5m, Label = "Gadget" };
        var result = Mapper.Map<InitOnlySource, InitOnlyRecordWithMapFrom>(source);
        Assert.Equal(99.5m, result.Price);   // [MapFrom("TotalAmount")]
        Assert.Equal("Gadget", result.Label);
    }

    private sealed class InitOnlySource
    {
        public decimal TotalAmount { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    private sealed record InitOnlyRecord
    {
        public decimal TotalAmount { get; init; }
        public string Label { get; init; } = default!;
    }

    private sealed record InitOnlyRecordWithMapFrom
    {
        [MapFrom("TotalAmount")]
        public decimal Price { get; init; }
        public string Label { get; init; } = default!;
    }

    private sealed class SampleProfile : MappSharkProfile
    {
        public SampleProfile()
        {
            CreateMap<PersonSource, PersonDestination>();
        }
    }

    // ---------- Test types for [MapTo] + [MapConverter] ----------

    private sealed class TaggedSource
    {
        [MapTo("Tag")]
        [MapConverter(typeof(UpperCaseConverter))]
        public string Label { get; set; } = string.Empty;
    }

    private sealed class TaggedDestination
    {
        public string Tag { get; set; } = string.Empty;
    }

    private sealed class UpperCaseConverter : IMapValueConverter<string, string>
    {
        public string Convert(string source) => source.ToUpperInvariant();
    }

    private sealed class PostCommandSource
    {
        public string Content { get; set; } = string.Empty;

        [MapTo("Images")]
        [MapConverter(typeof(UrlListToImageEntityListConverter))]
        public List<string> ImageUrls { get; set; } = new();
    }

    private sealed record PostRecordCommand(
        [MapTo("Content")] string PostContent,
        [MapTo("Images"), MapConverter(typeof(UrlListToImageEntityListConverter))] List<string> ImageUrls
    );

    private sealed class PostEntityDestination
    {
        public string Content { get; set; } = string.Empty;
        public List<PostImageItem>? Images { get; set; }
    }

    private sealed class PostImageItem
    {
        public string Url { get; set; } = string.Empty;
    }

    private sealed class UrlListToImageEntityListConverter : IMapValueConverter<List<string>, List<PostImageItem>>
    {
        public List<PostImageItem> Convert(List<string> source)
            => source.Select(url => new PostImageItem { Url = url }).ToList();
    }
}
