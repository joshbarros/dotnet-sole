using Sole.Shared;
using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sole.Claims;

public static class ClaimsModule
{
    public static IServiceCollection AddClaimsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Sole") ?? throw new InvalidOperationException("Connection string 'Sole' is not configured.");
        services.AddDbContext<ClaimsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ClaimsDbContext.SchemaName)));
        services.AddScoped<ClaimsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ClaimsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ClaimsDbContext>>("claims-db", tags: ["ready"]);

        messaging.AddOutbox<ClaimsDbContext>()
            .AddConsumer<ClaimsHandler>(Topology.ClaimsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
