using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MappShark.Generator;
using Xunit;

namespace MappShark.Tests;

public sealed class GeneratorDiagnosticsTests
{
    [Fact]
    public void ReportsMsp003WhenDestinationIndexHasNoSourceCounterpart()
    {
        const string sourceCode = @"
using MappShark;

public sealed class SourceModel
{
    [MapIndex(0)]
    public int Id { get; set; }
}

public sealed class DestinationModel
{
    [MapIndex(0)]
    public int DestinationId { get; set; }

    [MapIndex(1)]
    public string MissingInSource { get; set; } = string.Empty;
}

public static class Usage
{
    public static DestinationModel Map(SourceModel source)
    {
        return Mapper.Map<SourceModel, DestinationModel>(source);
    }
}
";

        var diagnostics = RunGenerator(sourceCode);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "MSP003");
    }

    [Fact]
    public void ReportsMsp009WhenConverterContractDoesNotMatch()
    {
        const string sourceCode = @"
using MappShark;

public sealed class SourceModel
{
    [MapIndex(0)]
    public int Value { get; set; }
}

public sealed class DestinationModel
{
    [MapIndex(0)]
    [MapConverter(typeof(InvalidConverter))]
    public string Text { get; set; } = string.Empty;
}

public sealed class InvalidConverter : IMapValueConverter<long, string>
{
    public string Convert(long source) => source.ToString();
}

public static class Usage
{
    public static DestinationModel Map(SourceModel source)
    {
        return Mapper.Map<SourceModel, DestinationModel>(source);
    }
}
";

        var diagnostics = RunGenerator(sourceCode);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "MSP009");
    }

    [Fact]
    public void ReportsMsp001WhenSourceHasDuplicateIndex()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel
{
    [MapIndex(0)] public int A { get; set; }
    [MapIndex(0)] public int B { get; set; }
}
public sealed class DestinationModel { [MapIndex(0)] public int X { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP001");
    }

    [Fact]
    public void ReportsMsp002WhenDestinationHasDuplicateIndex()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public int X { get; set; } }
public sealed class DestinationModel
{
    [MapIndex(0)] public int A { get; set; }
    [MapIndex(0)] public int B { get; set; }
}
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP002");
    }

    [Fact]
    public void ReportsMsp004WhenIndexedTypesAreIncompatible()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public string Value { get; set; } = string.Empty; }
public sealed class DestinationModel { [MapIndex(0)] public int Value { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP004");
    }

    [Fact]
    public void ReportsMsp005WhenSourceIndexedPropertyHasNoPublicGetter()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] private int Value { get; set; } }
public sealed class DestinationModel { [MapIndex(0)] public int Value { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP005");
    }

    [Fact]
    public void ReportsMsp006WhenDestinationIndexedPropertyHasNoPublicSetter()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public int Value { get; set; } }
public sealed class DestinationModel { [MapIndex(0)] public int Value { get; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP006");
    }

    [Fact]
    public void ReportsMsp007WhenIndexedPropertyIsStatic()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public static int Value { get; set; } }
public sealed class DestinationModel { [MapIndex(0)] public int Value { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP007");
    }

    [Fact]
    public void ReportsMsp008WhenIndexIsNegative()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(-1)] public int Value { get; set; } }
public sealed class DestinationModel { [MapIndex(-1)] public int Value { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP008");
    }

    [Fact]
    public void ReportsMsp010WhenConverterIsAbstract()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public int Value { get; set; } }
public sealed class DestinationModel
{
    [MapIndex(0)]
    [MapConverter(typeof(AbstractConverter))]
    public string Text { get; set; } = string.Empty;
}
public abstract class AbstractConverter : IMapValueConverter<int, string>
{
    public abstract string Convert(int source);
}
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP010");
    }

    [Fact]
    public void ReportsMsp011WhenDestinationCollectionTypeIsUnsupported()
    {
        const string sourceCode = @"
using MappShark;
using System.Collections.Generic;
public sealed class SourceModel { [MapIndex(0)] public List<int> Items { get; set; } = new(); }
public sealed class DestinationModel { [MapIndex(0)] public HashSet<int> Items { get; set; } = new(); }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP011");
    }

    [Fact]
    public void ReportsMsp012WhenNestedDestinationTypeHasNoParameterlessConstructor()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public Inner Source { get; set; } = new(); }
public sealed class DestinationModel { [MapIndex(0)] public NoDefaultCtor Dest { get; set; } = new(0); }
public sealed class Inner { public int X { get; set; } }
public sealed class NoDefaultCtor { public int X { get; set; } public NoDefaultCtor(int x) { X = x; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP012");
    }

    [Fact]
    public void ReportsMsp013WhenConverterPropertyIsExcludedFromProjection()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public int Value { get; set; } }
public sealed class DestinationModel
{
    [MapIndex(0)]
    [MapConverter(typeof(IntToStringConverter))]
    public string Text { get; set; } = string.Empty;
}
public sealed class IntToStringConverter : IMapValueConverter<int, string>
{
    public string Convert(int source) => source.ToString();
}
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP013");
    }

    [Fact]
    public void ReportsMsp014WhenMapFromSourcePropertyNotFound()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { public int Id { get; set; } }
public sealed class DestinationModel { [MapFrom(""NonExistent"")] public int Id { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP014");
    }

    [Fact]
    public void ReportsMsp015WhenMapFromConflictsWithMapIndex()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] public int Id { get; set; } }
public sealed class DestinationModel { [MapIndex(0)] [MapFrom(""Id"")] public int Id { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP015");
    }

    [Fact]
    public void ReportsMsp016WhenMapToConflictsWithMapIndex()
    {
        const string sourceCode = @"
using MappShark;
public sealed class SourceModel { [MapIndex(0)] [MapTo(""Id"")] public int Id { get; set; } }
public sealed class DestinationModel { [MapIndex(0)] public int Id { get; set; } }
public static class Usage { public static DestinationModel Map(SourceModel s) => Mapper.Map<SourceModel, DestinationModel>(s); }
";
        var diagnostics = RunGenerator(sourceCode);
        Assert.Contains(diagnostics, d => d.Id == "MSP016");
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        var references = new List<MetadataReference>();
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();

        foreach (var assemblyPath in trustedPlatformAssemblies)
        {
            references.Add(MetadataReference.CreateFromFile(assemblyPath));
        }

        references.AddRange(new[]
        {
            MetadataReference.CreateFromFile(typeof(MapIndexAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MapConverterAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapValueConverter<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Mapper).Assembly.Location)
        });

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorDiagnosticsTests",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new IndexedMapSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var _, out var _);

        return driver.GetRunResult().Diagnostics;
    }
}
