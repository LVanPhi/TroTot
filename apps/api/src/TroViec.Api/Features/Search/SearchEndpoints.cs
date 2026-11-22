using System.Globalization;
using System.Text.Json;
using Dapper;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Search;

/// <summary>GET /listings and GET /listings/search (US-401, US-402; docs/03-database.md, Tìm tin, Tìm kiếm hybrid).</summary>
public static class SearchEndpoints
{
    public const int DefaultRadiusM = 3000;
    public const int MaxRadiusM = 10_000;

    public sealed record Item(Guid ListingId, string ListingType, string Title, string WardName, string StreetAddress, GeoPoint Location,
        string? CoverImageUrl, RoomSummary? Room, JobSummary? Job, int DistanceM, DateTime? VerifiedUntil);

    public sealed record RoomSummary(int MonthlyRentVnd, decimal AreaM2, bool IsSelfContained);

    public sealed record JobSummary(string EmployerName, int PayVnd, string PayUnit);

    public sealed record SearchResult(IReadOnlyList<Item> Items, int Page, int PageSize, long Total, AppliedFilters AppliedFilters, string Mode);

    /// <summary>Hard filters after validation; Lat/Lng are always set (a campus when the caller gave none).</summary>
    public sealed record Filters(string? Type, int? MinRent, int? MaxRent, double Lat, double Lng, int RadiusM, string[] Amenities,
        bool? SelfContained, string Sort);

    private sealed record Row(long Id, Guid PublicId, string ListingType, string Title, string WardName, string StreetAddress, double Lat,
        double Lng, int? MonthlyRentVnd, decimal? AreaM2, bool? IsSelfContained, string? EmployerName, int? PayVnd, string? PayUnit,
        double DistanceM, string? CoverKey, DateTime? ExpiresAt, long Total);

    public static IServiceCollection AddSearch(this IServiceCollection services)
    {
        services.AddHttpClient<IEmbeddingClient, EmbeddingClient>();
        return services;
    }

    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        var listings = app.MapGroup("/api/v1/listings").WithTags("Listings").AllowAnonymous();
        listings.MapGet("/", ListAsync).Produces<Paged<Item>>();
        listings.MapGet("/search", SearchAsync).Produces<SearchResult>();
    }

    public static async Task<IResult> ListAsync(HttpContext http, NpgsqlDataSource db, MediaUrls urls, IConfiguration config,
        string? type, int? minRent, int? maxRent, double? lat, double? lng, int? radiusM, string? amenities, bool? selfContained,
        string? sort, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var filters = await BuildAsync(connection, config, type, minRent, maxRent, lat, lng, radiusM, Csv(amenities), selfContained, sort, ct);
        var rows = await FilteredAsync(connection, filters, size, offset, ct);
        return Results.Ok(new Paged<Item>(rows.Select(r => ToItem(r, urls)).ToList(), p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>
    /// Hard conditions from the sentence (plus explicit parameters) become SQL filters; the rest is ranked by full-text and
    /// vector search fused with RRF (k = 60). Embedding down → "fulltext"; nothing left to rank → "filter" (by distance).
    /// </summary>
    public static async Task<IResult> SearchAsync(HttpContext http, NpgsqlDataSource db, MediaUrls urls, IConfiguration config,
        IEmbeddingClient embeddings, string? q, string? type, int? minRent, int? maxRent, double? lat, double? lng, int? radiusM,
        string? amenities, bool? selfContained, int? page, int? pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 300)
            throw Validation("q", "Nhập câu tìm kiếm (tối đa 300 ký tự)");
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var active = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM app.amenities WHERE is_active", cancellationToken: ct))).ToHashSet();
        var parsed = QueryParser.Parse(q, active);
        var f = parsed.Filters;
        var applied = new AppliedFilters(type ?? f.Type, minRent ?? f.MinRent, maxRent ?? f.MaxRent,
            Csv(amenities).Concat(f.Amenities).Distinct().ToList(), selfContained ?? f.SelfContained, f.NearCampus);
        var filters = await BuildAsync(connection, config, applied.Type, applied.MinRent, applied.MaxRent, lat, lng, radiusM,
            [.. applied.Amenities], applied.SelfContained, "distance", ct);

        if (parsed.Remainder.Length == 0)
        {
            var rows = await FilteredAsync(connection, filters, size, offset, ct);
            return Results.Ok(new SearchResult(rows.Select(r => ToItem(r, urls)).ToList(), p, size, rows.Count > 0 ? rows[0].Total : 0,
                applied, "filter"));
        }

        var vector = await embeddings.EmbedAsync(parsed.Remainder, ct);
        var ranked = await HybridAsync(connection, filters, parsed.Remainder, vector, size, offset, ct);
        var items = ranked.Count == 0 ? [] : await ByIdsAsync(connection, filters, ranked.Select(r => r.Id).ToArray(), ct);
        return Results.Ok(new SearchResult(items.Select(r => ToItem(r, urls)).ToList(), p, size, ranked.Count > 0 ? ranked[0].Total : 0,
            applied, vector is null ? "fulltext" : "hybrid"));
    }

    private static string[] Csv(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<Filters> BuildAsync(NpgsqlConnection connection, IConfiguration config, string? type, int? minRent,
        int? maxRent, double? lat, double? lng, int? radiusM, string[] amenities, bool? selfContained, string? sort, CancellationToken ct)
    {
        if (type is not (null or "room" or "job")) throw Validation("type", "Loại tin là room hoặc job");
        if (minRent is < 0 || maxRent is < 0 || (minRent is { } a && maxRent is { } b && a > b))
            throw Validation("minRent", "Khoảng giá không hợp lệ");
        if ((lat is null) != (lng is null)) throw Validation("lat", "Cần cả lat và lng");
        if (lat is not null && (lat is < 8 or > 24 || lng is < 102 or > 110)) throw Validation("lat", "Tọa độ ngoài Việt Nam");
        var radius = radiusM ?? DefaultRadiusM;
        if (radius is < 100 or > MaxRadiusM) throw Validation("radiusM", "Bán kính từ 100 đến 10.000 m");
        var order = sort ?? "distance";
        if (order is not ("distance" or "newest" or "rent_asc")) throw Validation("sort", "Sắp xếp: distance, newest hoặc rent_asc");
        if (amenities.Length > 0)
        {
            var known = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM app.amenities WHERE code = ANY(@Codes) AND is_active", new { Codes = amenities }, cancellationToken: ct));
            if (known != amenities.Distinct().Count()) throw Validation("amenities", "Có tiện nghi không hợp lệ");
        }

        var (centerLat, centerLng) = lat is { } la && lng is { } ln ? (la, ln) : FirstCampus(config);
        return new Filters(type, minRent, maxRent, centerLat, centerLng, radius, amenities.Distinct().ToArray(), selfContained, order);
    }

    /// <summary>Missing coordinates → the first campus in Campuses__Json (docs/04-api.md).</summary>
    private static (double Lat, double Lng) FirstCampus(IConfiguration config)
    {
        var json = config["Campuses:Json"];
        var first = string.IsNullOrWhiteSpace(json) ? null
            : JsonSerializer.Deserialize<List<JsonElement>>(json)?.FirstOrDefault();
        if (first is not { } c) throw Validation("lat", "Chọn vị trí (chưa cấu hình cơ sở đào tạo mặc định)");
        return (c.GetProperty("lat").GetDouble(), c.GetProperty("lng").GetDouble());
    }

    private const string Select = """
        SELECT l.id, l.public_id, l.listing_type::text AS listing_type, l.title, w.name AS ward_name, l.street_address,
               ST_Y(l.location::geometry) AS lat, ST_X(l.location::geometry) AS lng,
               r.monthly_rent_vnd, r.area_m2, r.is_self_contained,
               j.employer_name, j.pay_vnd, j.pay_unit::text AS pay_unit,
               ST_Distance(l.location, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography) AS distance_m,
               (SELECT li.object_key FROM app.listing_images li WHERE li.listing_id = l.id ORDER BY li.sort_order LIMIT 1) AS cover_key,
               l.expires_at,
               count(*) OVER () AS total
        FROM app.listings l
        JOIN app.administrative_units w ON w.code = l.ward_code
        LEFT JOIN app.room_listings r ON r.listing_id = l.id
        LEFT JOIN app.job_listings  j ON j.listing_id = l.id
        """;

    /// <summary>Public (BR-01) and every hard filter; shared by the filter query and the hybrid candidates.</summary>
    private const string Where = """
        l.status = 'verified' AND l.expires_at > now()
          AND (@Type::text IS NULL OR l.listing_type = @Type::app.listing_type)
          AND (@MinRent::int IS NULL OR r.monthly_rent_vnd >= @MinRent)
          AND (@MaxRent::int IS NULL OR r.monthly_rent_vnd <= @MaxRent)
          AND (@SelfContained::boolean IS NULL OR r.is_self_contained = @SelfContained)
          AND ST_DWithin(l.location, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @RadiusM)
          AND (cardinality(@Amenities::text[]) = 0 OR (
                SELECT count(*) FROM app.room_amenities ra JOIN app.amenities am ON am.id = ra.amenity_id
                WHERE ra.listing_id = l.id AND am.code = ANY(@Amenities::text[])) = cardinality(@Amenities::text[]))
        """;

    private static object Args(Filters f, object? extra = null)
    {
        var args = new DynamicParameters(new
        {
            f.Type, f.MinRent, f.MaxRent, f.SelfContained, f.Lat, f.Lng, f.RadiusM, f.Amenities,
        });
        if (extra is not null) args.AddDynamicParams(extra);
        return args;
    }

    private static async Task<List<Row>> FilteredAsync(NpgsqlConnection connection, Filters f, int limit, int offset, CancellationToken ct)
    {
        var order = f.Sort switch
        {
            "newest" => "l.created_at DESC, l.id DESC",
            "rent_asc" => "r.monthly_rent_vnd ASC NULLS LAST, l.id",
            _ => "distance_m, l.id",
        };
        return (await connection.QueryAsync<Row>(new CommandDefinition(
            $"{Select} WHERE {Where} ORDER BY {order} LIMIT @Limit OFFSET @Offset",
            Args(f, new { Limit = limit, Offset = offset }), cancellationToken: ct))).ToList();
    }

    private static async Task<List<Row>> ByIdsAsync(NpgsqlConnection connection, Filters f, long[] ids, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            $"{Select} WHERE l.id = ANY(@Ids)", Args(f, new { Ids = ids }), cancellationToken: ct))).ToDictionary(r => r.Id);
        return ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
    }

    /// <summary>docs/03-database.md, Tìm kiếm hybrid (RRF); without a vector only the full-text branch runs.</summary>
    private static async Task<List<(long Id, long Total)>> HybridAsync(NpgsqlConnection connection, Filters f, string query,
        QueryVector? vector, int limit, int offset, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SET LOCAL hnsw.iterative_scan = relaxed_order", transaction: tx, cancellationToken: ct));
        var vec = vector is null ? "" : """
            ,
            vec AS (
                SELECT e.listing_id AS id, row_number() OVER (ORDER BY e.embedding <=> @QueryVector::vector) AS rnk
                FROM app.listing_embeddings e JOIN candidates c ON c.id = e.listing_id
                WHERE e.model_version_id = @ModelVersionId
                ORDER BY e.embedding <=> @QueryVector::vector LIMIT 100)
            """;
        var union = vector is null ? "SELECT id, rnk FROM fts" : "SELECT id, rnk FROM fts UNION ALL SELECT id, rnk FROM vec";
        var rows = (await connection.QueryAsync<(long Id, long Total)>(new CommandDefinition(
            $"""
            WITH candidates AS (
                SELECT l.id FROM app.listings l LEFT JOIN app.room_listings r ON r.listing_id = l.id WHERE {Where}),
            fts AS (
                SELECT l.id, row_number() OVER (ORDER BY ts_rank_cd(l.search_tsv, app.or_tsquery(@Query)) DESC) AS rnk
                FROM app.listings l JOIN candidates c ON c.id = l.id
                WHERE l.search_tsv @@ app.or_tsquery(@Query)
                ORDER BY ts_rank_cd(l.search_tsv, app.or_tsquery(@Query)) DESC LIMIT 100)
            {vec}
            SELECT id, count(*) OVER () AS total FROM (
                SELECT id, sum(1.0 / (60 + rnk)) AS rrf_score FROM ({union}) u GROUP BY id) s
            ORDER BY rrf_score DESC, id
            LIMIT @Limit OFFSET @Offset
            """, Args(f, new
            {
                Query = query, Limit = limit, Offset = offset,
                QueryVector = vector is null ? null : "[" + string.Join(',', vector.Vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]",
                ModelVersionId = (short?)vector?.ModelVersionId,
            }), tx, cancellationToken: ct))).ToList();
        await tx.CommitAsync(ct);
        return rows;
    }

    private static Item ToItem(Row r, MediaUrls urls) => new(r.PublicId, r.ListingType, r.Title, r.WardName, r.StreetAddress,
        new GeoPoint(r.Lat, r.Lng), r.CoverKey is null ? null : urls.ListingImage(r.CoverKey),
        r.MonthlyRentVnd is { } rent ? new RoomSummary(rent, r.AreaM2!.Value, r.IsSelfContained!.Value) : null,
        r.EmployerName is { } e ? new JobSummary(e, r.PayVnd!.Value, r.PayUnit!) : null,
        (int)Math.Round(r.DistanceM), r.ExpiresAt);

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });
}
