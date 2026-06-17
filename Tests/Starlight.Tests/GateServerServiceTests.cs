using Microsoft.Extensions.Logging.Abstractions;
using Starlight.Common;
using Starlight.Game;
using Xunit;
using System.Reflection;

namespace Starlight.Tests;

public sealed class GateServerServiceTests
{
    // Verifies that a KCP startup failure is treated as fatal and propagated
    // back to the host instead of being logged and swallowed.
    [Fact]
    public async Task ExecuteAsync_WhenKcpStartupFails_PropagatesError()
    {
        var service = new GateServerService(
            NullLogger<GateServerService>.Instance,
            new StarlightConfig {
                Server = new ServerConfig {
                    Game = new GameConfig {
                        BindAddress = "not-an-ip-address",
                        BindPort = 22102
                    }
                }
            });

        var method = typeof(GateServerService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(service, [CancellationToken.None])!;

        await Assert.ThrowsAsync<FormatException>(() => task);
    }
}
