using Dapper;
using FluentValidation;
using Microsoft.Extensions.Options;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Features.Moderation;
using TroViec.Api.Features.Scans;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;
using TroViec.Api.Realtime;

namespace TroViec.Api.Features.Reports;

public sealed record NoteRequest(string Note);

public sealed record ConfirmRequest(string Note, bool HideListing, int? ListingVersion);

public sealed class NoteValidator : AbstractValidator<NoteRequest>
{
    public NoteValidator() => RuleFor(r => r.Note).Must(n => n is not null && n.Trim().Length is >= 1 and <= 500)
        .WithMessage("Nhập ghi chú (tối đa 500 ký tự)");
}

public sealed class ConfirmValidator : AbstractValidator<ConfirmRequest>
{
    public ConfirmValidator()
    {
        RuleFor(r => r.Note).Must(n => n is not null && n.Trim().Length is >= 1 and <= 500).WithMessage("Nhập ghi chú (tối đa 500 ký tự)");
        RuleFor(r => r.ListingVersion).NotNull().GreaterThan(0).When(r => r.HideListing).WithMessage("Thiếu phiên bản tin để ẩn");
    }
}

/// <summary>Reporting a listing (US-406) and handling reports (US-606; docs/04-api.md, Kiểm duyệt).</summary>
public static class ReportEndpoints
{
    public sealed record ReportListItem(long Id, string Category, string? Description, string Status, string TargetType,
        Guid? ListingId, string? ListingTitle, Guid? ScanId, DateTime CreatedAt);

    public sealed record ScanTarget(Guid ScanId, string? InputText, IReadOnlyList<int> ImageNumbers, GetScan.ListingScanResult? Result);

    public sealed record ReportDetail(long Id, string Category, string? Description, string Status, string? ResolutionNote,
        DateTime CreatedAt, ListingModerationEndpoints.ReviewScreen? Listing, ScanTarget? Scan);

    public sealed record ResolveResponse(long Id, string Status);

    private sealed record ReportRow(long Id, long ReporterId, long? ListingId, long? ScanId, string Category, string? Description,
        string Status, string? ResolutionNote, DateTime CreatedAt, Guid? ListingPublicId, long? PosterId);

    public static void MapReports(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/listings/{listingId:guid}/reports", ReportListingAsync).WithTags("Listings")
            .RequireAuthorization(Permissions.ReportCreate).Validate<ScanEndpoints.ReportRequest>()
            .Produces<ScanEndpoints.ReportResponse>(StatusCodes.Status201Created);

        var mod = app.MapGroup("/api/v1/moderation/reports").WithTags("Moderation").RequireAuthorization(Permissions.ReportResolve);
        mod.MapGet("/", ListAsync).Produces<Paged<ReportListItem>>();
        mod.MapGet("/{id:long}", DetailAsync).Produces<ReportDetail>();
        mod.MapGet("/{id:long}/scan-images/{imageNo:int}", ScanImageAsync);
        mod.MapPost("/{id:long}/confirm", ConfirmAsync).Validate<ConfirmRequest>().Produces<ResolveResponse>();
        mod.MapPost("/{id:long}/dismiss", DismissAsync).Validate<NoteRequest>().Produces<ResolveResponse>();
    }

    /// <summary>US-406: only a listing the reporter can see (public); one open report per reporter and listing (BR-15).</summary>
    public static async Task<IResult> ReportListingAsync(Guid listingId, ScanEndpoints.ReportRequest request, HttpContext http,
        NpgsqlDataSource db, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var listing = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM app.listings WHERE public_id = @Id AND status = 'verified' AND expires_at > now()",
            new { Id = listingId }, cancellationToken: ct)) ?? throw new ProblemException("listing.not_found");
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.reports (reporter_id, listing_id, category, description)
            VALUES (@UserId, @ListingId, @Category::app.report_category, @Description)
            RETURNING id
            """, new { UserId = userId, ListingId = listing, request.Category, Description = request.Description?.Trim() },
            cancellationToken: ct));
        return Results.Created($"/api/v1/moderation/reports/{id}", new ScanEndpoints.ReportResponse(id));
    }

    public static async Task<IResult> ListAsync(NpgsqlDataSource db, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (status is not (null or "open" or "confirmed" or "dismissed"))
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["status"] = ["Trạng thái không hợp lệ"] });
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, string Category, string? Description, string Status, Guid? ListingId,
            string? Title, Guid? ScanId, DateTime CreatedAt, long Total)>(new CommandDefinition(
            """
            SELECT r.id, r.category::text, r.description, r.status::text, l.public_id, l.title, s.public_id, r.created_at, count(*) OVER ()
            FROM app.reports r
            LEFT JOIN app.listings l ON l.id = r.listing_id
            LEFT JOIN app.scans s ON s.id = r.scan_id
            WHERE @Status::text IS NULL OR r.status = @Status::app.report_status
            ORDER BY r.created_at, r.id
            LIMIT @Size OFFSET @Offset
            """, new { Status = status, Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new ReportListItem(r.Id, r.Category, r.Description, r.Status, r.ListingId is null ? "scan" : "listing",
            r.ListingId, r.Title, r.ScanId, r.CreatedAt)).ToList();
        return Results.Ok(new Paged<ReportListItem>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>Report plus its target: the listing review screen, or the reported quick check's text, result and images.</summary>
    public static async Task<IResult> DetailAsync(long id, HttpContext http, NpgsqlDataSource db, MediaUrls urls, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var report = await LoadAsync(connection, null, id, ct);
        ListingModerationEndpoints.ReviewScreen? listing = null;
        ScanTarget? scan = null;
        if (report.ListingPublicId is { } listingId)
        {
            var screen = await ListingModerationEndpoints.ReviewScreenAsync(listingId, http, db, urls, ct);
            listing = (ListingModerationEndpoints.ReviewScreen)((IValueHttpResult)screen).Value!;
        }
        else if (report.ScanId is { } scanId)
        {
            var text = await connection.QuerySingleAsync<string?>(new CommandDefinition(
                "SELECT input_text FROM app.scans WHERE id = @Id", new { Id = scanId }, cancellationToken: ct));
            var images = (await connection.QueryAsync<int>(new CommandDefinition(
                "SELECT image_no FROM app.scan_images WHERE scan_id = @Id ORDER BY image_no", new { Id = scanId }, cancellationToken: ct))).ToList();
            var result = await GetScan.QuickCheckByIdAsync(connection, scanId, ct);
            scan = new ScanTarget(result!.ScanId, text, images, result);
        }
        return Results.Ok(new ReportDetail(report.Id, report.Category, report.Description, report.Status, report.ResolutionNote,
            report.CreatedAt, listing, scan));
    }

    /// <summary>Image of the reported quick check, only through a report that targets that scan.</summary>
    public static async Task<IResult> ScanImageAsync(long id, int imageNo, HttpContext http, NpgsqlDataSource db, IObjectStorage storage,
        IOptions<StorageBuckets> buckets, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var key = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT si.object_key FROM app.reports r JOIN app.scan_images si ON si.scan_id = r.scan_id
            WHERE r.id = @Id AND si.image_no = @No
            """, new { Id = id, No = (short)imageNo }, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        var bytes = await storage.GetAsync(buckets.Value.ScanImages, key, ct) ?? throw new ProblemException("resource.not_found");
        http.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(bytes, "image/webp");
    }

    /// <summary>
    /// Confirm (note required): the trigger adds one to every related identifier's counter; with hideListing the listing
    /// is hidden in the same transaction, reason = note.
    /// </summary>
    public static async Task<IResult> ConfirmAsync(long id, ConfirmRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, ModerationNotifier notifier, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var report = await LoadAsync(connection, tx, id, ct);
        CheckResolvable(report, moderator);
        var note = request.Note.Trim();
        await ResolveAsync(connection, tx, id, "confirmed", moderator, note, ct);
        if (request.HideListing)
        {
            if (report.ListingId is not { } listingId) throw new ProblemException("listing.invalid_transition");
            await transitions.TransitionAsync(connection, tx, listingId, request.ListingVersion!.Value, ListingStatus.Verified,
                ListingStatus.Hidden, moderator, note, rules: new TransitionRules(ModeratorAction: true), ct: ct);
        }
        await tx.CommitAsync(ct);
        if (request.HideListing) await notifier.ListingUpdatedAsync(report.ListingPublicId!.Value, "hidden", request.ListingVersion!.Value + 1, ct);
        return Results.Ok(new ResolveResponse(id, "confirmed"));
    }

    public static async Task<IResult> DismissAsync(long id, NoteRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        CheckResolvable(await LoadAsync(connection, tx, id, ct), moderator);
        await ResolveAsync(connection, tx, id, "dismissed", moderator, request.Note.Trim(), ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new ResolveResponse(id, "dismissed"));
    }

    /// <summary>BR-19: not a report one filed, nor one about one's own listing.</summary>
    private static void CheckResolvable(ReportRow report, long moderator)
    {
        if (report.ReporterId == moderator || report.PosterId == moderator) throw new ProblemException("moderation.conflict_of_interest");
        if (report.Status != "open") throw new ProblemException("report.already_resolved");
    }

    private static Task ResolveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long id, string status, long moderator, string note,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.reports SET status = @Status::app.report_status, resolver_id = @Moderator, resolved_at = now(), resolution_note = @Note
            WHERE id = @Id AND status = 'open'
            """, new { Id = id, Status = status, Moderator = moderator, Note = note }, tx, cancellationToken: ct));

    private static async Task<ReportRow> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, long id, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<ReportRow>(new CommandDefinition(
            $"""
            SELECT r.id, r.reporter_id, r.listing_id, r.scan_id, r.category::text AS category, r.description, r.status::text AS status,
                   r.resolution_note, r.created_at, l.public_id AS listing_public_id, l.poster_id
            FROM app.reports r LEFT JOIN app.listings l ON l.id = r.listing_id
            WHERE r.id = @Id
            {(tx is null ? "" : "FOR UPDATE OF r")}
            """, new { Id = id }, tx, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
}
