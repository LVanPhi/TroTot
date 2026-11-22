using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Scans;

/// <summary>
/// GET /api/v1/scans/{scanId}: status and result (docs/04-api.md, Kiểm tra tin). Anyone who knows the UUID may read it;
/// only quick checks are served (BR-20: listing screenings → 404). Never returns the original text or images.
/// </summary>
public static class GetScan
{
    public sealed record Signals(double? Text, double? Image, double? Price, double? Community);

    public sealed record Flag(string Code, string Name, string Evidence, string? LegalBasis);

    public sealed record PublicListing(Guid ListingId, string Title);

    public sealed record ImageMatch(int SourceImageNo, int OtherListingCount, int RemovedListingCount, bool DifferentLocation, PublicListing? PublicListing);

    public sealed record Price(int RentVnd, string RentSource, int MedianVnd, int SampleSize);

    public sealed record CommunityItem(string Kind, string Hint, int ConfirmedReports, DateTime? LastConfirmedAt);

    public sealed record Assessment(
        int Score, string RiskLevel, Signals Signals, IReadOnlyList<Flag> Flags,
        IReadOnlyList<ImageMatch> ImageMatches, Price? Price, IReadOnlyList<CommunityItem> Community);

    public sealed record Response(
        Guid ScanId, string Status, DateTime CreatedAt, DateTime? CompletedAt, string? ErrorCode,
        Assessment? Assessment, bool CanReport);

    private sealed record ScanRow(
        long Id, Guid PublicId, string Status, string? ErrorCode, DateTime CreatedAt, DateTime? CompletedAt,
        int? ClaimedRentVnd, long? AssessmentId, short? Score, string? RiskLevel,
        float? TextSignal, float? ImageSignal, float? PriceSignal, float? CommunitySignal,
        int? PriceMedianVnd, short? PriceSampleSize, int? PriceVnd);

    public static async Task<IResult> HandleAsync(Guid scanId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var scan = await connection.QuerySingleOrDefaultAsync<ScanRow>(new CommandDefinition(
            """
            SELECT s.id, s.public_id, s.status::text AS status, s.error_code, s.created_at, s.completed_at, s.claimed_rent_vnd,
                   a.id AS assessment_id, a.score, a.risk_level::text AS risk_level,
                   a.text_signal, a.image_signal, a.price_signal, a.community_signal,
                   a.price_median_vnd, a.price_sample_size, a.price_vnd
            FROM app.scans s
            LEFT JOIN LATERAL (SELECT * FROM app.assessments a1 WHERE a1.scan_id = s.id
                               ORDER BY a1.id DESC LIMIT 1) a ON true
            WHERE s.public_id = @ScanId
              AND s.source = 'quick_check'
            """, new { ScanId = scanId }, cancellationToken: ct))
            ?? throw new ProblemException("scan.not_found");

        Assessment? assessment = null;
        if (scan is { Status: "completed", AssessmentId: { } assessmentId })
            assessment = await LoadAssessmentAsync(connection, scan, assessmentId, ct);

        var canReport = http.User.HasClaim(TokenService.PermissionClaim, Permissions.ReportCreate);
        return Results.Ok(new Response(scan.PublicId, scan.Status, scan.CreatedAt, scan.CompletedAt, scan.ErrorCode, assessment, canReport));
    }

    public sealed record ListingScanResult(Guid ScanId, string Status, string? ErrorCode, DateTime CreatedAt, Assessment? Assessment);

    /// <summary>
    /// The latest screening of a listing with its AI result, for moderators only (BR-20). Null when never screened.
    /// </summary>
    public static async Task<ListingScanResult?> LatestForListingAsync(NpgsqlConnection connection, long listingId, CancellationToken ct)
    {
        var scan = await connection.QuerySingleOrDefaultAsync<ScanRow>(new CommandDefinition(
            """
            SELECT s.id, s.public_id, s.status::text AS status, s.error_code, s.created_at, s.completed_at, s.claimed_rent_vnd,
                   a.id AS assessment_id, a.score, a.risk_level::text AS risk_level,
                   a.text_signal, a.image_signal, a.price_signal, a.community_signal,
                   a.price_median_vnd, a.price_sample_size, a.price_vnd
            FROM app.scans s
            LEFT JOIN LATERAL (SELECT * FROM app.assessments a1 WHERE a1.scan_id = s.id
                               ORDER BY a1.id DESC LIMIT 1) a ON true
            WHERE s.listing_id = @ListingId AND s.source = 'listing'
            ORDER BY s.created_at DESC, s.id DESC
            LIMIT 1
            """, new { ListingId = listingId }, cancellationToken: ct));
        if (scan is null) return null;
        var assessment = scan is { Status: "completed", AssessmentId: { } id }
            ? await LoadAssessmentAsync(connection, scan, id, ct, rentSource: "listing")
            : null;
        return new ListingScanResult(scan.PublicId, scan.Status, scan.ErrorCode, scan.CreatedAt, assessment);
    }

    private static async Task<Assessment> LoadAssessmentAsync(NpgsqlConnection connection, ScanRow scan, long assessmentId, CancellationToken ct,
        string? rentSource = null)
    {
        var flags = (await connection.QueryAsync<Flag>(new CommandDefinition(
            """
            SELECT c.code, c.name, f.evidence_text AS evidence, c.legal_basis
            FROM app.assessment_flags f JOIN app.criteria c ON c.id = f.criterion_id
            WHERE f.assessment_id = @AssessmentId ORDER BY c.id
            """, new { AssessmentId = assessmentId }, cancellationToken: ct))).ToList();

        var community = (await connection.QueryAsync<CommunityItem>(new CommandDefinition(
            """
            SELECT i.kind::text AS kind, i.hint,
                   coalesce(st.confirmed_report_count, 0)::int AS confirmed_reports, st.last_confirmed_at
            FROM app.scan_identifiers si
            JOIN app.identifiers i ON i.id = si.identifier_id
            LEFT JOIN app.identifier_stats st ON st.identifier_id = i.id
            WHERE si.scan_id = @ScanId
            ORDER BY i.kind, i.hint
            """, new { ScanId = scan.Id }, cancellationToken: ct))).ToList();

        var matches = (await connection.QueryAsync<(short SourceImageNo, long OtherListingCount, long RemovedListingCount,
            bool DifferentLocation, Guid? PublicListingId, string? PublicListingTitle)>(new CommandDefinition(
            """
            SELECT m.source_image_no,
                   count(DISTINCT li.listing_id),
                   count(DISTINCT li.listing_id) FILTER (WHERE l.status IN ('hidden', 'removed')),
                   count(DISTINCT l.ward_code) > 1,
                   (array_agg(l.public_id ORDER BY m.hamming_distance)
                        FILTER (WHERE l.status = 'verified' AND l.expires_at > now()))[1],
                   (array_agg(l.title ORDER BY m.hamming_distance)
                        FILTER (WHERE l.status = 'verified' AND l.expires_at > now()))[1]
            FROM app.assessment_image_matches m
            JOIN app.listing_images li ON li.id = m.listing_image_id
            JOIN app.listings l ON l.id = li.listing_id
            WHERE m.assessment_id = @AssessmentId
            GROUP BY m.source_image_no
            ORDER BY m.source_image_no
            """, new { AssessmentId = assessmentId }, cancellationToken: ct)))
            .Select(m => new ImageMatch(m.SourceImageNo, (int)m.OtherListingCount, (int)m.RemovedListingCount, m.DifferentLocation,
                m is { PublicListingId: { } id, PublicListingTitle: { } title } ? new PublicListing(id, title) : null))
            .ToList();

        Price? price = scan is { PriceSignal: not null, PriceVnd: { } rent, PriceMedianVnd: { } median, PriceSampleSize: { } sample }
            ? new Price(rent, rentSource ?? (scan.ClaimedRentVnd == rent ? "claimed" : "extracted"), median, sample)
            : null;

        return new Assessment(
            scan.Score!.Value, scan.RiskLevel!,
            new Signals(Round(scan.TextSignal), Round(scan.ImageSignal), Round(scan.PriceSignal), Round(scan.CommunitySignal)),
            flags, matches, price, community);
    }

    private static double? Round(float? value) => value is { } v ? Math.Round(v, 2) : null;
}
