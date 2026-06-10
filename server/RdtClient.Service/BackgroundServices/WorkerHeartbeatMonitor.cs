using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace RdtClient.Service.BackgroundServices;

/// <summary>
///     Detects a <em>hung</em> background worker — the case where <see cref="TaskRunner" /> stops making
///     progress without throwing (so <c>BackgroundServiceExceptionBehavior.StopHost</c> can't catch it).
///     If the worker's heartbeat goes stale the process is force-exited; the container's
///     <c>restart: unless-stopped</c> policy then brings it back. This converts a silent zombie into a
///     crash-and-recover.
/// </summary>
public class WorkerHeartbeatMonitor(ILogger<WorkerHeartbeatMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Grace period after the worker is Ready before we'll consider exiting, so a slow startup
    ///     (e.g. a large Initialize) can't trip a false positive before the first tick lands.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        var readyAt = DateTimeOffset.UtcNow;

        logger.LogInformation("WorkerHeartbeatMonitor started (stale threshold {Threshold}).", TaskRunner.StaleThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Give the worker time to get going before we start judging it.
            if (DateTimeOffset.UtcNow - readyAt < StartupGrace)
            {
                continue;
            }

            if (!TaskRunner.IsHeartbeatStale)
            {
                continue;
            }

            var staleFor = DateTimeOffset.UtcNow - TaskRunner.LastTick;

            logger.LogCritical("Worker heartbeat stale ({StaleSeconds:n0}s since last tick, threshold {ThresholdSeconds:n0}s) — exiting for restart.",
                               staleFor.TotalSeconds,
                               TaskRunner.StaleThreshold.TotalSeconds);

            // Flush the log file before we yank the process — Environment.Exit won't run finalizers.
            Log.CloseAndFlush();

            // Use Environment.Exit (not StopApplication) to guarantee the process dies even if other
            // threads are wedged. Exit code 70 (EX_SOFTWARE) marks an internal software fault.
            Environment.Exit(70);
        }
    }
}
