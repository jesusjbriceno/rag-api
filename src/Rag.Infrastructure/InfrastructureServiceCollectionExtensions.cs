using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        services.AddDbContextFactory<IngestionDbContext>(options => options.UseNpgsql(connectionString));
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
        services.AddSingleton<IOperationProcessor, DeferredOperationProcessor>();
        services.AddHostedService<OperationWorker>();
        return services;
    }
}
