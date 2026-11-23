using System.Net;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TroViec.Api.Features.Rental;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Rental;

/// <summary>T6.2: properties, rooms, payout account, flagged-account review, room → listing (US-1001…1005, BR-31, BR-32).</summary>
[Collection(nameof(InfraCollection))]
public sealed class RentalPropertyTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private Task<TestUser> OwnerAsync() => TestUsers.CreateAsync(Client(), infra, ["owner"]);

    private static object Property(string name = "Nhà trọ Cầu Diễn") => new
    {
        name,
        wardCode = "90001",
        streetAddress = "Số 1 ngõ 2 đường Cầu Diễn",
        location = new { lat = 21.0537, lng = 105.7353 },
        note = (string?)null,
    };

    private static object Room(string code = "P101", string[]? amenities = null) => new
    {
        code,
        floor = (short)1,
        areaM2 = 20.5m,
        isSelfContained = true,
        listRentVnd = 2_500_000,
        amenityCodes = amenities ?? ["wifi"],
    };

    private async Task<long> CreatePropertyAsync(TestUser owner)
    {
        var response = await Client().SendAsync(HttpMethod.Post, "/api/v1/rental/properties", owner.Token, Property());
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AuthClient.ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private async Task<long> CreateRoomAsync(TestUser owner, long property, string code = "P101")
    {
        var response = await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/properties/{property}/rooms", owner.Token, Room(code));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AuthClient.ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static object RoomUpdate(int version, string? status = null, string code = "P101") => new
    {
        code, floor = (short)2, areaM2 = 22.0m, isSelfContained = false, listRentVnd = 2_700_000,
        amenityCodes = new[] { "wifi", "air_conditioner" }, status, version,
    };

    private async Task SetRoomStatusAsync(long room, string status)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        await connection.ExecuteAsync("UPDATE app.rooms SET status = @Status::app.room_status WHERE id = @Id", new { Id = room, Status = status });
    }

    // ---- properties and rooms (US-1001, US-1002) -----------------------------------------------------------

    [Fact]
    public async Task Property_CreateListDetailUpdate_RoundTrips()
    {
        var owner = await OwnerAsync();
        var client = Client();
        var id = await CreatePropertyAsync(owner);
        await CreateRoomAsync(owner, id);

        var list = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/rental/properties", owner.Token));
        var update = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/properties/{id}", owner.Token, Property("Nhà trọ mới"));
        var detail = await AuthClient.ReadJsonAsync(update);

        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("roomCount").GetInt32().ShouldBe(1);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        detail.GetProperty("name").GetString().ShouldBe("Nhà trọ mới");
        detail.GetProperty("rooms")[0].GetProperty("code").GetString().ShouldBe("P101");
        detail.GetProperty("rooms")[0].GetProperty("status").GetString().ShouldBe("vacant");
        detail.GetProperty("rooms")[0].GetProperty("amenityCodes")[0].GetString().ShouldBe("wifi");
    }

    [Fact]
    public async Task Property_OfAnotherOwner_Returns404Everywhere()
    {
        var owner = await OwnerAsync();
        var other = await OwnerAsync();
        var client = Client();
        var id = await CreatePropertyAsync(owner);
        var room = await CreateRoomAsync(owner, id);

        (await client.SendAsync(HttpMethod.Get, $"/api/v1/rental/properties/{id}", other.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/properties/{id}", other.Token, Property())).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/properties/{id}/rooms", other.Token, Room("X1"))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/rooms/{room}", other.Token, RoomUpdate(1))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/rooms/{room}/listing", other.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/rental/properties", other.Token)))
            .GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Rental_WithoutRentalManage_Returns403()
    {
        var student = await TestUsers.CreateAsync(Client(), infra, ["student"]);

        (await Client().SendAsync(HttpMethod.Get, "/api/v1/rental/properties", student.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client().SendAsync(HttpMethod.Get, "/api/v1/me/payout-account", student.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Property_UnknownWard_ReturnsValidationFailed()
    {
        var owner = await OwnerAsync();
        var body = new { name = "Nhà", wardCode = "99999", streetAddress = "Số 1", location = new { lat = 21.0, lng = 105.7 } };

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, "/api/v1/rental/properties", owner.Token, body)))
            .ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Room_DuplicateCodeInProperty_Returns409RoomCodeTaken()
    {
        var owner = await OwnerAsync();
        var id = await CreatePropertyAsync(owner);
        await CreateRoomAsync(owner, id);

        var response = await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/properties/{id}/rooms", owner.Token, Room());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("rental.room_code_taken");
    }

    [Fact]
    public async Task Room_UpdateWithVersionAndMaintenance_ThenStaleVersionConflicts()
    {
        var owner = await OwnerAsync();
        var room = await CreateRoomAsync(owner, await CreatePropertyAsync(owner));
        var client = Client();

        var ok = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/rooms/{room}", owner.Token, RoomUpdate(1, "maintenance"));
        var body = await AuthClient.ReadJsonAsync(ok);
        var stale = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/rooms/{room}", owner.Token, RoomUpdate(1));

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("maintenance");
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("listRentVnd").GetInt32().ShouldBe(2_700_000);
        body.GetProperty("amenityCodes").GetArrayLength().ShouldBe(2);
        (await AuthClient.ProblemCodeAsync(stale)).ShouldBe("rental.version_conflict");
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("occupied")]
    public async Task Room_StatusSetByLease_CannotBeChangedByOwner(string leaseStatus)
    {
        var owner = await OwnerAsync();
        var room = await CreateRoomAsync(owner, await CreatePropertyAsync(owner));
        await SetRoomStatusAsync(room, leaseStatus);
        var client = Client();

        var locked = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/rooms/{room}", owner.Token, RoomUpdate(1, "vacant"));
        var otherFields = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/rooms/{room}", owner.Token, RoomUpdate(1));

        (await AuthClient.ProblemCodeAsync(locked)).ShouldBe("rental.room_status_locked");
        otherFields.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ReadJsonAsync(otherFields)).GetProperty("status").GetString().ShouldBe(leaseStatus);
    }

    // ---- payout account (US-1003, BR-31) ------------------------------------------------------------------

    private static object Payout(string number = "0123 456 789", string holder = "Nguyễn Văn Đức") =>
        new { bankId = (short)1, accountNumber = number, holderName = holder };

    [Fact]
    public async Task PayoutAccount_Saved_EncryptedWithHintAndNormalizedHolder()
    {
        var owner = await OwnerAsync();
        var client = Client();

        (await client.SendAsync(HttpMethod.Get, "/api/v1/me/payout-account", owner.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var put = await client.SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", owner.Token, Payout());
        var body = await AuthClient.ReadJsonAsync(put);
        var raw = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me/payout-account", owner.Token));

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("accountHint").GetString().ShouldBe("•••6789");
        body.GetProperty("holderName").GetString().ShouldBe("NGUYEN VAN DUC");
        body.GetProperty("status").GetString().ShouldBe("active");
        raw.ToString().ShouldNotContain("0123456789");

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var row = await connection.QuerySingleAsync<(byte[] Cipher, string KeyId)>(
            "SELECT account_ciphertext, key_id FROM app.payout_accounts WHERE owner_id = @Id", new { owner.Id });
        var cipher = _factory.Services.GetRequiredService<DocumentCipher>();
        Encoding.UTF8.GetString(cipher.Decrypt(row.KeyId, row.Cipher, PayoutAccountEndpoints.AssociatedData(owner.Id))).ShouldBe("0123456789");
        Should.Throw<System.Security.Cryptography.CryptographicException>(
            () => cipher.Decrypt(row.KeyId, row.Cipher, PayoutAccountEndpoints.AssociatedData(owner.Id + 1)));
    }

    [Theory]
    [InlineData("12345", "NGUYEN VAN A")]
    [InlineData("12345678901234567890", "NGUYEN VAN A")]
    [InlineData("0123abc456", "NGUYEN VAN A")]
    [InlineData("0123456789", "NGUYEN VAN A 2")]
    public async Task PayoutAccount_InvalidNumberOrHolder_ReturnsValidationFailed(string number, string holder)
    {
        var owner = await OwnerAsync();

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", owner.Token,
            Payout(number, holder)))).ShouldBe("validation.failed");
    }

    /// <summary>Puts the account number into identifiers with one confirmed report (as the report trigger would).</summary>
    private async Task MarkReportedAsync(string number)
    {
        var hmac = new IdentifierHasher(IdentifierHasherTests.SharedKeyBase64).Hash("bank_account", number);
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        await connection.ExecuteAsync("""
            WITH i AS (
                INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES ('bank_account', @Hmac, '•••' || right(@Number, 4))
                ON CONFLICT (kind, value_hmac) DO UPDATE SET hint = EXCLUDED.hint
                RETURNING id)
            INSERT INTO app.identifier_stats (identifier_id, confirmed_report_count, last_confirmed_at)
            SELECT id, 1, now() FROM i
            ON CONFLICT (identifier_id) DO UPDATE SET confirmed_report_count = 1, last_confirmed_at = now()
            """, new { Hmac = hmac, Number = number });
    }

    [Fact]
    public async Task PayoutAccount_ReportedNumber_IsFlaggedThenModeratorClears()
    {
        var number = $"9{Random.Shared.NextInt64(100_000_000, 999_999_999)}";
        await MarkReportedAsync(number);
        var owner = await OwnerAsync();
        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        var client = Client();

        var put = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", owner.Token, Payout(number)));
        var list = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/moderation/payout-accounts?pageSize=50", moderator.Token));
        var review = await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/payout-accounts/{owner.Id}/review", moderator.Token,
            new { decision = "clear", note = "Đã gọi điện xác minh chủ tài khoản" });
        var after = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me/payout-account", owner.Token));
        var resave = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", owner.Token, Payout(number)));

        put.GetProperty("status").GetString().ShouldBe("flagged");
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("ownerId").GetInt64() == owner.Id);
        item.GetProperty("confirmedReportCount").GetInt32().ShouldBe(1);
        item.ToString().ShouldNotContain(number);
        review.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        after.GetProperty("status").GetString().ShouldBe("active");
        resave.GetProperty("status").GetString().ShouldBe("active");   // same number keeps the review
    }

    [Fact]
    public async Task PayoutAccount_ModeratorReviewsOwnAccount_ReturnsConflictOfInterest()
    {
        var both = await TestUsers.CreateAsync(Client(), infra, ["owner", "moderator"]);
        await Client().SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", both.Token, Payout());

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post,
            $"/api/v1/moderation/payout-accounts/{both.Id}/review", both.Token, new { decision = "clear", note = "ok" })))
            .ShouldBe("moderation.conflict_of_interest");
    }

    [Fact]
    public async Task Banks_ListedForAnonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/meta/banks", TestContext.Current.CancellationToken);
        var banks = await AuthClient.ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        banks.EnumerateArray().Select(b => b.GetProperty("bin").GetString()).ShouldContain("970436");
    }

    // ---- room → listing (US-1005, BR-32) ------------------------------------------------------------------

    [Fact]
    public async Task RoomListing_VacantRoom_CreatesPrefilledDraftOnce()
    {
        var owner = await OwnerAsync();
        var room = await CreateRoomAsync(owner, await CreatePropertyAsync(owner));
        var client = Client();

        var first = await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/rooms/{room}/listing", owner.Token);
        var listingId = (await AuthClient.ReadJsonAsync(first)).GetProperty("listingId").GetGuid();
        var second = await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/rooms/{room}/listing", owner.Token);
        var listing = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, $"/api/v1/listings/{listingId}", owner.Token));

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await AuthClient.ProblemCodeAsync(second)).ShouldBe("rental.room_not_vacant");
        listing.GetProperty("status").GetString().ShouldBe("draft");
        listing.GetProperty("title").GetString().ShouldBe("Phòng P101 – Nhà trọ Cầu Diễn");

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        (await connection.ExecuteScalarAsync<long?>(
            "SELECT rl.room_id FROM app.room_listings rl JOIN app.listings l ON l.id = rl.listing_id WHERE l.public_id = @Id",
            new { Id = listingId })).ShouldBe(room);
    }

    [Fact]
    public async Task RoomListing_OccupiedRoom_Returns409()
    {
        var owner = await OwnerAsync();
        var room = await CreateRoomAsync(owner, await CreatePropertyAsync(owner));
        await SetRoomStatusAsync(room, "occupied");

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/rooms/{room}/listing", owner.Token)))
            .ShouldBe("rental.room_not_vacant");
    }

    // ---- renter registration ------------------------------------------------------------------------------

    [Fact]
    public async Task Register_Renter_AnyEmailGetsTenantPermissions()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync(email: $"renter-{Guid.NewGuid():N}@gmail.example", accountType: "renter");
        var (_, _, body) = await client.LoginAsync(email);

        var permissions = body.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        permissions.ShouldContain("rental.tenant");
        permissions.ShouldNotContain("listing.create");
    }
}
