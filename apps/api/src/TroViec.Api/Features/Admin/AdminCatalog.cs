using System.Reflection;
using System.Text.Json;
using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Admin;

/// <summary>Criteria (US-802), model versions (US-803), statistics (US-804) and full audit (US-805).</summary>
public static class AdminCatalog
{
    public sealed record Criterion(int Id, string Code, string Name, string Guidance, string Scope, string? LegalBasis, bool IsActive,
        DateTime UpdatedAt);

    public sealed record CriterionCreate(string Code, string Name, string Guidance, string Scope, string? LegalBasis);

    public sealed record CriterionUpdate(string Name, string Guidance, string Scope, string? LegalBasis, bool IsActive);

    public sealed record Thresholds(int Caution, int High);

    public sealed record ModelVersion(int Id, string Component, string Name, string Version, bool IsActive, DateTime? ActivatedAt,
        Thresholds? Thresholds);

    public sealed record DailyScans(DateOnly Day, string Source, long ScanCount, long HighCount, long CautionCount, long LowCount,
        long FailedCount, double? P95Seconds);

    public sealed record DailyModeration(DateOnly Day, long DecisionCount, double? MedianMinutes);

    public sealed record RiskBucket(string Source, int ScoreBucket, long AssessmentCount);

    public sealed record Stats(IReadOnlyList<DailyScans> Scans, IReadOnlyList<DailyModeration> Moderation, IReadOnlyList<RiskBucket> Risk);

    public sealed record TransitionLog(long Id, Guid ListingId, string ListingTitle, string? FromStatus, string ToStatus, string? Reason,
        long ActorId, string ActorName, DateTime CreatedAt);

    public sealed record DocumentAccessLog(long Id, long DocumentId, long OwnerId, long ViewerId, string ViewerName, string Purpose,
        DateTime AccessedAt);

    private static readonly string[] Scopes = ["room", "job", "both"];

    public sealed class CreateValidator : AbstractValidator<CriterionCreate>
    {
        public CreateValidator()
        {
            RuleFor(c => c.Code).Matches("^[a-z][a-z_]*$").MaximumLength(50).WithMessage("Mã gồm chữ thường a-z và gạch dưới");
            Text(this, c => c.Name, c => c.Guidance, c => c.Scope, c => c.LegalBasis);
        }
    }

    public sealed class UpdateValidator : AbstractValidator<CriterionUpdate>
    {
        public UpdateValidator() => Text(this, c => c.Name, c => c.Guidance, c => c.Scope, c => c.LegalBasis);
    }

    private static void Text<T>(AbstractValidator<T> v, Func<T, string> name, Func<T, string> guidance, Func<T, string> scope,
        Func<T, string?> legal)
    {
        v.RuleFor(x => name(x)).Must(s => s is not null && s.Trim().Length is >= 1 and <= 150).OverridePropertyName("name")
            .WithMessage("Tên từ 1 đến 150 ký tự");
        v.RuleFor(x => guidance(x)).Must(s => s is not null && s.Trim().Length is >= 1 and <= 1000).OverridePropertyName("guidance")
            .WithMessage("Hướng dẫn từ 1 đến 1.000 ký tự");
        v.RuleFor(x => scope(x)).Must(s => Scopes.Contains(s)).OverridePropertyName("scope").WithMessage("Phạm vi: room, job hoặc both");
        v.RuleFor(x => legal(x)).Must(s => s is null || s.Trim().Length is >= 1 and <= 300).OverridePropertyName("legalBasis")
            .WithMessage("Căn cứ tối đa 300 ký tự");
    }

    public static void MapAdminCatalog(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("Admin");
        admin.MapGet("/criteria", CriteriaAsync).RequireAuthorization(Permissions.CriteriaManage).Produces<List<Criterion>>();
        admin.MapPost("/criteria", CreateCriterionAsync).RequireAuthorization(Permissions.CriteriaManage).Validate<CriterionCreate>()
            .Produces<Criterion>(StatusCodes.Status201Created);
        admin.MapPut("/criteria/{id:int}", UpdateCriterionAsync).RequireAuthorization(Permissions.CriteriaManage)
            .Validate<CriterionUpdate>().Produces<Criterion>();
        admin.MapGet("/model-versions", ModelVersionsAsync).RequireAuthorization(Permissions.ModelManage).Produces<List<ModelVersion>>();
        admin.MapPost("/model-versions/{id:int}/activate", ActivateAsync).RequireAuthorization(Permissions.ModelManage)
            .Produces<ModelVersion>();
        admin.MapGet("/stats", StatsAsync).RequireAuthorization(Permissions.StatsView).Produces<Stats>();
        admin.MapGet("/audit/transitions", TransitionsAsync).RequireAuthorization(Permissions.AuditViewAll).Produces<Paged<TransitionLog>>();
        admin.MapGet("/audit/document-access", DocumentAccessAsync).RequireAuthorization(Permissions.AuditViewAll)
            .Produces<Paged<DocumentAccessLog>>();
    }

    private const string CriterionColumns =
        "id::int AS id, code, name, guidance, scope::text AS scope, legal_basis, is_active, updated_at";

    public static async Task<IResult> CriteriaAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok((await connection.QueryAsync<Criterion>(new CommandDefinition(
            $"SELECT {CriterionColumns} FROM app.criteria ORDER BY is_active DESC, id", cancellationToken: ct))).ToList());
    }

    /// <summary>New criteria are used by the LLM at once (the worker reloads every 60 s); rules exist only for the seeded ones.</summary>
    public static async Task<IResult> CreateCriterionAsync(CriterionCreate c, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var created = await connection.QuerySingleAsync<Criterion>(new CommandDefinition(
            $"""
            INSERT INTO app.criteria (code, name, guidance, scope, legal_basis)
            VALUES (@Code, @Name, @Guidance, @Scope::app.criterion_scope, @LegalBasis)
            RETURNING {CriterionColumns}
            """, new { c.Code, Name = c.Name.Trim(), Guidance = c.Guidance.Trim(), c.Scope, LegalBasis = c.LegalBasis?.Trim() },
            cancellationToken: ct));
        return Results.Created($"/api/v1/admin/criteria/{created.Id}", created);
    }

    /// <summary>Wording, scope and active flag change; the code never does (flags keep pointing at it). No delete (US-802).</summary>
    public static async Task<IResult> UpdateCriterionAsync(int id, CriterionUpdate c, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.QuerySingleOrDefaultAsync<Criterion>(new CommandDefinition(
            $"""
            UPDATE app.criteria
            SET name = @Name, guidance = @Guidance, scope = @Scope::app.criterion_scope, legal_basis = @LegalBasis, is_active = @IsActive
            WHERE id = @Id
            RETURNING {CriterionColumns}
            """, new { Id = (short)id, Name = c.Name.Trim(), Guidance = c.Guidance.Trim(), c.Scope, LegalBasis = c.LegalBasis?.Trim(), c.IsActive },
            cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        return Results.Ok(updated);
    }

    public static async Task<IResult> ModelVersionsAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok((await LoadModelsAsync(connection, null, null, ct)).ToList());
    }

    /// <summary>
    /// US-803: one transaction turns the running version of the component off and this one on. An aggregator version
    /// without a configuration file the worker knows would fail every scan, so it is refused (model.not_implemented).
    /// </summary>
    public static async Task<IResult> ActivateAsync(int id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var target = await connection.QuerySingleOrDefaultAsync<(string Component, string Version)?>(new CommandDefinition(
            "SELECT component::text, version FROM app.model_versions WHERE id = @Id FOR UPDATE", new { Id = (short)id }, tx, cancellationToken: ct))
            ?? throw new ProblemException("resource.not_found");
        if (target.Component == "aggregator" && AggregatorThresholds(target.Version) is null)
            throw new ProblemException("model.not_implemented");

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.model_versions SET is_active = false WHERE component = @Component::app.model_component AND is_active AND id <> @Id",
            new { target.Component, Id = (short)id }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.model_versions SET is_active = true, activated_at = now() WHERE id = @Id AND NOT is_active",
            new { Id = (short)id }, tx, cancellationToken: ct));
        var result = (await LoadModelsAsync(connection, tx, id, ct)).Single();
        await tx.CommitAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<IEnumerable<ModelVersion>> LoadModelsAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, int? id,
        CancellationToken ct) =>
        (await connection.QueryAsync<(short Id, string Component, string Name, string Version, bool IsActive, DateTime? ActivatedAt)>(
            new CommandDefinition(
                """
                SELECT id, component::text, name, version, is_active, activated_at FROM app.model_versions
                WHERE @Id::smallint IS NULL OR id = @Id
                ORDER BY component, is_active DESC, created_at DESC
                """, new { Id = (short?)id }, tx, cancellationToken: ct)))
        .Select(m => new ModelVersion(m.Id, m.Component, m.Name, m.Version, m.IsActive, m.ActivatedAt,
            m.Component == "aggregator" ? AggregatorThresholds(m.Version) : null));

    /// <summary>
    /// Thresholds from the aggregator files the worker ships (apps/worker/models/aggregator/*.json), embedded at build
    /// time so the confirmation dialog can show what activation changes. Null when no such file exists.
    /// </summary>
    public static Thresholds? AggregatorThresholds(string version)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"aggregator.{version}.json");
        if (stream is null) return null;
        using var doc = JsonDocument.Parse(stream);
        var t = doc.RootElement.GetProperty("thresholds");
        return new Thresholds(t.GetProperty("caution").GetInt32(), t.GetProperty("high").GetInt32());
    }

    /// <summary>US-804 from the materialized views (refreshed nightly; quick checks older than 30 days are gone).</summary>
    public static async Task<IResult> StatsAsync(NpgsqlDataSource db, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber > 366)
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["from"] = ["Khoảng ngày không hợp lệ (tối đa 1 năm)"] });
        await using var connection = await db.OpenConnectionAsync(ct);
        var args = new { From = start, To = end };
        var scans = (await connection.QueryAsync<DailyScans>(new CommandDefinition(
            """
            SELECT day, source::text AS source, scan_count, high_count, caution_count, low_count, failed_count, p95_seconds
            FROM reporting.daily_scans WHERE day BETWEEN @From AND @To ORDER BY day, source
            """, args, cancellationToken: ct))).ToList();
        var moderation = (await connection.QueryAsync<DailyModeration>(new CommandDefinition(
            "SELECT day, decision_count, median_minutes FROM reporting.daily_moderation WHERE day BETWEEN @From AND @To ORDER BY day",
            args, cancellationToken: ct))).ToList();
        var risk = (await connection.QueryAsync<RiskBucket>(new CommandDefinition(
            "SELECT source::text AS source, score_bucket, assessment_count FROM reporting.risk_distribution ORDER BY source, score_bucket",
            cancellationToken: ct))).ToList();
        return Results.Ok(new Stats(scans, moderation, risk));
    }

    public static async Task<IResult> TransitionsAsync(NpgsqlDataSource db, long? actorId, Guid? listingId, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, Guid ListingId, string Title, string? From, string To, string? Reason, long ActorId,
            string ActorName, DateTime CreatedAt, long Total)>(new CommandDefinition(
            """
            SELECT t.id, l.public_id, l.title, t.from_status::text, t.to_status::text, t.reason, t.actor_id, u.full_name, t.created_at,
                   count(*) OVER ()
            FROM app.listing_transitions t
            JOIN app.listings l ON l.id = t.listing_id
            JOIN app.users u ON u.id = t.actor_id
            WHERE (@ActorId::bigint IS NULL OR t.actor_id = @ActorId)
              AND (@ListingId::uuid IS NULL OR l.public_id = @ListingId)
              AND (@From::timestamptz IS NULL OR t.created_at >= @From)
              AND (@To::timestamptz IS NULL OR t.created_at < @To)
            ORDER BY t.id DESC
            LIMIT @Size OFFSET @Offset
            """, new { ActorId = actorId, ListingId = listingId, From = from, To = to, Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new TransitionLog(r.Id, r.ListingId, r.Title, r.From, r.To, r.Reason, r.ActorId, r.ActorName, r.CreatedAt)).ToList();
        return Results.Ok(new Paged<TransitionLog>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    public static async Task<IResult> DocumentAccessAsync(NpgsqlDataSource db, long? viewerId, long? documentId, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, long DocumentId, long OwnerId, long ViewerId, string ViewerName, string Purpose,
            DateTime AccessedAt, long Total)>(new CommandDefinition(
            """
            SELECT a.id, a.owner_document_id, d.owner_id, a.viewer_id, u.full_name, a.purpose, a.accessed_at, count(*) OVER ()
            FROM app.document_access_logs a
            JOIN app.owner_documents d ON d.id = a.owner_document_id
            JOIN app.users u ON u.id = a.viewer_id
            WHERE (@ViewerId::bigint IS NULL OR a.viewer_id = @ViewerId)
              AND (@DocumentId::bigint IS NULL OR a.owner_document_id = @DocumentId)
              AND (@From::timestamptz IS NULL OR a.accessed_at >= @From)
              AND (@To::timestamptz IS NULL OR a.accessed_at < @To)
            ORDER BY a.id DESC
            LIMIT @Size OFFSET @Offset
            """, new { ViewerId = viewerId, DocumentId = documentId, From = from, To = to, Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new DocumentAccessLog(r.Id, r.DocumentId, r.OwnerId, r.ViewerId, r.ViewerName, r.Purpose, r.AccessedAt)).ToList();
        return Results.Ok(new Paged<DocumentAccessLog>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }
}
