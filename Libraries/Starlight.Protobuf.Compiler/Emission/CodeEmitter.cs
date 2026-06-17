using System.Text;
using Google.Protobuf.Reflection;

namespace Starlight.Protobuf.Compiler;

/// <summary>
/// Emits POCOs, per-version hardcoded serializers, and version registries from
/// parsed proto descriptors. Covers proto3 scalars, enums, bytes, nested
/// messages, packed/unpacked repeated, maps, explicit proto3 <c>optional</c>
/// presence, and <c>oneof</c>.
/// </summary>
internal static partial class CodeEmitter
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
}
