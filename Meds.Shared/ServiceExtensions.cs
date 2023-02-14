using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meds.Shared
{
    public static class ServiceExtensions
    {
        public static IServiceCollection AddSingletonAlias<TService, TImplementation>(this IServiceCollection collection)
            where TService : class where TImplementation : TService
        {
            return collection.AddSingleton<TService>(svc => svc.GetRequiredService<TImplementation>());
        }

        public static IServiceCollection AddSingletonAndHost<TService>(this IServiceCollection collection) where TService : class, IHostedService
        {
            return collection.AddSingleton<TService>().AddHostedAlias<TService>();
        }

        public static IServiceCollection AddHostedAlias<TService>(this IServiceCollection collection) where TService : class, IHostedService
        {
            return collection.AddHostedService<TService>(svc => svc.GetRequiredService<TService>());
        }
    }
}