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
