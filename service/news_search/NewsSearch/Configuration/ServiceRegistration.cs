using Microsoft.Extensions.Http.Resilience;
using NewsSearch.Sources;
using NewsSearch.Web;
using Polly;

namespace NewsSearch.Configuration;

/// <summary>
/// Registers news_search. A source is registered only when its backend is configured; with none, the service
/// still starts and <c>/sources</c> lists nothing.
/// </summary>
public static class ServiceRegistration
{
    public static IReadOnlyList<string> AddNewsSearchServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NewsSearchOptions>(configuration.GetSection(NewsSearchOptions.SectionName));
        var enabled = new List<string>();

        // vector: news_idx on CTRL (NEWSIDX_URL, e.g. http://news-idx-host:4324).
        string? newsIdx = configuration["NEWSIDX_URL"];
        if (!string.IsNullOrWhiteSpace(newsIdx))
        {
            int timeout = configuration.GetValue($"{NewsSearchOptions.SectionName}:Vector:TimeoutSeconds", 5);
            services.AddHttpClient(VectorSource.HttpClientName, http =>
                {
                    http.BaseAddress = new Uri(newsIdx.TrimEnd('/') + "/");
                    http.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns the timeouts
                })
                .AddResilienceHandler("newsidx", pipeline =>
                {
                    // Outermost: the whole call, retry included, never takes longer than the budget.
                    pipeline.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, timeout)));
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 1,
                        Delay = TimeSpan.FromMilliseconds(200),
                        BackoffType = DelayBackoffType.Constant,
                    });
                    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        FailureRatio = 0.5,
                        MinimumThroughput = 5,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        BreakDuration = TimeSpan.FromSeconds(30),
                    });
                    // Per attempt, so one slow attempt still leaves time for the retry.
                    pipeline.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, timeout) * 0.6));
                });
            services.AddSingleton<ISearchSource, VectorSource>();
            enabled.Add("vector");
        }

        services.AddSingleton<SearchService>();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        return enabled;
    }
}
