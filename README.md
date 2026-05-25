# MappShark

> **Zero-overhead object mapping for .NET — powered by a Roslyn source generator.**

MappShark generates plain C# mapping code at **compile time**. There is no reflection on the hot path, no warm-up cost, and no runtime configuration exceptions — mapping errors become **build errors**.

[![NuGet](https://img.shields.io/nuget/v/MappShark.svg)](https://www.nuget.org/packages/MappShark)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Library Comparison

The table below scores MappShark against the most popular .NET object-mapping libraries across technical criteria. Documentation quality and community size are intentionally excluded — MappShark is a newer library and a direct comparison on those axes would not be meaningful.

> Scores are out of 100. Higher is better.

| Criterion | AutoMapper | Mapster | Mapperly | **MappShark** |
|---|:---:|:---:|:---:|:---:|
| **Runtime performance** ¹ | 50 | 89 | 96 | **94** |
| **Startup / warm-up overhead** | 30 | 55 | 95 | **90** |
| **Compile-time safety** | 15 | 20 | 80 | **90** |
| **Native AOT / trimming support** | 15 | 40 | 95 | **90** |
| **Configuration simplicity** | 50 | 65 | 60 | **75** |
| **Name-based automatic mapping** | 90 | 85 | 80 | **75** |
| **Custom type converters** | 90 | 80 | 65 | **75** |
| **Collection & dictionary mapping** | 85 | 80 | 70 | **80** |
| **IQueryable / EF Core projections** | 85 | 80 | 25 | **75** |
| **Records & init-only support** | 55 | 65 | 85 | **90** |

> ¹ Score derived from real BenchmarkDotNet results (net8.0, .NET 8.0.27, Intel i5-4430S):
> ManualMapping 427 ns · **MappShark 456 ns (1.07×)** · Mapperly 443 ns (1.04×) · Mapster 483 ns (1.13×) · AutoMapper 854 ns (2.00×)

**Key takeaways:**
- **AutoMapper** and **Mapster** are mature and flexible but rely heavily on runtime reflection, making them incompatible with Native AOT and carrying significant startup costs.
- **Mapperly** generates code at compile time and has excellent AOT support, but its IQueryable projection story is limited and its configuration model is more verbose.
- **MappShark** combines compile-time code generation, build-time error reporting, Native AOT compatibility, and first-class EF Core projection support in a single lightweight package.

---

## Table of Contents

1. [Installation](#installation)
2. [Quick Start](#quick-start)
3. [How It Works](#how-it-works)
4. [Mapping Strategies](#mapping-strategies)
   - [Index-Based Mapping](#1-index-based-mapping-mapindex)
   - [Name-Based Fallback](#2-name-based-fallback)
   - [Name Override Attributes](#3-name-override-attributes-mapfrom--mapto)
     - [Dot-Path Notation](#dot-path-notation-nested-property-access)
5. [Records and Init-Only Properties](#records-and-init-only-properties)
6. [Nested Objects](#nested-objects)
7. [Collections](#collections)
8. [Dictionary Mapping](#dictionary-mapping)
9. [Custom Value Converters](#custom-value-converters)
10. [Organizing Mappings with Profiles](#organizing-mappings-with-profiles)
11. [Custom Property Mappings with ForMember](#custom-property-mappings-with-formember)
12. [Reverse Mapping with BothWays](#reverse-mapping-with-bothways)
13. [IQueryable Projections (EF Core)](#iqueryable-projections-ef-core)
14. [Strict Generated Mode](#strict-generated-mode)
15. [Build-Time Diagnostics](#build-time-diagnostics)
16. [API Reference](#api-reference)
17. [Targets & Compatibility](#targets--compatibility)

---

## Installation

```bash
dotnet add package MappShark
```

Requires **.NET Standard 2.0** or **.NET 8+**. The source generator is included in the package — no additional setup is needed.

---

## Quick Start

### 1. Annotate your types

```csharp
using MappShark;

public class UserEntity
{
    [MapIndex(0)] public int Id { get; set; }
    [MapIndex(1)] public string FullName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } // no [MapIndex] → ignored
}

public class UserDto
{
    [MapIndex(0)] public int UserId { get; set; }    // mapped from Id   (index 0)
    [MapIndex(1)] public string Name { get; set; } = string.Empty; // mapped from FullName (index 1)
}
```

### 2. Call `Mapper.Map`

```csharp
var dto = Mapper.Map<UserEntity, UserDto>(entity);
// dto.UserId == entity.Id
// dto.Name   == entity.FullName
```

The source generator produces an optimized static method during your build — no warm-up, no reflection, no surprises.

---

## How It Works

When you write `Mapper.Map<UserEntity, UserDto>(...)`, MappShark's Roslyn analyzer detects the call at compile time and emits a file called `IndexedMapResolver.g.cs` into your project. This file contains a plain C# method:

```csharp
private static UserDto MapPair_0(UserEntity source)
{
    var destination = new UserDto();
    destination.UserId = source.Id;
    destination.Name   = source.FullName;
    return destination;
}
```

At runtime, `Mapper.Map` resolves to this method via a cached static delegate. **Zero reflection. Zero allocations beyond the destination object itself.**

For `init`-only properties and records, object-initializer or constructor-call syntax is emitted instead — the only valid way to assign those members after construction.

---

## Mapping Strategies

### 1. Index-Based Mapping (`[MapIndex]`)

Apply `[MapIndex(n)]` to source and destination properties. Any two properties sharing the same index are mapped to each other, regardless of their names.

```csharp
public class OrderEntity
{
    [MapIndex(0)] public string Reference { get; set; } = string.Empty;
    [MapIndex(1)] public decimal TotalAmount { get; set; }
}

public class OrderDto
{
    [MapIndex(0)] public string Code { get; set; } = string.Empty;  // ← from Reference
    [MapIndex(1)] public decimal Total { get; set; }                // ← from TotalAmount
}
```

> **Available since v1.0.0**

### 2. Name-Based Fallback

Properties that share the **same name and a compatible type** are mapped automatically — no annotation needed.

```csharp
public class ProductEntity
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockCount { get; set; }
}

public class ProductDto
{
    public string Name { get; set; } = string.Empty;  // ← mapped by name
    public decimal Price { get; set; }                // ← mapped by name
    // StockCount is absent → simply not mapped
}

var dto = Mapper.Map<ProductEntity, ProductDto>(entity);
```

`[MapIndex]` always takes priority. Name-based mapping only applies to properties that carry no index annotation on either side.

> **Available since v1.0.0**

### 3. Name Override Attributes (`[MapFrom]` / `[MapTo]`)

Use these attributes when source and destination properties have different names but you do not want to use `[MapIndex]` on both sides — for example, to keep domain models free of mapping concerns.

#### `[MapFrom("SourcePropertyName")]`

Apply to a **destination** property. Reads the value from the named source property.

```csharp
public class OrderDto
{
    public int Id { get; set; }

    [MapFrom("TotalAmount")]
    public decimal Total { get; set; }
}

public class OrderEntity
{
    public int Id { get; set; }
    public decimal TotalAmount { get; set; } // no annotation needed
}

var dto = Mapper.Map<OrderEntity, OrderDto>(entity);
// dto.Total == entity.TotalAmount
```

#### `[MapTo("DestinationPropertyName")]`

Apply to a **source** property. Writes its value into the named destination property.

```csharp
public class CreateOrderCommand
{
    public string Reference { get; set; } = string.Empty;

    [MapTo("Price")]
    public decimal Amount { get; set; }
}

public class OrderDto
{
    public string Reference { get; set; } = string.Empty;
    public decimal Price { get; set; } // no annotation needed
}

var dto = Mapper.Map<CreateOrderCommand, OrderDto>(command);
// dto.Price == command.Amount
```

#### Priority order

When multiple strategies could resolve the same destination property, MappShark uses the following priority:

1. `[MapIndex]` — highest priority, always wins
2. `[MapFrom]` on the destination property
3. `[MapTo]` on the source property
4. Same-name fallback — lowest priority

> **Note:** `[MapFrom]` and `[MapIndex]` cannot be combined on the same property (diagnostic `MSP015`). `[MapTo]` and `[MapIndex]` cannot be combined either (`MSP016`).

> **Available since v1.0.0**

#### Dot-Path Notation (nested property access)

`[MapFrom]` accepts a **dot-separated path** to read from a chain of nested properties — no profile or `ForMember` call needed. Arbitrary depth is supported.

```csharp
public class PostEntity
{
    public int Id { get; set; }
    public AuthorEntity Author { get; set; } = new();
}

public class AuthorEntity
{
    public string UserName { get; set; } = string.Empty;
    public ContactInfo Contact { get; set; } = new();
}

public class ContactInfo
{
    public string Email { get; set; } = string.Empty;
}

public class PostDto
{
    public int Id { get; set; }

    [MapFrom("Author.UserName")]
    public string AuthorUserName { get; set; } = string.Empty;

    [MapFrom("Author.Contact.Email")]
    public string AuthorEmail { get; set; } = string.Empty;
}

var dto = Mapper.Map<PostEntity, PostDto>(entity);
// dto.AuthorUserName == entity.Author.UserName
// dto.AuthorEmail    == entity.Author.Contact.Email
```

The source generator emits the path verbatim into the produced code:

```csharp
private static PostDto MapPair_0(PostEntity source) =>
    new PostDto
    {
        Id             = source.Id,
        AuthorUserName = source.Author.UserName,
        AuthorEmail    = source.Author.Contact.Email,
    };
```

Dot-paths also work on **positional record parameters**:

```csharp
public sealed record PostDto(
    int Id,
    [MapFrom("Author.UserName")]       string AuthorUserName,
    [MapFrom("Author.Contact.Email")]  string AuthorEmail
);
```

The reflection fallback resolves the property chain at runtime, so dot-paths work even when the source generator is not active.

> If any segment cannot be resolved to a readable public property on the preceding type, build error `MSP017` is reported.

> **Available since v2.1.0**

---

## Records and Init-Only Properties

MappShark fully supports C# **records** and any class or struct with `init`-only setters — no extra configuration required.

### Records with explicit properties

The source generator emits **object-initializer syntax** for destination types that have a public parameterless constructor and `init`-only setters (the default for `record` types with explicit properties):

```csharp
public record OrderDto
{
    public int Id { get; init; }

    [MapFrom("TotalAmount")]
    public decimal Total { get; init; }

    [MapFrom("CustomerName")]
    public string Customer { get; init; } = string.Empty;
}

var dto = Mapper.Map<OrderEntity, OrderDto>(entity);
```

Generated code:

```csharp
private static OrderDto MapPair_0(OrderEntity source) =>
    new OrderDto
    {
        Id = source.Id,
        Total = source.TotalAmount,
        Customer = source.CustomerName,
    };
```

### Positional records

Positional records (`record Foo(int Bar)`) have no parameterless constructor. MappShark emits **constructor-call syntax** for them. All three mapping attributes — `[MapIndex]`, `[MapFrom]`, and `[MapTo]` — can be placed directly on positional parameters:

```csharp
public record OrderDto(
    int Id,
    [MapFrom("TotalAmount")] decimal Total,
    [MapFrom("CustomerName")] string Customer
);

var dto = Mapper.Map<OrderEntity, OrderDto>(entity);
```

Generated code:

```csharp
private static OrderDto MapPair_0(OrderEntity source) =>
    new OrderDto(
        Id: source.Id,
        Total: source.TotalAmount,
        Customer: source.CustomerName);
```

> The `[property: MapFrom("X")]` target specifier is also accepted as an alternative syntax.

> **Limitation:** `Mapper.Projection` (IQueryable expression trees) is not supported for positional records.

### Orphan parameters

Positional parameters that have no matching source property are treated as *orphans* and silently skipped. Their constructor slot receives the CLR default (`null` / `default(T)`). This allows you to include extra parameters in the record that you populate yourself after mapping:

```csharp
public sealed record RegisterUserResponseDto(
    [MapFrom("PublicId")]  Guid   UserId,
    [MapFrom("UserName")]  string UserName,
    [MapFrom("Email")]     string Email,
    bool VerificationRequired   // orphan → defaults to false; set it yourself
);

var dto = Mapper.Map<UserEntity, RegisterUserResponseDto>(entity);
// dto.VerificationRequired == false  ← set it afterward as needed
```

> **Available since v1.0.0.** Orphan parameter fix in reflection fallback: **v1.0.2**.

---

## Nested Objects

MappShark handles nested objects automatically. Call `Mapper.Map` once for the top-level type — the generator discovers and wires up all required nested pairs recursively.

```csharp
public class OrderEntity
{
    [MapIndex(0)] public string OrderNumber { get; set; } = string.Empty;
    [MapIndex(1)] public CustomerEntity? Customer { get; set; }
    [MapIndex(2)] public List<OrderLineEntity>? Lines { get; set; }
}

public class OrderDto
{
    [MapIndex(0)] public string Code { get; set; } = string.Empty;
    [MapIndex(1)] public CustomerDto? Customer { get; set; }
    [MapIndex(2)] public List<OrderLineDto>? Items { get; set; }
}

// One call maps the entire object graph:
var dto = Mapper.Map<OrderEntity, OrderDto>(order);
```

The generator discovers `CustomerEntity → CustomerDto` and `OrderLineEntity → OrderLineDto` automatically and generates optimized code for each pair.

> **Available since v1.0.0**

---

## Collections

Supported destination collection types:

| Type | Supported |
|---|:---:|
| `List<T>` | ✅ |
| `IList<T>`, `ICollection<T>`, `IEnumerable<T>` | ✅ |
| `T[]` (arrays) | ✅ |
| `Dictionary<TKey, TValue>` | ✅ |
| `IDictionary<TKey, TValue>` | ✅ |
| `IReadOnlyDictionary<TKey, TValue>` | ✅ |

Use `Mapper.MapMany` to map an entire flat collection in a single call:

```csharp
IEnumerable<UserEntity> entities = GetUsers();
List<UserDto> dtos = Mapper.MapMany<UserEntity, UserDto>(entities);
```

> **Available since v1.0.0**

---

## Dictionary Mapping

MappShark maps `Dictionary<TKey, TValue>` properties end-to-end. Both keys and values are handled without any extra configuration.

### Simple value types

```csharp
public class PriceListEntity
{
    [MapIndex(0)] public Dictionary<string, decimal> Prices { get; set; } = new();
}

public class PriceListDto
{
    [MapIndex(0)] public Dictionary<string, decimal> Prices { get; set; } = new();
}

var dto = Mapper.Map<PriceListEntity, PriceListDto>(entity);
// dto.Prices["apple"] == entity.Prices["apple"]
```

### Nested object values

When source and destination value types are themselves a mappable pair, MappShark generates a mapper for the value type automatically:

```csharp
public class CatalogEntity
{
    [MapIndex(0)] public Dictionary<string, ProductEntity> Items { get; set; } = new();
}

public class CatalogDto
{
    [MapIndex(0)] public Dictionary<string, ProductDto> Items { get; set; } = new();
}

var dto = Mapper.Map<CatalogEntity, CatalogDto>(catalog);
// Each ProductEntity value is mapped to a ProductDto via a generated mapper.
```

The destination property can be declared as `Dictionary<K,V>`, `IDictionary<K,V>`, or `IReadOnlyDictionary<K,V>`. Any source type that implements one of those interfaces works as a source (e.g., `SortedDictionary<K,V>`).

> **Notes:**
> - Keys must be directly assignable. Incompatible keys produce build error `MSP004`.
> - Dictionary properties are excluded from `Mapper.Projection` expressions — `MSP013` warning is emitted.

> **Available since v1.0.0**

---

## Custom Value Converters

When a property needs a custom transformation (e.g., `decimal → string`), implement `IMapValueConverter<TSource, TDestination>` and attach it with `[MapConverter]`.

```csharp
public class PercentConverter : IMapValueConverter<decimal, string>
{
    public string Convert(decimal value) => $"{value * 100:0.##}%";
}

public class MetricDto
{
    [MapIndex(0)]
    [MapConverter(typeof(PercentConverter))]
    public string RatioLabel { get; set; } = string.Empty;
}
```

Requirements:
- Must implement `IMapValueConverter<TSourceMember, TDestinationMember>`.
- Must be a concrete, non-abstract class with a public parameterless constructor.

### `[MapConverter]` on source properties

You can also place `[MapConverter]` on a **source** property (or positional record parameter). This keeps the destination type completely free of MappShark attributes:

```csharp
public class MetricEntity
{
    [MapIndex(0)]
    [MapConverter(typeof(PercentConverter))]  // converter lives on the source side
    public decimal Ratio { get; set; }
}

public class MetricDto
{
    [MapIndex(0)]
    public string RatioLabel { get; set; } = string.Empty; // no MappShark attributes needed
}
```

> **`[MapConverter]` on destination properties: available since v1.0.0**
>
> **`[MapConverter]` on source properties / record parameters: available since v1.1.0**

> **Note:** Properties with `[MapConverter]` are excluded from `Mapper.Projection` (IQueryable) expressions — diagnostic `MSP013` is emitted.

---

## Organizing Mappings with Profiles

For medium and large projects, use `MappSharkProfile` to group related mappings together. The source generator discovers your profiles automatically — **no runtime registration needed**.

```csharp
using MappShark;

public class OrderMappingProfile : MappSharkProfile
{
    public OrderMappingProfile()
    {
        CreateMap<OrderEntity, OrderDto>();
        CreateMap<OrderDto, OrderEntity>();       // reverse direction
        CreateMap<OrderLineEntity, OrderLineDto>();
    }
}
```

> **Available since v1.0.0**

---

## Custom Property Mappings with ForMember

`ForMember` lets you define custom property mappings directly inside a `MappSharkProfile`, using a plain lambda expression as the resolver. This is the recommended approach for:

- **Nested path access** — reading a property from a child object (`src => src.User.UserName`)
- **Computed / aggregate values** — any expression that cannot be expressed as a direct property copy (`src => src.PostVotes!.Count(v => v.IsRelevant)`)

```csharp
using MappShark;

public class PostMappingProfile : MappSharkProfile
{
    public PostMappingProfile()
    {
        CreateMap<PostEntity, PostDto>()
            .ForMember(dto => dto.AuthorUserName,   src => src.User!.UserName)
            .ForMember(dto => dto.RelevantVotes,    src => src.PostVotes!.Count(v => v.IsRelevant));
    }
}
```

The resolver lambda body is emitted verbatim into the generated mapper method — there is no runtime delegate invocation overhead on the hot path.

### Registering ForMember for the Reflection Fallback Path

When the Roslyn source generator is active (the default in any project that references the MappShark NuGet), `ForMember` mappings are generated at compile time. No registration call is needed.

If you are using MappShark **without** the source generator (e.g., in a dynamically loaded assembly), call `Mapper.UseProfile<T>()` at startup to register the ForMember delegates:

```csharp
// Call once at application startup, before any mapping takes place.
Mapper.UseProfile<PostMappingProfile>();
```

> **Note:** `ForMember` takes priority over name-based fallback. If a destination property is covered by both a `ForMember` override and a same-name auto-mapping, `ForMember` wins.

> **Note:** Properties mapped via `ForMember` are excluded from `Mapper.Projection()` / `Mapper.ProjectMany()` — LINQ expression trees cannot translate arbitrary C# expressions to SQL.

> **Available since v2.0.0**

---

## Reverse Mapping with `BothWays`

`Mapper.BothWays` signals the source generator to produce mappers for **both directions** — `A → B` and `B → A` — from a single call.

```csharp
// This call registers both UserEntity → UserDto and UserDto → UserEntity.
var dto = Mapper.BothWays<UserEntity, UserDto>(entity);

// Later, map back:
var entity = Mapper.Map<UserDto, UserEntity>(dto);
```

You only need one `BothWays` call anywhere in your codebase (e.g., in a profile or startup file) to register both directions.

> **Available since v1.0.0**

---

## IQueryable Projections (EF Core)

`Mapper.Projection<TSource, TDestination>()` returns a compile-time-generated `Expression<Func<TSource, TDestination>>` that you can pass directly to LINQ `.Select()`. This lets EF Core translate the projection to SQL and fetch only the columns you need.

```csharp
List<UserDto> dtos = await dbContext.Users
    .Where(u => u.IsActive)
    .Select(Mapper.Projection<UserEntity, UserDto>())
    .ToListAsync();
```

No EF Core package is required by MappShark itself — `System.Linq.Expressions` is part of the standard library.

> **Note:** Properties with `[MapConverter]` and dictionary properties are excluded from projections (`MSP013`). Positional records are not supported in projections.

> **Available since v1.0.0**

---

## Strict Generated Mode

By default, MappShark falls back to a reflection-based mapper when no generated mapper is found — useful during development or for types discovered at runtime. To enforce that **only generated mappers are used**, set the AppContext switch at startup:

```csharp
// Program.cs or equivalent startup code:
AppContext.SetSwitch("MappShark.StrictGeneratedMode", true);
```

With strict mode enabled, `Mapper.Map` throws `InvalidOperationException` if no generated mapper exists for a given pair, making misconfiguration immediately visible.

> **Available since v1.0.0**

---

## Build-Time Diagnostics

MappShark surfaces configuration problems as **compiler errors or warnings** — they appear in your IDE and CI build output, never at runtime.

| Code | Severity | Description |
|---|---|---|
| `MSP001` | Error | Duplicate `[MapIndex]` on source type |
| `MSP002` | Error | Duplicate `[MapIndex]` on destination type |
| `MSP003` | Error | Destination index has no matching source index |
| `MSP004` | Error | Source and destination indexed properties are type-incompatible |
| `MSP005` | Error | Indexed source property has no public getter |
| `MSP006` | Error | Indexed destination property has no public setter |
| `MSP007` | Error | `[MapIndex]` on a static property |
| `MSP008` | Error | Index value is negative |
| `MSP009` | Error | Converter does not implement `IMapValueConverter<,>` |
| `MSP010` | Error | Converter cannot be instantiated (abstract or no public parameterless constructor) |
| `MSP011` | Error | Destination collection type is not supported |
| `MSP012` | Error | Destination nested/element type has no public parameterless constructor |
| `MSP013` | Warning | Property with `[MapConverter]` or dictionary is excluded from IQueryable projections |
| `MSP014` | Error | `[MapFrom]` references a source property that does not exist |
| `MSP015` | Error | `[MapFrom]` and `[MapIndex]` used on the same property |
| `MSP016` | Error | `[MapTo]` and `[MapIndex]` used on the same property |
| `MSP017` | Error | A segment of a `[MapFrom]` dot-path cannot be resolved to a readable public property on the preceding type |

---

## API Reference

| Method | Description |
|---|---|
| `Mapper.Map<TSource, TDest>(source)` | Maps a single object. |
| `Mapper.MapMany<TSource, TDest>(source)` | Maps a collection to `List<TDest>`. |
| `Mapper.BothWays<TSource, TDest>(source)` | Maps forward and registers the reverse pair for code generation. |
| `Mapper.Projection<TSource, TDest>()` | Returns a compile-time-generated expression for `IQueryable.Select()`. |

---

## Targets & Compatibility

| Framework | Supported |
|---|:---:|
| .NET Standard 2.0 | ✅ |
| .NET 8 | ✅ |
| .NET 9+ | ✅ |
| Native AOT | ✅ |
| Blazor / MAUI | ✅ |

---

## License

MIT — see [LICENSE](LICENSE).

