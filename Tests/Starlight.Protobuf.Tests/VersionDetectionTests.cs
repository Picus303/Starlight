using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Starlight.Game.Protocol;
using Starlight.Game.Protocol.V66;
using Starlight.Protobuf.DependencyInjection;
using Starlight.Protobuf.Registry;
using Xunit;

namespace Starlight.Protobuf.Tests;

/// <summary>
/// Version detection + DI initialization. Covers assembly discovery,
/// first-packet resolution, version lookup, collision tie-break, and the
/// <c>AddStarlightProtocol</c> DI extension.
/// </summary>
public sealed class VersionDetectionTests
{
    private static IProtocolRegistryProvider Provider() =>
        new ProtocolRegistryProvider(ProtocolHelper.DiscoverRegistries());

    [Fact]
    public void Discover_FindsCompiledV66Registry()
    {
        var registries = ProtocolHelper.DiscoverRegistries();
        Assert.Contains(registries, r => r is V66ProtocolRegistry);
    }

    [Fact]
    public void ResolveByFirstPacket_ReturnsV66_ForGetPlayerTokenReq()
    {
        var registry = Provider().ResolveByFirstPacket(GetPlayerTokenReq.CmdId);

        Assert.NotNull(registry);
        Assert.Equal("V66", registry!.Version);
    }

    [Fact]
    public void ResolveByFirstPacket_ReturnsNull_ForUnknownCmdId()
    {
        Assert.Null(Provider().ResolveByFirstPacket(-1));
    }

    [Fact]
    public void GetByVersion_IsCaseInsensitive()
    {
        var provider = Provider();
        Assert.NotNull(provider.GetByVersion("V66"));
        Assert.NotNull(provider.GetByVersion("v66"));
        Assert.Null(provider.GetByVersion("V999"));
    }

    [Fact]
    public void FirstPacketCollision_PrefersNewestVersion()
    {
        const int sharedCmdId = 4242;
        var provider = new ProtocolRegistryProvider(
        [
            new FakeRegistry("V64", sharedCmdId),
            new FakeRegistry("V66", sharedCmdId),
            new FakeRegistry("V65", sharedCmdId),
        ]);

        Assert.Equal("V66", provider.ResolveByFirstPacket(sharedCmdId)!.Version);
    }

    [Fact]
    public void DuplicateVersion_Throws_WithBothTypesNamed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProtocolRegistryProvider(
            [
                new FakeRegistry("V66", 1),
                new FakeRegistry("V66", 2),
            ]));

        Assert.Contains("V66", ex.Message);
    }

    [Fact]
    public void AddStarlightProtocol_RegistersResolvableSingletonProvider()
    {
        var services = new ServiceCollection();
        services.AddStarlightProtocol();
        using var sp = services.BuildServiceProvider();

        var provider = sp.GetRequiredService<IProtocolRegistryProvider>();

        Assert.Same(provider, sp.GetRequiredService<IProtocolRegistryProvider>());
        Assert.Equal("V66", provider.ResolveByFirstPacket(GetPlayerTokenReq.CmdId)!.Version);
    }

    private sealed class FakeRegistry(string version, params int[] knownFirst) : ProtocolRegistry
    {
        public override string Version { get; } = version;
        public override IReadOnlySet<int> KnownFirst { get; } = new HashSet<int>(knownFirst);

        public override int GetCmdId(Starlight.Protobuf.Core.IMessage message) => throw new NotSupportedException();
        public override Starlight.Protobuf.Core.IMessage Create(int cmdId) => throw new NotSupportedException();
        public override int CalculateSize(Starlight.Protobuf.Core.IMessage message) => throw new NotSupportedException();
        public override void Serialize(Starlight.Protobuf.Core.IMessage message, CodedOutputStream output) => throw new NotSupportedException();
        public override void Deserialize(Starlight.Protobuf.Core.IMessage message, CodedInputStream input) => throw new NotSupportedException();
    }
}
