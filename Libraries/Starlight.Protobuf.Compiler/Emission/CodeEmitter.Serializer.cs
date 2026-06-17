using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;
using FType = Google.Protobuf.Reflection.FieldDescriptorProto.Type;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Label;

namespace Starlight.Protobuf.Compiler;

internal static partial class CodeEmitter
{
    // ---- serializer emission ------------------------------------------------

    /// <param name="baseMsg">canonical message (drives POCO type + field types/names)</param>
    /// <param name="versionMsg">version dump message (drives real wire field numbers)</param>
    public static void EmitSerializer(StringBuilder sb, DescriptorProto baseMsg, DescriptorProto versionMsg, string baseNs, Resolver resolveBase, TransformTable? transforms = null)
    {
        var versionByName = versionMsg.Fields.ToDictionary(f => f.Name, f => f.Number);

        var size = new StringBuilder();
        var write = new StringBuilder();
        var read = new StringBuilder();

        foreach (var field in baseMsg.Fields)
        {
            if (!versionByName.TryGetValue(field.Name, out var number))
                continue; // canonical field absent in this version -> not serialized

            var oneofName = InRealOneof(field) ? OneofName(baseMsg, field) : null;
            var transform = transforms?.Get(versionMsg.Name, field.Name);
            EmitField(field, number, baseMsg.Name, baseNs, resolveBase, size, write, read, oneofName, transform);
        }

        var type = $"global::{baseNs}.{baseMsg.Name}";
        sb.AppendLine($"public sealed class {baseMsg.Name}Serializer : global::Starlight.Protobuf.Core.ISerializer<{type}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static readonly {baseMsg.Name}Serializer Instance = new();");
        sb.AppendLine();
        EmitDescriptor(sb, baseMsg, versionMsg, baseNs, resolveBase, transforms);
        sb.AppendLine($"    public int CalculateSize({type} m)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (Descriptor.HasRemaps) return global::Starlight.Protobuf.Serialization.ReflectiveEngine.CalculateSize(Descriptor, m);");
        sb.AppendLine("        var size = 0;");
        sb.Append(size);
        sb.AppendLine("        return size;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Serialize({type} m, global::Google.Protobuf.CodedOutputStream output)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (Descriptor.HasRemaps) { global::Starlight.Protobuf.Serialization.ReflectiveEngine.Serialize(Descriptor, m, output); return; }");
        sb.Append(write);
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Deserialize({type} m, global::Google.Protobuf.CodedInputStream input)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (Descriptor.HasRemaps) { global::Starlight.Protobuf.Serialization.ReflectiveEngine.Deserialize(Descriptor, m, input); return; }");
        sb.AppendLine("        uint tag;");
        sb.AppendLine("        while ((tag = input.ReadTag()) != 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (tag)");
        sb.AppendLine("            {");
        sb.Append(read);
        sb.AppendLine("                default:");
        sb.AppendLine("                    (m.UnknownFields ??= new global::Starlight.Protobuf.Core.UnknownFieldSet())");
        sb.AppendLine("                        .Add(global::Starlight.Protobuf.Core.UnknownFieldSet.ReadFrom(tag, input));");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void EmitField(FieldDescriptorProto field, int number, string msgName, string baseNs, Resolver resolve,
        StringBuilder size, StringBuilder write, StringBuilder read, string? oneofName = null, Transform? transform = null)
    {
        var prop = Prop(field.Name, msgName);
        var acc = $"m.{prop}";

        if (IsMap(field, resolve, out var entry))
        {
            EmitMap(field, number, msgName, baseNs, entry!, size, write, read);
            return;
        }

        if (field.label == Label.LabelRepeated)
        {
            EmitRepeated(field, number, msgName, baseNs, size, write, read);
            return;
        }

        if (field.type == FType.TypeMessage)
        {
            // Both plain and oneof message fields carry presence via null: a oneof
            // message getter returns null unless its case is active, so the null
            // check below doubles as the case guard.
            var msgType = Simple(field.TypeName);
            var tag = TagLen(number, 2);
            // size
            size.AppendLine($"        if ({acc} != null)");
            size.AppendLine("        {");
            size.AppendLine($"            int s = {msgType}Serializer.Instance.CalculateSize({acc});");
            size.AppendLine($"            size += {tag} + global::Google.Protobuf.CodedOutputStream.ComputeLengthSize(s) + s;");
            size.AppendLine("        }");
            // write
            write.AppendLine($"        if ({acc} != null)");
            write.AppendLine("        {");
            write.AppendLine($"            {RawTag(number, 2)};");
            write.AppendLine($"            output.WriteLength({msgType}Serializer.Instance.CalculateSize({acc}));");
            write.AppendLine($"            {msgType}Serializer.Instance.Serialize({acc}, output);");
            write.AppendLine("        }");
            // read
            read.AppendLine($"                case {TagValue(number, 2)}:");
            read.AppendLine("                {");
            read.AppendLine($"                    var sub = new global::{baseNs}.{msgType}();");
            read.AppendLine($"                    {msgType}Serializer.Instance.Deserialize(sub, input.ReadBytes().CreateCodedInput());");
            read.AppendLine($"                    {acc} = sub;");
            read.AppendLine("                    break;");
            read.AppendLine("                }");
            return;
        }

        // singular scalar / enum / string / bytes / bool
        var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");
        var tagLen = TagLen(number, w.WireType);

        // Presence guard + the expression carrying the value to write/size:
        //  - oneof: write whenever this field's case is active (even the default value).
        //  - proto3 optional: write whenever set (null == absent); unwrap Nullable<T> for value types.
        //  - implicit: standard proto3 default-omission.
        string guard, valueAcc = acc;
        if (oneofName != null)
        {
            guard = $"m.{oneofName}Case == global::{baseNs}.{msgName}.{oneofName}OneofCase.{Pascal(field.Name)}";
        }
        else if (IsProto3Optional(field))
        {
            guard = $"{acc} != null";
            valueAcc = field.type is FType.TypeString or FType.TypeBytes ? $"{acc}!" : $"{acc}.Value";
        }
        else
        {
            guard = Omit(w, field.type, acc);
        }

        // Transforms obfuscate the real value into the wire value on encode and
        // invert it on decode; the presence guard still tests the real value.
        var wireVal = Encode(transform, valueAcc, w.CsType);
        var readExpr = Decode(transform, ReadCall(w, "input"), w.CsType);

        size.AppendLine($"        if ({guard}) size += {tagLen} + {SizeCall(w, wireVal)};");
        write.AppendLine($"        if ({guard})");
        write.AppendLine("        {");
        write.AppendLine($"            {RawTag(number, w.WireType)};");
        write.AppendLine($"            {WriteCall(w, wireVal)};");
        write.AppendLine("        }");
        read.AppendLine($"                case {TagValue(number, w.WireType)}:");
        read.AppendLine($"                    {acc} = {readExpr};");
        read.AppendLine("                    break;");
    }

    private static void EmitRepeated(FieldDescriptorProto field, int number, string msgName, string baseNs,
        StringBuilder size, StringBuilder write, StringBuilder read)
    {
        var prop = Prop(field.Name, msgName);
        var acc = $"m.{prop}";

        if (field.type == FType.TypeMessage)
        {
            var msgType = Simple(field.TypeName);
            var tag = TagLen(number, 2);
            size.AppendLine($"        foreach (var v in {acc})");
            size.AppendLine("        {");
            size.AppendLine($"            int s = {msgType}Serializer.Instance.CalculateSize(v);");
            size.AppendLine($"            size += {tag} + global::Google.Protobuf.CodedOutputStream.ComputeLengthSize(s) + s;");
            size.AppendLine("        }");
            write.AppendLine($"        foreach (var v in {acc})");
            write.AppendLine("        {");
            write.AppendLine($"            {RawTag(number, 2)};");
            write.AppendLine($"            output.WriteLength({msgType}Serializer.Instance.CalculateSize(v));");
            write.AppendLine($"            {msgType}Serializer.Instance.Serialize(v, output);");
            write.AppendLine("        }");
            read.AppendLine($"                case {TagValue(number, 2)}:");
            read.AppendLine("                {");
            read.AppendLine($"                    var sub = new global::{baseNs}.{msgType}();");
            read.AppendLine($"                    {msgType}Serializer.Instance.Deserialize(sub, input.ReadBytes().CreateCodedInput());");
            read.AppendLine($"                    {acc}.Add(sub);");
            read.AppendLine("                    break;");
            read.AppendLine("                }");
            return;
        }

        var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");

        // string/bytes are length-delimited and never packed
        if (field.type is FType.TypeString or FType.TypeBytes)
        {
            var tag = TagLen(number, 2);
            size.AppendLine($"        foreach (var v in {acc}) size += {tag} + {SizeCall(w, "v")};");
            write.AppendLine($"        foreach (var v in {acc})");
            write.AppendLine("        {");
            write.AppendLine($"            {RawTag(number, 2)};");
            write.AppendLine($"            output.{w.Write}(v);");
            write.AppendLine("        }");
            read.AppendLine($"                case {TagValue(number, 2)}:");
            read.AppendLine($"                    {acc}.Add({ReadCall(w, "input")});");
            read.AppendLine("                    break;");
            return;
        }

        // packed scalar/enum
        var fieldTag = TagLen(number, 2);
        size.AppendLine($"        if ({acc}.Count > 0)");
        size.AppendLine("        {");
        size.AppendLine("            int d = 0;");
        size.AppendLine($"            foreach (var v in {acc}) d += {SizeCall(w, "v")};");
        size.AppendLine($"            size += {fieldTag} + global::Google.Protobuf.CodedOutputStream.ComputeLengthSize(d) + d;");
        size.AppendLine("        }");
        write.AppendLine($"        if ({acc}.Count > 0)");
        write.AppendLine("        {");
        write.AppendLine("            int d = 0;");
        write.AppendLine($"            foreach (var v in {acc}) d += {SizeCall(w, "v")};");
        write.AppendLine($"            {RawTag(number, 2)};");
        write.AppendLine("            output.WriteLength(d);");
        write.AppendLine($"            foreach (var v in {acc}) {WriteCall(w, "v")};");
        write.AppendLine("        }");
        // packed read
        read.AppendLine($"                case {TagValue(number, 2)}:");
        read.AppendLine("                {");
        read.AppendLine("                    var ci = input.ReadBytes().CreateCodedInput();");
        read.AppendLine($"                    while (!ci.IsAtEnd) {acc}.Add({ReadCall(w, "ci")});");
        read.AppendLine("                    break;");
        read.AppendLine("                }");
        // unpacked fallback
        read.AppendLine($"                case {TagValue(number, w.WireType)}:");
        read.AppendLine($"                    {acc}.Add({ReadCall(w, "input")});");
        read.AppendLine("                    break;");
    }

    private static void EmitMap(FieldDescriptorProto field, int number, string msgName, string baseNs, DescriptorProto entry,
        StringBuilder size, StringBuilder write, StringBuilder read)
    {
        var prop = Prop(field.Name, msgName);
        var acc = $"m.{prop}";
        var keyField = entry.Fields.First(f => f.Number == 1);
        var valField = entry.Fields.First(f => f.Number == 2);
        var kw = Scalar(keyField.type, "");
        var valIsMessage = valField.type == FType.TypeMessage;
        var vw = valIsMessage ? null : Scalar(valField.type, valField.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(valField.TypeName)}" : "");
        var fieldTagLen = TagLen(number, 2);
        var keyTagLen = TagLen(1, kw.WireType);
        var valWire = valIsMessage ? 2 : vw!.WireType;
        var valTagLen = TagLen(2, valWire);

        // value size/write/read fragments
        string valSizeExpr;
        if (valIsMessage)
        {
            var msgType = Simple(valField.TypeName);
            valSizeExpr = $"global::Google.Protobuf.CodedOutputStream.ComputeLengthSize({msgType}Serializer.Instance.CalculateSize(kv.Value)) + {msgType}Serializer.Instance.CalculateSize(kv.Value)";
        }
        else
        {
            valSizeExpr = SizeCall(vw!, "kv.Value");
        }

        // size
        size.AppendLine($"        foreach (var kv in {acc})");
        size.AppendLine("        {");
        size.AppendLine($"            int es = {keyTagLen} + {SizeCall(kw, "kv.Key")} + {valTagLen} + {valSizeExpr};");
        size.AppendLine($"            size += {fieldTagLen} + global::Google.Protobuf.CodedOutputStream.ComputeLengthSize(es) + es;");
        size.AppendLine("        }");

        // write
        write.AppendLine($"        foreach (var kv in {acc})");
        write.AppendLine("        {");
        write.AppendLine($"            int es = {keyTagLen} + {SizeCall(kw, "kv.Key")} + {valTagLen} + {valSizeExpr};");
        write.AppendLine($"            {RawTag(number, 2)};");
        write.AppendLine("            output.WriteLength(es);");
        write.AppendLine($"            {RawTag(1, kw.WireType)};");
        write.AppendLine($"            {WriteCall(kw, "kv.Key")};");
        if (valIsMessage)
        {
            var msgType = Simple(valField.TypeName);
            write.AppendLine($"            {RawTag(2, 2)};");
            write.AppendLine($"            output.WriteLength({msgType}Serializer.Instance.CalculateSize(kv.Value));");
            write.AppendLine($"            {msgType}Serializer.Instance.Serialize(kv.Value, output);");
        }
        else
        {
            write.AppendLine($"            {RawTag(2, valWire)};");
            write.AppendLine($"            {WriteCall(vw!, "kv.Value")};");
        }

        write.AppendLine("        }");

        // read
        var keyCs = ElemCsType(keyField, baseNs);
        var valCs = ElemCsType(valField, baseNs);
        var keyInit = keyField.type == FType.TypeString ? " = \"\"" : keyField.type == FType.TypeBytes ? " = global::Google.Protobuf.ByteString.Empty" : "";
        string valInit;
        if (valIsMessage) valInit = $" = new {valCs}()";
        else if (valField.type == FType.TypeString) valInit = " = \"\"";
        else if (valField.type == FType.TypeBytes) valInit = " = global::Google.Protobuf.ByteString.Empty";
        else valInit = " = default";

        read.AppendLine($"                case {TagValue(number, 2)}:");
        read.AppendLine("                {");
        read.AppendLine("                    var ci = input.ReadBytes().CreateCodedInput();");
        read.AppendLine($"                    {keyCs} k = default{keyInit};");
        read.AppendLine($"                    {valCs} v{valInit};");
        read.AppendLine("                    uint t;");
        read.AppendLine("                    while ((t = ci.ReadTag()) != 0)");
        read.AppendLine("                    {");
        read.AppendLine("                        switch (t)");
        read.AppendLine("                        {");
        read.AppendLine($"                            case {TagValue(1, kw.WireType)}:");
        read.AppendLine($"                                k = {ReadCall(kw, "ci")};");
        read.AppendLine("                                break;");
        read.AppendLine($"                            case {TagValue(2, valWire)}:");
        if (valIsMessage)
        {
            var msgType = Simple(valField.TypeName);
            read.AppendLine($"                                v = new global::{baseNs}.{msgType}();");
            read.AppendLine($"                                {msgType}Serializer.Instance.Deserialize(v, ci.ReadBytes().CreateCodedInput());");
        }
        else
        {
            read.AppendLine($"                                v = {ReadCall(vw!, "ci")};");
        }

        read.AppendLine("                                break;");
        read.AppendLine("                            default:");
        read.AppendLine("                                ci.SkipLastField();");
        read.AppendLine("                                break;");
        read.AppendLine("                        }");
        read.AppendLine("                    }");
        read.AppendLine($"                    {acc}[k] = v;");
        read.AppendLine("                    break;");
        read.AppendLine("                }");
    }
}
