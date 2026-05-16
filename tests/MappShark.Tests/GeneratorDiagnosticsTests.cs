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
