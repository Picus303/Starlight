using Google.Protobuf.WellKnownTypes;
using Starlight.Rpc;
using Xunit;

namespace Starlight.Tests;

public sealed class RpcMessageTests
{
    // Verifies that replying without a configured reply subject or transport
    // fails explicitly instead of silently dropping the response.
    [Fact]
    public async Task Reply_WithoutReplyConfiguration_Throws()
    {
        var message = new TestRpcMessage([]);

        await Assert.ThrowsAsync<NullReferenceException>(() => message.Reply((RpcMessage?)null));
    }

    // Verifies that Reply(null) still publishes a concrete reply message, using
    // an empty payload as the transport-level fallback.
    [Fact]
    public async Task Reply_Null_SendsEmptyMessage()
    {
        var transport = new DirectRpcTransport();
        RpcMessage? received = null;

        await transport.Subscribe("reply.subject", msg => {
            received = msg;
            return Task.CompletedTask;
        });

        var message = new TestRpcMessage([1, 2, 3], "reply.subject", transport);

        await message.Reply((RpcMessage?)null);

        Assert.NotNull(received);
        Assert.Empty(received!.Payload);
    }

    // Verifies that typed subscribers are only invoked for payloads that can be
    // deserialized to the requested protobuf type.
    [Fact]
    public async Task SubscribeTyped_InvalidPayload_DoesNotInvokeHandler()
    {
        var transport = new DirectRpcTransport();
        var invoked = false;

        await transport.Subscribe<StringValue>("typed.subject", (_, _) => {
            invoked = true;
            return Task.CompletedTask;
        });

        await transport.Publish("typed.subject", new RpcMessage([0x0A, 0x01]));

        Assert.False(invoked);
    }

    private sealed class TestRpcMessage : RpcMessage
    {
        public TestRpcMessage(byte[] payload, string? replySubject = null, RpcTransport? transport = null) : base(payload)
        {
            ReplySubject = replySubject;
            Transport = transport;
        }
    }
}
