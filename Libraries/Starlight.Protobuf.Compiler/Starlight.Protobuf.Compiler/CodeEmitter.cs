using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Google.Protobuf.Reflection;
using FType = Google.Protobuf.Reflection.FieldDescriptorProto.Type;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Label;

namespace Starlight.Protobuf.Compiler;

/// <summary>
/// Emits POCOs, per-version hardcoded serializers, and version registries from
/// parsed proto descriptors. Covers proto3 scalars, enums, bytes, nested
/// messages, packed/unpacked repeated, maps, explicit proto3 <c>optional</c>
/// presence, and <c>oneof</c>.
/// </summary>
internal static class CodeEmitter
{
    /// <summary>Maps a message's fully-qualified proto name to its descriptor (for map-entry / nested resolution).</summary>
    internal delegate DescriptorProto? Resolver(string fullyQualifiedTypeName);

    // ---- presence classification --------------------------------------------

    /// <summary>True for a proto3 <c>optional</c> field (explicit presence via a synthetic one-field oneof).</summary>
    private static bool IsProto3Optional(FieldDescriptorProto f) => f.Proto3Optional;

    /// <summary>True for a field inside a real (user-declared) <c>oneof</c> -- excludes synthetic proto3-optional oneofs.</summary>
    private static bool InRealOneof(FieldDescriptorProto f) => f.ShouldSerializeOneofIndex() && !f.Proto3Optional;

    private static string OneofName(DescriptorProto msg, FieldDescriptorProto f) => Pascal(msg.OneofDecls[f.OneofIndex].Name);

    // ---- naming -------------------------------------------------------------

    public static string Pascal(string snake)
    {
        var sb = new StringBuilder(snake.Length);
        var upper = true;
        foreach (var c in snake)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    public static string Camel(string snake)
    {
        var p = Pascal(snake);
        return p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p.Substring(1);
    }

    public static string Simple(string typeName)
    {
        var name = typeName.TrimStart('.');
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }

    /// <summary>
    /// Property name for a field. C# forbids a member sharing its enclosing type's
    /// name, so (matching protoc's C# generator) we suffix "_" when they collide.
    /// </summary>
    public static string Prop(string fieldName, string messageName)
    {
        var p = Pascal(fieldName);
        return p == messageName ? p + "_" : p;
    }

    // ---- wire info ----------------------------------------------------------

    private sealed class Wire
    {
        public string CsType = "";
        public int WireType;
        public string Write = "";
        public string Read = "";
        public string Compute = "";
        public bool IsEnum;
    }

    private static Wire Scalar(FType type, string enumCsType)
    {
        switch (type)
        {
            case FType.TypeDouble: return new Wire { CsType = "double", WireType = 1, Write = "WriteDouble", Read = "ReadDouble", Compute = "ComputeDoubleSize" };
            case FType.TypeFloat: return new Wire { CsType = "float", WireType = 5, Write = "WriteFloat", Read = "ReadFloat", Compute = "ComputeFloatSize" };
            case FType.TypeInt64: return new Wire { CsType = "long", WireType = 0, Write = "WriteInt64", Read = "ReadInt64", Compute = "ComputeInt64Size" };
            case FType.TypeUint64: return new Wire { CsType = "ulong", WireType = 0, Write = "WriteUInt64", Read = "ReadUInt64", Compute = "ComputeUInt64Size" };
            case FType.TypeInt32: return new Wire { CsType = "int", WireType = 0, Write = "WriteInt32", Read = "ReadInt32", Compute = "ComputeInt32Size" };
            case FType.TypeFixed64: return new Wire { CsType = "ulong", WireType = 1, Write = "WriteFixed64", Read = "ReadFixed64", Compute = "ComputeFixed64Size" };
            case FType.TypeFixed32: return new Wire { CsType = "uint", WireType = 5, Write = "WriteFixed32", Read = "ReadFixed32", Compute = "ComputeFixed32Size" };
            case FType.TypeBool: return new Wire { CsType = "bool", WireType = 0, Write = "WriteBool", Read = "ReadBool", Compute = "ComputeBoolSize" };
            case FType.TypeString: return new Wire { CsType = "string", WireType = 2, Write = "WriteString", Read = "ReadString", Compute = "ComputeStringSize" };
            case FType.TypeBytes: return new Wire { CsType = "global::Google.Protobuf.ByteString", WireType = 2, Write = "WriteBytes", Read = "ReadBytes", Compute = "ComputeBytesSize" };
            case FType.TypeUint32: return new Wire { CsType = "uint", WireType = 0, Write = "WriteUInt32", Read = "ReadUInt32", Compute = "ComputeUInt32Size" };
            case FType.TypeSfixed32: return new Wire { CsType = "int", WireType = 5, Write = "WriteSFixed32", Read = "ReadSFixed32", Compute = "ComputeSFixed32Size" };
            case FType.TypeSfixed64: return new Wire { CsType = "long", WireType = 1, Write = "WriteSFixed64", Read = "ReadSFixed64", Compute = "ComputeSFixed64Size" };
            case FType.TypeSint32: return new Wire { CsType = "int", WireType = 0, Write = "WriteSInt32", Read = "ReadSInt32", Compute = "ComputeSInt32Size" };
            case FType.TypeSint64: return new Wire { CsType = "long", WireType = 0, Write = "WriteSInt64", Read = "ReadSInt64", Compute = "ComputeSInt64Size" };
            case FType.TypeEnum: return new Wire { CsType = enumCsType, WireType = 0, Write = "WriteEnum", Read = "ReadEnum", Compute = "ComputeEnumSize", IsEnum = true };
            default: throw new InvalidOperationException($"Unsupported scalar proto type: {type}");
        }
    }

    private static byte[] TagBytes(int number, int wireType)
    {
        var tag = ((uint) number << 3) | (uint) wireType;
        var bytes = new List<byte>();
        do
        {
            var b = (byte) (tag & 0x7F);
            tag >>= 7;
            if (tag != 0) b |= 0x80;
            bytes.Add(b);
        } while (tag != 0);

        return bytes.ToArray();
    }

    private static uint TagValue(int number, int wireType) => ((uint) number << 3) | (uint) wireType;

    private static string RawTag(int number, int wireType) =>
        $"output.WriteRawTag({string.Join(", ", TagBytes(number, wireType).Select(b => "0x" + b.ToString("X2")))})";

    private static int TagLen(int number, int wireType) => TagBytes(number, wireType).Length;

    // expression helpers (acc = the value expression being written/sized/read)
    private static string WriteCall(Wire w, string acc) =>
        $"output.{w.Write}({(w.IsEnum ? $"(int) {acc}" : acc)})";

    private static string SizeCall(Wire w, string acc) =>
        $"global::Google.Protobuf.CodedOutputStream.{w.Compute}({(w.IsEnum ? $"(int) {acc}" : acc)})";

    private static string ReadCall(Wire w, string stream) =>
        w.IsEnum ? $"({w.CsType}) {stream}.ReadEnum()" : $"{stream}.{w.Read}()";

    private static string Omit(Wire w, FType type, string acc)
    {
        if (type is FType.TypeString or FType.TypeBytes) return $"{acc}.Length != 0";
        if (type == FType.TypeBool) return acc;
        if (w.IsEnum) return $"(int) {acc} != 0";
        return $"{acc} != 0";
    }

    // ---- field classification ----------------------------------------------

    private static bool IsMap(FieldDescriptorProto field, Resolver resolve, out DescriptorProto? entry)
    {
        entry = null;
        if (field.label != Label.LabelRepeated || field.type != FType.TypeMessage) return false;
        var d = resolve(field.TypeName);
        if (d?.Options?.MapEntry == true)
        {
            entry = d;
            return true;
        }

        return false;
    }

    private static string ElemCsType(FieldDescriptorProto field, string baseNs)
    {
        if (field.type == FType.TypeMessage) return $"global::{baseNs}.{Simple(field.TypeName)}";
        if (field.type == FType.TypeEnum) return $"global::{baseNs}.{Simple(field.TypeName)}";
        return Scalar(field.type, "").CsType;
    }

    // ---- POCO emission ------------------------------------------------------

    public static void EmitPoco(StringBuilder sb, DescriptorProto msg, string baseNs, int? cmdId, Resolver resolve)
    {
        sb.AppendLine($"public sealed class {msg.Name} : global::Starlight.Protobuf.Core.IMessage<{msg.Name}>");
        sb.AppendLine("{");

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
            }
            else if (field.label == Label.LabelRepeated)
            {
                sb.AppendLine($"    public global::System.Collections.Generic.List<{ElemCsType(field, baseNs)}> {prop} {{ get; set; }} = new();");
            }
            else if (field.type == FType.TypeMessage)
            {
                // message fields already carry presence via null; proto3 `optional` is a no-op here.
                sb.AppendLine($"    public global::{baseNs}.{Simple(field.TypeName)}? {prop} {{ get; set; }}");
            }
            else if (IsProto3Optional(field))
            {
                // explicit presence: null == absent (even the default value is written when set).
                var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");
                sb.AppendLine($"    public {w.CsType}? {prop} {{ get; set; }}");
            }
            else
            {
                var w = Scalar(field.type, field.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(field.TypeName)}" : "");
                var init = field.type switch
                {
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
                sb.AppendLine($"        set {{ {store} = value; {caseStore} = {f.Number}; }}");
                sb.AppendLine("    }");
            }
            else
            {
                var w = Scalar(f.type, f.type == FType.TypeEnum ? $"global::{baseNs}.{Simple(f.TypeName)}" : "");
                var def = f.type switch
                {
                    FType.TypeString => "\"\"",
                    FType.TypeBytes => "global::Google.Protobuf.ByteString.Empty",
                    _ => $"default({w.CsType})",
                };
                sb.AppendLine();
                sb.AppendLine($"    public {w.CsType} {prop}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => {caseStore} == {f.Number} ? ({w.CsType}) {store}! : {def};");
                sb.AppendLine($"        set {{ {store} = value; {caseStore} = {f.Number}; }}");
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

    // ---- serializer emission ------------------------------------------------

    /// <param name="baseMsg">canonical message (drives POCO type + field types/names)</param>
    /// <param name="versionMsg">version dump message (drives real wire field numbers)</param>
    public static void EmitSerializer(StringBuilder sb, DescriptorProto baseMsg, DescriptorProto versionMsg, string baseNs, Resolver resolveBase)
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
            EmitField(field, number, baseMsg.Name, baseNs, resolveBase, size, write, read, oneofName);
        }

        var type = $"global::{baseNs}.{baseMsg.Name}";
        sb.AppendLine($"public sealed class {baseMsg.Name}Serializer : global::Starlight.Protobuf.Core.ISerializer<{type}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static readonly {baseMsg.Name}Serializer Instance = new();");
        sb.AppendLine();
        EmitDescriptor(sb, baseMsg, versionMsg, baseNs, resolveBase);
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
    private static void EmitDescriptor(StringBuilder sb, DescriptorProto baseMsg, DescriptorProto versionMsg, string baseNs, Resolver resolve)
    {
        var versionByName = versionMsg.Fields.ToDictionary(f => f.Name, f => f.Number);
        var type = $"global::{baseNs}.{baseMsg.Name}";

        sb.AppendLine($"    public static readonly global::Starlight.Protobuf.Core.MessageDescriptor Descriptor =");
        sb.AppendLine($"        new global::Starlight.Protobuf.Core.MessageDescriptor(\"{baseMsg.Name}\", typeof({type}), new {FdType}[]");
        sb.AppendLine("        {");
        foreach (var field in baseMsg.Fields)
        {
            if (!versionByName.TryGetValue(field.Name, out var number)) continue;
            sb.AppendLine($"            {FieldDescriptorExpr(field, number, baseMsg, resolve)},");
        }

        sb.AppendLine("        });");
        sb.AppendLine();
    }

    private static string FieldDescriptorExpr(FieldDescriptorProto field, int number, DescriptorProto msg, Resolver resolve)
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

        return $"{head}, {PkType}.{Kind(field.type)}, {FrType}.{rule}{named})";
    }

    private static string Kind(FType type) => type switch
    {
        FType.TypeDouble => "Double",
        FType.TypeFloat => "Float",
        FType.TypeInt64 => "Int64",
        FType.TypeUint64 => "UInt64",
        FType.TypeInt32 => "Int32",
        FType.TypeFixed64 => "Fixed64",
        FType.TypeFixed32 => "Fixed32",
        FType.TypeBool => "Bool",
        FType.TypeString => "String",
        FType.TypeBytes => "Bytes",
        FType.TypeUint32 => "UInt32",
        FType.TypeSfixed32 => "SFixed32",
        FType.TypeSfixed64 => "SFixed64",
        FType.TypeSint32 => "SInt32",
        FType.TypeSint64 => "SInt64",
        FType.TypeEnum => "Enum",
        FType.TypeMessage => "Message",
        _ => throw new InvalidOperationException($"Unsupported proto type for descriptor: {type}"),
    };

    private static void EmitField(FieldDescriptorProto field, int number, string msgName, string baseNs, Resolver resolve,
        StringBuilder size, StringBuilder write, StringBuilder read, string? oneofName = null)
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

        size.AppendLine($"        if ({guard}) size += {tagLen} + {SizeCall(w, valueAcc)};");
        write.AppendLine($"        if ({guard})");
        write.AppendLine("        {");
        write.AppendLine($"            {RawTag(number, w.WireType)};");
        write.AppendLine($"            {WriteCall(w, valueAcc)};");
        write.AppendLine("        }");
        read.AppendLine($"                case {TagValue(number, w.WireType)}:");
        read.AppendLine($"                    {acc} = {ReadCall(w, "input")};");
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
