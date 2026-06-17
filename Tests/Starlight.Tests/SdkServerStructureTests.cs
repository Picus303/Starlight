using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Starlight.Common;
using Starlight.SDK;
using Starlight.SDK.Database;
using Xunit;

namespace Starlight.Tests;

public sealed class SdkServerStructureTests
{
    // Verifies that SDK service registration contributes its repository to the
    // main host container instead of relying on a nested web-only service graph.
    [Fact]
    public void AddSdkServer_RegistersAccountRepositoryInHostContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSdkServer(new StarlightConfig {
            Database = new DatabaseConfig {
                ConnectionString = "sqlite::memory:"
            }
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IAccountRepository>());
    }

    // Verifies that the SDK root endpoint is mapped onto the main application
    // pipeline and returns the expected health-style response.
    [Fact]
    public async Task MapSdkServer_MapsRootEndpointOnMainApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSdkServer(new StarlightConfig {
            Database = new DatabaseConfig {
                ConnectionString = "sqlite::memory:"
            }
        });

        var app = builder.Build();
        app.MapSdkServer();

        var routes = (IEndpointRouteBuilder)app;
        var endpoint = Assert.Single(
            routes.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/");

        var context = new DefaultHttpContext {
            RequestServices = app.Services,
            Response = {
                Body = new MemoryStream()
            }
        };

        Assert.NotNull(endpoint.RequestDelegate);
        await endpoint.RequestDelegate(context);
        context.Response.Body.Position = 0;

        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Starlight", body, StringComparison.Ordinal);
    }
}
