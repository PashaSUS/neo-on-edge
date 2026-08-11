using NeoOnEdge.Api.Services;

namespace NeoOnEdge.Api.Extensions;

internal static class ServiceCollectionQuoteExtensions
{
    public static IServiceCollection AddNeoRouteQuoteServices(this IServiceCollection services)
    {
        services.AddSingleton<AlcorRouterService>();
        services.AddScoped<NeoRoutePoolSnapshotProvider>();
        services.AddScoped<NeoRoutePathFinder>();
        services.AddScoped<NeoRouteQuoteService>();
        services.AddSingleton<NeoRouteQuoteSimulator>();
        services.AddSingleton<NeoRouteResultFactory>();
        return services;
    }
}
