using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;

namespace Starlight.Protobuf.Compiler;

[Generator]
public sealed partial class ProtobufCompiler : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ParseError = new(
        id: "SLPB001",
        title: "Protobuf parse error",
        messageFormat: "{0}({1},{2}): {3}",
        category: "Starlight.Protobuf",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingMetaError = new(
        id: "SLPB002",
        title: "Missing protobuf metadata",
        messageFormat: "A Version proto group must include _meta.proto with a package declaration",
        category: "Starlight.Protobuf",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private const string Roof = "Starlight.Protobuf";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var protos = context.AdditionalTextsProvider
            .Where(f => f.Path.EndsWith(".proto"))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, ct) =>
            {
                var (file, provider) = pair;
                provider.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.SLProtoRole", out var role);
                return new Proto(
                    Path.GetFileName(file.Path),
                    file.Path,
                    file.GetText(ct)?.ToString() ?? "",
                    role ?? "");
            })
            .Collect();

        context.RegisterSourceOutput(protos, Generate);
    }

    private readonly struct Proto
    {
        public Proto(string fileName, string fullPath, string content, string role)
        {
            FileName = fileName;
            FullPath = fullPath;
            Content = content;
            Role = role;
        }

        public string FileName { get; }
        public string FullPath { get; }
        public string Content { get; }
        public string Role { get; }

        public string ResolvedRole
        {
            get
            {
                if (!string.IsNullOrEmpty(Role)) return Role;
                var p = FullPath.Replace('\\', '/');
                if (p.Contains("/Base/")) return "Base";
                if (FileName == "extra.proto") return "Independent";
                return "Version";
            }
        }
    }

    private static void Generate(SourceProductionContext ctx, ImmutableArray<Proto> protos)
    {
        if (protos.IsDefaultOrEmpty) return;

        var baseFiles = protos.Where(p => p.ResolvedRole == "Base").ToList();
        var versionFiles = protos.Where(p => p.ResolvedRole == "Version").ToList();
        var independentFiles = protos.Where(p => p.ResolvedRole == "Independent").ToList();

        // --- Base: parse + emit POCOs ---------------------------------------
        var baseSet = baseFiles.Count > 0 ? Parse(ctx, baseFiles, protos) : null;
        var baseNs = "Generated";
        var baseByName = new Dictionary<string, DescriptorProto>();
        CodeEmitter.Resolver baseResolver = _ => null;
        var cmdIds = ScanCmdIds(baseFiles.Concat(versionFiles));
        var transforms = CodeEmitter.ScanTransforms(protos.Select(p => p.Content));

        if (baseSet is not null)
        {
            baseNs = NamespaceOf(baseSet) ?? baseNs;
            baseResolver = BuildResolver(baseSet);
            foreach (var msg in baseSet.Files.SelectMany(f => f.MessageTypes))
                baseByName[msg.Name] = msg;

            foreach (var file in baseSet.Files)
            {
                if (file.MessageTypes.Count == 0 && file.EnumTypes.Count == 0) continue;

                var body = new StringBuilder();
                foreach (var e in file.EnumTypes)
                    EmitTopLevelEnum(body, e);
                foreach (var msg in file.MessageTypes)
                {
                    CodeEmitter.EmitPoco(body, msg, baseNs, cmdIds.TryGetValue(msg.Name, out var id) ? id : (int?) null, baseResolver);
                    body.AppendLine();
                }

                ctx.AddSource($"{Stem(file.Name)}.Poco.g.cs", Wrap(baseNs, body.ToString()));
            }
        }

        // --- Version: parse + emit serializers + registry -------------------
        if (versionFiles.Count > 0)
        {
            var versionSet = Parse(ctx, versionFiles, protos);
            var meta = versionSet.Files.FirstOrDefault(f => f.Name.EndsWith("meta.proto"));
            if (meta is null || string.IsNullOrEmpty(meta.Package))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(MissingMetaError, Location.None));
            }
            else
            {
                var version = Capitalize(meta.Package);
                var versionNs = $"{baseNs}.{version}";

                var correlated = versionSet.Files
                    .SelectMany(f => f.MessageTypes)
                    .Where(vm => baseByName.ContainsKey(vm.Name))
                    .Select(vm => (Version: vm, Base: baseByName[vm.Name]))
                    .ToList();

                var serializers = new StringBuilder();
                foreach (var (vm, bm) in correlated)
                {
                    CodeEmitter.EmitSerializer(serializers, bm, vm, baseNs, baseResolver, transforms);
                    serializers.AppendLine();
                }

                ctx.AddSource($"{version}.Serializers.g.cs", Wrap(versionNs, serializers.ToString()));
                ctx.AddSource($"{version}.Registry.g.cs",
                    Wrap(versionNs, EmitRegistry(version, baseNs, correlated.Select(c => c.Base).ToList(), cmdIds)));
            }
        }

        // --- Independent: POCO + single serializer, no version system -------
        foreach (var file in independentFiles)
        {
            var set = Parse(ctx, new[] { file }, protos);
            var ns = NamespaceOf(set) ?? "Generated";
            var resolver = BuildResolver(set);

            var body = new StringBuilder();
            // Emit only the file itself; imported dependencies (e.g. descriptor.proto)
            // resolve through the filesystem but must not be POCO'd here.
            foreach (var f in set.Files.Where(f => f.Name == file.FileName))
            {
                foreach (var e in f.EnumTypes)
                    EmitTopLevelEnum(body, e);
                foreach (var msg in f.MessageTypes)
                {
                    CodeEmitter.EmitPoco(body, msg, ns, cmdIds.TryGetValue(msg.Name, out var id) ? id : (int?) null, resolver);
                    body.AppendLine();
                    CodeEmitter.EmitSerializer(body, msg, msg, ns, resolver, transforms);
                    body.AppendLine();
                }
            }

            ctx.AddSource($"{Stem(file.FileName)}.Independent.g.cs", Wrap(ns, body.ToString()));
        }
    }
}
