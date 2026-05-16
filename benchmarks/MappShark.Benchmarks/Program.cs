using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Mapster;
using MappShark;
using MappSharkMapper = MappShark.Mapper;
using Riok.Mapperly.Abstractions;

BenchmarkRunner.Run<MappingBenchmarks>();

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 10, iterationCount: 20)]
public class MappingBenchmarks
{
    private OrderEntity _source = null!;
    private IMapper _autoMapper = null!;
    private TypeAdapterConfig _mapsterConfig = null!;
    private MapperlyMapper _mapperlyMapper = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = BuildSource();

        var autoMapperConfig = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<OrderEntity, OrderDto>()
                .ForMember(destination => destination.Code, options => options.MapFrom(source => source.OrderNumber))
                .ForMember(destination => destination.Customer, options => options.MapFrom(source => source.Customer))
                .ForMember(destination => destination.Items, options => options.MapFrom(source => source.Lines));

            configuration.CreateMap<CustomerEntity, CustomerDto>()
                .ForMember(destination => destination.CustomerId, options => options.MapFrom(source => source.Id))
                .ForMember(destination => destination.DisplayName, options => options.MapFrom(source => source.Name));

            configuration.CreateMap<OrderLineEntity, OrderLineDto>()
                .ForMember(destination => destination.Sku, options => options.MapFrom(source => source.Product))
                .ForMember(destination => destination.Units, options => options.MapFrom(source => source.Quantity));
        });

        _autoMapper = autoMapperConfig.CreateMapper();

        _mapsterConfig = new TypeAdapterConfig();
        _mapsterConfig.NewConfig<OrderEntity, OrderDto>()
            .Map(destination => destination.Code, source => source.OrderNumber)
            .Map(destination => destination.Customer, source => source.Customer)
            .Map(destination => destination.Items, source => source.Lines);

        _mapsterConfig.NewConfig<CustomerEntity, CustomerDto>()
            .Map(destination => destination.CustomerId, source => source.Id)
            .Map(destination => destination.DisplayName, source => source.Name);

        _mapsterConfig.NewConfig<OrderLineEntity, OrderLineDto>()
            .Map(destination => destination.Sku, source => source.Product)
            .Map(destination => destination.Units, source => source.Quantity);

        _mapsterConfig.Compile();

        _mapperlyMapper = new MapperlyMapper();
    }

    [Benchmark(Baseline = true)]
    public OrderDto ManualMapping()
    {
        return new OrderDto
        {
            Code = _source.OrderNumber,
            Customer = _source.Customer is null
                ? null
                : new CustomerDto
                {
                    CustomerId = _source.Customer.Id,
                    DisplayName = _source.Customer.Name
                },
            Items = _source.Lines is null
                ? null
                : _source.Lines
                    .ConvertAll(item => new OrderLineDto
                    {
                        Sku = item.Product,
                        Units = item.Quantity
                    })
        };
    }

    [Benchmark]
    public OrderDto MappSharkMapping()
    {
        return MappSharkMapper.Map<OrderEntity, OrderDto>(_source);
    }

    [Benchmark]
    public OrderDto AutoMapperMapping()
    {
        return _autoMapper.Map<OrderDto>(_source);
    }

    [Benchmark]
    public OrderDto MapsterMapping()
    {
        return _source.Adapt<OrderDto>(_mapsterConfig);
    }

    [Benchmark]
    public OrderDto MapperlyMapping()
    {
        return _mapperlyMapper.MapOrder(_source);
    }

    private static OrderEntity BuildSource()
    {
        var lines = new List<OrderLineEntity>(capacity: 25);
        for (var index = 0; index < 25; index++)
        {
            lines.Add(new OrderLineEntity
            {
                Product = "SKU-" + index,
                Quantity = (index % 4) + 1
            });
        }

        return new OrderEntity
        {
            OrderNumber = "ORD-2026-0001",
            Customer = new CustomerEntity
            {
                Id = 1729,
                Name = "Grace Hopper"
            },
            Lines = lines
        };
    }

    public sealed class OrderEntity
    {
        [MapIndex(0)]
        public string OrderNumber { get; set; } = string.Empty;

        [MapIndex(1)]
        public CustomerEntity? Customer { get; set; }

        [MapIndex(2)]
        public List<OrderLineEntity>? Lines { get; set; }
    }

    public sealed class OrderDto
    {
        [MapIndex(0)]
        public string Code { get; set; } = string.Empty;

        [MapIndex(1)]
        public CustomerDto? Customer { get; set; }

        [MapIndex(2)]
        public List<OrderLineDto>? Items { get; set; }
    }

    public sealed class CustomerEntity
    {
        [MapIndex(0)]
        public int Id { get; set; }

        [MapIndex(1)]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CustomerDto
    {
        [MapIndex(0)]
        public int CustomerId { get; set; }

        [MapIndex(1)]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class OrderLineEntity
    {
        [MapIndex(0)]
        public string Product { get; set; } = string.Empty;

        [MapIndex(1)]
        public int Quantity { get; set; }
    }

    public sealed class OrderLineDto
    {
        [MapIndex(0)]
        public string Sku { get; set; } = string.Empty;

        [MapIndex(1)]
        public int Units { get; set; }
    }
}

[Riok.Mapperly.Abstractions.Mapper]
public partial class MapperlyMapper
{
    [MapProperty("OrderNumber", "Code")]
    [MapProperty("Lines", "Items")]
    public partial MappingBenchmarks.OrderDto MapOrder(MappingBenchmarks.OrderEntity source);

    [MapProperty("Id", "CustomerId")]
    [MapProperty("Name", "DisplayName")]
    private partial MappingBenchmarks.CustomerDto MapCustomer(MappingBenchmarks.CustomerEntity source);

    [MapProperty("Product", "Sku")]
    [MapProperty("Quantity", "Units")]
    private partial MappingBenchmarks.OrderLineDto MapOrderLine(MappingBenchmarks.OrderLineEntity source);
}
