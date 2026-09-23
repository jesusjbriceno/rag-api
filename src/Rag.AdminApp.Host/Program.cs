namespace Rag.AdminApp.Host;

/// <summary>
/// Thin composition root for the AdminApp backend-for-frontend. Registration and
/// endpoint mapping live in focused extension classes so later work units can extend
/// the pipeline additively without rewriting this entry point.
/// </summary>
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAdminAppHost(builder.Configuration);
        var app = builder.Build();
        app.MapAdminAppHost();
        app.Run();
    }
}
