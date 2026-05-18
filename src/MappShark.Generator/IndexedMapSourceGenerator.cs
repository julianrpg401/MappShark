using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MappShark.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class IndexedMapSourceGenerator : IIncrementalGenerator
{
    private const string MapperMetadataName = "MappShark.Mapper";
    private const string MapIndexAttributeMetadataName = "MappShark.MapIndexAttribute";
    private const string MapConverterAttributeMetadataName = "MappShark.MapConverterAttribute";
    private const string MapValueConverterInterfaceMetadataName = "MappShark.IMapValueConverter`2";
    private const string ListTypeMetadataName = "System.Collections.Generic.List`1";

    private static readonly DiagnosticDescriptor DuplicateSourceIndexDescriptor = new(
        id: "MSP001",
        title: "Duplicate source map index",
        messageFormat: "Type '{0}' contains duplicate [MapIndex({1})] in source properties.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Source types must not contain duplicated map indexes.");

    private static readonly DiagnosticDescriptor DuplicateDestinationIndexDescriptor = new(
        id: "MSP002",
        title: "Duplicate destination map index",
        messageFormat: "Type '{0}' contains duplicate [MapIndex({1})] in destination properties.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Destination types must not contain duplicated map indexes.");

    private static readonly DiagnosticDescriptor MissingSourceIndexDescriptor = new(
        id: "MSP003",
        title: "Destination index has no source counterpart",
        messageFormat: "Destination property '{0}.{1}' has [MapIndex({2})] but source type '{3}' does not define that index.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every indexed destination property must have a compatible indexed source property.");

    private static readonly DiagnosticDescriptor IncompatibleTypeDescriptor = new(
        id: "MSP004",
        title: "Indexed properties are not type-compatible",
        messageFormat: "[MapIndex({0})] is incompatible: source '{1}.{2}' ({3}) cannot map to destination '{4}.{5}' ({6}).",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Indexed source and destination properties must be implicitly assignable or map-compatible.");

    private static readonly DiagnosticDescriptor SourceGetterDescriptor = new(
        id: "MSP005",
        title: "Indexed source property must be readable",
        messageFormat: "Source property '{0}.{1}' has [MapIndex({2})] but does not expose a public getter.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Indexed source properties require a public getter.");

    private static readonly DiagnosticDescriptor DestinationSetterDescriptor = new(
        id: "MSP006",
        title: "Indexed destination property must be writable",
        messageFormat: "Destination property '{0}.{1}' has [MapIndex({2})] but does not expose a public setter.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Indexed destination properties require a public setter.");

    private static readonly DiagnosticDescriptor StaticIndexedPropertyDescriptor = new(
        id: "MSP007",
        title: "Static indexed properties are not supported",
        messageFormat: "Property '{0}.{1}' has [MapIndex({2})] but is static. Only instance properties are supported.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "MapIndex can only be used on instance properties.");

    private static readonly DiagnosticDescriptor InvalidIndexDescriptor = new(
        id: "MSP008",
        title: "Map index must be non-negative",
        messageFormat: "Property '{0}.{1}' uses [MapIndex({2})]. Index must be zero or greater.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "MapIndex values must be greater than or equal to zero.");

    private static readonly DiagnosticDescriptor InvalidConverterDescriptor = new(
        id: "MSP009",
        title: "Invalid property converter",
        messageFormat: "Property '{0}.{1}' declares converter '{2}', but it must implement IMapValueConverter<{3}, {4}>.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Converters must implement IMapValueConverter<TSourceMember, TDestinationMember> with matching generic arguments.");

    private static readonly DiagnosticDescriptor ConverterInstantiationDescriptor = new(
        id: "MSP010",
        title: "Converter must be instantiable",
        messageFormat: "Converter '{0}' used by '{1}.{2}' must be a non-abstract type with a public parameterless constructor.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Converters must be instantiable to be used by generated mappers.");

    private static readonly DiagnosticDescriptor UnsupportedCollectionDestinationDescriptor = new(
        id: "MSP011",
        title: "Unsupported destination collection type",
        messageFormat: "Destination property '{0}.{1}' uses unsupported collection type '{2}'. Supported targets are arrays and list-compatible interfaces.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Collection mapping currently supports arrays and list-compatible destination types.");

    private static readonly DiagnosticDescriptor DestinationConstructionDescriptor = new(
        id: "MSP012",
        title: "Destination type must be constructible",
        messageFormat: "Type '{0}' used by [MapIndex({1})] must expose a public parameterless constructor to be mapped.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nested and element destination types must satisfy Mapper.Map generic constraints.");

    private static readonly DiagnosticDescriptor ProjectionConverterSkippedDescriptor = new(
        id: "MSP013",
        title: "Converter not supported in projection",
        messageFormat: "Property '{0}.{1}' uses [MapConverter] which is not supported in projections. The property will be excluded from the generated expression.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Properties with [MapConverter] cannot be translated to expression trees and are excluded from IQueryable projections.");

    private const string MapFromAttributeMetadataName = "MappShark.MapFromAttribute";
    private const string MapToAttributeMetadataName = "MappShark.MapToAttribute";
    private const string MappSharkProfileMetadataName = "MappShark.MappSharkProfile";

    private static readonly DiagnosticDescriptor MapFromSourceNotFoundDescriptor = new(
        id: "MSP014",
        title: "[MapFrom] source property not found",
        messageFormat: "Destination property '{0}' uses [MapFrom(\"{1}\")] but source type '{2}' has no accessible property with that name.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The property name in [MapFrom] must match a readable public property on the source type.");

    private static readonly DiagnosticDescriptor MapFromIndexConflictDescriptor = new(
        id: "MSP015",
        title: "[MapFrom] conflicts with [MapIndex]",
        messageFormat: "Property '{0}.{1}' has both [MapFrom] and [MapIndex]. Use only one — [MapIndex] for indexed mapping, [MapFrom] for name-override mapping.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A property cannot use both [MapIndex] and [MapFrom] simultaneously.");

    private static readonly DiagnosticDescriptor MapToIndexConflictDescriptor = new(
        id: "MSP016",
        title: "[MapTo] conflicts with [MapIndex]",
        messageFormat: "Property '{0}.{1}' has both [MapTo] and [MapIndex]. Use only one — [MapIndex] for indexed mapping, [MapTo] for name-override mapping.",
        category: "MappShark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A property cannot use both [MapIndex] and [MapTo] simultaneously.");

    private static readonly SymbolDisplayFormat FullyQualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mapInvocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsCandidateInvocation(node),
                static (syntaxContext, cancellationToken) => GetMapInvocation(syntaxContext, cancellationToken))
            .Where(static invocation => invocation is not null)
            .Select(static (invocation, _) => invocation!.Value);

        var profileInvocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsCandidateProfileInvocation(node),
                static (syntaxContext, cancellationToken) => GetProfileInvocation(syntaxContext, cancellationToken))
            .Where(static invocation => invocation is not null)
            .Select(static (invocation, _) => invocation!.Value);

        var allInvocations = mapInvocations.Collect().Combine(profileInvocations.Collect());
        var combined = context.CompilationProvider.Combine(allInvocations);

        context.RegisterSourceOutput(combined, static (productionContext, source) =>
        {
            var mergedInvocations = source.Right.Left.AddRange(source.Right.Right);
            Execute(source.Left, mergedInvocations, productionContext);
        });
    }

    private static bool IsCandidateProfileInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return false;

        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName } =>
                genericName.Identifier.ValueText == "CreateMap" && genericName.TypeArgumentList.Arguments.Count == 2,
            GenericNameSyntax genericName =>
                genericName.Identifier.ValueText == "CreateMap" && genericName.TypeArgumentList.Arguments.Count == 2,
            _ => false
        };
    }

    private static MapInvocationInfo? GetProfileInvocation(GeneratorSyntaxContext syntaxContext, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)syntaxContext.Node;
        var symbolInfo = syntaxContext.SemanticModel.GetSymbolInfo(invocation, cancellationToken);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (methodSymbol is null)
            return null;

        if (!string.Equals(methodSymbol.Name, "CreateMap", StringComparison.Ordinal) || methodSymbol.TypeArguments.Length != 2)
            return null;

        // Must be declared in a class inheriting MappSharkProfile
        var containingType = methodSymbol.ContainingType;
        if (containingType is null)
            return null;

        // Check that the base type chain includes MappSharkProfile
        var baseType = containingType.BaseType;
        var isProfile = false;
        while (baseType is not null)
        {
            if (string.Equals(baseType.ToDisplayString(), MappSharkProfileMetadataName, StringComparison.Ordinal))
            {
                isProfile = true;
                break;
            }
            baseType = baseType.BaseType;
        }

        if (!isProfile)
            return null;

        if (ContainsTypeParameter(methodSymbol.TypeArguments[0]) || ContainsTypeParameter(methodSymbol.TypeArguments[1]))
            return null;

        return new MapInvocationInfo(
            sourceType: NormalizeTypeForPair(methodSymbol.TypeArguments[0]),
            destinationType: NormalizeTypeForPair(methodSymbol.TypeArguments[1]),
            invocationLocation: invocation.GetLocation(),
            bothWays: false);
    }

    private static bool IsCandidateInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName } =>
                (genericName.Identifier.ValueText == "Map" || genericName.Identifier.ValueText == "BothWays") && genericName.TypeArgumentList.Arguments.Count == 2,
            GenericNameSyntax genericName =>
                (genericName.Identifier.ValueText == "Map" || genericName.Identifier.ValueText == "BothWays") && genericName.TypeArgumentList.Arguments.Count == 2,
            _ => false
        };
    }

    private static MapInvocationInfo? GetMapInvocation(GeneratorSyntaxContext syntaxContext, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)syntaxContext.Node;
        var symbolInfo = syntaxContext.SemanticModel.GetSymbolInfo(invocation, cancellationToken);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (methodSymbol is null)
        {
            return null;
        }

        var isMap = string.Equals(methodSymbol.Name, "Map", StringComparison.Ordinal);
        var isBothWays = string.Equals(methodSymbol.Name, "BothWays", StringComparison.Ordinal);

        if ((!isMap && !isBothWays) || methodSymbol.TypeArguments.Length != 2)
        {
            return null;
        }

        if (!string.Equals(methodSymbol.ContainingType.ToDisplayString(), MapperMetadataName, StringComparison.Ordinal))
        {
            return null;
        }

        if (ContainsTypeParameter(methodSymbol.TypeArguments[0])
            || ContainsTypeParameter(methodSymbol.TypeArguments[1]))
        {
            return null;
        }

        return new MapInvocationInfo(
            sourceType: NormalizeTypeForPair(methodSymbol.TypeArguments[0]),
            destinationType: NormalizeTypeForPair(methodSymbol.TypeArguments[1]),
            invocationLocation: invocation.GetLocation(),
            bothWays: isBothWays);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
        {
            return true;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return ContainsTypeParameter(arrayType.ElementType);
        }

        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            foreach (var typeArgument in namedType.TypeArguments)
            {
                if (ContainsTypeParameter(typeArgument))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Execute(Compilation compilation, ImmutableArray<MapInvocationInfo> invocations, SourceProductionContext context)
    {
        if (invocations.IsDefaultOrEmpty)
        {
            return;
        }

        var mapIndexAttributeSymbol = compilation.GetTypeByMetadataName(MapIndexAttributeMetadataName);
        if (mapIndexAttributeSymbol is null)
        {
            return;
        }

        var mapConverterAttributeSymbol = compilation.GetTypeByMetadataName(MapConverterAttributeMetadataName);
        var mapValueConverterInterfaceSymbol = compilation.GetTypeByMetadataName(MapValueConverterInterfaceMetadataName);
        var listTypeSymbol = compilation.GetTypeByMetadataName(ListTypeMetadataName);
        var mapFromAttributeSymbol = compilation.GetTypeByMetadataName(MapFromAttributeMetadataName);
        var mapToAttributeSymbol = compilation.GetTypeByMetadataName(MapToAttributeMetadataName);

        var pairs = new Dictionary<TypePair, MapPairContext>(TypePairComparer.Instance);
        var pendingPairs = new Queue<MapPairContext>();

        foreach (var invocation in invocations)
        {
            AddPendingPair(pairs, pendingPairs, invocation.SourceType, invocation.DestinationType, invocation.InvocationLocation);
            if (invocation.BothWays)
            {
                AddPendingPair(pairs, pendingPairs, invocation.DestinationType, invocation.SourceType, invocation.InvocationLocation);
            }
        }

        var processedPairs = new HashSet<TypePair>(TypePairComparer.Instance);
        var validPairsByType = new Dictionary<TypePair, GeneratedPair>(TypePairComparer.Instance);

        while (pendingPairs.Count > 0)
        {
            var pair = pendingPairs.Dequeue();
            var pairKey = new TypePair(pair.SourceType, pair.DestinationType);
            if (!processedPairs.Add(pairKey))
            {
                continue;
            }

            var discoveredDependencies = new HashSet<TypePair>(TypePairComparer.Instance);
            if (TryBuildGeneratedPair(
                compilation,
                mapIndexAttributeSymbol,
                mapConverterAttributeSymbol,
                mapValueConverterInterfaceSymbol,
                listTypeSymbol,
                mapFromAttributeSymbol,
                mapToAttributeSymbol,
                pair,
                context,
                discoveredDependencies,
                out var generatedPair))
            {
                validPairsByType[pairKey] = generatedPair;
            }

            foreach (var dependency in discoveredDependencies)
            {
                AddPendingPair(pairs, pendingPairs, dependency.Source, dependency.Destination, pair.InvocationLocation);
            }
        }

        var validPairs = validPairsByType.Values.ToList();

        if (validPairs.Count == 0)
        {
            return;
        }

        validPairs.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        for (var i = 0; i < validPairs.Count; i++)
        {
            validPairs[i].MethodName = "MapPair_" + i.ToString(CultureInfo.InvariantCulture);
        }

        var sourceText = GenerateResolverSource(validPairs);
        context.AddSource("IndexedMapResolver.g.cs", SourceText.From(sourceText, Encoding.UTF8));
    }

    private static void AddPendingPair(
        IDictionary<TypePair, MapPairContext> knownPairs,
        Queue<MapPairContext> pendingPairs,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType,
        Location location)
    {
        var normalizedSourceType = NormalizeTypeForPair(sourceType);
        var normalizedDestinationType = NormalizeTypeForPair(destinationType);

        if (ContainsTypeParameter(normalizedSourceType) || ContainsTypeParameter(normalizedDestinationType))
        {
            return;
        }

        var key = new TypePair(normalizedSourceType, normalizedDestinationType);
        if (knownPairs.ContainsKey(key))
        {
            return;
        }

        var pairContext = new MapPairContext(normalizedSourceType, normalizedDestinationType, location);
        knownPairs[key] = pairContext;
        pendingPairs.Enqueue(pairContext);
    }

    private static ITypeSymbol NormalizeTypeForPair(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType)
        {
            return namedType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return arrayType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        return type;
    }

    private static bool TryBuildGeneratedPair(
        Compilation compilation,
        INamedTypeSymbol mapIndexAttributeSymbol,
        INamedTypeSymbol? mapConverterAttributeSymbol,
        INamedTypeSymbol? mapValueConverterInterfaceSymbol,
        INamedTypeSymbol? listTypeSymbol,
        INamedTypeSymbol? mapFromAttributeSymbol,
        INamedTypeSymbol? mapToAttributeSymbol,
        MapPairContext pair,
        SourceProductionContext context,
        HashSet<TypePair> discoveredDependencies,
        out GeneratedPair generatedPair)
    {
        generatedPair = null!;

        var hasErrors = false;

        var sourcePropertiesByIndex = CollectIndexedProperties(
            type: pair.SourceType,
            mapIndexAttributeSymbol: mapIndexAttributeSymbol,
            mapConverterAttributeSymbol: null,
            fallbackLocation: pair.InvocationLocation,
            duplicateDescriptor: DuplicateSourceIndexDescriptor,
            accessorDescriptor: SourceGetterDescriptor,
            isDestination: false,
            context: context,
            hasErrors: ref hasErrors);

        var destinationPropertiesByIndex = CollectIndexedProperties(
            type: pair.DestinationType,
            mapIndexAttributeSymbol: mapIndexAttributeSymbol,
            mapConverterAttributeSymbol: mapConverterAttributeSymbol,
            fallbackLocation: pair.InvocationLocation,
            duplicateDescriptor: DuplicateDestinationIndexDescriptor,
            accessorDescriptor: DestinationSetterDescriptor,
            isDestination: true,
            context: context,
            hasErrors: ref hasErrors);

        var assignments = new List<PropertyAssignment>();

        // Track which destination property names are handled by indexed mappings
        var destinationMappedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var destinationEntry in destinationPropertiesByIndex.OrderBy(entry => entry.Key))
        {
            var index = destinationEntry.Key;
            var destinationIndexed = destinationEntry.Value;
            var destinationProperty = destinationIndexed.Property;

            if (!sourcePropertiesByIndex.TryGetValue(index, out var sourceIndexed))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingSourceIndexDescriptor,
                    GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                    pair.DestinationType.ToDisplayString(),
                    destinationProperty.Name,
                    index,
                    pair.SourceType.ToDisplayString()));

                hasErrors = true;
                continue;
            }

            if (!TryBuildPropertyAssignment(
                compilation,
                pair,
                index,
                sourceIndexed,
                destinationIndexed,
                mapValueConverterInterfaceSymbol,
                listTypeSymbol,
                context,
                discoveredDependencies,
                ref hasErrors,
                out var assignment))
            {
                continue;
            }

            assignments.Add(assignment);
            destinationMappedNames.Add(destinationProperty.Name);
        }

        // Name-based fallback + [MapFrom]/[MapTo] overrides.
        // Priority: [MapFrom] on dest > [MapTo] on source > same-name fallback.
        // BothWays: each direction is a separate pair, so [MapFrom]/[MapTo] are resolved
        // per-direction without ambiguity.

        // mapFromOverrides: destPropName → sourcePropName (from [MapFrom] on destination properties)
        var mapFromOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mapFromAttributeSymbol is not null)
        {
            foreach (var destProp in GetAllProperties(pair.DestinationType)
                .Where(p => !p.IsStatic))
            {
                if (!TryGetStringAttributeArg(destProp, mapFromAttributeSymbol, out var sourceName))
                    continue;

                if (destinationPropertiesByIndex.Values.Any(ip => SymbolEqualityComparer.Default.Equals(ip.Property, destProp)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MapFromIndexConflictDescriptor,
                        GetLocationOrFallback(destProp, pair.InvocationLocation),
                        pair.DestinationType.ToDisplayString(),
                        destProp.Name));
                    hasErrors = true;
                }
                else
                {
                    mapFromOverrides[destProp.Name] = sourceName;
                }
            }
        }

        // mapToOverrides: destPropName → source property (from [MapTo] on source properties)
        var mapToOverrides = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        if (mapToAttributeSymbol is not null)
        {
            foreach (var srcProp in GetAllProperties(pair.SourceType)
                .Where(p => !p.IsStatic && p.GetMethod is not null && p.GetMethod.DeclaredAccessibility == Accessibility.Public))
            {
                if (!TryGetStringAttributeArg(srcProp, mapToAttributeSymbol, out var destName))
                    continue;

                if (sourcePropertiesByIndex.Values.Any(ip => SymbolEqualityComparer.Default.Equals(ip.Property, srcProp)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MapToIndexConflictDescriptor,
                        GetLocationOrFallback(srcProp, pair.InvocationLocation),
                        pair.SourceType.ToDisplayString(),
                        srcProp.Name));
                    hasErrors = true;
                }
                else
                {
                    mapToOverrides[destName] = srcProp;
                }
            }
        }

        var sourceByName = GetAllProperties(pair.SourceType)
            .Where(p => !p.IsStatic && p.GetMethod is not null && p.GetMethod.DeclaredAccessibility == Accessibility.Public
                        && !sourcePropertiesByIndex.Values.Any(sp => SymbolEqualityComparer.Default.Equals(sp.Property, p)))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        var nextFallbackIndex = (destinationPropertiesByIndex.Count > 0 ? destinationPropertiesByIndex.Keys.Max() : -1) + 1;

        foreach (var destProperty in GetAllProperties(pair.DestinationType)
            .Where(p => !p.IsStatic && p.SetMethod is not null && p.SetMethod.DeclaredAccessibility == Accessibility.Public
                        && !destinationMappedNames.Contains(p.Name)))
        {
            IPropertySymbol? srcProperty;
            var isExplicitOverride = false;

            if (mapFromOverrides.TryGetValue(destProperty.Name, out var fromSourceName))
            {
                // [MapFrom("X")] — explicit name override on destination property
                if (!sourceByName.TryGetValue(fromSourceName, out srcProperty))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MapFromSourceNotFoundDescriptor,
                        GetLocationOrFallback(destProperty, pair.InvocationLocation),
                        destProperty.Name,
                        fromSourceName,
                        pair.SourceType.ToDisplayString()));
                    hasErrors = true;
                    continue;
                }

                isExplicitOverride = true;
            }
            else if (mapToOverrides.TryGetValue(destProperty.Name, out var srcFromMapTo))
            {
                // [MapTo("Y")] — explicit name override on source property
                srcProperty = srcFromMapTo;
                isExplicitOverride = true;
            }
            else if (!sourceByName.TryGetValue(destProperty.Name, out srcProperty))
            {
                continue;
            }

            var fakeSourceIndexed = new IndexedProperty(srcProperty!, null);
            var fakeDestIndexed = new IndexedProperty(destProperty, null);

            if (!TryBuildPropertyAssignment(
                compilation,
                pair,
                nextFallbackIndex,
                fakeSourceIndexed,
                fakeDestIndexed,
                mapValueConverterInterfaceSymbol,
                listTypeSymbol,
                context,
                discoveredDependencies,
                ref hasErrors,
                out var fallbackAssignment))
            {
                if (!isExplicitOverride)
                    hasErrors = false; // name-based fallback failures are not errors
                continue;
            }

            assignments.Add(fallbackAssignment);
            nextFallbackIndex++;
        }

        if (hasErrors)
        {
            return false;
        }

        var useObjectInitializer = assignments.Any(a => a.IsInitOnly);
        // Positional records: init-only properties but no parameterless constructor.
        // Object initializer syntax requires new(), so skip generated code and let the reflection fallback handle them.
        if (useObjectInitializer && !CanConstructDestinationType(pair.DestinationType))
        {
            if (pair.DestinationType is not INamedTypeSymbol positionalRecordType)
            {
                generatedPair = null!;
                return false;
            }

            var primaryCtor = GetPositionalRecordPrimaryConstructor(positionalRecordType);
            if (primaryCtor is null)
            {
                generatedPair = null!;
                return false;
            }

            // Re-order assignments to match constructor parameter order.
            // [MapFrom]/[MapTo]/name attributes are on the synthesized properties (C# forwards
            // property-targeted attributes from positional parameters to the generated property),
            // so the existing assignment resolution is already correct.
            var byDestName = assignments.ToDictionary(a => a.DestinationPropertyName, StringComparer.OrdinalIgnoreCase);
            var ctorAssignments = new List<PropertyAssignment>(primaryCtor.Parameters.Length);
            foreach (var param in primaryCtor.Parameters)
            {
                if (!byDestName.TryGetValue(param.Name, out var ctorAssignment))
                {
                    // Unresolved constructor parameter — cannot generate code; reflection fallback will handle it.
                    generatedPair = null!;
                    return false;
                }

                ctorAssignments.Add(ctorAssignment);
                byDestName.Remove(param.Name);
            }

            // Any remaining assignments are non-positional init properties; append them after constructor params
            // (they will be emitted via SetValue semantics in the generated code).
            foreach (var remaining in byDestName.Values)
            {
                ctorAssignments.Add(remaining);
            }

            generatedPair = new GeneratedPair(
                key: pair.SourceType.ToDisplayString(FullyQualifiedTypeFormat) + "->" + pair.DestinationType.ToDisplayString(FullyQualifiedTypeFormat),
                sourceTypeName: pair.SourceType.ToDisplayString(FullyQualifiedTypeFormat),
                destinationTypeName: pair.DestinationType.ToDisplayString(FullyQualifiedTypeFormat),
                assignments: ctorAssignments,
                useConstructorCall: true);

            // Projections are not generated for positional records.
            generatedPair.ProjectionAssignments = null;
            return true;
        }

        generatedPair = new GeneratedPair(
            key: pair.SourceType.ToDisplayString(FullyQualifiedTypeFormat) + "->" + pair.DestinationType.ToDisplayString(FullyQualifiedTypeFormat),
            sourceTypeName: pair.SourceType.ToDisplayString(FullyQualifiedTypeFormat),
            destinationTypeName: pair.DestinationType.ToDisplayString(FullyQualifiedTypeFormat),
            assignments: assignments,
            useObjectInitializer: useObjectInitializer);

        // Build projection assignments (skip converter properties — they're not expression-tree compatible)
        generatedPair.ProjectionAssignments = BuildProjectionAssignments(pair, assignments, context);

        return true;
    }

    private static bool TryBuildPropertyAssignment(
        Compilation compilation,
        MapPairContext pair,
        int index,
        IndexedProperty sourceIndexed,
        IndexedProperty destinationIndexed,
        INamedTypeSymbol? mapValueConverterInterfaceSymbol,
        INamedTypeSymbol? listTypeSymbol,
        SourceProductionContext context,
        HashSet<TypePair> discoveredDependencies,
        ref bool hasErrors,
        out PropertyAssignment assignment)
    {
        var sourceProperty = sourceIndexed.Property;
        var destinationProperty = destinationIndexed.Property;
        var sourceAccess = "source." + EscapeIdentifier(sourceProperty.Name);
        var isInitOnly = destinationProperty.SetMethod?.IsInitOnly == true;

        if (destinationIndexed.ConverterType is not null)
        {
            if (!TryBuildConverterAssignment(
                sourceProperty,
                destinationProperty,
                destinationIndexed.ConverterType,
                mapValueConverterInterfaceSymbol,
                index,
                pair,
                context,
                out var converterExpression))
            {
                hasErrors = true;
                assignment = default;
                return false;
            }

            assignment = new PropertyAssignment(index, destinationProperty.Name, converterExpression, isInitOnly);
            return true;
        }

        var directConversion = compilation.ClassifyConversion(sourceProperty.Type, destinationProperty.Type);
        if (directConversion.Exists && directConversion.IsImplicit)
        {
            assignment = new PropertyAssignment(index, destinationProperty.Name, sourceAccess, isInitOnly);
            return true;
        }

        if (TryBuildCollectionAssignment(
            compilation,
            pair,
            index,
            sourceProperty,
            destinationProperty,
            sourceAccess,
            listTypeSymbol,
            context,
            discoveredDependencies,
            out var collectionExpression,
            out var collectionCandidate,
            out var inlineCollection))
        {
            assignment = inlineCollection is not null
                ? new PropertyAssignment(index, destinationProperty.Name, collectionExpression, inlineCollection, isInitOnly)
                : new PropertyAssignment(index, destinationProperty.Name, collectionExpression, isInitOnly);
            return true;
        }

        if (collectionCandidate)
        {
            hasErrors = true;
            assignment = default;
            return false;
        }

        if (TryBuildNestedAssignment(
            pair,
            sourceProperty,
            destinationProperty,
            index,
            context,
            discoveredDependencies,
            out var nestedExpression,
            out var nestedCandidate))
        {
            assignment = new PropertyAssignment(index, destinationProperty.Name, nestedExpression, isInitOnly);
            return true;
        }

        if (nestedCandidate)
        {
            hasErrors = true;
            assignment = default;
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            IncompatibleTypeDescriptor,
            GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
            index,
            pair.SourceType.ToDisplayString(),
            sourceProperty.Name,
            sourceProperty.Type.ToDisplayString(),
            pair.DestinationType.ToDisplayString(),
            destinationProperty.Name,
            destinationProperty.Type.ToDisplayString()));

        hasErrors = true;
        assignment = default;
        return false;
    }

    private static bool TryBuildConverterAssignment(
        IPropertySymbol sourceProperty,
        IPropertySymbol destinationProperty,
        INamedTypeSymbol converterType,
        INamedTypeSymbol? mapValueConverterInterfaceSymbol,
        int index,
        MapPairContext pair,
        SourceProductionContext context,
        out string expression)
    {
        expression = string.Empty;

        if (converterType.IsAbstract || converterType.TypeKind == TypeKind.Interface || !HasPublicParameterlessConstructor(converterType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ConverterInstantiationDescriptor,
                GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                converterType.ToDisplayString(),
                pair.DestinationType.ToDisplayString(),
                destinationProperty.Name));
            return false;
        }

        if (mapValueConverterInterfaceSymbol is null || !ImplementsConverterContract(converterType, mapValueConverterInterfaceSymbol, sourceProperty.Type, destinationProperty.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidConverterDescriptor,
                GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                pair.DestinationType.ToDisplayString(),
                destinationProperty.Name,
                converterType.ToDisplayString(),
                sourceProperty.Type.ToDisplayString(),
                destinationProperty.Type.ToDisplayString()));
            return false;
        }

        var converterTypeName = converterType.ToDisplayString(FullyQualifiedTypeFormat);
        var sourceTypeName = sourceProperty.Type.ToDisplayString(FullyQualifiedTypeFormat);
        var destinationTypeName = destinationProperty.Type.ToDisplayString(FullyQualifiedTypeFormat);
        var sourceAccess = "source." + EscapeIdentifier(sourceProperty.Name);

        expression = $"ConvertWith<{converterTypeName}, {sourceTypeName}, {destinationTypeName}>({sourceAccess})";

        return true;
    }

    private static bool TryBuildCollectionAssignment(
        Compilation compilation,
        MapPairContext pair,
        int index,
        IPropertySymbol sourceProperty,
        IPropertySymbol destinationProperty,
        string sourceAccess,
        INamedTypeSymbol? listTypeSymbol,
        SourceProductionContext context,
        HashSet<TypePair> discoveredDependencies,
        out string expression,
        out bool collectionCandidate,
        out InlineCollectionData? inlineCollection)
    {
        expression = string.Empty;
        collectionCandidate = false;
        inlineCollection = null;

        if (!TryGetEnumerableElementType(sourceProperty.Type, out var sourceElementType)
            || !TryGetEnumerableElementType(destinationProperty.Type, out var destinationElementType))
        {
            return false;
        }

        collectionCandidate = true;

        if (!TryBuildCollectionElementProjection(
            compilation,
            pair,
            index,
            sourceProperty,
            destinationProperty,
            sourceElementType,
            destinationElementType,
            context,
            discoveredDependencies,
            out var projection,
            out var srcElemFqnForInline,
            out var dstElemFqnForInline,
            out var elemCanBeNullForInline))
        {
            return false;
        }

        if (destinationProperty.Type is IArrayTypeSymbol)
        {
            expression = $"MapCollectionToArray<{sourceElementType.ToDisplayString(FullyQualifiedTypeFormat)}, {destinationElementType.ToDisplayString(FullyQualifiedTypeFormat)}>({sourceAccess}, static item => {projection})";
            return true;
        }

        if (listTypeSymbol is not null)
        {
            var constructedList = listTypeSymbol.Construct(destinationElementType);
            var listConversion = compilation.ClassifyConversion(constructedList, destinationProperty.Type);
            if (listConversion.Exists && listConversion.IsImplicit)
            {
                var srcIsConcreteList = sourceProperty.Type is INamedTypeSymbol srcNamed
                    && SymbolEqualityComparer.Default.Equals(srcNamed.OriginalDefinition, listTypeSymbol);
                var destIsConcreteList = destinationProperty.Type is INamedTypeSymbol dstNamed
                    && SymbolEqualityComparer.Default.Equals(dstNamed.OriginalDefinition, listTypeSymbol);
                var listHelperName = srcIsConcreteList && destIsConcreteList ? "MapListToList" : "MapCollectionToList";
                expression = $"{listHelperName}<{sourceElementType.ToDisplayString(FullyQualifiedTypeFormat)}, {destinationElementType.ToDisplayString(FullyQualifiedTypeFormat)}>({sourceAccess}, static item => {projection})";
                // For List<T>-to-List<D> with a nested-type element, record inline loop data so emission
                // can generate a direct for-loop instead of a delegate-based helper call.
                if (srcIsConcreteList && destIsConcreteList && srcElemFqnForInline is not null)
                {
                    inlineCollection = new InlineCollectionData(sourceAccess, srcElemFqnForInline, dstElemFqnForInline!, elemCanBeNullForInline);
                }

                return true;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(
            UnsupportedCollectionDestinationDescriptor,
            GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
            pair.DestinationType.ToDisplayString(),
            destinationProperty.Name,
            destinationProperty.Type.ToDisplayString()));

        return false;
    }

    private static bool TryBuildCollectionElementProjection(
        Compilation compilation,
        MapPairContext pair,
        int index,
        IPropertySymbol sourceProperty,
        IPropertySymbol destinationProperty,
        ITypeSymbol sourceElementType,
        ITypeSymbol destinationElementType,
        SourceProductionContext context,
        HashSet<TypePair> discoveredDependencies,
        out string projection,
        out string? normalizedSrcElemFqn,
        out string? normalizedDstElemFqn,
        out bool elemCanBeNull)
    {
        projection = string.Empty;
        normalizedSrcElemFqn = null;
        normalizedDstElemFqn = null;
        elemCanBeNull = false;

        var elementConversion = compilation.ClassifyConversion(sourceElementType, destinationElementType);
        if (elementConversion.Exists && elementConversion.IsImplicit)
        {
            projection = "item";
            return true;
        }

        if (!IsNestedCandidate(sourceElementType, destinationElementType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                IncompatibleTypeDescriptor,
                GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                index,
                pair.SourceType.ToDisplayString(),
                sourceProperty.Name,
                sourceProperty.Type.ToDisplayString(),
                pair.DestinationType.ToDisplayString(),
                destinationProperty.Name,
                destinationProperty.Type.ToDisplayString()));
            return false;
        }

        if (!CanConstructDestinationType(destinationElementType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DestinationConstructionDescriptor,
                GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                destinationElementType.ToDisplayString(),
                index));
            return false;
        }

        var normalizedSourceElementType = NormalizeTypeForPair(sourceElementType);
        var normalizedDestinationElementType = NormalizeTypeForPair(destinationElementType);

        var sourceElementTypeName = normalizedSourceElementType.ToDisplayString(FullyQualifiedTypeFormat);
        var destinationElementTypeName = normalizedDestinationElementType.ToDisplayString(FullyQualifiedTypeFormat);
        discoveredDependencies.Add(new TypePair(
            normalizedSourceElementType,
            normalizedDestinationElementType));

        elemCanBeNull = CanBeNull(sourceElementType);
        normalizedSrcElemFqn = sourceElementTypeName;
        normalizedDstElemFqn = destinationElementTypeName;

        if (elemCanBeNull)
        {
            projection = $"item is null ? default! : MapKnown<{sourceElementTypeName}, {destinationElementTypeName}>(item)";
        }
        else
        {
            projection = $"MapKnown<{sourceElementTypeName}, {destinationElementTypeName}>(item)";
        }

        return true;
    }

    private static bool TryBuildNestedAssignment(
        MapPairContext pair,
        IPropertySymbol sourceProperty,
        IPropertySymbol destinationProperty,
        int index,
        SourceProductionContext context,
        HashSet<TypePair> discoveredDependencies,
        out string expression,
        out bool nestedCandidate)
    {
        expression = string.Empty;
        nestedCandidate = false;

        if (!IsNestedCandidate(sourceProperty.Type, destinationProperty.Type))
        {
            return false;
        }

        nestedCandidate = true;

        if (!CanConstructDestinationType(destinationProperty.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DestinationConstructionDescriptor,
                GetLocationOrFallback(destinationProperty, pair.InvocationLocation),
                destinationProperty.Type.ToDisplayString(),
                index));
            return false;
        }

        var normalizedSourceType = NormalizeTypeForPair(sourceProperty.Type);
        var normalizedDestinationType = NormalizeTypeForPair(destinationProperty.Type);

        var sourceTypeName = normalizedSourceType.ToDisplayString(FullyQualifiedTypeFormat);
        var destinationTypeName = normalizedDestinationType.ToDisplayString(FullyQualifiedTypeFormat);
        var sourceAccess = "source." + EscapeIdentifier(sourceProperty.Name);
        discoveredDependencies.Add(new TypePair(
            normalizedSourceType,
            normalizedDestinationType));

        if (CanBeNull(sourceProperty.Type))
        {
            expression = $"{sourceAccess} is null ? default : MapKnown<{sourceTypeName}, {destinationTypeName}>({sourceAccess})";
        }
        else
        {
            expression = $"MapKnown<{sourceTypeName}, {destinationTypeName}>({sourceAccess})";
        }

        return true;
    }

    private static IReadOnlyList<ProjectionAssignment>? BuildProjectionAssignments(
        MapPairContext pair,
        IReadOnlyList<PropertyAssignment> assignments,
        SourceProductionContext context)
    {
        var projections = new List<ProjectionAssignment>(assignments.Count);

        foreach (var assignment in assignments)
        {
            var valueExpr = assignment.ValueExpression;

            // Skip converter-based assignments — not translatable to expression trees
            if (valueExpr.StartsWith("ConvertWith<", StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ProjectionConverterSkippedDescriptor,
                    pair.InvocationLocation,
                    pair.DestinationType.ToDisplayString(),
                    assignment.DestinationPropertyName));
                continue;
            }

            // For inline collections we need a .Select().ToList() expression
            if (assignment.InlineCollection is { } inline)
            {
                // Build: source.Prop == null ? null : source.Prop.Select(__e => ProjectPair_N(__e)).ToList()
                // We'll use a placeholder that gets substituted later just like MapKnown substitution
                var projExpr = BuildProjectionCollectionExpression(inline.SourceAccess, inline.SourceElemFqn, inline.DestElemFqn, inline.ElemCanBeNull);
                projections.Add(new ProjectionAssignment(assignment.DestinationPropertyName, projExpr));
                continue;
            }

            // Standard value expression — replace MapKnown<A,B>(x) with inline member-init call placeholder
            // We keep the expression but replace MapKnown with ProjectKnown so the generator can substitute
            var projValue = valueExpr.Replace("MapKnown<", "ProjectKnown<");
            // Expression trees don't support 'is null' / 'is not null' pattern matching — replace with == / !=
            projValue = projValue.Replace(" is null ?", " == null ?");
            projValue = projValue.Replace(" is null ", " == null ");
            projValue = projValue.Replace(" is not null ", " != null ");
            // Replace "? default :" with "? null :" so expression trees can compile (for reference types)
            projValue = projValue.Replace("? default :", "? null :");
            projections.Add(new ProjectionAssignment(assignment.DestinationPropertyName, projValue));
        }

        return projections;
    }

    private static string BuildProjectionCollectionExpression(string sourceAccess, string srcElemFqn, string dstElemFqn, bool elemCanBeNull)
    {
        if (elemCanBeNull)
        {
            return $"{sourceAccess} == null ? null : {sourceAccess}.Select(static __e => __e == null ? default({dstElemFqn})! : ProjectKnown<{srcElemFqn}, {dstElemFqn}>(__e)).ToList()";
        }
        return $"{sourceAccess} == null ? null : {sourceAccess}.Select(static __e => ProjectKnown<{srcElemFqn}, {dstElemFqn}>(__e)).ToList()";
    }

    private static IEnumerable<IPropertySymbol> GetAllProperties(ITypeSymbol type)
    {
        var current = type;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current is INamedTypeSymbol namedType)
        {
            foreach (var member in namedType.GetMembers().OfType<IPropertySymbol>())
            {
                // Derived-class declaration takes priority; skip base-class duplicate names.
                if (seen.Add(member.Name))
                    yield return member;
            }
            current = namedType.BaseType;
        }
    }

    private static Dictionary<int, IndexedProperty> CollectIndexedProperties(
        ITypeSymbol type,
        INamedTypeSymbol mapIndexAttributeSymbol,
        INamedTypeSymbol? mapConverterAttributeSymbol,
        Location fallbackLocation,
        DiagnosticDescriptor duplicateDescriptor,
        DiagnosticDescriptor accessorDescriptor,
        bool isDestination,
        SourceProductionContext context,
        ref bool hasErrors)
    {
        var indexedProperties = new Dictionary<int, IndexedProperty>();

        foreach (var property in GetAllProperties(type))
        {
            if (!TryGetMapIndex(property, mapIndexAttributeSymbol, out var index))
            {
                continue;
            }

            if (index < 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIndexDescriptor,
                    GetLocationOrFallback(property, fallbackLocation),
                    type.ToDisplayString(),
                    property.Name,
                    index));
                hasErrors = true;
                continue;
            }

            if (property.IsStatic)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StaticIndexedPropertyDescriptor,
                    GetLocationOrFallback(property, fallbackLocation),
                    type.ToDisplayString(),
                    property.Name,
                    index));
                hasErrors = true;
                continue;
            }

            if (isDestination)
            {
                if (property.SetMethod is null || property.SetMethod.DeclaredAccessibility != Accessibility.Public)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        accessorDescriptor,
                        GetLocationOrFallback(property, fallbackLocation),
                        type.ToDisplayString(),
                        property.Name,
                        index));
                    hasErrors = true;
                    continue;
                }
            }
            else
            {
                if (property.GetMethod is null || property.GetMethod.DeclaredAccessibility != Accessibility.Public)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        accessorDescriptor,
                        GetLocationOrFallback(property, fallbackLocation),
                        type.ToDisplayString(),
                        property.Name,
                        index));
                    hasErrors = true;
                    continue;
                }
            }

            var converterType = isDestination && mapConverterAttributeSymbol is not null
                ? TryGetMapConverterType(property, mapConverterAttributeSymbol)
                : null;

            if (indexedProperties.ContainsKey(index))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    duplicateDescriptor,
                    GetLocationOrFallback(property, fallbackLocation),
                    type.ToDisplayString(),
                    index));
                hasErrors = true;
            }
            else
            {
                indexedProperties.Add(index, new IndexedProperty(property, converterType));
            }
        }

        return indexedProperties;
    }

    private static bool TryGetMapIndex(IPropertySymbol property, INamedTypeSymbol mapIndexAttributeSymbol, out int index)
    {
        foreach (var attributeData in property.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attributeData.AttributeClass, mapIndexAttributeSymbol))
            {
                continue;
            }

            if (attributeData.ConstructorArguments.Length == 1
                && attributeData.ConstructorArguments[0].Value is int intValue)
            {
                index = intValue;
                return true;
            }
        }

        index = default;
        return false;
    }

    private static INamedTypeSymbol? TryGetMapConverterType(IPropertySymbol property, INamedTypeSymbol mapConverterAttributeSymbol)
    {
        foreach (var attributeData in property.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attributeData.AttributeClass, mapConverterAttributeSymbol))
            {
                continue;
            }

            if (attributeData.ConstructorArguments.Length == 1
                && attributeData.ConstructorArguments[0].Value is INamedTypeSymbol converterType)
            {
                return converterType;
            }
        }

        return null;
    }

    private static bool TryGetStringAttributeArg(IPropertySymbol property, INamedTypeSymbol attributeSymbol, out string value)
    {
        foreach (var attributeData in property.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attributeData.AttributeClass, attributeSymbol))
                continue;

            if (attributeData.ConstructorArguments.Length == 1
                && attributeData.ConstructorArguments[0].Value is string s
                && !string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool ImplementsConverterContract(
        INamedTypeSymbol converterType,
        INamedTypeSymbol converterInterface,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType)
    {
        foreach (var contract in converterType.AllInterfaces)
        {
            if (!contract.IsGenericType)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, converterInterface))
            {
                continue;
            }

            var genericArguments = contract.TypeArguments;
            if (genericArguments.Length != 2)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(genericArguments[0], sourceType)
                && SymbolEqualityComparer.Default.Equals(genericArguments[1], destinationType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.IsValueType)
        {
            return true;
        }

        return type.InstanceConstructors.Any(constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public
            && constructor.Parameters.Length == 0);
    }

    private static bool IsNestedCandidate(ITypeSymbol sourceType, ITypeSymbol destinationType)
    {
        if (sourceType.SpecialType == SpecialType.System_String || destinationType.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (TryGetEnumerableElementType(sourceType, out _) || TryGetEnumerableElementType(destinationType, out _))
        {
            return false;
        }

        return sourceType.TypeKind is TypeKind.Class or TypeKind.Struct
            && destinationType.TypeKind is TypeKind.Class or TypeKind.Struct;
    }

    private static bool TryGetEnumerableElementType(ITypeSymbol type, out ITypeSymbol elementType)
    {
        elementType = null!;

        if (type.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (type is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && namedType.TypeArguments.Length == 1)
        {
            var candidate = namedType.TypeArguments[0];
            foreach (var interfaceType in namedType.AllInterfaces)
            {
                if (interfaceType.IsGenericType
                    && interfaceType.ConstructUnboundGenericType().ToDisplayString() == "System.Collections.Generic.IEnumerable<>")
                {
                    elementType = candidate;
                    return true;
                }
            }

            if (namedType.ConstructUnboundGenericType().ToDisplayString() == "System.Collections.Generic.IEnumerable<>")
            {
                elementType = candidate;
                return true;
            }
        }

        foreach (var interfaceType in type.AllInterfaces)
        {
            if (interfaceType.IsGenericType
                && interfaceType.ConstructUnboundGenericType().ToDisplayString() == "System.Collections.Generic.IEnumerable<>")
            {
                elementType = interfaceType.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private static bool CanConstructDestinationType(ITypeSymbol destinationType)
    {
        if (destinationType.IsValueType)
        {
            return true;
        }

        if (destinationType is not INamedTypeSymbol namedType)
        {
            return false;
        }

        return namedType.InstanceConstructors.Any(constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public
            && constructor.Parameters.Length == 0);
    }

    /// <summary>
    /// Returns the primary constructor of a positional record: the public constructor whose
    /// parameters correspond to the record's positional properties (not the copy constructor).
    /// Returns null if the type is not a positional record or has no suitable constructor.
    /// </summary>
    private static IMethodSymbol? GetPositionalRecordPrimaryConstructor(INamedTypeSymbol type)
    {
        if (!type.IsRecord)
        {
            return null;
        }

        // The copy constructor has exactly one parameter of the same record type.
        // The primary constructor is any other public constructor with at least one parameter.
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (ctor.Parameters.Length == 0)
            {
                continue;
            }

            // Skip the compiler-generated copy constructor
            if (ctor.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type))
            {
                continue;
            }

            return ctor;
        }

        return null;
    }

    private static bool CanBeNull(ITypeSymbol type)
    {
        if (type.IsReferenceType)
        {
            return true;
        }

        return type is INamedTypeSymbol namedType
            && namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private static Location GetLocationOrFallback(ISymbol symbol, Location fallback)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                return location;
            }
        }

        return fallback;
    }

    private static string GenerateResolverSource(IReadOnlyList<GeneratedPair> pairs)
    {
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Linq;");
        builder.AppendLine("using System.Linq.Expressions;");
        builder.AppendLine();
        builder.AppendLine("namespace MappShark.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    internal static class IndexedMapResolver");
        builder.AppendLine("    {");
        // Untyped mappers: nested class so the dictionary is only initialized when TryResolve (untyped path) is first called,
        // not when TryResolveTyped (typed hot path) is first called. Avoids creating N box delegates at startup.
        builder.AppendLine("        private static class UntypedMappersCache");
        builder.AppendLine("        {");
        builder.AppendLine("            internal static readonly Dictionary<(Type Source, Type Destination), Func<object, object>> Mappers = new()");
        builder.AppendLine("            {");

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var trailingComma = i == pairs.Count - 1 ? string.Empty : ",";

            builder.Append("                [(typeof(");
            builder.Append(pair.SourceTypeName);
            builder.Append("), typeof(");
            builder.Append(pair.DestinationTypeName);
            builder.Append("))] = static source => ");
            builder.Append(pair.MethodName);
            builder.Append("((");
            builder.Append(pair.SourceTypeName);
            builder.Append(")source)");
            builder.AppendLine(trailingComma);
        }

        builder.AppendLine("            };");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public static bool TryResolve(Type sourceType, Type destinationType, out Func<object, object> mapper)");
        builder.AppendLine("        {");
        builder.AppendLine("            return UntypedMappersCache.Mappers.TryGetValue((sourceType, destinationType), out mapper!);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public static bool TryResolveTyped<TSource, TDestination>(out Func<TSource, TDestination> mapper)");
        builder.AppendLine("            where TDestination : new()");
        builder.AppendLine("        {");

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            // Positional records use the untyped path (no new() constraint) — skip TryResolveTyped
            if (pair.UseConstructorCall) continue;
            builder.Append("            if (typeof(TSource) == typeof(");
            builder.Append(pair.SourceTypeName);
            builder.Append(") && typeof(TDestination) == typeof(");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine("))");
            builder.AppendLine("            {");
            builder.Append("                mapper = (Func<TSource, TDestination>)(object)(Func<");
            builder.Append(pair.SourceTypeName);
            builder.Append(", ");
            builder.Append(pair.DestinationTypeName);
            builder.Append(">)");
            builder.Append(pair.MethodName);
            builder.AppendLine(";");
            builder.AppendLine("                return true;");
            builder.AppendLine("            }");
        }

        builder.AppendLine();
        builder.AppendLine("            mapper = default!;");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        // TryResolveProjection
        builder.AppendLine("        public static bool TryResolveProjection<TSource, TDestination>(out Expression<Func<TSource, TDestination>> projection)");
        builder.AppendLine("            where TDestination : new()");
        builder.AppendLine("        {");

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.UseConstructorCall) continue; // positional records have no projection support
            if (pair.ProjectionAssignments is null)
                continue;
            builder.Append("            if (typeof(TSource) == typeof(");
            builder.Append(pair.SourceTypeName);
            builder.Append(") && typeof(TDestination) == typeof(");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine("))");
            builder.AppendLine("            {");
            builder.Append("                projection = (Expression<Func<TSource, TDestination>>)(object)");
            builder.Append("ProjectionCache_");
            builder.Append(pair.MethodName);
            builder.AppendLine(".Expr;");
            builder.AppendLine("                return true;");
            builder.AppendLine("            }");
        }

        builder.AppendLine();
        builder.AppendLine("            projection = default!;");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        // ProjectKnown helper (evaluates at runtime via the projection cache, not for expression tree nesting — 
        // nested projections are inlined during code generation)
        builder.AppendLine("        private static TDestination MapKnown<TSource, TDestination>(TSource source)");
        builder.AppendLine("            where TDestination : new()");
        builder.AppendLine("        {");
        builder.AppendLine("            return KnownMapCache<TSource, TDestination>.Map(source);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static class KnownMapCache<TSource, TDestination>");
        builder.AppendLine("            where TDestination : new()");
        builder.AppendLine("        {");
        builder.AppendLine("            public static readonly Func<TSource, TDestination> Map = Resolve();");
        builder.AppendLine();
        builder.AppendLine("            private static Func<TSource, TDestination> Resolve()");
        builder.AppendLine("            {");

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            // KnownMapCache has where TDestination : new() — positional records use the untyped path
            if (pair.UseConstructorCall) continue;
            builder.Append("                if (typeof(TSource) == typeof(");
            builder.Append(pair.SourceTypeName);
            builder.Append(") && typeof(TDestination) == typeof(");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine("))");
            builder.AppendLine("                {");
            builder.Append("                    return (Func<TSource, TDestination>)(object)(Func<");
            builder.Append(pair.SourceTypeName);
            builder.Append(", ");
            builder.Append(pair.DestinationTypeName);
            builder.Append(">)");
            builder.Append(pair.MethodName);
            builder.AppendLine(";");
            builder.AppendLine("                }");
        }

        builder.AppendLine();
        builder.AppendLine("                return static source => global::MappShark.Mapper.Map<TSource, TDestination>(source!);");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static List<TDestinationElement>? MapCollectionToList<TSourceElement, TDestinationElement>(IEnumerable<TSourceElement>? source, Func<TSourceElement, TDestinationElement> projector)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (source is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                return null;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (source is ICollection<TSourceElement> sourceCollection)");
        builder.AppendLine("            {");
        builder.AppendLine("                var result = new List<TDestinationElement>(sourceCollection.Count);");
        builder.AppendLine("                foreach (var item in sourceCollection)");
        builder.AppendLine("                {");
        builder.AppendLine("                    result.Add(projector(item));");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                return result;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            var list = new List<TDestinationElement>();");
        builder.AppendLine("            foreach (var item in source)");
        builder.AppendLine("            {");
        builder.AppendLine("                list.Add(projector(item));");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return list;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static TDestinationElement[]? MapCollectionToArray<TSourceElement, TDestinationElement>(IEnumerable<TSourceElement>? source, Func<TSourceElement, TDestinationElement> projector)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (source is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                return null;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (source is ICollection<TSourceElement> sourceCollection)");
        builder.AppendLine("            {");
        builder.AppendLine("                var result = new TDestinationElement[sourceCollection.Count];");
        builder.AppendLine("                var index = 0;");
        builder.AppendLine("                foreach (var item in sourceCollection)");
        builder.AppendLine("                {");
        builder.AppendLine("                    result[index++] = projector(item);");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                return result;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            var list = new List<TDestinationElement>();");
        builder.AppendLine("            foreach (var item in source)");
        builder.AppendLine("            {");
        builder.AppendLine("                list.Add(projector(item));");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return list.ToArray();");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static List<TDestinationElement>? MapListToList<TSourceElement, TDestinationElement>(List<TSourceElement>? source, Func<TSourceElement, TDestinationElement> projector)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (source is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                return null;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            var result = new List<TDestinationElement>(source.Count);");
        builder.AppendLine("            for (var i = 0; i < source.Count; i++)");
        builder.AppendLine("            {");
        builder.AppendLine("                result.Add(projector(source[i]));");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return result;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static TDestinationMember ConvertWith<TConverter, TSourceMember, TDestinationMember>(TSourceMember value)");
        builder.AppendLine("            where TConverter : global::MappShark.IMapValueConverter<TSourceMember, TDestinationMember>, new()");
        builder.AppendLine("        {");
        builder.AppendLine("            return ConverterCache<TConverter, TSourceMember, TDestinationMember>.Instance.Convert(value);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static class ConverterCache<TConverter, TSourceMember, TDestinationMember>");
        builder.AppendLine("            where TConverter : global::MappShark.IMapValueConverter<TSourceMember, TDestinationMember>, new()");
        builder.AppendLine("        {");
        builder.AppendLine("            public static readonly TConverter Instance = new();");
        builder.AppendLine("        }");

        // Build direct-call substitution map: "MapKnown<SrcFQN, DstFQN>(" → "MethodName("
        var directCallMap = new Dictionary<string, string>(pairs.Count);
        foreach (var pair in pairs)
        {
            directCallMap[$"MapKnown<{pair.SourceTypeName}, {pair.DestinationTypeName}>("] = pair.MethodName + "(";
        }

        foreach (var pair in pairs)
        {
            builder.AppendLine();
            builder.Append("        private static ");
            builder.Append(pair.DestinationTypeName);
            builder.Append(' ');
            builder.Append(pair.MethodName);
            builder.Append("(");
            builder.Append(pair.SourceTypeName);
            builder.AppendLine(" source)");
            builder.AppendLine("        {");

            if (pair.UseObjectInitializer)
            {
                // Object initializer syntax for init-only properties (records with explicit init setters).
                // Inline-collection optimization is not used here — ValueExpression is always a single expression.
                builder.Append("            return new ");
                builder.AppendLine(pair.DestinationTypeName);
                builder.AppendLine("            {");

                foreach (var assignment in pair.Assignments)
                {
                    builder.Append("                ");
                    builder.Append(EscapeIdentifier(assignment.DestinationPropertyName));
                    builder.Append(" = ");
                    var valueExprInit = assignment.ValueExpression;
                    foreach (var kv in directCallMap)
                        valueExprInit = valueExprInit.Replace(kv.Key, kv.Value);
                    foreach (var kv in directCallMap)
                    {
                        var methodRef = kv.Value.Substring(0, kv.Value.Length - 1);
                        valueExprInit = valueExprInit.Replace("static item => " + methodRef + "(item)", methodRef);
                    }
                    builder.Append(valueExprInit);
                    builder.AppendLine(",");
                }

                builder.AppendLine("            };");
                builder.AppendLine("        }");
            }
            else if (pair.UseConstructorCall)
            {
                // Constructor-call syntax for positional records (no parameterless constructor).
                // Assignments are ordered to match the constructor parameter positions.
                builder.Append("            return new ");
                builder.Append(pair.DestinationTypeName);
                builder.AppendLine("(");

                for (var assignIdx = 0; assignIdx < pair.Assignments.Count; assignIdx++)
                {
                    var assignment = pair.Assignments[assignIdx];
                    builder.Append("                ");
                    builder.Append(EscapeIdentifier(assignment.DestinationPropertyName));
                    builder.Append(": ");
                    var valueExprCtor = assignment.ValueExpression;
                    foreach (var kv in directCallMap)
                        valueExprCtor = valueExprCtor.Replace(kv.Key, kv.Value);
                    foreach (var kv in directCallMap)
                    {
                        var methodRef = kv.Value.Substring(0, kv.Value.Length - 1);
                        valueExprCtor = valueExprCtor.Replace("static item => " + methodRef + "(item)", methodRef);
                    }
                    builder.Append(valueExprCtor);
                    if (assignIdx < pair.Assignments.Count - 1)
                    {
                        builder.AppendLine(",");
                    }
                    else
                    {
                        builder.AppendLine(");");
                    }
                }

                builder.AppendLine("        }");
            }
            else
            {
                builder.Append("            var destination = new ");
                builder.Append(pair.DestinationTypeName);
                builder.AppendLine("();");

                foreach (var assignment in pair.Assignments)
                {
                    // Inline List<T>→List<D> loop: eliminates per-element delegate call overhead
                    if (assignment.InlineCollection is { } inline)
                    {
                        var lookupKey = $"MapKnown<{inline.SourceElemFqn}, {inline.DestElemFqn}>(";
                        if (directCallMap.TryGetValue(lookupKey, out var elemMethodWithParen))
                        {
                            var elemMethod = elemMethodWithParen.Substring(0, elemMethodWithParen.Length - 1);
                            var propName = EscapeIdentifier(assignment.DestinationPropertyName);
                            builder.AppendLine("            {");
                            builder.AppendLine("                var __src = " + inline.SourceAccess + ";");
                            builder.AppendLine("                if (__src is null)");
                            builder.AppendLine("                {");
                            builder.AppendLine("                    destination." + propName + " = null;");
                            builder.AppendLine("                }");
                            builder.AppendLine("                else");
                            builder.AppendLine("                {");
                            builder.AppendLine("                    var __result = new List<" + inline.DestElemFqn + ">(__src.Count);");
                            builder.AppendLine("                    for (var __i = 0; __i < __src.Count; __i++)");
                            builder.AppendLine("                    {");
                            if (inline.ElemCanBeNull)
                            {
                                builder.AppendLine("                        var __elem = __src[__i];");
                                builder.AppendLine("                        __result.Add(__elem is null ? default! : " + elemMethod + "(__elem));");
                            }
                            else
                            {
                                builder.AppendLine("                        __result.Add(" + elemMethod + "(__src[__i]));");
                            }
                            builder.AppendLine("                    }");
                            builder.AppendLine("                    destination." + propName + " = __result;");
                            builder.AppendLine("                }");
                            builder.AppendLine("            }");
                            continue;
                        }
                    }

                    builder.Append("            destination.");
                    builder.Append(EscapeIdentifier(assignment.DestinationPropertyName));
                    builder.Append(" = ");
                    var valueExpr = assignment.ValueExpression;
                    // Replace MapKnown<T,D>( with direct MapPair_N( calls
                    foreach (var kv in directCallMap)
                        valueExpr = valueExpr.Replace(kv.Key, kv.Value);
                    // Simplify "static item => MapPair_N(item)" → "MapPair_N" (method group, no lambda wrapper)
                    foreach (var kv in directCallMap)
                    {
                        var methodRef = kv.Value.Substring(0, kv.Value.Length - 1);
                        valueExpr = valueExpr.Replace("static item => " + methodRef + "(item)", methodRef);
                    }
                    builder.Append(valueExpr);
                    builder.AppendLine(";");
                }

                builder.AppendLine("            return destination;");
                builder.AppendLine("        }");
            }
        }

        // Build direct-call map for projections (ProjectKnown<A,B>( → ProjectPair_N()
        // We'll inline the member-init expressions directly into projection methods
        var projectionDirectCallMap = new Dictionary<string, string>(pairs.Count);
        foreach (var pair in pairs)
        {
            if (pair.ProjectionAssignments is not null)
                projectionDirectCallMap[$"ProjectKnown<{pair.SourceTypeName}, {pair.DestinationTypeName}>("] = "Inline_" + pair.MethodName + "(";
        }

        // Build a map for recursive inlining: "ProjectKnown<A,B>(expr)" → "new B { X = expr.X, ... }"
        // keyed by "ProjectKnown<A,B>("
        var projInlineBuilders = new Dictionary<string, (string DestTypeName, IReadOnlyList<ProjectionAssignment> Assignments)>();
        foreach (var pair in pairs)
        {
            if (pair.ProjectionAssignments is not null)
                projInlineBuilders[$"ProjectKnown<{pair.SourceTypeName}, {pair.DestinationTypeName}>("] = (pair.DestinationTypeName, pair.ProjectionAssignments);
        }

        // Emit ProjectionCache_<N> lazy classes + ProjectPair_N projection methods
        foreach (var pair in pairs)
        {
            if (pair.ProjectionAssignments is null)
                continue;

            // Projection cache (lazy static readonly)
            builder.AppendLine();
            builder.Append("        private static class ProjectionCache_");
            builder.AppendLine(pair.MethodName);
            builder.AppendLine("        {");
            builder.Append("            public static readonly global::System.Linq.Expressions.Expression<global::System.Func<");
            builder.Append(pair.SourceTypeName);
            builder.Append(", ");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine(">> Expr = Build();");
            builder.AppendLine();
            builder.Append("            private static global::System.Linq.Expressions.Expression<global::System.Func<");
            builder.Append(pair.SourceTypeName);
            builder.Append(", ");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine(">> Build()");
            builder.AppendLine("            {");
            builder.Append("                return static source => new ");
            builder.Append(pair.DestinationTypeName);
            builder.AppendLine();
            builder.AppendLine("                {");

            foreach (var proj in pair.ProjectionAssignments)
            {
                var projExpr = BuildInlinedProjectionExpression(proj.ValueExpression, projInlineBuilders, depth: 0);
                builder.Append("                    ");
                builder.Append(EscapeIdentifier(proj.DestinationPropertyName));
                builder.Append(" = ");
                builder.Append(projExpr);
                builder.AppendLine(",");
            }

            builder.AppendLine("                };");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>
    /// Recursively inlines <c>ProjectKnown&lt;A,B&gt;(x)</c> calls in a projection expression
    /// with <c>new B { Prop = x.Prop, ... }</c> member-init syntax safe for expression trees.
    /// </summary>
    private static string BuildInlinedProjectionExpression(
        string expression,
        Dictionary<string, (string DestTypeName, IReadOnlyList<ProjectionAssignment> Assignments)> builders,
        int depth)
    {
        if (depth > 10)
            return expression; // guard against cycles

        var result = expression;
        foreach (var kv in builders)
        {
            var placeholder = kv.Key; // "ProjectKnown<A,B>("
            var destTypeName = kv.Value.DestTypeName;
            var assignments = kv.Value.Assignments;

            var idx = result.IndexOf(placeholder, StringComparison.Ordinal);
            while (idx >= 0)
            {
                // Find matching closing paren for the argument
                var argStart = idx + placeholder.Length;
                var argEnd = FindMatchingParen(result, argStart - 1);
                if (argEnd < 0)
                    break;

                var argExpr = result.Substring(argStart, argEnd - argStart);

                // Build inline member-init: argExpr == null ? default : new DestType { P = argExpr.P, ... }
                var memberInits = new StringBuilder();
                memberInits.Append("new ");
                memberInits.Append(destTypeName);
                memberInits.Append(" { ");
                for (var ai = 0; ai < assignments.Count; ai++)
                {
                    var a = assignments[ai];
                    // Remap "source." in nested projection to the actual arg expression
                    var nestedExpr = a.ValueExpression.Replace("source.", argExpr + ".");
                    nestedExpr = BuildInlinedProjectionExpression(nestedExpr, builders, depth + 1);
                    memberInits.Append(EscapeIdentifier(a.DestinationPropertyName));
                    memberInits.Append(" = ");
                    memberInits.Append(nestedExpr);
                    if (ai < assignments.Count - 1) memberInits.Append(", ");
                }
                memberInits.Append(" }");

                // Wrap with null check if arg can be null (heuristic: if original expression had null check)
                var inlined = memberInits.ToString();

                result = result.Substring(0, idx) + inlined + result.Substring(argEnd + 1);
                idx = result.IndexOf(placeholder, idx + inlined.Length, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;
    }

    private readonly struct MapInvocationInfo
    {
        public MapInvocationInfo(ITypeSymbol sourceType, ITypeSymbol destinationType, Location invocationLocation, bool bothWays = false)
        {
            SourceType = sourceType;
            DestinationType = destinationType;
            InvocationLocation = invocationLocation;
            BothWays = bothWays;
        }

        public ITypeSymbol SourceType { get; }

        public ITypeSymbol DestinationType { get; }

        public Location InvocationLocation { get; }

        public bool BothWays { get; }
    }

    private readonly struct TypePair
    {
        public TypePair(ITypeSymbol source, ITypeSymbol destination)
        {
            Source = source;
            Destination = destination;
        }

        public ITypeSymbol Source { get; }

        public ITypeSymbol Destination { get; }
    }

    private sealed class TypePairComparer : IEqualityComparer<TypePair>
    {
        public static readonly TypePairComparer Instance = new();

        public bool Equals(TypePair x, TypePair y)
        {
            return SymbolEqualityComparer.Default.Equals(x.Source, y.Source)
                && SymbolEqualityComparer.Default.Equals(x.Destination, y.Destination);
        }

        public int GetHashCode(TypePair obj)
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + SymbolEqualityComparer.Default.GetHashCode(obj.Source);
                hash = (hash * 31) + SymbolEqualityComparer.Default.GetHashCode(obj.Destination);
                return hash;
            }
        }
    }

    private sealed class MapPairContext
    {
        public MapPairContext(ITypeSymbol sourceType, ITypeSymbol destinationType, Location invocationLocation)
        {
            SourceType = sourceType;
            DestinationType = destinationType;
            InvocationLocation = invocationLocation;
        }

        public ITypeSymbol SourceType { get; }

        public ITypeSymbol DestinationType { get; }

        public Location InvocationLocation { get; }
    }

    private sealed class GeneratedPair
    {
        public GeneratedPair(string key, string sourceTypeName, string destinationTypeName, IReadOnlyList<PropertyAssignment> assignments, bool useObjectInitializer = false, bool useConstructorCall = false)
        {
            Key = key;
            SourceTypeName = sourceTypeName;
            DestinationTypeName = destinationTypeName;
            Assignments = assignments;
            ProjectionAssignments = null;
            MethodName = string.Empty;
            UseObjectInitializer = useObjectInitializer;
            UseConstructorCall = useConstructorCall;
        }

        public string Key { get; }

        public string SourceTypeName { get; }

        public string DestinationTypeName { get; }

        public IReadOnlyList<PropertyAssignment> Assignments { get; }

        /// <summary>True when any mapped destination property uses an init-only setter. The generated mapper method will use object-initializer syntax instead of post-construction assignment.</summary>
        public bool UseObjectInitializer { get; }

        /// <summary>True when the destination is a positional record (no parameterless constructor). The generated method uses named constructor arguments.</summary>
        public bool UseConstructorCall { get; }

        /// <summary>Projection assignments (excludes converter properties). Null means projection not generated.</summary>
        public IReadOnlyList<ProjectionAssignment>? ProjectionAssignments { get; set; }

        public string MethodName { get; set; }
    }

    private readonly struct IndexedProperty
    {
        public IndexedProperty(IPropertySymbol property, INamedTypeSymbol? converterType)
        {
            Property = property;
            ConverterType = converterType;
        }

        public IPropertySymbol Property { get; }

        public INamedTypeSymbol? ConverterType { get; }
    }

    private readonly struct PropertyAssignment
    {
        public PropertyAssignment(int index, string destinationPropertyName, string valueExpression, bool isInitOnly = false)
        {
            Index = index;
            DestinationPropertyName = destinationPropertyName;
            ValueExpression = valueExpression;
            InlineCollection = null;
            IsInitOnly = isInitOnly;
        }

        public PropertyAssignment(int index, string destinationPropertyName, string valueExpression, InlineCollectionData inlineCollection, bool isInitOnly = false)
        {
            Index = index;
            DestinationPropertyName = destinationPropertyName;
            ValueExpression = valueExpression;
            InlineCollection = inlineCollection;
            IsInitOnly = isInitOnly;
        }

        public int Index { get; }

        public string DestinationPropertyName { get; }

        public string ValueExpression { get; }

        public InlineCollectionData? InlineCollection { get; }

        public bool IsInitOnly { get; }
    }

    private sealed class InlineCollectionData
    {
        public InlineCollectionData(string sourceAccess, string sourceElemFqn, string destElemFqn, bool elemCanBeNull)
        {
            SourceAccess = sourceAccess;
            SourceElemFqn = sourceElemFqn;
            DestElemFqn = destElemFqn;
            ElemCanBeNull = elemCanBeNull;
        }

        public string SourceAccess { get; }
        public string SourceElemFqn { get; }
        public string DestElemFqn { get; }
        public bool ElemCanBeNull { get; }
    }

    /// <summary>A single member binding inside a projection expression (no converter support).</summary>
    private readonly struct ProjectionAssignment
    {
        public ProjectionAssignment(string destinationPropertyName, string valueExpression)
        {
            DestinationPropertyName = destinationPropertyName;
            ValueExpression = valueExpression;
        }

        public string DestinationPropertyName { get; }

        /// <summary>Expression using "source." prefix, safe for expression trees (no delegate calls).</summary>
        public string ValueExpression { get; }
    }
}
