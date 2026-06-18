using System.Collections.Generic;
using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;
using FType = Google.Protobuf.Reflection.FieldDescriptorProto.Type;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Label;

namespace Starlight.Protobuf.Compiler;

internal static partial class CodeEmitter
{
    // ---- POCO emission ------------------------------------------------------

    public static void EmitPoco(StringBuilder sb, DescriptorProto msg, string baseNs, int? cmdId, Resolver resolve, bool selfSerializable = false)
    {
        // Version-independent messages own their single serializer, so they implement
        // ISelfSerializable<T> for the argument-free ToByteArray()/MergeFrom() path.
        var iface = selfSerializable ? "ISelfSerializable" : "IMessage";
        sb.AppendLine($"public sealed class {msg.Name} : global::Starlight.Protobuf.Core.{iface}<{msg.Name}>");
        sb.AppendLine("{");

        if (selfSerializable)
            sb.AppendLine($"    public static global::Starlight.Protobuf.Core.ISerializer<{msg.Name}> Serializer => {msg.Name}Serializer.Instance;").AppendLine();

        if (cmdId.HasValue)
            sb.AppendLine($"    public const int CmdId = {cmdId.Value};").AppendLine();

        foreach (var enumType in msg.EnumTypes)
            EmitEnum(sb, enumType, "    ");

        var oneofFields = new HashSet<FieldDescriptorProto>(msg.Fields.Where(InRealOneof));

        foreach (var field in msg.Fields)
        {
            if (oneofFields.Contains(field)) continue; // emitted as a discriminated group below

            var prop = Prop(field.Name, msg.Name);
            if (IsMap(field, resolve, out var entry))
            {
                var key = entry!.Fields.First(f => f.Number == 1);
                var val = entry.Fields.First(f => f.Number == 2);
                sb.AppendLine($"    public global::System.Collections.Generic.Dictionary<{ElemCsType(key, baseNs)}, {ElemCsType(val, baseNs)}> {prop} {{ get; set; }} = new();");
            } else if (field.label == Label.LabelRepeated)
            {
                sb.AppendLine($"    public global::System.Collections.Generic.List<{ElemCsType(field, baseNs)}> {prop} {{ get; set; }} = new();");
            } else if (field.type == FType.TypeMessage)
            {
                // message fields already carry presence via null; proto3 `optional` is a no-op here.
                sb.AppendLine($"    public global::{baseNs}.{Simple(field.TypeName)}? {prop} {{ get; set; }}");
            } else if (IsProto3Optional(field))
            {
                // explicit presence: null == absent (even the default value is written when set).
                var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");
                sb.AppendLine($"    public {w.CsType}? {prop} {{ get; set; }}");
            } else
            {
                var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");
                var init = field.type switch {
                    FType.TypeString => " = \"\";",
                    FType.TypeBytes => " = global::Google.Protobuf.ByteString.Empty;",
                    _ => "",
                };
                sb.AppendLine($"    public {w.CsType} {prop} {{ get; set; }}{init}");
            }
        }

        foreach (var group in msg.Fields.Where(InRealOneof).GroupBy(f => f.OneofIndex).OrderBy(g => g.Key))
            EmitOneofMembers(sb, msg, baseNs, group.Key, group.ToList());

        sb.AppendLine();
        sb.AppendLine("    public global::Starlight.Protobuf.Core.UnknownFieldSet? UnknownFields { get; set; }");
        sb.AppendLine("}");
    }

    /// <summary>
    /// Emits a oneof as a discriminated union: a <c>{Name}OneofCase</c> enum, shared
    /// <c>object?</c> + case backing fields, a case accessor, a <c>Clear{Name}()</c>, and
    /// per-field properties that read/write the shared slot. Matches protoc's C# shape.
    /// </summary>
    private static void EmitOneofMembers(StringBuilder sb, DescriptorProto msg, string baseNs, int oneofIndex, List<FieldDescriptorProto> fields)
    {
        var name = Pascal(msg.OneofDecls[oneofIndex].Name);
        var caseEnum = $"{name}OneofCase";
        var store = $"_{Camel(msg.OneofDecls[oneofIndex].Name)}";
        var caseStore = $"{store}Case";

        sb.AppendLine();
        sb.AppendLine($"    public enum {caseEnum}");
        sb.AppendLine("    {");
        sb.AppendLine("        None = 0,");
        foreach (var f in fields)
            sb.AppendLine($"        {Pascal(f.Name)} = {f.Number},");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    private object? {store};");
        sb.AppendLine($"    private int {caseStore};");
        sb.AppendLine($"    public {caseEnum} {name}Case => ({caseEnum}) {caseStore};");
        sb.AppendLine($"    public void Clear{name}() {{ {caseStore} = 0; {store} = null; }}");

        foreach (var f in fields)
        {
            var prop = Prop(f.Name, msg.Name);
            if (f.type == FType.TypeMessage)
            {
                var cs = $"global::{baseNs}.{Simple(f.TypeName)}";
                sb.AppendLine();
                sb.AppendLine($"    public {cs}? {prop}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => {caseStore} == {f.Number} ? ({cs}?) {store} : null;");
                sb.AppendLine($"        set {{ {store} = value; {caseStore} = value == null ? 0 : {f.Number}; }}");
                sb.AppendLine("    }");
            } else
            {
                var w = Scalar(f.type, f.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(f.TypeName)}" : "");
                var def = f.type switch {
                    FType.TypeString => "\"\"",
                    FType.TypeBytes => "global::Google.Protobuf.ByteString.Empty",
                    _ => $"default({w.CsType})",
                };
                sb.AppendLine();
                sb.AppendLine($"    public {w.CsType} {prop}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => {caseStore} == {f.Number} ? ({w.CsType}) {store}! : {def};");
                var assign = f.type is FType.TypeString or FType.TypeBytes
                    ? "global::Google.Protobuf.ProtoPreconditions.CheckNotNull(value, \"value\")"
                    : "value";
                sb.AppendLine($"        set {{ {store} = {assign}; {caseStore} = {f.Number}; }}");
                sb.AppendLine("    }");
            }
        }
    }

    private static void EmitEnum(StringBuilder sb, EnumDescriptorProto e, string indent)
    {
        sb.AppendLine($"{indent}public enum {e.Name}");
        sb.AppendLine($"{indent}{{");
        foreach (var v in e.Values)
            sb.AppendLine($"{indent}    {v.Name} = {v.Number},");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }
}
