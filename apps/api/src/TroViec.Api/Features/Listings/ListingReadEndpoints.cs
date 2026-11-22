using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Listings;

/// <summary>GET /listings/{id}, GET /me/listings, GET /listings/{id}/history.</summary>
public static class ListingReadEndpoints
{
    public sealed record Image(long ImageId, string Url, int Width, int Height, int SortOrder);

    public sealed record Amenity(string Code, string Name);

    public sealed record Room(int MonthlyRentVnd, int? DepositVnd, decimal AreaM2, bool IsSelfContained);

    public sealed record Job(string EmployerName, int PayVnd, string PayUnit, int HoursPerWeek);

    /// <summary>
    /// Detail. Status and version only for the poster and moderators; the public fields (poster, contactPhone,
    /// reviewSummary, isSaved) arrive with T3.6.
    /// </summary>
    public sealed record Detail(Guid ListingId, string ListingType, string Title, string Description, string WardCode,
        string WardName, string StreetAddress, GeoPoint Location, Room? Room, Job? Job, IReadOnlyList<Amenity> Amenities,
        IReadOnlyList<Image> Images, DateTime? ExpiresAt, string? Status, int? Version);

    public sealed record MyListing(Guid ListingId, string ListingType, string Title, string Status, int Version,
        DateTime UpdatedAt, DateTime? ExpiresAt, string? CoverImageUrl);

    public sealed record HistoryItem(string? FromStatus, string ToStatus, string? Reason, string Actor, DateTime CreatedAt);

    private sealed record Row(long Id, Guid PublicId, long PosterId, string ListingType, string Title, string Description,
        string WardCode, string WardName, string StreetAddress, double Lat, double Lng, string Status, int Version,
        DateTime? ExpiresAt, bool IsPublic, int? MonthlyRentVnd, int? DepositVnd, decimal? AreaM2, bool? IsSelfContained,
        string? EmployerName, int? PayVnd, string? PayUnit, short? HoursPerWeek);

    /// <summary>Poster and listing.moderate see every status; anyone else only public listings (BR-01), otherwise 404.</summary>
    public static async Task<IResult> DetailAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, MediaUrls urls, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var (detail, _, _) = await LoadAsync(connection, listingId, urls, http, ct);
        return Results.Ok(detail);
    }

    /// <summary>Detail with the internal id and poster id; throws 404 when the caller may not see the listing.</summary>
    public static async Task<(Detail Detail, long Id, long PosterId)> LoadAsync(NpgsqlConnection connection, Guid listingId, MediaUrls urls,
        HttpContext http, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT l.id, l.public_id, l.poster_id, l.listing_type::text AS listing_type, l.title, l.description,
                   l.ward_code, w.name AS ward_name, l.street_address,
                   ST_Y(l.location::geometry) AS lat, ST_X(l.location::geometry) AS lng,
                   l.status::text AS status, l.version, l.expires_at,
                   (l.status = 'verified' AND l.expires_at > now()) AS is_public,
                   r.monthly_rent_vnd, r.deposit_vnd, r.area_m2, r.is_self_contained,
                   j.employer_name, j.pay_vnd, j.pay_unit::text AS pay_unit, j.hours_per_week
            FROM app.listings l
            JOIN app.administrative_units w ON w.code = l.ward_code
            LEFT JOIN app.room_listings r ON r.listing_id = l.id
            LEFT JOIN app.job_listings j ON j.listing_id = l.id
            WHERE l.public_id = @Id
            """, new { Id = listingId }, cancellationToken: ct)) ?? throw new ProblemException("listing.not_found");

        var privileged = IsPoster(http, row.PosterId) || http.User.HasClaim(TokenService.PermissionClaim, Permissions.ListingModerate);
        if (!privileged && !row.IsPublic) throw new ProblemException("listing.not_found");

        var amenities = (await connection.QueryAsync<Amenity>(new CommandDefinition(
            """
            SELECT a.code, a.name FROM app.room_amenities ra JOIN app.amenities a ON a.id = ra.amenity_id
            WHERE ra.listing_id = @Id ORDER BY a.code
            """, new { row.Id }, cancellationToken: ct))).ToList();
        var images = (await connection.QueryAsync<(long Id, string Key, short W, short H, short Sort)>(new CommandDefinition(
            "SELECT id, object_key, width_px, height_px, sort_order FROM app.listing_images WHERE listing_id = @Id ORDER BY sort_order",
            new { row.Id }, cancellationToken: ct)))
            .Select(i => new Image(i.Id, urls.ListingImage(i.Key), i.W, i.H, i.Sort)).ToList();

        return (new Detail(row.PublicId, row.ListingType, row.Title, row.Description, row.WardCode, row.WardName,
            row.StreetAddress, new GeoPoint(row.Lat, row.Lng),
            row.MonthlyRentVnd is { } rent ? new Room(rent, row.DepositVnd, row.AreaM2!.Value, row.IsSelfContained!.Value) : null,
            row.EmployerName is { } employer ? new Job(employer, row.PayVnd!.Value, row.PayUnit!, row.HoursPerWeek!.Value) : null,
            amenities, images, row.ExpiresAt, privileged ? row.Status : null, privileged ? row.Version : null), row.Id, row.PosterId);
    }

    public static async Task<IResult> MineAsync(HttpContext http, NpgsqlDataSource db, MediaUrls urls, string? status, int? page,
        int? pageSize, CancellationToken ct)
    {
        string[] statuses = ["draft", "screening", "pending_review", "verified", "rejected", "hidden", "removed", "expired"];
        if (status is not null && !statuses.Contains(status))
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["status"] = ["Trạng thái không hợp lệ"] });
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid PublicId, string ListingType, string Title, string Status, int Version,
            DateTime UpdatedAt, DateTime? ExpiresAt, string? Cover, long Total)>(new CommandDefinition(
            """
            SELECT l.public_id, l.listing_type::text, l.title, l.status::text, l.version, l.updated_at, l.expires_at,
                   (SELECT i.object_key FROM app.listing_images i WHERE i.listing_id = l.id ORDER BY i.sort_order LIMIT 1),
                   count(*) OVER ()
            FROM app.listings l
            WHERE l.poster_id = @UserId AND (@Status::text IS NULL OR l.status = @Status::app.listing_status)
            ORDER BY l.updated_at DESC, l.id DESC
            LIMIT @Size OFFSET @Offset
            """, new { UserId = CurrentUser.Id(http.User), Status = status, Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new MyListing(r.PublicId, r.ListingType, r.Title, r.Status, r.Version, r.UpdatedAt, r.ExpiresAt,
            r.Cover is null ? null : urls.ListingImage(r.Cover))).ToList();
        return Results.Ok(new Paged<MyListing>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>
    /// Timeline (US-308). Moderators see who acted; the poster sees "Bạn", "Hệ thống" or "Cán bộ xác minh", never a
    /// moderator's name.
    /// </summary>
    public static async Task<IResult> HistoryAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var listing = await connection.QuerySingleOrDefaultAsync<(long Id, long PosterId, bool IsPublic)?>(new CommandDefinition(
            "SELECT id, poster_id, status = 'verified' AND expires_at > now() FROM app.listings WHERE public_id = @Id",
            new { Id = listingId }, cancellationToken: ct)) ?? throw new ProblemException("listing.not_found");
        var moderator = http.User.HasClaim(TokenService.PermissionClaim, Permissions.ListingModerate);
        var poster = IsPoster(http, listing.PosterId);
        if (!moderator && !poster) throw new ProblemException(listing.IsPublic ? "listing.not_owner" : "listing.not_found");

        return Results.Ok(await LoadHistoryAsync(connection, listing.Id, listing.PosterId, showNames: moderator && !poster, ct));
    }

    /// <summary>Timeline rows; without names the poster sees "Bạn", "Hệ thống" or "Cán bộ xác minh".</summary>
    public static async Task<List<HistoryItem>> LoadHistoryAsync(NpgsqlConnection connection, long listingId, long posterId, bool showNames,
        CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(string? From, string To, string? Reason, long ActorId, string ActorName, DateTime At)>(
            new CommandDefinition(
                """
                SELECT t.from_status::text, t.to_status::text, t.reason, t.actor_id, u.full_name, t.created_at
                FROM app.listing_transitions t JOIN app.users u ON u.id = t.actor_id
                WHERE t.listing_id = @Id ORDER BY t.created_at, t.id
                """, new { Id = listingId }, cancellationToken: ct));
        var system = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT app.system_user_id()", cancellationToken: ct));
        return rows.Select(r => new HistoryItem(r.From, r.To, r.Reason,
            showNames ? r.ActorName
            : r.ActorId == posterId ? "Bạn"
            : r.ActorId == system ? "Hệ thống"
            : "Cán bộ xác minh",
            r.At)).ToList();
    }

    private static bool IsPoster(HttpContext http, long posterId) =>
        http.User.Identity?.IsAuthenticated == true
        && long.TryParse(http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var id)
        && id == posterId;
}
