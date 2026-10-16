using NewsIdx.Data;
using NewsIdx.Embedding;
using NewsIdx.Indexing;
using NewsIdx.Messaging;
using NewsIdx.Search;
using NewsIdx.Web;
using Qdrant.Client;

namespace NewsIdx.Configuration;

/// <summary>
/// Registers every news_idx component. What runs depends on which flat keys are set:
///
/// <list type="table">
///   <item><term><c>MYSQL_NEWS_URL</c></term><description>articles come from MySQL; unset → no articles
///   (in-memory backing) so the service still starts on a workstation.</description></item>
///   <item><term><c>QDRANT_URL</c></term><description>gRPC endpoint, default <c>http://localhost:6334</c>;
///   <c>QDRANT_API_KEY</c> optional.</description></item>
///   <item><term><c>RABBITMQ_MANAGEMENT_URL</c> + <c>_USERNAME</c> + <c>_PASSWORD</c></term><description>the
///   event consumer runs; any of them unset → no consumer (search and sync still work).</description></item>
/// </list>
/// </summary>
public static class ServiceRegistration
{
    public sealed record Wiring(bool MySql, bool Events, string QdrantUrl);

    public static Wiring AddNewsIdxServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NewsIdxOptions>(configuration.GetSection(NewsIdxOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddNewsIdxResiliencePipelines();

        // Articles.
        bool useMySql = !string.IsNullOrWhiteSpace(configuration["MYSQL_NEWS_URL"]);
        if (useMySql)
        {
            var source = new MySqlNewsArticleSource(MySqlUrl.Build(
                configuration["MYSQL_NEWS_URL"]!, configuration["MYSQL_NEWS_USERNAME"], configuration["MYSQL_NEWS_PASSWORD"]));
            services.AddSingleton(source);
            services.AddSingleton<INewsArticleSource>(source);
        }
        else
        {
            services.AddSingleton<INewsArticleSource, InMemoryNewsArticleSource>();
        }

        // Model — constructed on first use, so a test host that replaces it never loads the ONNX file.
        services.AddSingleton<ITextEmbedder, BgeSmallEmbedder>();

        // Qdrant.
        string qdrantUrl = string.IsNullOrWhiteSpace(configuration["QDRANT_URL"]) ? "http://localhost:6334" : configuration["QDRANT_URL"]!.Trim();
        services.AddSingleton(sp =>
        {
            int timeout = configuration.GetValue($"{NewsIdxOptions.SectionName}:Qdrant:TimeoutSeconds", 30);
            return new QdrantClient(new Uri(qdrantUrl), NullIfBlank(configuration["QDRANT_API_KEY"]),
                TimeSpan.FromSeconds(timeout), sp.GetRequiredService<ILoggerFactory>());
        });
        services.AddSingleton<QdrantVectorStore>();
        services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<QdrantVectorStore>());

        // Indexing + search.
        services.AddSingleton<NewsIndexer>();
        services.AddSingleton<NewsSyncService>();
        services.AddSingleton<NewsSearchService>();
        services.AddSingleton<IndexBootstrap>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexBootstrap>());

        // Events.
        string? rabbitUrl = NullIfBlank(configuration["RABBITMQ_MANAGEMENT_URL"]);
        string? rabbitUser = NullIfBlank(configuration["RABBITMQ_USERNAME"]);
        string? rabbitPassword = NullIfBlank(configuration["RABBITMQ_PASSWORD"]);
        bool useEvents = rabbitUrl is not null && rabbitUser is not null && rabbitPassword is not null;
        if (useEvents)
        {
            int timeout = configuration.GetValue($"{NewsIdxOptions.SectionName}:Events:HttpTimeoutSeconds", 10);
            services.AddHttpClient<IMessageQueue, RabbitMqManagementClient>(http =>
                RabbitMqManagementClient.Configure(http, rabbitUrl!, rabbitUser!, rabbitPassword!, TimeSpan.FromSeconds(timeout)));
            services.AddHostedService<NewsEventConsumer>();
        }

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();

        return new Wiring(useMySql, useEvents, qdrantUrl);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
