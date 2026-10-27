using Sole.Shared;
using Sole.Shared.Contracts;
using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sole.Raffles;

public static class RafflesModule
{
    public static IServiceCollection AddRafflesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Sole") ?? throw new InvalidOperationException("Connection string 'Sole' is not configured.");
        services.AddDbContext<RafflesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", RafflesDbContext.SchemaName)));
        services.Configure<RaffleOptions>(config.GetSection("Raffles"));
        services.AddScoped<RafflesHandler>();
        services.AddHostedService<RaffleDrawer>();
        services.AddScoped<IRaffleCatalog, RaffleCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<RafflesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<RafflesDbContext>>("pairs-db", tags: ["ready"]);

        messaging.AddOutbox<RafflesDbContext>()
            .AddConsumer<RafflesHandler>(Topology.RafflesQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
