using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Listings;

/// <summary>POST /listings and PUT /listings/{id} (US-301, US-302, US-307).</summary>
public static class ListingWriteEndpoints
{
    /// <summary>
    /// Creates a draft: listings + child table + room_amenities + the first listing_transitions row (∅ → draft),
    /// in one transaction.
    /// </summary>
    public static async Task<IResult> CreateAsync(ListingInput input, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await CheckReferencesAsync(connection, tx, input, ct);
        var (id, publicId) = await connection.QuerySingleAsync<(long, Guid)>(new CommandDefinition(
            """
            INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location)
            VALUES (@UserId, @ListingType::app.listing_type, @Title, @Description, @WardCode, @StreetAddress,
                    ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography)
            RETURNING id, public_id
            """, Common(input, new { UserId = userId }), tx, cancellationToken: ct));
        await WriteChildAsync(connection, tx, id, input, insert: true, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) VALUES (@Id, NULL, 'draft', @UserId)",
            new { Id = id, UserId = userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/listings/{publicId}", new CreatedListing(publicId, 1));
    }

    /// <summary>Only drafts, with the version the client edited; the type cannot change (it decides the child table).</summary>
    public static async Task<IResult> UpdateAsync(Guid listingId, ListingUpdate update, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var input = update.Input;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        ListingAccess.RequireDraft(listing);
        ListingAccess.RequireVersion(listing, update.Version);
        if (listing.ListingType != input.ListingType)
            throw Validation("listingType", "Không đổi được loại tin; hãy tạo tin mới");
        await CheckReferencesAsync(connection, tx, input, ct);

        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE app.listings
            SET title = @Title, description = @Description, ward_code = @WardCode, street_address = @StreetAddress,
                location = ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, version = version + 1
            WHERE id = @Id AND version = @Version
            RETURNING version
            """, Common(input, new { listing.Id, update.Version }), tx, cancellationToken: ct));
        await WriteChildAsync(connection, tx, listing.Id, input, insert: false, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new VersionResponse(version));
    }

    private static object Common(ListingInput i, object extra)
    {
        var values = new DynamicParameters(extra);
        values.AddDynamicParams(new
        {
            i.ListingType,
            Title = i.Title.Trim(),
            Description = i.Description.Trim(),
            i.WardCode,
            StreetAddress = i.StreetAddress.Trim(),
            i.Location.Lat,
            i.Location.Lng,
        });
        return values;
    }

    /// <summary>wardCode must be an existing commune; amenityCodes must all be active (docs/04-api.md).</summary>
    private static async Task CheckReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, ListingInput input, CancellationToken ct)
    {
        var ward = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.administrative_units WHERE code = @Code AND unit_level = 'commune')",
            new { Code = input.WardCode }, tx, cancellationToken: ct));
        if (!ward) throw Validation("wardCode", "Phường/xã không tồn tại");

        var codes = input.Room?.AmenityCodes ?? [];
        if (codes.Count == 0) return;
        var active = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM app.amenities WHERE code = ANY(@Codes) AND is_active",
            new { Codes = codes.ToArray() }, tx, cancellationToken: ct));
        if (active != codes.Count) throw Validation("room.amenityCodes", "Có tiện nghi không còn dùng");
    }

    private static async Task WriteChildAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long id, ListingInput input,
        bool insert, CancellationToken ct)
    {
        if (input.Room is { } room)
        {
            await connection.ExecuteAsync(new CommandDefinition(insert
                ? """
                  INSERT INTO app.room_listings (listing_id, monthly_rent_vnd, deposit_vnd, area_m2, is_self_contained)
                  VALUES (@Id, @MonthlyRentVnd, @DepositVnd, @AreaM2, @IsSelfContained)
                  """
                : """
                  UPDATE app.room_listings SET monthly_rent_vnd = @MonthlyRentVnd, deposit_vnd = @DepositVnd,
                      area_m2 = @AreaM2, is_self_contained = @IsSelfContained
                  WHERE listing_id = @Id
                  """,
                new { Id = id, room.MonthlyRentVnd, room.DepositVnd, room.AreaM2, room.IsSelfContained }, tx, cancellationToken: ct));
            if (!insert)
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM app.room_amenities WHERE listing_id = @Id", new { Id = id }, tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO app.room_amenities (listing_id, amenity_id)
                SELECT @Id, a.id FROM app.amenities a WHERE a.code = ANY(@Codes)
                """, new { Id = id, Codes = (room.AmenityCodes ?? []).ToArray() }, tx, cancellationToken: ct));
        }
        else if (input.Job is { } job)
        {
            await connection.ExecuteAsync(new CommandDefinition(insert
                ? """
                  INSERT INTO app.job_listings (listing_id, employer_name, pay_vnd, pay_unit, hours_per_week)
                  VALUES (@Id, @EmployerName, @PayVnd, @PayUnit::app.pay_unit, @HoursPerWeek)
                  """
                : """
                  UPDATE app.job_listings SET employer_name = @EmployerName, pay_vnd = @PayVnd,
                      pay_unit = @PayUnit::app.pay_unit, hours_per_week = @HoursPerWeek
                  WHERE listing_id = @Id
                  """,
                new { Id = id, EmployerName = job.EmployerName.Trim(), job.PayVnd, job.PayUnit, job.HoursPerWeek }, tx, cancellationToken: ct));
        }
    }

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });
}
