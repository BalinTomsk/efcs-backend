using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Http.Resilience;
using CsNews.News;
using CsNews.Sources;
using CsNews.Web;
using Polly;

namespace CsNews.Configuration;

/// <summary>
/// Registers csnews-env. A search source is registered only when its backend is configured; with none, the
/// service still starts and <c>/sources</c> lists nothing. The news data API (<c>/api/v1/csnews/*</c>) needs
/// <c>MYSQL_NEWS_URL</c> (+ <c>_USERNAME</c>/<c>_PASSWORD</c>, the keys docapi used); unset, its routes answer 503.
/// </summary>
public static class ServiceRegistration
{
    public static IReadOnlyList<string> AddCsNewsServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CsNewsOptions>(configuration.GetSection(CsNewsOptions.SectionName));
        var enabled = new List<string>();

        // vector + bm25: news_idx on CTRL (NEWSIDX_URL, e.g. http://news-idx-host:4324), one shared client.
        string? newsIdx = configuration["NEWSIDX_URL"];
        if (!string.IsNullOrWhiteSpace(newsIdx))
        {
            int timeout = configuration.GetValue($"{CsNewsOptions.SectionName}:Vector:TimeoutSeconds", 5);
            services.AddHttpClient(NewsIdxSource.HttpClientName, http =>
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
            services.AddSingleton<ISearchSource, Bm25Source>();
            enabled.Add("vector");
            enabled.Add("bm25");
        }

        // The news data API: the only MySQL news access on the platform (docapi calls it).
        string? mysqlUrl = configuration["MYSQL_NEWS_URL"];
        if (!string.IsNullOrWhiteSpace(mysqlUrl))
        {
            string connectionString = MySqlUrl.Build(mysqlUrl,
                configuration["MYSQL_NEWS_USERNAME"], configuration["MYSQL_NEWS_PASSWORD"]);
            services.AddSingleton<NewsDbGuard>();
            services.AddSingleton<INewsStore>(sp => new MySqlNewsStore(connectionString, sp.GetRequiredService<NewsDbGuard>()));
            if (configuration.GetValue("NEWS_SCHEMA_BOOTSTRAP", true))
            {
                services.AddHostedService(sp => new NewsSchemaBootstrap(connectionString,
                    sp.GetRequiredService<ILogger<NewsSchemaBootstrap>>()));
            }
            enabled.Add("news-db");
        }
        else
        {
            services.AddSingleton<INewsStore, UnconfiguredNewsStore>();
        }

        // A malformed body is our own 400 envelope, never ASP.NET's ProblemDetails; and a non-nullable property
        // is not implicitly [Required] (docapi sends explicit nulls for empty fields). PostConfigure: AddControllers
        // registers its own defaults after this method runs, and would overwrite a plain Configure.
        services.PostConfigure<MvcOptions>(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
        services.PostConfigure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = context =>
        {
            string message = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .Select(e => $"{(string.IsNullOrEmpty(e.Key) ? "body" : e.Key)}: {e.Value!.Errors[0].ErrorMessage}")
                .FirstOrDefault() ?? "invalid request";
            return new BadRequestObjectResult(ApiResponse.Fail("invalid_request", message));
        });

        services.AddSingleton<SearchService>();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        return enabled;
    }
}
