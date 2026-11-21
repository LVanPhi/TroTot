using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TroViec.Api.Features.Admin;
using TroViec.Api.Features.Auth;
using TroViec.Api.Features.Me;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Config;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Health;
using TroViec.Api.Infrastructure.Notifications;
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
    .AddAuth(builder.Configuration)
    .AddEmail(builder.Configuration)
    .AddSms()
    .AddValidators()
    .AddCorsForSite(builder.Configuration)
    .AddOpenApi();

var app = builder.Build();

// Fail fast on configuration that must never reach production (e.g. Sms__Provider=console).
_ = app.Services.GetRequiredService<ISmsSender>();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();               // /openapi/v1.json, used by `make gen-api`
    app.MapScalarApiReference().AllowAnonymous();    // /scalar
}

app.MapHealth();
app.MapAuth();
app.MapMe();
app.MapAdminUsers();

app.Run();

/// <summary>Entry point; public so WebApplicationFactory can host it in tests.</summary>
public partial class Program;
