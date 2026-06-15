using Common.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Starlight.SDK;

public sealed class HttpServerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSerilog();
        
        // NOTE: If you wish to use SSL, do so behind a reverse proxy.
        builder.WebHost.UseUrls($"http://{Config.Server.Http.BindAddress}:{Config.Server.Http.BindPort}");

        var app = builder.Build();

        app.MapGet("/", () => Results.Ok("Starlight"));

        await app.RunAsync(stoppingToken);
    }
}
