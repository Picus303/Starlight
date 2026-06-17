using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;

namespace Starlight.Protobuf.Compiler;

[Generator]
public sealed class ProtobufCompiler : IIncrementalGenerator
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
        var baseSet = baseFiles.Count > 0 ? Parse(ctx, baseFiles) : null;
        var baseNs = "Generated";
        var baseByName = new Dictionary<string, DescriptorProto>();
        CodeEmitter.Resolver baseResolver = _ => null;
        var cmdIds = ScanCmdIds(baseFiles.Concat(versionFiles));

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
            var versionSet = Parse(ctx, versionFiles);
            var meta = versionSet.Files.FirstOrDefault(f => f.Name == "_meta.proto");
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
                    CodeEmitter.EmitSerializer(serializers, bm, vm, baseNs, baseResolver);
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
            var set = Parse(ctx, new[] { file });
            var ns = NamespaceOf(set) ?? "Generated";
            var resolver = BuildResolver(set);

            var body = new StringBuilder();
            foreach (var f in set.Files)
            {
                foreach (var e in f.EnumTypes)
                    EmitTopLevelEnum(body, e);
                foreach (var msg in f.MessageTypes)
                {
                    CodeEmitter.EmitPoco(body, msg, ns, cmdIds.TryGetValue(msg.Name, out var id) ? id : (int?) null, resolver);
                    body.AppendLine();
                    CodeEmitter.EmitSerializer(body, msg, msg, ns, resolver);
                    body.AppendLine();
                }
            }

            ctx.AddSource($"{Stem(file.FileName)}.Independent.g.cs", Wrap(ns, body.ToString()));
        }
    }

    // -- registry -------------------------------------------------------------

    private static string EmitRegistry(string version, string baseNs, List<DescriptorProto> messages, Dictionary<string, int> cmdIds)
    {
        int? Cmd(DescriptorProto m) => cmdIds.TryGetValue(m.Name, out var id) ? id : (int?) null;

        var knownFirstNames = new HashSet<string> { "GetPlayerTokenReq", "PingReq" };
        var knownFirst = messages
            .Where(m => knownFirstNames.Contains(m.Name) && Cmd(m).HasValue)
            .Select(m => Cmd(m)!.Value)
            .Distinct()
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"public sealed class {version}ProtocolRegistry : global::Starlight.Protobuf.Registry.ProtocolRegistry");
        sb.AppendLine("{");
        sb.AppendLine($"    public override string Version => \"{version}\";");
        sb.AppendLine();
        sb.AppendLine("    public override global::System.Collections.Generic.IReadOnlySet<int> KnownFirst { get; } =");
        sb.AppendLine($"        new global::System.Collections.Generic.HashSet<int> {{ {string.Join(", ", knownFirst)} }};");
        sb.AppendLine();

        sb.AppendLine("    public override int GetCmdId(global::Starlight.Protobuf.Core.IMessage message) => message switch");
        sb.AppendLine("    {");
        foreach (var m in messages.Where(m => Cmd(m).HasValue))
            sb.AppendLine($"        global::{baseNs}.{m.Name} => {Cmd(m)!.Value},");
        sb.AppendLine("        _ => 0,");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public override global::Starlight.Protobuf.Core.IMessage Create(int cmdId) => cmdId switch");
        sb.AppendLine("    {");
        foreach (var m in messages.Where(m => Cmd(m).HasValue))
            sb.AppendLine($"        {Cmd(m)!.Value} => new global::{baseNs}.{m.Name}(),");
        sb.AppendLine($"        _ => throw new global::System.ArgumentOutOfRangeException(nameof(cmdId), cmdId, \"Unknown CmdId for {version}.\"),");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public override int CalculateSize(global::Starlight.Protobuf.Core.IMessage message) => message switch");
        sb.AppendLine("    {");
        foreach (var m in messages)
            sb.AppendLine($"        global::{baseNs}.{m.Name} v => {m.Name}Serializer.Instance.CalculateSize(v),");
        sb.AppendLine("        _ => 0,");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public override void Serialize(global::Starlight.Protobuf.Core.IMessage message, global::Google.Protobuf.CodedOutputStream output)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (message)");
        sb.AppendLine("        {");
        foreach (var m in messages)
            sb.AppendLine($"            case global::{baseNs}.{m.Name} v: {m.Name}Serializer.Instance.Serialize(v, output); break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public override void Deserialize(global::Starlight.Protobuf.Core.IMessage message, global::Google.Protobuf.CodedInputStream input)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (message)");
        sb.AppendLine("        {");
        foreach (var m in messages)
            sb.AppendLine($"            case global::{baseNs}.{m.Name} v: {m.Name}Serializer.Instance.Deserialize(v, input); break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public override global::System.Collections.Generic.IReadOnlyCollection<global::Starlight.Protobuf.Core.MessageDescriptor> Descriptors { get; } =");
        sb.AppendLine("        new global::Starlight.Protobuf.Core.MessageDescriptor[]");
        sb.AppendLine("        {");
        foreach (var m in messages)
            sb.AppendLine($"            {m.Name}Serializer.Descriptor,");
        sb.AppendLine("        };");
        sb.AppendLine();

        sb.AppendLine("    public override global::Starlight.Protobuf.Core.MessageDescriptor? GetDescriptor(int cmdId) => cmdId switch");
        sb.AppendLine("    {");
        foreach (var m in messages.Where(m => Cmd(m).HasValue))
            sb.AppendLine($"        {Cmd(m)!.Value} => {m.Name}Serializer.Descriptor,");
        sb.AppendLine("        _ => null,");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public override global::Starlight.Protobuf.Core.MessageDescriptor? GetDescriptor(global::System.Type messageType)");
        sb.AppendLine("    {");
        foreach (var m in messages)
            sb.AppendLine($"        if (messageType == typeof(global::{baseNs}.{m.Name})) return {m.Name}Serializer.Descriptor;");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // -- parse + helpers ------------------------------------------------------

    private static FileDescriptorSet Parse(SourceProductionContext ctx, IEnumerable<Proto> group)
    {
        var sources = new Dictionary<string, string>();
        foreach (var p in group)
            sources[p.FileName] = p.Content;

        var set = new FileDescriptorSet { FileSystem = new InMemoryFileSystem(sources) };
        foreach (var kv in sources)
            set.Add(kv.Key, includeInOutput: true, source: new StringReader(kv.Value));

        set.Process();

        foreach (var error in set.GetErrors().Where(e => e.IsError))
            ctx.ReportDiagnostic(Diagnostic.Create(
                ParseError, Location.None, error.File, error.LineNumber, error.ColumnNumber, error.Message));

        return set;
    }

    private static string? NamespaceOf(FileDescriptorSet set) =>
        set.Files
            .Select(f => f.Options?.CsharpNamespace)
            .FirstOrDefault(ns => !string.IsNullOrEmpty(ns));

    private static CodeEmitter.Resolver BuildResolver(FileDescriptorSet set)
    {
        var map = new Dictionary<string, DescriptorProto>();
        foreach (var file in set.Files)
        {
            var prefix = string.IsNullOrEmpty(file.Package) ? "" : file.Package;
            foreach (var msg in file.MessageTypes)
                Index(msg, prefix, map);
        }

        return name =>
        {
            var key = name.TrimStart('.');
            return map.TryGetValue(key, out var d) ? d : null;
        };
    }

    private static void Index(DescriptorProto d, string prefix, Dictionary<string, DescriptorProto> map)
    {
        var fq = string.IsNullOrEmpty(prefix) ? d.Name : $"{prefix}.{d.Name}";
        map[fq] = d;
        foreach (var nested in d.NestedTypes)
            Index(nested, fq, map);
    }

    private static Dictionary<string, int> ScanCmdIds(IEnumerable<Proto> files)
    {
        var map = new Dictionary<string, int>();
        foreach (var f in files)
        {
            // Preferred: a `// CmdId: <n>` comment immediately preceding a message.
            foreach (Match m in Regex.Matches(f.Content, @"//\s*CmdId\s*:\s*(-?\d+)\s*[\r\n]+\s*message\s+(\w+)"))
            {
                if (int.TryParse(m.Groups[1].Value, out var id))
                    map[m.Groups[2].Value] = id;
            }

            // Fallback: an `enum CmdId { CMD_ID = <n>; }` inside the message body.
            foreach (Match m in Regex.Matches(f.Content, @"message\s+(\w+)\s*\{(.*?)\}", RegexOptions.Singleline))
            {
                var name = m.Groups[1].Value;
                if (map.ContainsKey(name)) continue;
                var e = Regex.Match(m.Groups[2].Value, @"CMD_ID\s*=\s*(-?\d+)");
                if (e.Success && int.TryParse(e.Groups[1].Value, out var id))
                    map[name] = id;
            }
        }

        return map;
    }

    private static void EmitTopLevelEnum(StringBuilder sb, EnumDescriptorProto e)
    {
        sb.AppendLine($"public enum {e.Name}");
        sb.AppendLine("{");
        foreach (var v in e.Values)
            sb.AppendLine($"    {v.Name} = {v.Number},");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string Capitalize(string package) =>
        package.Length == 0 ? package : char.ToUpperInvariant(package[0]) + package.Substring(1);

    private static string Stem(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName.Substring(0, dot);
    }

    private static string Wrap(string ns, string body) => $"""
        // <auto-generated/>
        #nullable enable
        namespace {ns};

        {body}
        """;
}

internal sealed class InMemoryFileSystem(IReadOnlyDictionary<string, string> sources) : IFileSystem
{
    public bool Exists(string path) => sources.ContainsKey(Normalize(path));

    public TextReader? OpenText(string path) =>
        sources.TryGetValue(Normalize(path), out var content) ? new StringReader(content) : null;

    private static string Normalize(string path) => Path.GetFileName(path);
}
