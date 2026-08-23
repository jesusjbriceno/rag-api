using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<IIngestionRepository, IngestionRepository>();
        services.AddSingleton<IImmutableContentStore>(_ => new FileSystemImmutableContentStore(contentRoot));
        return services;
    }
}
