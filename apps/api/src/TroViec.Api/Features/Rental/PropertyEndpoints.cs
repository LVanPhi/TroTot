using System.Globalization;
using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Rental;

/// <summary>
/// /api/v1/rental/properties, /rental/rooms (US-1001, US-1002, US-1005; docs/12-rental.md). Every endpoint needs
/// rental.manage and ownership of the property: another owner's property or room answers 404 (AGENTS.md rule 12).
/// </summary>
public static class PropertyEndpoints
{
    public sealed record PropertyRequest(string Name, string WardCode, string StreetAddress, GeoPoint Location, string? Note);

    public sealed record PropertyItem(long Id, string Name, string WardCode, string StreetAddress, GeoPoint Location,
        string? Note, int RoomCount, DateTime CreatedAt);

    public sealed record PropertyDetail(long Id, string Name, string WardCode, string StreetAddress, GeoPoint Location,
        string? Note, DateTime CreatedAt, IReadOnlyList<RoomItem> Rooms);

    public sealed record RoomRequest(string Code, short? Floor, decimal AreaM2, bool IsSelfContained, int ListRentVnd,
        IReadOnlyList<string>? AmenityCodes);

    /// <summary>PUT /rental/rooms/{id}: Status only "vacant" or "maintenance"; null keeps the current status.</summary>
    public sealed record RoomUpdate(string Code, short? Floor, decimal AreaM2, bool IsSelfContained, int ListRentVnd,
        IReadOnlyList<string>? AmenityCodes, string? Status, int Version)
    {
        public RoomRequest Fields => new(Code, Floor, AreaM2, IsSelfContained, ListRentVnd, AmenityCodes);
    }

    public sealed record RoomItem(long Id, long PropertyId, string Code, short? Floor, decimal AreaM2, bool IsSelfContained,
        int ListRentVnd, string Status, int Version, IReadOnlyList<string> AmenityCodes);

    public sealed record Created(long Id);

    public sealed record CreatedRoom(long Id, int Version);

    public sealed class PropertyValidator : AbstractValidator<PropertyRequest>
    {
        public PropertyValidator()
        {
            RuleFor(r => r.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 150)
                .WithMessage("Tên nhà từ 1 đến 150 ký tự");
            RuleFor(r => r.WardCode).Must(w => w is not null && w.Length == 5 && w.All(char.IsAsciiDigit))
                .WithMessage("Chọn phường/xã");
            RuleFor(r => r.StreetAddress).Must(a => a is not null && a.Trim().Length is >= 1 and <= 200)
                .WithMessage("Địa chỉ từ 1 đến 200 ký tự");
            RuleFor(r => r.Location).Must(l => l is { Lat: >= 8 and <= 24, Lng: >= 102 and <= 110 })
                .WithMessage("Ghim vị trí trên bản đồ");
            RuleFor(r => r.Note).Must(n => n is null || n.Trim().Length is >= 1 and <= 1000)
                .WithMessage("Ghi chú tối đa 1.000 ký tự");
        }
    }

    public sealed class RoomValidator : AbstractValidator<RoomRequest>
    {
        public RoomValidator() => AddRoomRules(this, r => r);
    }

    public sealed class RoomUpdateValidator : AbstractValidator<RoomUpdate>
    {
        public RoomUpdateValidator()
        {
            AddRoomRules(this, r => r.Fields);
            RuleFor(r => r.Status).Must(s => s is null or "vacant" or "maintenance")
                .WithMessage("Chỉ đổi được giữa Trống và Đang sửa");
            RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản");
        }
    }

    private static void AddRoomRules<T>(AbstractValidator<T> v, Func<T, RoomRequest> room)
    {
        v.RuleFor(x => room(x).Code).Must(c => c is not null && c.Trim().Length is >= 1 and <= 20)
            .OverridePropertyName("code").WithMessage("Mã phòng từ 1 đến 20 ký tự");
        v.RuleFor(x => room(x).Floor).Must(f => f is null or >= -5 and <= 100)
            .OverridePropertyName("floor").WithMessage("Tầng không hợp lệ");
        v.RuleFor(x => room(x).AreaM2).Must(a => a >= 0.1m && a <= 500 && decimal.Round(a, 1) == a)
            .OverridePropertyName("areaM2").WithMessage("Diện tích từ 0,1 đến 500 m²");
        v.RuleFor(x => room(x).ListRentVnd).InclusiveBetween(100_000, 100_000_000)
            .OverridePropertyName("listRentVnd").WithMessage("Giá từ 100.000 đến 100.000.000 ₫");
        v.RuleFor(x => room(x).AmenityCodes).Must(c => c is null || (c.Count <= 30 && c.Distinct().Count() == c.Count))
            .OverridePropertyName("amenityCodes").WithMessage("Danh sách tiện nghi không hợp lệ");
    }

    public static void MapProperties(this RouteGroupBuilder rental)
    {
        rental.MapGet("/properties", ListAsync).Produces<IReadOnlyList<PropertyItem>>();
        rental.MapPost("/properties", CreateAsync).Validate<PropertyRequest>().Produces<Created>(StatusCodes.Status201Created);
        rental.MapGet("/properties/{id:long}", DetailAsync).Produces<PropertyDetail>();
        rental.MapPut("/properties/{id:long}", UpdateAsync).Validate<PropertyRequest>().Produces<PropertyDetail>();
        rental.MapPost("/properties/{id:long}/rooms", CreateRoomAsync).Validate<RoomRequest>()
            .Produces<CreatedRoom>(StatusCodes.Status201Created);
        rental.MapPut("/rooms/{id:long}", UpdateRoomAsync).Validate<RoomUpdate>().Produces<RoomItem>();
        rental.MapPost("/rooms/{id:long}/listing", CreateListingAsync)
            .RequireAuthorization(Permissions.ListingCreate)
            .Produces<CreatedListing>(StatusCodes.Status201Created);
    }

    private const string PropertyColumns = """
        p.id, p.name, p.ward_code, p.street_address, ST_Y(p.location::geometry) AS lat, ST_X(p.location::geometry) AS lng,
        p.note, p.created_at
        """;

    // Row classes with settable properties: Dapper maps them by column name without requiring exact constructor types.
    private sealed class PropertyRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string WardCode { get; init; } = "";
        public string StreetAddress { get; init; } = "";
        public double Lat { get; init; }
        public double Lng { get; init; }
        public string? Note { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class PropertyListRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string WardCode { get; init; } = "";
        public string StreetAddress { get; init; } = "";
        public double Lat { get; init; }
        public double Lng { get; init; }
        public string? Note { get; init; }
        public DateTime CreatedAt { get; init; }
        public int RoomCount { get; init; }
    }

    private sealed class RoomRow
    {
        public long Id { get; init; }
        public long PropertyId { get; init; }
        public string Code { get; init; } = "";
        public short? Floor { get; init; }
        public decimal AreaM2 { get; init; }
        public bool IsSelfContained { get; init; }
        public int ListRentVnd { get; init; }
        public string Status { get; init; } = "";
        public int Version { get; init; }
        public string[] AmenityCodes { get; init; } = [];
    }

    public static async Task<IResult> ListAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<PropertyListRow>(new CommandDefinition(
            $"""
            SELECT {PropertyColumns}, (SELECT count(*)::int FROM app.rooms r WHERE r.property_id = p.id) AS room_count
            FROM app.properties p
            WHERE p.owner_id = @Owner
            ORDER BY p.created_at DESC
            """, new { Owner = CurrentUser.Id(http.User) }, cancellationToken: ct));
        return Results.Ok(rows.Select(r => new PropertyItem(r.Id, r.Name, r.WardCode, r.StreetAddress, new GeoPoint(r.Lat, r.Lng),
            r.Note, r.RoomCount, r.CreatedAt)).ToList());
    }

    public static async Task<IResult> CreateAsync(PropertyRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireCommuneAsync(connection, request.WardCode, ct);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.properties (owner_id, name, ward_code, street_address, location, note)
            VALUES (@Owner, @Name, @WardCode, @StreetAddress, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @Note)
            RETURNING id
            """, PropertyArgs(request, CurrentUser.Id(http.User), 0), cancellationToken: ct));
        return Results.Created($"/api/v1/rental/properties/{id}", new Created(id));
    }

    public static async Task<IResult> DetailAsync(long id, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await PropertyDetailAsync(connection, id, CurrentUser.Id(http.User), ct));
    }

    public static async Task<IResult> UpdateAsync(long id, PropertyRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireCommuneAsync(connection, request.WardCode, ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.properties
            SET name = @Name, ward_code = @WardCode, street_address = @StreetAddress,
                location = ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, note = @Note
            WHERE id = @Id AND owner_id = @Owner
            """, PropertyArgs(request, owner, id), cancellationToken: ct));
        if (updated == 0) throw new ProblemException("resource.not_found");
        return Results.Ok(await PropertyDetailAsync(connection, id, owner, ct));
    }

    public static async Task<IResult> CreateRoomAsync(long id, RoomRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await RequireOwnedPropertyAsync(connection, tx, id, CurrentUser.Id(http.User), ct);
        await RequireActiveAmenitiesAsync(connection, tx, request.AmenityCodes, ct);
        var roomId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.rooms (property_id, code, floor, area_m2, is_self_contained, list_rent_vnd)
            VALUES (@PropertyId, @Code, @Floor, @AreaM2, @IsSelfContained, @ListRentVnd)
            RETURNING id
            """, new
            {
                PropertyId = id, Code = request.Code.Trim(), request.Floor, request.AreaM2, request.IsSelfContained,
                request.ListRentVnd,
            }, tx, cancellationToken: ct));
        await WriteAmenitiesAsync(connection, tx, roomId, request.AmenityCodes, ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/rental/rooms/{roomId}", new CreatedRoom(roomId, 1));
    }

    /// <summary>
    /// reserved/occupied are set by leases only (US-1002 AC1): asking to change the status of such a room answers
    /// 409 rental.room_status_locked; the other fields stay editable.
    /// </summary>
    public static async Task<IResult> UpdateRoomAsync(long id, RoomUpdate update, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var room = await OwnedRoomAsync(connection, tx, id, CurrentUser.Id(http.User), ct);
        if (room.Version != update.Version) throw new ProblemException("rental.version_conflict");
        if (update.Status is { } status && status != room.Status && room.Status is "reserved" or "occupied")
            throw new ProblemException("rental.room_status_locked");
        await RequireActiveAmenitiesAsync(connection, tx, update.AmenityCodes, ct);

        var version = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE app.rooms
            SET code = @Code, floor = @Floor, area_m2 = @AreaM2, is_self_contained = @IsSelfContained,
                list_rent_vnd = @ListRentVnd, status = coalesce(@Status::app.room_status, status), version = version + 1
            WHERE id = @Id AND version = @Version
            RETURNING version
            """, new
            {
                Id = id, Code = update.Code.Trim(), update.Floor, update.AreaM2, update.IsSelfContained, update.ListRentVnd,
                update.Status, update.Version,
            }, tx, cancellationToken: ct)) ?? throw new ProblemException("rental.version_conflict");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app.rental_room_amenities WHERE room_id = @Id", new { Id = id }, tx, cancellationToken: ct));
        await WriteAmenitiesAsync(connection, tx, id, update.AmenityCodes, ct);
        await tx.CommitAsync(ct);
        return Results.Ok((await RoomsAsync(connection, null, room.PropertyId, id, ct)).Single() with { Version = version });
    }

    /// <summary>
    /// US-1005 / BR-32: a draft listing prefilled from the room and its property. Only a vacant room without another
    /// listing that is not removed; the room row is locked so two requests cannot both create one.
    /// </summary>
    public static async Task<IResult> CreateListingAsync(long id, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var room = await OwnedRoomAsync(connection, tx, id, owner, ct, forUpdate: true);
        if (room.Status != "vacant") throw new ProblemException("rental.room_not_vacant");
        var hasListing = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM app.room_listings rl JOIN app.listings l ON l.id = rl.listing_id
                           WHERE rl.room_id = @Id AND l.status <> 'removed')
            """, new { Id = id }, tx, cancellationToken: ct));
        if (hasListing) throw new ProblemException("rental.room_not_vacant");

        var property = await connection.QuerySingleAsync<PropertyRow>(new CommandDefinition(
            $"SELECT {PropertyColumns} FROM app.properties p WHERE p.id = @Id", new { Id = room.PropertyId }, tx, cancellationToken: ct));
        var area = room.AreaM2.ToString("0.#", CultureInfo.GetCultureInfo("vi-VN"));
        var title = $"Phòng {room.Code} – {property.Name}";
        var input = new ListingInput(
            "room",
            title.Length <= 150 ? title : title[..150],
            $"Phòng {room.Code} tại {property.Name}, diện tích {area} m²"
                + (room.IsSelfContained ? ", khép kín" : "")
                + ". Bổ sung mô tả chi tiết (tiện ích, giờ giấc, chi phí điện nước) trước khi gửi duyệt.",
            property.WardCode,
            property.StreetAddress,
            new GeoPoint(property.Lat, property.Lng),
            new RoomInput(room.ListRentVnd, null, room.AreaM2, room.IsSelfContained, room.AmenityCodes),
            null);
        var (listingId, publicId) = await ListingWriteEndpoints.InsertDraftAsync(connection, tx, owner, input, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.room_listings SET room_id = @Room WHERE listing_id = @Listing",
            new { Room = id, Listing = listingId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/listings/{publicId}", new CreatedListing(publicId, 1));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static object PropertyArgs(PropertyRequest r, long owner, long id) => new
    {
        Id = id,
        Owner = owner,
        Name = r.Name.Trim(),
        r.WardCode,
        StreetAddress = r.StreetAddress.Trim(),
        r.Location.Lat,
        r.Location.Lng,
        Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(),
    };

    private static async Task<PropertyDetail> PropertyDetailAsync(NpgsqlConnection connection, long id, long owner, CancellationToken ct)
    {
        var p = await connection.QuerySingleOrDefaultAsync<PropertyRow>(new CommandDefinition(
            $"SELECT {PropertyColumns} FROM app.properties p WHERE p.id = @Id AND p.owner_id = @Owner",
            new { Id = id, Owner = owner }, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        var rooms = await RoomsAsync(connection, null, id, null, ct);
        return new PropertyDetail(p.Id, p.Name, p.WardCode, p.StreetAddress, new GeoPoint(p.Lat, p.Lng), p.Note, p.CreatedAt, rooms);
    }

    private static async Task<IReadOnlyList<RoomItem>> RoomsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long propertyId, long? roomId, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<RoomRow>(new CommandDefinition(
            """
            SELECT r.id, r.property_id, r.code, r.floor, r.area_m2, r.is_self_contained, r.list_rent_vnd, r.status::text AS status,
                   r.version,
                   coalesce(ARRAY(SELECT a.code FROM app.rental_room_amenities ra JOIN app.amenities a ON a.id = ra.amenity_id
                                  WHERE ra.room_id = r.id ORDER BY a.id), '{}') AS amenity_codes
            FROM app.rooms r
            WHERE r.property_id = @PropertyId AND (@RoomId::bigint IS NULL OR r.id = @RoomId)
            ORDER BY r.code
            """, new { PropertyId = propertyId, RoomId = roomId }, tx, cancellationToken: ct));
        return rows.Select(r => new RoomItem(r.Id, r.PropertyId, r.Code, r.Floor, r.AreaM2, r.IsSelfContained, r.ListRentVnd,
            r.Status, r.Version, r.AmenityCodes)).ToList();
    }

    private static async Task<RoomRow> OwnedRoomAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long roomId, long owner, CancellationToken ct, bool forUpdate = false) =>
        await connection.QuerySingleOrDefaultAsync<RoomRow>(new CommandDefinition(
            $"""
            SELECT r.id, r.property_id, r.code, r.floor, r.area_m2, r.is_self_contained, r.list_rent_vnd, r.status::text AS status,
                   r.version,
                   coalesce(ARRAY(SELECT a.code FROM app.rental_room_amenities ra JOIN app.amenities a ON a.id = ra.amenity_id
                                  WHERE ra.room_id = r.id AND a.is_active ORDER BY a.id), '{"{}"}') AS amenity_codes
            FROM app.rooms r
            JOIN app.properties p ON p.id = r.property_id
            WHERE r.id = @Id AND p.owner_id = @Owner
            {(forUpdate ? "FOR UPDATE OF r" : "")}
            """, new { Id = roomId, Owner = owner }, tx, cancellationToken: ct))
        ?? throw new ProblemException("resource.not_found");

    private static async Task RequireOwnedPropertyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long propertyId, long owner, CancellationToken ct)
    {
        var owned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.properties WHERE id = @Id AND owner_id = @Owner)",
            new { Id = propertyId, Owner = owner }, tx, cancellationToken: ct));
        if (!owned) throw new ProblemException("resource.not_found");
    }

    private static async Task RequireCommuneAsync(NpgsqlConnection connection, string wardCode, CancellationToken ct)
    {
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.administrative_units WHERE code = @Code AND unit_level = 'commune')",
            new { Code = wardCode }, cancellationToken: ct));
        if (!exists) throw Validation("wardCode", "Phường/xã không tồn tại");
    }

    private static async Task RequireActiveAmenitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<string>? codes, CancellationToken ct)
    {
        if (codes is not { Count: > 0 }) return;
        var active = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM app.amenities WHERE code = ANY(@Codes) AND is_active",
            new { Codes = codes.ToArray() }, tx, cancellationToken: ct));
        if (active != codes.Count) throw Validation("amenityCodes", "Có tiện nghi không còn dùng");
    }

    private static Task WriteAmenitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long roomId, IReadOnlyList<string>? codes, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.rental_room_amenities (room_id, amenity_id)
            SELECT @Room, a.id FROM app.amenities a WHERE a.code = ANY(@Codes)
            """, new { Room = roomId, Codes = (codes ?? []).ToArray() }, tx, cancellationToken: ct));

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });
}
