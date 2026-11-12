using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Urlog.Core.SourceGen;

/// <summary>
/// Emits <c>&lt;RootNamespace&gt;.LogSourceRegistry.Registrations</c>: one <c>SourceRegistration</c> per class in
/// the compilation marked <c>[LogSource]</c>, with a factory lambda instead of reflection. Plugins loaded from a
/// <c>plugins/</c> directory carry the same generated type under their own root namespace.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class LogSourceRegistryGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Urlog.Core.Sources.LogSourceAttribute";
    private const string InterfaceName = "Urlog.Core.Sources.ILogSource";
    private const string Category = "Urlog.Sources";

    private static readonly DiagnosticDescriptor DuplicateId = new(
        "LS0001", "Duplicate log source id", "The id or alias '{0}' is used by more than one [LogSource] class", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotASource = new(
        "LS0002", "[LogSource] class does not implement ILogSource", "'{0}' is marked [LogSource] but does not implement Urlog.Core.Sources.ILogSource", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotConstructible = new(
        "LS0003", "[LogSource] class is not constructible", "'{0}' must be public, non-abstract, non-generic and have an accessible parameterless constructor", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sources = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => Extract(ctx, ct));

        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Urlog.Generated");

        context.RegisterSourceOutput(sources.Collect().Combine(assemblyName), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    private static SourceCandidate Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var attribute = ctx.Attributes[0];
        var id = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
        var aliases = ImmutableArray<string>.Empty;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Aliases" && !named.Value.IsNull)
            {
                aliases = named.Value.Values.Select(v => v.Value as string ?? "").Where(v => v.Length > 0).ToImmutableArray();
            }
        }

        var implements = symbol.AllInterfaces.Any(i => i.ToDisplayString() == InterfaceName);
        var constructible = symbol.DeclaredAccessibility == Accessibility.Public
            && !symbol.IsAbstract
            && !symbol.IsGenericType
            && symbol.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.All(p => p.IsOptional));

        ct.ThrowIfCancellationRequested();
        var location = ctx.TargetNode.GetLocation();
        return new SourceCandidate(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            id,
            aliases,
            implements,
            constructible,
            location.SourceTree?.FilePath ?? "",
            location.SourceSpan);
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<SourceCandidate> candidates, string assemblyName)
    {
        var seen = new Dictionary<string, SourceCandidate>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<SourceCandidate>();
        foreach (var c in candidates.OrderBy(c => c.TypeName, StringComparer.Ordinal))
        {
            var ok = true;
            if (!c.ImplementsInterface)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotASource, c.Location(), c.TypeName));
                ok = false;
            }

            if (!c.Constructible)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotConstructible, c.Location(), c.TypeName));
                ok = false;
            }

            foreach (var name in new[] { c.Id }.Concat(c.Aliases))
            {
                if (name.Length == 0)
                {
                    continue;
                }

                if (seen.TryGetValue(name, out var other) && other.TypeName != c.TypeName)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, c.Location(), name));
                    ok = false;
                }
                else
                {
                    seen[name] = c;
                }
            }

            if (ok)
            {
                valid.Add(c);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by Urlog.Core.SourceGen from [LogSource] classes in this assembly. Do not edit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.Append("namespace ").Append(assemblyName).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Every [LogSource] in this assembly, discovered at compile time (no reflection scanning).</summary>");
        sb.AppendLine("public static class LogSourceRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static global::System.Collections.Immutable.ImmutableArray<global::Urlog.Core.Sources.SourceRegistration> Registrations { get; } =");
        sb.AppendLine("    [");
        foreach (var c in valid)
        {
            sb.Append("        new(").Append(Literal(c.Id)).Append(", [");
            sb.Append(string.Join(", ", c.Aliases.Select(Literal)));
            sb.Append("], static () => new ").Append(c.TypeName).AppendLine("()),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");
        spc.AddSource("LogSourceRegistry.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    private sealed class SourceCandidate
    {
        public SourceCandidate(string typeName, string id, ImmutableArray<string> aliases, bool implementsInterface, bool constructible, string filePath, TextSpan span)
        {
            TypeName = typeName;
            Id = id;
            Aliases = aliases;
            ImplementsInterface = implementsInterface;
            Constructible = constructible;
            FilePath = filePath;
            Span = span;
        }

        public string TypeName { get; }

        public string Id { get; }

        public ImmutableArray<string> Aliases { get; }

        public bool ImplementsInterface { get; }

        public bool Constructible { get; }

        public string FilePath { get; }

        public TextSpan Span { get; }

        public Location Location() => FilePath.Length == 0 ? Microsoft.CodeAnalysis.Location.None : Microsoft.CodeAnalysis.Location.Create(FilePath, Span, default);
    }
}
