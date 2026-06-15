using Microsoft.Extensions.Hosting;

namespace Starlight.Game;

public sealed class GateServerService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TODO: Listen for UDP connections on 22101, 22102.
        return Task.CompletedTask;
    }
}
