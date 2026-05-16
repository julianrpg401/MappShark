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
