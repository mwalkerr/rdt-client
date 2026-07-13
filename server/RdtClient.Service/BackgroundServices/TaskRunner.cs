using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services;

namespace RdtClient.Service.BackgroundServices;

public class TaskRunner(ILogger<TaskRunner> logger, IServiceProvider serviceProvider) : BackgroundService
{
    /// <summary>
    ///     Wall-clock time of the last worker loop iteration. Read by <see cref="WorkerHeartbeatMonitor" />
    ///     and the /health endpoint to detect a hung (no-exception) worker. Seeded to process start so the
    ///     worker is considered healthy until the first tick lands.
    /// </summary>
    public static DateTimeOffset LastTick { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Staleness window: if no loop iteration has run within this span the worker is hung.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    /// <summary>True once the worker has gone longer than <see cref="StaleThreshold" /> without a tick.</summary>
    public static Boolean IsHeartbeatStale => DateTimeOffset.UtcNow - LastTick > StaleThreshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("TaskRunner started.");

        try
        {
            using var startupScope = serviceProvider.CreateScope();
            var startupRunner = startupScope.ServiceProvider.GetRequiredService<TorrentRunner>();
            await startupRunner.Initialize();
        }
        catch (Exception ex)
        {
            // Don't let a transient startup hiccup (e.g. a brief DB lock) escape to StopHost and
            // crash-loop the container. The main loop's Tick is resilient and will recover.
            logger.LogError(ex, "Error during TorrentRunner initialization, continuing into main loop: {Message}", ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Heartbeat: bump every iteration regardless of Tick outcome so a hung worker (no
            // exception thrown) shows up as a stale LastTick to the monitor / health endpoint.
            LastTick = DateTimeOffset.UtcNow;

            using var scope = serviceProvider.CreateScope();
            var torrentRunner = scope.ServiceProvider.GetRequiredService<TorrentRunner>();

            try
            {
                await torrentRunner.Tick();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    try
                    {
                        var proposedValues = entry.CurrentValues;
                        var databaseValues = await entry.GetDatabaseValuesAsync(stoppingToken);

                        logger.LogWarning("DbUpdateConcurrencyException occurred:");
                        logger.LogWarning("Proposed Values:");
                        logger.LogWarning(JsonSerializer.Serialize(proposedValues));
                        logger.LogWarning("Database Values:");
                        logger.LogWarning(JsonSerializer.Serialize(databaseValues));
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Unexpected error occurred in TaskRunner: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        logger.LogInformation("TaskRunner stopped.");
    }
}
