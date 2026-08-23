using Rag.Application;
using Rag.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
