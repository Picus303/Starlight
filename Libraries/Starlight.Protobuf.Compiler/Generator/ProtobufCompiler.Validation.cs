using System.Collections.Generic;
using System.Linq;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;

namespace Starlight.Protobuf.Compiler;

public sealed partial class ProtobufCompiler
{
    /// <summary>
    /// Rejects proto names that would generate uncompilable C#: verbatim-emitted type
    /// and enum-value names that collide with a reserved keyword, and field names whose
    /// property form collides with an emitter-synthesized member. The collision rules
    /// live in <see cref="ReservedNames"/>; this walks the descriptors and reports.
    /// We still emit (the bad C# fails to compile anyway) but SLPB003 names the cause.
    /// </summary>
    private static void ValidateNames(SourceProductionContext ctx, IEnumerable<DescriptorProto> messages,
        IEnumerable<EnumDescriptorProto> topLevelEnums, IReadOnlyDictionary<string, int> cmdIds)
    {
        foreach (var e in topLevelEnums)
            ValidateEnum(ctx, e);

        foreach (var msg in messages)
            ValidateMessage(ctx, msg, cmdIds.ContainsKey(msg.Name));
    }

    private static void ValidateMessage(SourceProductionContext ctx, DescriptorProto msg, bool hasCmdId)
    {
        Report(ctx, ReservedNames.CheckKeyword("message", msg.Name));

        foreach (var e in msg.EnumTypes)
            ValidateEnum(ctx, e);

        var realOneofs = msg.Fields
            .Where(f => f.ShouldSerializeOneofIndex() && !f.Proto3Optional)
            .Select(f => msg.OneofDecls[f.OneofIndex].Name)
            .Distinct();

        foreach (var v in ReservedNames.GeneratedMemberCollisions(
                     msg.Name, hasCmdId, realOneofs, msg.Fields.Select(f => f.Name)))
            Report(ctx, v);
    }

    private static void ValidateEnum(SourceProductionContext ctx, EnumDescriptorProto e)
    {
        Report(ctx, ReservedNames.CheckKeyword("enum", e.Name));
        foreach (var value in e.Values)
            Report(ctx, ReservedNames.CheckKeyword("enum value", value.Name));
    }

    private static void Report(SourceProductionContext ctx, NameViolation? violation)
    {
        if (violation is { } v)
            ctx.ReportDiagnostic(Diagnostic.Create(ReservedNameError, Location.None,
                v.Kind, v.ProtoName, v.CsName, v.Reason));
    }
}
