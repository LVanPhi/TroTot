using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.RateLimiting;

namespace TroViec.Api.Features.Scans;

/// <summary>/api/v1/scans* and /api/v1/me/scans: quick checks only (BR-20).</summary>
public static class ScanEndpoints
{
    public sealed record QuotaResponse(int Limit, int Remaining, DateTimeOffset ResetAt);

    public sealed record MyScanItem(Guid ScanId, string Status, DateTime CreatedAt, DateTime? CompletedAt, int? Score, string? RiskLevel);

    public sealed record ReportRequest(string Category, string? Description);

    public sealed record ReportResponse(long Id);

    public sealed class ReportValidator : AbstractValidator<ReportRequest>
    {
        public ReportValidator()
        {
            RuleFor(r => r.Category).Must(c => c is "scam" or "misleading" or "inappropriate" or "other")
                .WithMessage("Loại báo cáo không hợp lệ");
            RuleFor(r => r.Description).MaximumLength(1000).Must(d => d is null || d.Trim().Length > 0)
                .WithMessage("Mô tả tối đa 1.000 ký tự");
        }
    }

    public static void MapScans(this IEndpointRouteBuilder app)
    {
        var scans = app.MapGroup("/api/v1/scans").WithTags("Scans");

        // Guests may scan; signed-in callers need scan.create (checked in the handler, since guests have no claims).
        scans.MapPost("/", CreateScan.HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(CreateScan.MaxRequestBytes))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CreateScan.Response>(StatusCodes.Status202Accepted);
        scans.MapGet("/quota", QuotaAsync).AllowAnonymous().Produces<QuotaResponse>();
        scans.MapGet("/{scanId:guid}", GetScan.HandleAsync).AllowAnonymous().Produces<GetScan.Response>();
        scans.MapPost("/{scanId:guid}/reports", ReportAsync).RequireAuthorization(Permissions.ReportCreate)
            .Validate<ReportRequest>().Produces<ReportResponse>(StatusCodes.Status201Created);

        app.MapGet("/api/v1/me/scans", MyScansAsync).WithTags("Me").RequireAuthorization().Produces<Paged<MyScanItem>>();
    }

    /// <summary>US-201 AC6: remaining scans today (by IP for guests, by account when signed in); never consumes.</summary>
    public static async Task<IResult> QuotaAsync(HttpContext http, QuotaService quota)
    {
        long? userId = http.User.Identity?.IsAuthenticated == true ? CurrentUser.Id(http.User) : null;
        var status = await quota.GetAsync(userId, AuthSetup.ClientIp(http));
        return Results.Ok(new QuotaResponse(status.Limit, status.Remaining, status.ResetAt));
    }

    /// <summary>US-204: the caller's quick checks still kept (30 days, or 1 year once reported), newest first.</summary>
    public static async Task<IResult> MyScansAsync(int? page, int? pageSize, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid PublicId, string Status, DateTime CreatedAt, DateTime? CompletedAt,
            short? Score, string? RiskLevel, long Total)>(new CommandDefinition(
            """
            SELECT s.public_id, s.status::text, s.created_at, s.completed_at, a.score, a.risk_level::text,
                   count(*) OVER ()
            FROM app.scans s
            LEFT JOIN LATERAL (SELECT score, risk_level FROM app.assessments a1 WHERE a1.scan_id = s.id
                               ORDER BY a1.id DESC LIMIT 1) a ON true
            WHERE s.requester_id = @UserId AND s.source = 'quick_check' AND s.expires_at > now()
            ORDER BY s.created_at DESC, s.id DESC
            LIMIT @PageSize OFFSET @Offset
            """, new { UserId = CurrentUser.Id(http.User), PageSize = size, Offset = offset }, cancellationToken: ct))).ToList();

        var items = rows.Select(r => new MyScanItem(r.PublicId, r.Status, r.CreatedAt, r.CompletedAt, r.Score, r.RiskLevel)).ToList();
        return Results.Ok(new Paged<MyScanItem>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>
    /// US-205: report a quick check; the scan is then kept one year as evidence (BR-07). One open report per person and
    /// scan (uq_reports_open_scan_reporter → 409 report.already_open). Listing screenings are not reportable here.
    /// </summary>
    public static async Task<IResult> ReportAsync(Guid scanId, ReportRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var internalId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM app.scans WHERE public_id = @ScanId AND source = 'quick_check' FOR UPDATE",
            new { ScanId = scanId }, tx, cancellationToken: ct))
            ?? throw new ProblemException("scan.not_found");

        var reportId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.reports (reporter_id, scan_id, category, description)
            VALUES (@UserId, @ScanId, @Category::app.report_category, @Description)
            RETURNING id
            """, new { UserId = userId, ScanId = internalId, request.Category, Description = request.Description?.Trim() },
            tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.scans SET expires_at = now() + interval '1 year' WHERE id = @Id",
            new { Id = internalId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/scans/{scanId}/reports/{reportId}", new ReportResponse(reportId));
    }
}
