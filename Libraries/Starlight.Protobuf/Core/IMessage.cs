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
