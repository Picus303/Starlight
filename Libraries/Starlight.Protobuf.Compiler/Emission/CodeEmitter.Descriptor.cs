using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;
using FType = Google.Protobuf.Reflection.FieldDescriptorProto.Type;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Label;

namespace Starlight.Protobuf.Compiler;

internal static partial class CodeEmitter
{
    // ---- descriptor emission (reflective engine / remap field table) --------

    private const string FdType = "global::Starlight.Protobuf.Core.FieldDescriptor";
    private const string PkType = "global::Starlight.Protobuf.Core.ProtoKind";
    private const string FrType = "global::Starlight.Protobuf.Core.FieldRule";

    /// <summary>
    /// Emits the per-message field table that drives the reflective slow path and
    /// field-ID remap. Only name-matched fields are included, mirroring the fast
    /// path; nested message references are lazy (<c>() =&gt; XSerializer.Descriptor</c>)
    /// to sidestep static-init ordering.
    /// </summary>
    private static void EmitDescriptor(StringBuilder sb, DescriptorProto baseMsg, DescriptorProto versionMsg, string baseNs, Resolver resolve, TransformTable? transforms)
    {
        var versionByName = versionMsg.Fields.ToDictionary(f => f.Name, f => f.Number);
        var type = $"global::{baseNs}.{baseMsg.Name}";

        sb.AppendLine($"    public static readonly global::Starlight.Protobuf.Core.MessageDescriptor Descriptor =");
        sb.AppendLine($"        new global::Starlight.Protobuf.Core.MessageDescriptor(\"{baseMsg.Name}\", typeof({type}), new {FdType}[]");
        sb.AppendLine("        {");
        foreach (var field in baseMsg.Fields)
        {
            if (!versionByName.TryGetValue(field.Name, out var number)) continue;
            var transform = IsTransformable(field.type) ? transforms?.Get(versionMsg.Name, field.Name) : null;
            sb.AppendLine($"            {FieldDescriptorExpr(field, number, baseMsg, resolve, transform)},");
        }

        sb.AppendLine("        });");
        sb.AppendLine();
    }

    private static string FieldDescriptorExpr(FieldDescriptorProto field, int number, DescriptorProto msg, Resolver resolve, Transform? transform = null)
    {
        var prop = Prop(field.Name, msg.Name);
        var head = $"new {FdType}(\"{field.Name}\", \"{prop}\", {field.Number}, {number}";

        if (IsMap(field, resolve, out var entry))
        {
            var keyField = entry!.Fields.First(f => f.Number == 1);
            var valField = entry.Fields.First(f => f.Number == 2);
            var extra = $", keyKind: {PkType}.{Kind(keyField.type)}";
            if (valField.type == FType.TypeMessage)
                extra += $", messageRef: () => {Simple(valField.TypeName)}Serializer.Descriptor";
            return $"{head}, {PkType}.{Kind(valField.type)}, {FrType}.Map{extra})";
        }

        if (field.label == Label.LabelRepeated)
        {
            var extra = field.type == FType.TypeMessage
                ? $", messageRef: () => {Simple(field.TypeName)}Serializer.Descriptor"
                : "";
            return $"{head}, {PkType}.{Kind(field.type)}, {FrType}.Repeated{extra})";
        }

        string rule;
        var named = "";
        if (InRealOneof(field))
        {
            rule = "Single";
            named = $", oneofName: \"{OneofName(msg, field)}\"";
        }
        else
        {
            rule = IsProto3Optional(field) ? "Optional" : "Single";
        }

        if (field.type == FType.TypeMessage)
            named += $", messageRef: () => {Simple(field.TypeName)}Serializer.Descriptor";

        // Only op-chain transforms (add/xor/fop and parseable masks) survive to the
        // reflective path; an unparseable raw mask has no runtime representation.
        if (transform is { RawMask: null })
            named += $", transform: new global::Starlight.Protobuf.Core.FieldTransform(\"{transform.Ops}\", new long[] {{ {string.Join(", ", transform.Operands)} }})";

        return $"{head}, {PkType}.{Kind(field.type)}, {FrType}.{rule}{named})";
    }
}
