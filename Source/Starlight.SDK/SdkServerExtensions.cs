using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Starlight.Common;
using Starlight.Database.DependencyInjection;
using Starlight.SDK.Database;
using Starlight.SDK.Database.Impl;

namespace Starlight.SDK;

public static class SdkServerExtensions
{
    public static IServiceCollection AddSdkServer(this IServiceCollection collection, StarlightConfig config)
    {
        var provider = DatabaseHelper.ParseProvider(config.Database.ConnectionString, out var connString);

        switch (provider)
        {
            case ProviderType.Sqlite:
                {
                    collection.AddStarlightDatabase(connString, config.Database.Sqlite, typeof(SdkServerExtensions).Assembly);
                    collection.AddSingleton<IAccountRepository, SqliteAccountRepository>();
                    break;
                }
            default:
                throw new NotSupportedException($"Unsupported or missing database provider '{provider?.ToString() ?? "<null>"}'.");
        }

        return collection;
    }

    public static IEndpointRouteBuilder MapSdkServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", static () => Results.Ok("Starlight"));
        return endpoints;
    }
}
