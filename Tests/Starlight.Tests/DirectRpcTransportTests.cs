using Google.Protobuf.WellKnownTypes;
using Starlight.Rpc;
using Xunit;

namespace Starlight.Tests;

public sealed class DirectRpcTransportTests
{
    private const string Subject = "test.subject";

    // Verifies that publishing a raw RpcMessage delivers the payload to a
    // subscriber registered on the same subject.
    [Fact]
    public async Task Publish_DeliversMessageToSubscriber()
    {
        var transport = new DirectRpcTransport();
        var payload = new byte[] { 1, 2, 3 };
        RpcMessage? received = null;

        await transport.Subscribe(Subject, msg => {
            received = msg;
            return Task.CompletedTask;
        });

        await transport.Publish(Subject, new RpcMessage(payload));

        Assert.NotNull(received);
        Assert.Equal(payload, received!.Payload);
    }

    // Verifies that publish fan-out reaches every subscriber attached to the
    // subject, not just the first one.
    [Fact]
    public async Task Publish_DeliversToAllSubscribers()
    {
        var transport = new DirectRpcTransport();
        var count = 0;

        await transport.Subscribe(Subject, _ => {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await transport.Subscribe(Subject, _ => {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await transport.Publish(Subject, new RpcMessage([]));

        Assert.Equal(expected: 2, count);
    }

    // Verifies the documented fail-fast contract: if one subscriber throws, the
    // publish operation propagates the error and stops before later handlers run.
    [Fact]
    public async Task Publish_HandlerThrows_StopsDeliveryAndPropagatesError()
    {
        var transport = new DirectRpcTransport();
        var count = 0;

        await transport.Subscribe(Subject, _ => {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("boom");
        });

        await transport.Subscribe(Subject, _ => {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Publish(Subject, new RpcMessage([])));
        Assert.Equal(1, count);
    }

    // Verifies that publishing to an unsubscribed subject is a no-op rather than
    // an exception path.
    [Fact]
    public async Task Publish_NoSubscribers_DoesNotThrow()
    {
        var transport = new DirectRpcTransport();

        await transport.Publish("nobody.listening", new RpcMessage([]));
    }

    // Verifies that subject routing is exact and does not deliver messages to
    // subscribers on a different subject.
    [Fact]
    public async Task Publish_OnlyDeliversToMatchingSubject()
    {
        var transport = new DirectRpcTransport();
        var received = false;

        await transport.Subscribe(Subject, _ => {
            received = true;
            return Task.CompletedTask;
        });

        await transport.Publish("other.subject", new RpcMessage([]));

        Assert.False(received);
    }

    // Verifies that disposing the returned subscription actually detaches the
    // handler from future publishes.
    [Fact]
    public async Task DisposedSubscription_StopsReceiving()
    {
        var transport = new DirectRpcTransport();
        var count = 0;

        var subscription = await transport.Subscribe(Subject, _ => {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await transport.Publish(Subject, new RpcMessage([]));
        subscription.Dispose();
        await transport.Publish(Subject, new RpcMessage([]));

        Assert.Equal(expected: 1, count);
    }

    // Verifies the DirectRpcMessage optimization: protobuf publishes can be
    // handed back to typed subscribers without a real deserialize/clone step.
    [Fact]
    public async Task Publish_Message_SerializesToDirectRpcMessageSharingMetadata()
    {
        var transport = new DirectRpcTransport();
        var sent = new StringValue { Value = "hello" };
        StringValue? received = null;

        await transport.Subscribe<StringValue>(Subject, (msg, _) => {
            received = msg;
            return Task.CompletedTask;
        });

        await transport.Publish(Subject, sent);

        // DirectRpcMessage stashes the protobuf object in metadata, so the
        // exact same instance should come back out without real deserialization.
        Assert.Same(sent, received);
    }

    // Verifies the full request/reply flow over the in-memory transport,
    // including deserializing the request and receiving the typed response.
    [Fact]
    public async Task Request_ReturnsReplyFromHandler()
    {
        var transport = new DirectRpcTransport();

        await transport.Subscribe(Subject, async msg => {
            var request = msg.Deserialize<StringValue>();
            await msg.Reply(new StringValue { Value = $"echo:{request.Value}" });
        });

        var response = await transport.Request<StringValue, StringValue>(
            Subject, new StringValue { Value = "ping" });

        Assert.Equal("echo:ping", response.Value);
    }

    // Verifies the fail-fast path when no responder is subscribed to the target
    // subject at request time.
    [Fact]
    public async Task Request_NoResponder_ThrowsImmediately()
    {
        var transport = new DirectRpcTransport();

        // No subscribers on the subject, so this must fail fast rather than
        // burning the full timeout window.
        await Assert.ThrowsAsync<NoRespondersException>(() =>
            transport.Request<StringValue, StringValue>(
                Subject, new StringValue { Value = "ping" },
                TimeSpan.FromSeconds(30)));
    }

    // Verifies the timeout path when a responder exists but never publishes a
    // reply to the generated reply subject.
    [Fact]
    public async Task Request_ResponderNeverReplies_TimesOut()
    {
        var transport = new DirectRpcTransport();

        // A responder exists but never replies, so the timeout path still applies.
        await transport.Subscribe(Subject, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            transport.Request<StringValue, StringValue>(
                Subject, new StringValue { Value = "ping" },
                TimeSpan.FromMilliseconds(100)));
    }
}
