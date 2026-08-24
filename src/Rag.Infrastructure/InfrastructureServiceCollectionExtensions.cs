using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application;

namespace Rag.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Rag")
            ?? throw new InvalidOperationException("Connection string 'Rag' is required.");
        var contentRoot = configuration["ContentStore:RootPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "content");

        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });
        services.AddDbContextFactory<IngestionDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                providerOptions => providerOptions.UseVector()));
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .Validate(options => TryValidateEmbeddingOptions(options, out _), "Embedding profiles are invalid.")
            .ValidateOnStart();
        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "Ollama:BaseUrl must be an absolute URL.")
            .ValidateOnStart();
        services.AddOptions<OperationWorkerOptions>()
            .Bind(configuration.GetSection(OperationWorkerOptions.SectionName))
            .Validate(
                options => options.LeaseDuration > TimeSpan.Zero && options.LeaseDuration <= TimeSpan.FromHours(1),
                "OperationWorker:LeaseDuration must be greater than zero and no more than one hour.")
            .Validate(
                options => options.PollInterval > TimeSpan.Zero && options.PollInterval <= TimeSpan.FromMinutes(1),
                "OperationWorker:PollInterval must be greater than zero and no more than one minute.")
            .Validate(
                options => options.WorkerId is null || options.WorkerId.Trim().Length is > 0 and <= 200,
                "OperationWorker:WorkerId must be omitted or contain at most 200 characters.")
            .ValidateOnStart();
        services.AddScoped<IIngestionRepository, IngestionRepository>();
        services.AddSingleton<IImmutableContentStore>(_ => new FileSystemImmutableContentStore(contentRoot));
        services.AddSingleton<IOperationClaimRepository, OperationClaimRepository>();
        services.AddSingleton<IOperationCompletionRepository, OperationCompletionRepository>();
        services.AddScoped<ICollectionEmbeddingProfileRepository, CollectionEmbeddingProfileRepository>();
        services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>((serviceProvider, client) =>
        {
            var ollama = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(ollama.BaseUrl, UriKind.Absolute);
        });
        services.AddSingleton<TxtChunker>();
        services.AddSingleton<IOperationProcessor, TxtOperationProcessor>();
        services.AddHostedService<OperationWorker>();
        return services;
    }

    private static bool TryValidateEmbeddingOptions(EmbeddingOptions options, out Exception? exception)
    {
        try
        {
            options.Validate();
            exception = null;
            return true;
        }
        catch (Exception caught) when (caught is ArgumentException or InvalidOperationException)
        {
            exception = caught;
            return false;
        }
    }
}
