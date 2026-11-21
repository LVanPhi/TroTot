using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TroViec.Api.Infrastructure.Config;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Health;
using TroViec.Api.Infrastructure.Problems;

DotEnv.LoadFromRepoRoot();
var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs with traceId; never log request URLs/headers (SignalR access_token is in the query).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services
    .AddProblems()
    .AddDatabase(builder.Configuration)
    .AddRedis(builder.Configuration)
    .AddObjectStorage(builder.Configuration)
    .AddTrustedProxies(builder.Configuration)
    .AddHealth()
    .AddOpenApi();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();               // /openapi/v1.json, used by `make gen-api`
    app.MapScalarApiReference().AllowAnonymous();    // /scalar
}

app.MapHealth();

app.Run();

/// <summary>Entry point; public so WebApplicationFactory can host it in tests.</summary>
public partial class Program;
