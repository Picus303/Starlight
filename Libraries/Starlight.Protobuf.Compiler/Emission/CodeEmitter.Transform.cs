using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FType = Google.Protobuf.Reflection.FieldDescriptorProto.Type;

namespace Starlight.Protobuf.Compiler;

internal static partial class CodeEmitter
{
    // ---- field value transforms (add / xor / fop / mask) --------------------

    /// <summary>
    /// An invertible integer transform applied to a field on the wire. <see cref="Ops"/>
    /// is the encode chain (real -&gt; wire), one char per step in <c>{ '+', '-', '^' }</c>,
    /// paired positionally with <see cref="Operands"/>. Decode (wire -&gt; real) applies the
    /// inverse of each op in reverse order. An unparseable <c>mask</c> stores its raw
    /// expression in <see cref="RawMask"/> and cannot be inverted (read returns the raw wire value).
    /// </summary>
    internal sealed class Transform
    {
        public string Ops = "";
        public long[] Operands = System.Array.Empty<long>();
        public string? RawMask;

        public bool CanDecode => RawMask is null;
    }

    /// <summary>Per-message field transform lookup, keyed by message name then field (proto) name.</summary>
    internal sealed class TransformTable
    {
        private readonly Dictionary<string, Dictionary<string, Transform>> _map;

        public TransformTable(Dictionary<string, Dictionary<string, Transform>> map) => _map = map;

        public Transform? Get(string message, string field) =>
            _map.TryGetValue(message, out var fields) && fields.TryGetValue(field, out var t) ? t : null;
    }

    /// <summary>Integer kinds the transforms apply to. Floats, bools, strings, enums and messages are excluded.</summary>
    private static bool IsTransformable(FType type) => type switch
    {
        FType.TypeInt32 or FType.TypeInt64 or FType.TypeUint32 or FType.TypeUint64
            or FType.TypeSint32 or FType.TypeSint64 or FType.TypeFixed32 or FType.TypeFixed64
            or FType.TypeSfixed32 or FType.TypeSfixed64 => true,
        _ => false,
    };

    // ---- codegen ------------------------------------------------------------

    /// <summary>Encode expression (real -&gt; wire) for <paramref name="valueExpr"/>, cast back to <paramref name="csType"/>.</summary>
    private static string Encode(Transform? t, string valueExpr, string csType)
    {
        if (t is null) return valueExpr;

        if (t.RawMask is not null)
        {
            var expr = Regex.Replace(t.RawMask, @"\bvalue\b", _ => $"((long)({valueExpr}))");
            return $"unchecked(({csType})({expr}))";
        }

        var inner = $"(long){valueExpr}";
        for (var i = 0; i < t.Ops.Length; i++)
            inner = $"({inner} {t.Ops[i]} {Lit(t.Operands[i])})";
        return $"unchecked(({csType})({inner}))";
    }

    /// <summary>Decode expression (wire -&gt; real) wrapping <paramref name="readExpr"/>, cast to <paramref name="csType"/>.</summary>
    private static string Decode(Transform? t, string readExpr, string csType)
    {
        if (t is null || !t.CanDecode) return readExpr;

        var inner = $"(long){readExpr}";
        for (var i = t.Ops.Length - 1; i >= 0; i--)
            inner = $"({inner} {Inverse(t.Ops[i])} {Lit(t.Operands[i])})";
        return $"unchecked(({csType})({inner}))";
    }

    private static char Inverse(char op) => op switch { '+' => '-', '-' => '+', _ => '^' };

    private static string Lit(long v) => $"({v.ToString(CultureInfo.InvariantCulture)}L)";

    // ---- scanning -----------------------------------------------------------

    /// <summary>
    /// Text-scans proto sources for field transform options. protobuf-net can't resolve
    /// these custom options, so we read their literal values directly, attributing each
    /// option-bearing field to its nearest enclosing <c>message</c>.
    /// </summary>
    public static TransformTable ScanTransforms(IEnumerable<string> contents)
    {
        var map = new Dictionary<string, Dictionary<string, Transform>>();

        var token = new Regex(
            @"(?<open>(?:message|enum|oneof)\s+(?<oname>\w+)\s*\{)" +
            @"|(?<close>\})" +
            @"|(?<field>(?<fname>\w+)\s*=\s*\d+\s*\[(?<opts>[^\]]*)\])",
            RegexOptions.Singleline);

        foreach (var content in contents)
        {
            var src = StripComments(content);
            var scope = new Stack<string?>();

            foreach (Match m in token.Matches(src))
            {
                if (m.Groups["open"].Success)
                {
                    var keyword = m.Value.TrimStart()[0];
                    scope.Push(keyword == 'm' ? m.Groups["oname"].Value : null);
                }
                else if (m.Groups["close"].Success)
                {
                    if (scope.Count > 0) scope.Pop();
                }
                else
                {
                    var message = scope.FirstOrDefault(s => s != null);
                    if (message is null) continue;

                    var t = BuildTransform(m.Groups["opts"].Value);
                    if (t is null) continue;

                    if (!map.TryGetValue(message, out var fields))
                        map[message] = fields = new Dictionary<string, Transform>();
                    fields[m.Groups["fname"].Value] = t;
                }
            }
        }

        return new TransformTable(map);
    }

    private static Transform? BuildTransform(string opts)
    {
        long? add = null, xor = null;
        string? fop = null, mask = null;

        foreach (Match p in Regex.Matches(opts, @"(\w+)\s*=\s*(?:""([^""]*)""|(-?\d+))"))
        {
            var key = p.Groups[1].Value;
            var str = p.Groups[2].Success ? p.Groups[2].Value : null;
            var num = p.Groups[3].Success && long.TryParse(p.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (long?) null;

            switch (key)
            {
                case "add": add = num; break;
                case "xor": xor = num; break;
                case "fop": fop = str; break;
                case "mask": mask = str; break;
            }
        }

        // `mask` is the manual alternative; when present it wins over add/xor.
        if (mask is not null)
            return ParseMask(mask) ?? new Transform { RawMask = mask };

        if (add.HasValue && xor.HasValue)
        {
            // fop = "first operation": which of add/xor is applied first on encode.
            return fop == "xor"
                ? new Transform { Ops = "^+", Operands = new[] { xor.Value, add.Value } }
                : new Transform { Ops = "+^", Operands = new[] { add.Value, xor.Value } };
        }

        if (add.HasValue) return new Transform { Ops = "+", Operands = new[] { add.Value } };
        if (xor.HasValue) return new Transform { Ops = "^", Operands = new[] { xor.Value } };
        return null;
    }

    /// <summary>
    /// Parses a left-deep, fully-parenthesized mask such as <c>(value - 49379) ^ 11523</c>
    /// into an invertible op-chain. Returns null for anything that doesn't reduce to
    /// <c>value</c> wrapped in a chain of <c>(sub OP integer)</c> steps.
    /// </summary>
    private static Transform? ParseMask(string mask)
    {
        var ops = new StringBuilder();
        var operands = new List<long>();
        return Walk(mask) ? new Transform { Ops = ops.ToString(), Operands = operands.ToArray() } : null;

        bool Walk(string expr)
        {
            expr = expr.Trim();
            expr = StripOuterParens(expr);
            if (expr == "value") return true;

            // Find the last top-level binary operator (the outermost / last-applied op).
            var depth = 0;
            for (var i = expr.Length - 1; i > 0; i--)
            {
                var c = expr[i];
                if (c == ')') depth++;
                else if (c == '(') depth--;
                else if (depth == 0 && (c == '+' || c == '-' || c == '^'))
                {
                    var right = expr.Substring(i + 1).Trim();
                    if (!long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var operand))
                        return false;
                    if (!Walk(expr.Substring(0, i))) return false;
                    ops.Append(c);
                    operands.Add(operand);
                    return true;
                }
            }

            return false;
        }
    }

    private static string StripOuterParens(string expr)
    {
        while (expr.Length >= 2 && expr[0] == '(' && expr[expr.Length - 1] == ')')
        {
            // Only strip when the leading '(' matches the trailing ')'.
            var depth = 0;
            var wraps = true;
            for (var i = 0; i < expr.Length; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')') depth--;
                if (depth == 0 && i < expr.Length - 1) { wraps = false; break; }
            }

            if (!wraps) break;
            expr = expr.Substring(1, expr.Length - 2).Trim();
        }

        return expr;
    }

    private static string StripComments(string content)
    {
        // Block comments first, then line comments.
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"//[^\r\n]*", "");
        return content;
    }
}
