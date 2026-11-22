using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TroViec.Api.Features.Admin;
using TroViec.Api.Features.Auth;
using TroViec.Api.Features.Me;
using TroViec.Api.Features.Appeals;
using TroViec.Api.Features.Listings;
using TroViec.Api.Features.Reports;
using TroViec.Api.Features.Reviews;
using TroViec.Api.Features.Meta;
using TroViec.Api.Features.Moderation;
using TroViec.Api.Features.OwnerProfiles;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Features.Scans;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Config;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Health;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.RateLimiting;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

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
    .AddStorage(builder.Configuration)
    .AddDocumentCrypto()
    .AddListings()
    .AddScanServices(builder.Configuration)
    .AddTrustedProxies(builder.Configuration)
    .AddHealth()
    .AddAuth(builder.Configuration)
    .AddEmail(builder.Configuration)
    .AddSms()
    .AddValidators()
    .AddCorsForSite(builder.Configuration)
    .AddOpenApi(options =>
    {
        // Request/response records are nested in their feature class (Login.Request, Register.Request...);
        // prefix the declaring type so each gets its own schema (LoginRequest, RegisterRequest...).
        options.CreateSchemaReferenceId = info =>
            info.Type.IsNested && info.Type.DeclaringType is { } outer
                ? outer.Name + info.Type.Name
                : Microsoft.AspNetCore.OpenApi.OpenApiOptions.CreateDefaultSchemaReferenceId(info);
    });

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
app.MapScans();
app.MapMeta();
app.MapOwnerProfiles();
app.MapListings();
app.MapOwnerModeration();
app.MapListingModeration();
app.MapReports();
app.MapAppeals();
app.MapReviewsAndSaved();
app.MapAudit();
app.MapHub<TroViec.Api.Realtime.ScanHub>(TroViec.Api.Realtime.ScanHub.Path).AllowAnonymous();

app.Run();

/// <summary>Entry point; public so WebApplicationFactory can host it in tests.</summary>
public partial class Program;
