# MappShark

> **Fast. Safe. Zero-overhead object mapping for .NET — powered by a Roslyn source generator.**

MappShark maps objects at the speed of hand-written code. Instead of relying on reflection at runtime, it generates a plain C# mapper during compilation. Errors in your mapping configuration become **build errors**, not production crashes.

[![NuGet](https://img.shields.io/nuget/v/MappShark.svg)](https://www.nuget.org/packages/MappShark)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Why MappShark?

Most mapping libraries do their heaviest work at **runtime** — scanning types via reflection, compiling expression trees on first use, and throwing configuration exceptions only when a mapping is actually executed. MappShark flips this model:

| | AutoMapper | Mapster | **MappShark** |
|---|:---:|:---:|:---:|
| Generated at compile time | ✗ | ✗ | ✅ |
| Errors caught at build time | ✗ | ✗ | ✅ |
| Zero reflection on hot path | ✗ | Partial | ✅ |
| Native AOT / Trimming friendly | ✗ | Partial | ✅ |
| IQueryable projections | ✅ | ✅ | ✅ |

---

## Installation

```bash
dotnet add package MappShark
```

Requires **.NET Standard 2.0** or **.NET 8+**. The source generator works in any project that references the package — no extra setup needed.

---

## Quick Start

### 1. Annotate your types

Mark each property you want to map with `[MapIndex(n)]`. Properties that share the same index get mapped to each other — regardless of name differences.

```csharp
using MappShark;

public class UserEntity
{
    [MapIndex(0)] public int Id { get; set; }
    [MapIndex(1)] public string FullName { get; set; } = string.Empty;

    // No [MapIndex] → always ignored by the indexed mapper.
    public DateTime CreatedAt { get; set; }
}

public class UserDto
{
    [MapIndex(0)] public int UserId { get; set; }    // maps from Id (same index 0)
    [MapIndex(1)] public string Name { get; set; } = string.Empty; // maps from FullName (same index 1)

    public string? AvatarUrl { get; set; } // no [MapIndex] → ignored
}
```

### 2. Call `Mapper.Map`

```csharp
var dto = Mapper.Map<UserEntity, UserDto>(entity);
// dto.UserId == entity.Id
// dto.Name   == entity.FullName
```

That's it. The source generator creates an optimized static mapper during your build. No warm-up, no reflection, no surprises.

---

## How it Works

When you write `Mapper.Map<UserEntity, UserDto>(...)`, MappShark's Roslyn analyzer detects the call at compile time and emits a file called `IndexedMapResolver.g.cs` in your project. This file contains a plain C# method like:

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

For destination types with `init`-only properties (e.g., records), the generator emits object-initializer syntax instead:

```csharp
private static UserDto MapPair_0(UserEntity source)
{
    return new UserDto
    {
        UserId = source.Id,
        Name   = source.FullName,
    };
}
```

---

## Name-Based Fallback

Don't want to add `[MapIndex]` to every property? If source and destination have properties with **matching names and compatible types**, MappShark maps them automatically — no annotation required.

```csharp
public class ProductEntity
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockCount { get; set; }
}

public class ProductDto
{
    public string Name { get; set; } = string.Empty;   // ← mapped by name
    public decimal Price { get; set; }                 // ← mapped by name
    // StockCount is missing → simply not mapped
}

var dto = Mapper.Map<ProductEntity, ProductDto>(entity);
```

> **Rule:** `[MapIndex]` always takes priority. Name-based fallback only applies to properties that have no index annotation on either side.

---

## Property Name Overrides: `[MapFrom]` and `[MapTo]`

When source and destination properties have different names but you don't want — or can't — add `[MapIndex]` to both sides (e.g., to keep domain models free of mapping concerns), use the name-override attributes.

### `[MapFrom("SourcePropertyName")]`

Apply on a **destination** property. Tells MappShark to read from the named source property instead of looking for a same-named one.

```csharp
public class OrderDto
{
    public int Id { get; set; }

    [MapFrom("TotalAmount")]   // read from OrderEntity.TotalAmount
    public decimal Total { get; set; }
}

public class OrderEntity
{
    public int Id { get; set; }
    public decimal TotalAmount { get; set; }  // no annotation needed here
}

var dto = Mapper.Map<OrderEntity, OrderDto>(entity);
// dto.Total == entity.TotalAmount
```

### `[MapTo("DestinationPropertyName")]`

Apply on a **source** property. Tells MappShark to write its value into the named destination property.

```csharp
public class CreateOrderCommand
{
    public string Reference { get; set; } = string.Empty;

    [MapTo("Price")]           // write into OrderDto.Price
    public decimal Amount { get; set; }
}

public class OrderDto
{
    public string Reference { get; set; } = string.Empty;
    public decimal Price { get; set; }    // no annotation needed here
}

var dto = Mapper.Map<CreateOrderCommand, OrderDto>(command);
// dto.Price == command.Amount
```

### Priority order

When multiple strategies could resolve the same destination property, MappShark uses the following priority:

1. `[MapIndex]` — highest priority, always wins
2. `[MapFrom]` on the destination property
3. `[MapTo]` on the source property
4. Same-name fallback — lowest priority

### Reverse mapping (`BothWays`)

Each mapping direction is resolved independently. Annotate the type that is the destination in each direction:

```csharp
public class ProductEntity
{
    public string Code { get; set; } = string.Empty;

    [MapFrom("Price")]          // when ProductEntity is destination: read from ProductDto.Price
    public decimal UnitPrice { get; set; }
}

public class ProductDto
{
    public string Code { get; set; } = string.Empty;

    [MapFrom("UnitPrice")]      // when ProductDto is destination: read from ProductEntity.UnitPrice
    public decimal Price { get; set; }
}

var dto    = Mapper.BothWays<ProductEntity, ProductDto>(entity);
var entity = Mapper.Map<ProductDto, ProductEntity>(dto);
```

> **Constraints:**
> - `[MapFrom]` and `[MapIndex]` cannot be combined on the same property (`MSP015`).
> - `[MapTo]` and `[MapIndex]` cannot be combined on the same property (`MSP016`).
> - If the source property named in `[MapFrom]` does not exist, the build fails with `MSP014`.

---

## Records and Init-Only Properties

MappShark fully supports C# **records** and any class or struct with `init`-only setters — no extra configuration needed.

### Records with explicit properties

If every `init` property has a **public parameterless constructor** (the default for `record` types with explicit properties), the source generator emits an object-initializer method:

```csharp
public class OrderEntity
{
    public int Id { get; set; }
    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
}

public record OrderDto
{
    public int Id { get; init; }

    [MapFrom("TotalAmount")]
    public decimal Total { get; init; }

    [MapFrom("CustomerName")]
    public string Customer { get; init; } = string.Empty;
}

var dto = Mapper.Map<OrderEntity, OrderDto>(entity);
// dto.Total    == entity.TotalAmount
// dto.Customer == entity.CustomerName
```

Generated code uses object-initializer syntax, which is the only way to assign `init` properties after construction:

```csharp
private static OrderDto MapPair_0(OrderEntity source)
{
    return new OrderDto
    {
        Id = source.Id,
        Total = source.TotalAmount,
        Customer = source.CustomerName,
    };
}
```

### Positional records

Positional records (`record Foo(int Bar)`) have **no parameterless constructor**. MappShark fully supports them via generated **constructor-call syntax** — no reflection fallback required.

All three mapping attributes — `[MapIndex]`, `[MapFrom]`, and `[MapTo]` — can be placed **directly on positional parameters**:

```csharp
// All properties map by name — no attributes needed.
public record PointDto(double X, double Y);

// [MapFrom] directly on the parameter
public record OrderDto(
    int Id,
    [MapFrom("TotalAmount")] decimal Total,
    [MapFrom("CustomerName")] string Customer
);

// [MapIndex] on positional parameters — index-based mapping
public record WidgetDto(
    [MapIndex(0)] string SerialNumber,
    [MapIndex(1)] int Version
);

// [MapTo] on the source — write into the named positional parameter
public sealed class ContractCommand
{
    [MapTo("Title")] public string ContractName { get; set; } = string.Empty;
    [MapTo("Value")] public decimal Amount { get; set; }
}
public record ContractDto(string Title, decimal Value);

var dto = Mapper.Map<OrderEntity, OrderDto>(entity); // uses generated constructor-call code
```

The source generator emits constructor-call syntax:

```csharp
private static OrderDto MapPair_N(OrderEntity source) =>
    new OrderDto(
        Id: source.Id,
        Total: source.TotalAmount,
        Customer: source.CustomerName);
```

> **Note:** The `[property: ...]` target specifier (e.g. `[property: MapFrom("X")]`) is also accepted as an alternative syntax — both forms are equivalent.

> **Limitation:** `Mapper.Projection<TSource, TDestination>()` (LINQ expression trees) is not supported for positional records — only `Mapper.Map` and `Mapper.MapMany`.

---

## Nested Objects

MappShark automatically handles nested objects. Just call `Mapper.Map` once for the top-level type — the generator discovers and wires up all required nested pairs.

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

// One call maps the whole graph:
var dto = Mapper.Map<OrderEntity, OrderDto>(order);
```

The generator recursively discovers `CustomerEntity → CustomerDto` and `OrderLineEntity → OrderLineDto` and generates optimized code for all of them.

---

## Collections

Supported collection targets:

| Destination type | Supported |
|---|:---:|
| `List<T>` | ✅ |
| `IList<T>`, `ICollection<T>`, `IEnumerable<T>` | ✅ |
| `T[]` (arrays) | ✅ |

---

## Map Many Items at Once

Use `Mapper.MapMany` to map an entire collection in a single call:

```csharp
IEnumerable<UserEntity> entities = GetUsers();

List<UserDto> dtos = Mapper.MapMany<UserEntity, UserDto>(entities);
```

---

## Reverse Mapping with `BothWays`

Using `Mapper.BothWays` instead of `Mapper.Map` tells the generator to produce mappers for **both directions** — `A → B` and `B → A`.

```csharp
// This single call causes the generator to emit mappers for:
//   UserEntity → UserDto
//   UserDto    → UserEntity
var dto = Mapper.BothWays<UserEntity, UserDto>(entity);

// Later, map back:
var entity = Mapper.Map<UserDto, UserEntity>(dto);
```

> **Tip:** You only need one `BothWays` call anywhere in your codebase (e.g., in a startup file or a mapping helper) to register both directions.

---

## IQueryable Projections (EF Core)

`Mapper.Projection<TSource, TDestination>()` returns a compile-time-generated `Expression<Func<TSource, TDestination>>` that you can pass directly to LINQ `.Select()`. This is the most efficient way to query only the columns you need from a database.

```csharp
// In your repository or service:
List<UserDto> dtos = await dbContext.Users
    .Where(u => u.IsActive)
    .Select(Mapper.Projection<UserEntity, UserDto>())
    .ToListAsync();
```

The expression is inlined at compile time — **no reflection, no dynamic expression building at runtime**. Properties with `[MapConverter]` are excluded from projections (see diagnostic `MSP013`).

> **Note:** No EF Core package is required by MappShark itself. `System.Linq.Expressions` is part of the standard library.

---

## Custom Value Converters

When a property needs a custom transformation (e.g., `decimal → string`), implement `IMapValueConverter<TSource, TDestination>` and attach it with `[MapConverter]`:

```csharp
public class PercentConverter : IMapValueConverter<decimal, string>
{
    public string Convert(decimal value) => $"{value * 100:0.##}%";
}

public class MetricEntity
{
    [MapIndex(0)] public decimal Ratio { get; set; }
}

public class MetricDto
{
    [MapIndex(0)]
    [MapConverter(typeof(PercentConverter))]
    public string RatioLabel { get; set; } = string.Empty;
}
```

Requirements for converters:
- Must implement `IMapValueConverter<TSourceMember, TDestinationMember>`.
- Must be a concrete, non-abstract class.
- Must have a public parameterless constructor.

---

## Organizing Mappings with Profiles

For medium and large projects, use `MappSharkProfile` to group related mappings together. The source generator discovers your profiles automatically and generates mappers for all registered pairs.

```csharp
using MappShark;

public class OrderMappingProfile : MappSharkProfile
{
    public OrderMappingProfile()
    {
        CreateMap<OrderEntity, OrderDto>();
        CreateMap<OrderDto, OrderEntity>();  // reverse direction
        CreateMap<OrderLineEntity, OrderLineDto>();
    }
}
```

> **Note:** You don't need to instantiate profiles manually — the generator reads the `CreateMap<,>()` calls at compile time. No runtime registration needed.

---

## Strict Generated Mode

By default, MappShark falls back to a reflection-based mapper when no generated mapper is found (useful during development or for types discovered at runtime). To enforce that **only generated mappers are used**, set the AppContext switch:

```csharp
// In your application startup (e.g., Program.cs):
AppContext.SetSwitch("MappShark.StrictGeneratedMode", true);
```

With strict mode on, `Mapper.Map` throws `InvalidOperationException` if no generated mapper exists for a given pair, making misconfiguration immediately visible.

---

## Build-Time Diagnostics

MappShark reports configuration problems as **compiler errors or warnings** — they show up in your IDE and CI build output, not at runtime.

| Code | Severity | Description |
|---|---|---|
| `MSP001` | Error | Duplicate `[MapIndex]` in source type |
| `MSP002` | Error | Duplicate `[MapIndex]` in destination type |
| `MSP003` | Error | Destination index has no matching source index |
| `MSP004` | Error | Source and destination indexed properties are type-incompatible |
| `MSP005` | Error | Indexed source property has no public getter |
| `MSP006` | Error | Indexed destination property has no public setter |
| `MSP007` | Error | `[MapIndex]` on a static property |
| `MSP008` | Error | Index value is negative |
| `MSP009` | Error | Converter does not implement the required `IMapValueConverter<,>` contract |
| `MSP010` | Error | Converter cannot be instantiated (abstract or no public constructor) |
| `MSP011` | Error | Destination collection type is not supported |
| `MSP012` | Error | Destination nested/element type has no public parameterless constructor |
| `MSP013` | Warning | Property with `[MapConverter]` is excluded from IQueryable projections |
| `MSP014` | Error | `[MapFrom]` references a source property that does not exist |
| `MSP015` | Error | `[MapFrom]` and `[MapIndex]` cannot be used on the same property |
| `MSP016` | Error | `[MapTo]` and `[MapIndex]` cannot be used on the same property |

---

## API Reference

| Method | Description |
|---|---|
| `Mapper.Map<TSource, TDest>(source)` | Maps a single object |
| `Mapper.MapMany<TSource, TDest>(source)` | Maps a collection to `List<TDest>` |
| `Mapper.BothWays<TSource, TDest>(source)` | Maps forward + registers reverse pair for code generation |
| `Mapper.Projection<TSource, TDest>()` | Returns a compiled expression for `IQueryable.Select()` |

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
