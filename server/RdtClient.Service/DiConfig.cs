using System.IO.Abstractions;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using RateLimitHeaders.Polly;
using RdtClient.Service.BackgroundServices;
using RdtClient.Service.Helpers;
using RdtClient.Service.Middleware;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Wrappers;

namespace RdtClient.Service;

public static class DiConfig
{
    public const String RD_CLIENT = "RdClient";
    public const String TORBOX_CLIENT = "TorBoxClient";
    public const String TORBOX_CLIENT_SLOW = "TorBoxClientSlow";
    public static readonly String UserAgent = $"rdt-client {Assembly.GetEntryAssembly()?.GetName().Version}";

    public static void RegisterRdtServices(this IServiceCollection services)
    {
        services.AddMemoryCache();

        services.AddSingleton<IAllDebridNetClientFactory, AllDebridNetClientFactory>();
        services.AddScoped<AllDebridDebridClient>();

        services.AddSingleton<IRateLimitCoordinator, RateLimitCoordinator>();
        services.AddSingleton<IProcessFactory, ProcessFactory>();
        services.AddSingleton<IFileSystem, FileSystem>();

        services.AddScoped<Authentication>();
        services.AddScoped<IDownloads, Downloads>();
        services.AddScoped<Downloads>();
        services.AddScoped<PremiumizeDebridClient>();
        services.AddScoped<QBittorrent>();
        services.AddScoped<Sabnzbd>();
        services.AddScoped<RemoteService>();
        services.AddScoped<RealDebridDebridClient>();
        services.AddScoped<Settings>();
        services.AddScoped<TorBoxDebridClient>();
        services.AddScoped<Torrents>();
        services.AddScoped<TorrentRunner>();
        services.AddScoped<DebridLinkClient>();

        services.AddSingleton<IDownloadableFileFilter, DownloadableFileFilter>();
        services.AddSingleton<ITrackerListGrabber, TrackerListGrabber>();
        services.AddSingleton<IEnricher, Enricher>();

        services.AddSingleton<IAuthorizationHandler, AuthSettingHandler>();
        services.AddScoped<IAuthorizationHandler, SabnzbdHandler>();

        services.AddHostedService<DiskSpaceMonitor>();
        services.AddHostedService<ProviderUpdater>();
        services.AddHostedService<Startup>();
        services.AddHostedService<TaskRunner>();
        services.AddHostedService<UpdateChecker>();
        services.AddHostedService<WatchFolderChecker>();
        services.AddHostedService<WebsocketsUpdater>();
        services.AddHostedService<WorkerHeartbeatMonitor>();
    }

    public static void RegisterHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient();

        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigureHttpClient(httpClient =>
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            });
        });

        services.AddTransient<RateLimitHandler>();

        services.AddHttpClient(RD_CLIENT)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                .AddHttpMessageHandler<RateLimitHandler>()
                .AddResilienceHandler("rd_client_handler", ConfigureResiliencePipeline);

        // This likely works for most providers, but should be verified and then the providers changed
        // to this HTTP client for added resilience.
        services.AddHttpClient(TORBOX_CLIENT)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                .AddResilienceHandler("torbox_client_handler", ConfigureResiliencePipeline);

        services.AddHttpClient(TORBOX_CLIENT_SLOW)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                .AddHttpMessageHandler<RateLimitHandler>()
                .AddResilienceHandler("torbox_client_handler_slow", ConfigureResiliencePipeline);
    }

    private static void ConfigureResiliencePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRateLimitHeaders(options =>
        {
            options.EnableProactiveThrottling = true;
        });

        builder.AddRetry(new()
        {
            ShouldHandle = args => args.Outcome switch
            {
                { Exception: HttpRequestException } => PredicateResult.True(),
                { Result.StatusCode: HttpStatusCode.RequestTimeout } => PredicateResult.True(),
                { Result.StatusCode: HttpStatusCode.TooManyRequests } => PredicateResult.True(),
                _ => PredicateResult.False()
            },
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            DelayGenerator = args =>
            {
                if (args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests } response)
                {
                    var delay = RateLimitHandler.GetRetryAfterDelay(response);
                    var timeout = TimeSpan.FromSeconds(Settings.Get.Provider.Timeout);

                    if (delay >= timeout)
                    {
                        return new((TimeSpan?)null);
                    }

                    return new(delay);
                }

                return new((TimeSpan?)null);
            }
        });

        // Defense in depth: when the debrid endpoint is flapping (connection errors / timeouts), stop
        // issuing calls for a cooldown instead of having every request independently retry-and-time-out,
        // which maximizes thread/socket pressure. Placed outside the timeout (so it counts timeouts) and
        // inside the retry (so retries feed the failure window). Deliberately does NOT handle 429 — those
        // are normal rate-limit backoff handled proactively by AddRateLimitHeaders / RateLimitHandler.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => args.Outcome switch
            {
                { Exception: HttpRequestException } => PredicateResult.True(),
                { Exception: TimeoutRejectedException } => PredicateResult.True(),
                { Result.StatusCode: HttpStatusCode.RequestTimeout } => PredicateResult.True(),
                _ => PredicateResult.False()
            },
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30)
        });

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = _ => new(TimeSpan.FromSeconds(Settings.Get.Provider.Timeout))
        });
    }
}
