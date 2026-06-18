namespace Starlight.Protobuf.Core;

/// <summary>
/// Slim marker for a protocol message. Messages are pure POCOs; all
/// (de)serialization lives in an external <see cref="ISerializer{T}"/>.
/// </summary>
public interface IMessage
{
    /// <summary>
    /// Fields seen on the wire during deserialization that had no matching
    /// base field (obfuscated / unknown). Captured for inspection, never
    /// re-emitted on serialize. <c>null</c> until the first unknown field is
    /// captured, so the common case allocates nothing.
    /// </summary>
    UnknownFieldSet? UnknownFields { get; set; }
}

/// <summary>
/// Self-typed message marker. The type parameter exists so generated code and
/// extension methods can recover the concrete message type.
/// </summary>
public interface IMessage<T> : IMessage where T : IMessage<T>;

/// <summary>
/// A message that owns its single, canonical serializer. Implemented only by
/// version-independent messages (<c>extra.proto</c>), which have exactly one
/// serializer regardless of protocol version. The static member lets the
/// argument-free <c>ToByteArray()</c> / <c>MergeFrom(byte[])</c> extensions find
/// that serializer with no runtime lookup. Versioned messages deliberately do
/// not implement this, so callers must keep supplying a per-version serializer.
/// </summary>
public interface ISelfSerializable<T> : IMessage<T> where T : ISelfSerializable<T>
{
    /// <summary>The one serializer for this message type.</summary>
    abstract static ISerializer<T> Serializer { get; }
}
