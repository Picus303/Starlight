using Google.Protobuf;

namespace Starlight.Protobuf.Core;

/// <summary>Ergonomic wrappers around <see cref="ISerializer{T}"/> for the common byte[] paths.</summary>
public static class MessageExtensions
{
    /// <summary>Serializes <paramref name="message"/> to a right-sized byte array using <paramref name="serializer"/>.</summary>
    public static byte[] ToByteArray<T>(this T message, ISerializer<T> serializer) where T : IMessage
    {
        var buffer = new byte[serializer.CalculateSize(message)];
        using var output = new CodedOutputStream(buffer);
        serializer.Serialize(message, output);
        output.CheckNoSpaceLeft();
        return buffer;
    }

    /// <summary>Merges the wire data in <paramref name="data"/> into <paramref name="message"/> using <paramref name="serializer"/>.</summary>
    public static void MergeFrom<T>(this T message, ISerializer<T> serializer, byte[] data) where T : IMessage
    {
        using var input = new CodedInputStream(data);
        serializer.Deserialize(message, input);
    }

    /// <summary>
    /// Serializes <paramref name="message"/> using its own canonical serializer.
    /// Available only for version-independent messages (<see cref="ISelfSerializable{T}"/>),
    /// which have a single serializer; versioned messages must use the
    /// <see cref="ToByteArray{T}(T, ISerializer{T})"/> overload.
    /// </summary>
    public static byte[] ToByteArray<T>(this T message) where T : ISelfSerializable<T> =>
        message.ToByteArray(T.Serializer);

    /// <summary>
    /// Merges <paramref name="data"/> into <paramref name="message"/> using its own
    /// canonical serializer. Available only for version-independent messages
    /// (<see cref="ISelfSerializable{T}"/>).
    /// </summary>
    public static void MergeFrom<T>(this T message, byte[] data) where T : ISelfSerializable<T> =>
        message.MergeFrom(T.Serializer, data);
}
