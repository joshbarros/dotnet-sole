using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sole.Raffles;

public class RaffleOptions
{
    /// <summary>How often each instance looks for raffles whose draw time has come.</summary>
    public int DrawIntervalMs { get; set; } = 1000;
}

/// <summary>Draws due raffles. Every instance runs one; the claim on each raffle is a conditional UPDATE, so only one draws it.</summary>
public sealed class RaffleDrawer(IServiceScopeFactory scopes, IOptions<RaffleOptions> options, ILogger<RaffleDrawer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(options.Value.DrawIntervalMs, 50)));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var drawn = await scope.ServiceProvider.GetRequiredService<RafflesHandler>().DrawDueAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (drawn > 0)
                    log.LogInformation("Drew {Count} raffles", drawn);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Drawing raffles failed; it will be tried again");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
