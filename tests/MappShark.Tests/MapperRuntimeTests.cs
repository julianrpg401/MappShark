using System;
using System.Collections.Generic;
using System.Globalization;
using MappShark;
using Xunit;

namespace MappShark.Tests;

public sealed class MapperRuntimeTests
{
    [Fact]
    public void MapsByIndexAndIgnoresUnindexedProperties()
    {
        var source = new UserEntity
        {
            Identifier = 17,
            FullName = "Ada Lovelace",
            IgnoredNote = "source-note"
        };

        var destination = Mapper.Map<UserEntity, UserDto>(source);

        Assert.Equal(17, destination.Id);
        Assert.Equal("Ada Lovelace", destination.Name);
        Assert.Null(destination.DestinationOnlyIgnored);
    }

    [Fact]
    public void MapsNestedObjectsAndCollections()
    {
        var source = new OrderEntity
        {
            OrderNumber = "ORD-001",
            Customer = new CustomerEntity
            {
                Id = 7,
                Name = "Linus"
            },
            Lines = new List<OrderLineEntity>
            {
                new() { Product = "Keyboard", Quantity = 2 },
                new() { Product = "Mouse", Quantity = 1 }
            }
        };

        var destination = Mapper.Map<OrderEntity, OrderDto>(source);

        Assert.Equal("ORD-001", destination.Code);
        Assert.NotNull(destination.Customer);
        Assert.Equal(7, destination.Customer!.CustomerId);
        Assert.Equal("Linus", destination.Customer.DisplayName);
        Assert.NotNull(destination.Items);
        Assert.Equal(2, destination.Items!.Count);
        Assert.Equal("Keyboard", destination.Items[0].Sku);
        Assert.Equal(2, destination.Items[0].Units);
    }

    [Fact]
    public void UsesCustomConverterWhenConfigured()
    {
        var source = new MetricEntity
        {
            Ratio = 0.81234m
        };

        var destination = Mapper.Map<MetricEntity, MetricDto>(source);

        Assert.Equal("81.23%", destination.RatioLabel);
    }

    private sealed class UserEntity
    {
        [MapIndex(0)]
        public int Identifier { get; set; }

        [MapIndex(1)]
        public string FullName { get; set; } = string.Empty;

        public string? IgnoredNote { get; set; }
    }

    private sealed class UserDto
    {
        [MapIndex(0)]
        public int Id { get; set; }

        [MapIndex(1)]
        public string Name { get; set; } = string.Empty;

        public string? DestinationOnlyIgnored { get; set; }
    }

    private sealed class OrderEntity
    {
        [MapIndex(0)]
        public string OrderNumber { get; set; } = string.Empty;

        [MapIndex(1)]
        public CustomerEntity? Customer { get; set; }

        [MapIndex(2)]
        public List<OrderLineEntity>? Lines { get; set; }
    }

    private sealed class OrderDto
    {
        [MapIndex(0)]
        public string Code { get; set; } = string.Empty;

        [MapIndex(1)]
        public CustomerDto? Customer { get; set; }

        [MapIndex(2)]
        public List<OrderLineDto>? Items { get; set; }
    }

    private sealed class CustomerEntity
    {
        [MapIndex(0)]
        public int Id { get; set; }

        [MapIndex(1)]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CustomerDto
    {
        [MapIndex(0)]
        public int CustomerId { get; set; }

        [MapIndex(1)]
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class OrderLineEntity
    {
        [MapIndex(0)]
        public string Product { get; set; } = string.Empty;

        [MapIndex(1)]
        public int Quantity { get; set; }
    }

    private sealed class OrderLineDto
    {
        [MapIndex(0)]
        public string Sku { get; set; } = string.Empty;

        [MapIndex(1)]
        public int Units { get; set; }
    }

    private sealed class MetricEntity
    {
        [MapIndex(0)]
        public decimal Ratio { get; set; }
    }

    private sealed class MetricDto
    {
        [MapIndex(0)]
        [MapConverter(typeof(PercentLabelConverter))]
        public string RatioLabel { get; set; } = string.Empty;
    }

    private sealed class PercentLabelConverter : IMapValueConverter<decimal, string>
    {
        public string Convert(decimal source)
        {
            return (source * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }
    }
}
