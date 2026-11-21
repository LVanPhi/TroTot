using System.Text.Json;
using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Meta;

/// <summary>/api/v1/meta: public reference data, cached 1 hour (docs/04-api.md, Danh mục).</summary>
public static class MetaEndpoints
{
    public sealed record Province(string Code, string Name);

    /// <summary>A commune with its province, e.g. "Phường X, Thành phố Hà Nội" (docs/06-frontend.md, Form).</summary>
    public sealed record Commune(string Code, string Name, string ProvinceCode, string ProvinceName);

    public sealed record Amenity(string Code, string Name);

    public sealed record Criterion(string Code, string Name, string Guidance, string Scope, string? LegalBasis);

    public sealed record Campus(string Code, string Name, double Lat, double Lng);

    private const string CacheControl = "public, max-age=3600";

    public static void MapMeta(this IEndpointRouteBuilder app)
    {
        var meta = app.MapGroup("/api/v1/meta").WithTags("Meta").AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                context.HttpContext.Response.Headers.CacheControl = CacheControl;
                return result;
            });

        meta.MapGet("/provinces", ProvincesAsync).Produces<IReadOnlyList<Province>>();
        meta.MapGet("/provinces/{code}/communes", CommunesOfProvinceAsync).Produces<IReadOnlyList<Commune>>();
        meta.MapGet("/communes", SearchCommunesAsync).Produces<IReadOnlyList<Commune>>();
        meta.MapGet("/amenities", AmenitiesAsync).Produces<IReadOnlyList<Amenity>>();
        meta.MapGet("/criteria", CriteriaAsync).Produces<IReadOnlyList<Criterion>>();
        meta.MapGet("/campuses", Campuses).Produces<IReadOnlyList<Campus>>();
    }

    private const string CommuneColumns =
        "c.code, c.name, p.code AS province_code, p.name AS province_name";

    public static async Task<IResult> ProvincesAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await connection.QueryAsync<Province>(new CommandDefinition(
            "SELECT code, name FROM app.administrative_units WHERE unit_level = 'province' ORDER BY name",
            cancellationToken: ct)));
    }

    public static async Task<IResult> CommunesOfProvinceAsync(string code, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.administrative_units WHERE code = @Code AND unit_level = 'province')",
            new { Code = code }, cancellationToken: ct));
        if (!exists) throw new ProblemException("resource.not_found");

        return Results.Ok(await connection.QueryAsync<Commune>(new CommandDefinition(
            $"""
            SELECT {CommuneColumns}
            FROM app.administrative_units c JOIN app.administrative_units p ON p.code = c.parent_code
            WHERE c.parent_code = @Code ORDER BY c.name
            """, new { Code = code }, cancellationToken: ct)));
    }

    /// <summary>
    /// Suggestions by name, accents optional ("phuong dich vong" finds "Phường Dịch Vọng"): trigram similarity on
    /// immutable_unaccent(lower(name)) backed by ix_administrative_units_name_trgm; substring matches rank first.
    /// </summary>
    public static async Task<IResult> SearchCommunesAsync(string? q, NpgsqlDataSource db, CancellationToken ct)
    {
        var term = q?.Trim() ?? "";
        if (term.Length < 2) return Results.Ok(Array.Empty<Commune>());
        if (term.Length > 100) term = term[..100];

        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await connection.QueryAsync<Commune>(new CommandDefinition(
            $"""
            WITH query AS (SELECT app.immutable_unaccent(lower(@Term)) AS t)
            SELECT {CommuneColumns}
            FROM app.administrative_units c
            JOIN app.administrative_units p ON p.code = c.parent_code,
                 query
            WHERE c.unit_level = 'commune'
              AND (app.immutable_unaccent(lower(c.name)) % query.t
                   OR app.immutable_unaccent(lower(c.name)) LIKE '%' || query.t || '%')
            ORDER BY (app.immutable_unaccent(lower(c.name)) LIKE '%' || query.t || '%') DESC,
                     similarity(app.immutable_unaccent(lower(c.name)), query.t) DESC,
                     c.name
            LIMIT 20
            """, new { Term = term }, cancellationToken: ct)));
    }

    public static async Task<IResult> AmenitiesAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await connection.QueryAsync<Amenity>(new CommandDefinition(
            "SELECT code, name FROM app.amenities WHERE is_active ORDER BY id", cancellationToken: ct)));
    }

    /// <summary>Active criteria for the "how scoring works" page (transparency, US-203).</summary>
    public static async Task<IResult> CriteriaAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await connection.QueryAsync<Criterion>(new CommandDefinition(
            "SELECT code, name, guidance, scope::text AS scope, legal_basis FROM app.criteria WHERE is_active ORDER BY id",
            cancellationToken: ct)));
    }

    /// <summary>Campuses__Json: default search centres. A malformed value is a configuration error (500).</summary>
    public static IResult Campuses(IConfiguration configuration)
    {
        var json = configuration["Campuses:Json"];
        if (string.IsNullOrWhiteSpace(json)) return Results.Ok(Array.Empty<Campus>());
        var campuses = JsonSerializer.Deserialize<List<Campus>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Campuses__Json is not a JSON array");
        return Results.Ok(campuses);
    }
}
