using System;
using System.Collections.Generic;
using System.Linq;
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

// Public positional records — generator should emit constructor-call syntax.
// Attributes work directly on parameters without the [property: ...] specifier.
public sealed class PersonInfo
{
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
}

public sealed record PersonPositionalDto([MapFrom("FullName")] string Name, int Age);

public sealed class ItemInfo
{
    public string Code { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public sealed record ItemPositionalDto(string Code, [MapFrom("Price")] decimal UnitPrice, int Quantity);

// [MapIndex] on positional record parameters — both source and destination use index-based mapping.
public sealed class WidgetInfo
{
    [MapIndex(0)] public string SerialNumber { get; set; } = string.Empty;
    [MapIndex(1)] public int Version { get; set; }
}

public sealed record WidgetPositionalDto([MapIndex(0)] string SerialNumber, [MapIndex(1)] int Version);

// [MapTo] on source properties → positional record destination (attribute lives on source, not on destination).
public sealed class ContractInfo
{
    [MapTo("Title")] public string ContractName { get; set; } = string.Empty;
    [MapTo("Value")] public decimal Amount { get; set; }
}

public sealed record ContractPositionalDto(string Title, decimal Value);

// [MapTo] on a positional record's own parameters — the record itself is the SOURCE.
public sealed record SourcePositionalRecord([MapTo("PublicId")] Guid Id, [MapTo("DisplayName")] string Name);

public sealed class TargetFromPositionalDto
{
    public Guid PublicId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
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

/// <summary>
/// Tests for positional record support via the generated code path.
/// The generator emits constructor-call syntax (new T(Param: val)) since positional
/// records have no parameterless constructor.
/// </summary>
public sealed class PositionalRecordGeneratorTests
{
    [Fact]
    public void Map_PublicPositionalRecord_MapFrom_OnParameter_MapsCorrectly()
    {
        var source = new PersonInfo { FullName = "Alice Smith", Age = 30 };
        var result = Mapper.Map<PersonInfo, PersonPositionalDto>(source);

        Assert.Equal("Alice Smith", result.Name);
        Assert.Equal(30, result.Age);
    }

    [Fact]
    public void Map_PublicPositionalRecord_MapFrom_OnMiddleParameter_MapsCorrectly()
    {
        var source = new ItemInfo { Code = "SKU-1", Price = 9.99m, Quantity = 5 };
        var result = Mapper.Map<ItemInfo, ItemPositionalDto>(source);

        Assert.Equal("SKU-1", result.Code);
        Assert.Equal(9.99m, result.UnitPrice);
        Assert.Equal(5, result.Quantity);
    }

    [Fact]
    public void MapMany_PublicPositionalRecord_MapsCollection()
    {
        var sources = new[]
        {
            new PersonInfo { FullName = "Alice", Age = 25 },
            new PersonInfo { FullName = "Bob", Age = 40 },
        };

        var results = Mapper.MapMany<PersonInfo, PersonPositionalDto>(sources);

        Assert.Equal(2, results.Count);
        Assert.Equal("Alice", results[0].Name);
        Assert.Equal(25, results[0].Age);
        Assert.Equal("Bob", results[1].Name);
        Assert.Equal(40, results[1].Age);
    }

    [Fact]
    public void Map_PositionalRecord_MapIndex_OnParameter_MapsCorrectly()
    {
        var source = new WidgetInfo { SerialNumber = "SN-001", Version = 3 };
        var result = Mapper.Map<WidgetInfo, WidgetPositionalDto>(source);

        Assert.Equal("SN-001", result.SerialNumber); // [MapIndex(0)] on parameter
        Assert.Equal(3, result.Version);             // [MapIndex(1)] on parameter
    }

    [Fact]
    public void Map_PositionalRecord_MapTo_OnSourceProperty_MapsCorrectly()
    {
        var source = new ContractInfo { ContractName = "Service Agreement", Amount = 5000m };
        var result = Mapper.Map<ContractInfo, ContractPositionalDto>(source);

        Assert.Equal("Service Agreement", result.Title); // [MapTo("Title")] on source
        Assert.Equal(5000m, result.Value);               // [MapTo("Value")] on source
    }

    [Fact]
    public void Map_PositionalRecordAsSource_MapTo_OnParameter_MapsCorrectly()
    {
        var id = Guid.NewGuid();
        var source = new SourcePositionalRecord(id, "Alice");
        var result = Mapper.Map<SourcePositionalRecord, TargetFromPositionalDto>(source);

        Assert.Equal(id, result.PublicId);     // [MapTo("PublicId")] on positional parameter Id
        Assert.Equal("Alice", result.DisplayName); // [MapTo("DisplayName")] on positional parameter Name
    }
}

// ─── Public types for ForMember generator-path tests ──────────────────────────
// Types must be public so the source generator can emit IndexedMapResolver.g.cs
// referencing them. Private or nested types fall back to reflection.

public sealed class ArticleAuthor
{
    public string UserName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public sealed class ArticleEntity
{
    public int Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public ArticleAuthor Author { get; set; } = new();
    public System.Collections.Generic.List<ArticleVote> Votes { get; set; } = new();
}

public sealed class ArticleVote
{
    public bool IsPositive { get; set; }
}

// Positional record — ForMember must be emitted via generated constructor-call syntax.
public sealed record ArticleDto(
    int Id,
    string Body,
    string AuthorUserName,
    string AuthorFullName,
    int PositiveVoteCount);

public sealed class ArticleMappingProfile : MappSharkProfile
{
    public ArticleMappingProfile()
    {
        CreateMap<ArticleEntity, ArticleDto>()
            .ForMember(dto => dto.AuthorUserName,    src => src.Author.UserName)
            .ForMember(dto => dto.AuthorFullName,    src => $"{src.Author.FirstName} {src.Author.LastName}")
            .ForMember(dto => dto.PositiveVoteCount, src => src.Votes.Count(v => v.IsPositive));
    }
}

/// <summary>
/// Tests for ForMember via the generated code path.
/// The profile uses public types so the source generator produces a full constructor-call
/// mapper that inlines the ForMember lambda bodies.
/// </summary>
public sealed class ForMemberGeneratorTests
{
    [Fact]
    public void ForMember_NestedPath_GeneratedMapper_MapsCorrectly()
    {
        var source = new ArticleEntity
        {
            Id = 42,
            Body = "Hello world",
            Author = new ArticleAuthor { UserName = "ada", FirstName = "Ada", LastName = "Lovelace" },
            Votes = new() { new ArticleVote { IsPositive = true }, new ArticleVote { IsPositive = false } }
        };

        var dto = Mapper.Map<ArticleEntity, ArticleDto>(source);

        Assert.Equal(42, dto.Id);
        Assert.Equal("Hello world", dto.Body);
        Assert.Equal("ada", dto.AuthorUserName);
        Assert.Equal("Ada Lovelace", dto.AuthorFullName);
        Assert.Equal(1, dto.PositiveVoteCount);
    }

    [Fact]
    public void ForMember_MapMany_GeneratedMapper_MapsAllItems()
    {
        var sources = new[]
        {
            new ArticleEntity
            {
                Id = 1,
                Body = "First",
                Author = new ArticleAuthor { UserName = "user1", FirstName = "Alan", LastName = "Turing" },
                Votes = new() { new ArticleVote { IsPositive = true }, new ArticleVote { IsPositive = true } }
            },
            new ArticleEntity
            {
                Id = 2,
                Body = "Second",
                Author = new ArticleAuthor { UserName = "user2", FirstName = "Grace", LastName = "Hopper" },
                Votes = new()
            }
        };

        var dtos = Mapper.MapMany<ArticleEntity, ArticleDto>(sources);

        Assert.Equal(2, dtos.Count);
        Assert.Equal("Alan Turing", dtos[0].AuthorFullName);
        Assert.Equal(2, dtos[0].PositiveVoteCount);
        Assert.Equal("Grace Hopper", dtos[1].AuthorFullName);
        Assert.Equal(0, dtos[1].PositiveVoteCount);
    }
}

// ─── Public types for [MapFrom] dot-path generator-path tests ─────────────────

public sealed class PostSource
{
    public int Id { get; set; }
    public PostAuthor Author { get; set; } = new();
}

public sealed class PostAuthor
{
    public string UserName { get; set; } = string.Empty;
    public PostAuthorContact Contact { get; set; } = new();
}

public sealed class PostAuthorContact
{
    public string Email { get; set; } = string.Empty;
}

// Regular class destination with [MapFrom] dot-paths on properties
public sealed class PostFlatDto
{
    public int Id { get; set; }

    [MapFrom("Author.UserName")]
    public string AuthorUserName { get; set; } = string.Empty;

    [MapFrom("Author.Contact.Email")]
    public string AuthorEmail { get; set; } = string.Empty;
}

// Positional record destination with [MapFrom] dot-paths on constructor parameters
public sealed record PostFlatRecord(
    int Id,
    [MapFrom("Author.UserName")] string AuthorUserName,
    [MapFrom("Author.Contact.Email")] string AuthorEmail);

/// <summary>
/// Tests for [MapFrom] dot-path support via the generated code path.
/// Public types allow the source generator to emit an optimized mapper with verbatim
/// property-chain expressions (e.g. <c>source.Author.UserName</c>).
/// </summary>
public sealed class MapFromDotPathGeneratorTests
{
    [Fact]
    public void MapFrom_DotPath_TwoLevel_GeneratorPath_ClassDest_MapsCorrectly()
    {
        var source = new PostSource
        {
            Id = 10,
            Author = new PostAuthor
            {
                UserName = "alice",
                Contact = new PostAuthorContact { Email = "alice@example.com" }
            }
        };

        var result = Mapper.Map<PostSource, PostFlatDto>(source);

        Assert.Equal(10, result.Id);
        Assert.Equal("alice", result.AuthorUserName);
        Assert.Equal("alice@example.com", result.AuthorEmail);
    }

    [Fact]
    public void MapFrom_DotPath_TwoAndThreeLevel_GeneratorPath_PositionalRecord_MapsCorrectly()
    {
        var source = new PostSource
        {
            Id = 20,
            Author = new PostAuthor
            {
                UserName = "bob",
                Contact = new PostAuthorContact { Email = "bob@example.com" }
            }
        };

        var result = Mapper.Map<PostSource, PostFlatRecord>(source);

        Assert.Equal(20, result.Id);
        Assert.Equal("bob", result.AuthorUserName);
        Assert.Equal("bob@example.com", result.AuthorEmail);
    }

    [Fact]
    public void MapFrom_DotPath_GeneratorPath_MapMany_MapsAllItems()
    {
        var sources = new[]
        {
            new PostSource { Id = 1, Author = new PostAuthor { UserName = "u1", Contact = new() { Email = "u1@x.com" } } },
            new PostSource { Id = 2, Author = new PostAuthor { UserName = "u2", Contact = new() { Email = "u2@x.com" } } },
        };

        var results = Mapper.MapMany<PostSource, PostFlatRecord>(sources);

        Assert.Equal(2, results.Count);
        Assert.Equal("u1", results[0].AuthorUserName);
        Assert.Equal("u1@x.com", results[0].AuthorEmail);
        Assert.Equal("u2", results[1].AuthorUserName);
        Assert.Equal("u2@x.com", results[1].AuthorEmail);
    }
}
