using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Features.Rental;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Rental;

/// <summary>T6.3: lease draft, send, invite, claim, OTP confirmation, activation, cancel, end (E11, BR-21…BR-27, BR-32…BR-34).</summary>
[Collection(nameof(InfraCollection))]
public sealed partial class LeaseTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly MutableClock _clock = new();
    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ??= new ApiFactory(infra) { Clock = _clock };

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    private AuthClient Client() => new(Factory.CreateClient(), infra);

    [GeneratedRegex(@"/loi-moi/([A-Za-z0-9_-]+)")]
    private static partial Regex InviteLink();

    private async Task ExecAsync(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        await connection.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>Owner meeting BR-21: verified phone, verified owner profile, active payout account; one property, one room.</summary>
    private async Task<(TestUser Owner, long Room)> ReadyOwnerAsync()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        await ExecAsync("""
            INSERT INTO app.owner_profiles (user_id, owner_type, verified_until, verifier_id)
            VALUES (@Id, 'individual', now() + interval '1 year', app.system_user_id())
            """, new { owner.Id });
        var client = Client();
        (await client.SendAsync(HttpMethod.Put, "/api/v1/me/payout-account", owner.Token,
            new { bankId = (short)1, accountNumber = $"8{Random.Shared.NextInt64(100_000_000, 999_999_999)}", holderName = "CHU TRO" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var property = await client.SendAsync(HttpMethod.Post, "/api/v1/rental/properties", owner.Token, new
        {
            name = "Nhà trọ Cầu Diễn", wardCode = "90001", streetAddress = "Số 1 ngõ 2", location = new { lat = 21.05, lng = 105.73 },
        });
        var propertyId = (await AuthClient.ReadJsonAsync(property)).GetProperty("id").GetInt64();
        var room = await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/properties/{propertyId}/rooms", owner.Token,
            new { code = "P101", areaM2 = 20.0m, isSelfContained = true, listRentVnd = 2_500_000 });
        return (owner, (await AuthClient.ReadJsonAsync(room)).GetProperty("id").GetInt64());
    }

    private static object LeaseBody(long room, string[] tenants, string start = "2027-01-01", string end = "2027-07-01", int? version = null) => new
    {
        roomId = room,
        tenantEmails = tenants,
        startDate = start,
        endDate = end,
        rentVnd = 2_500_000,
        depositVnd = 2_500_000,
        billingDay = (short)5,
        electricityPriceVnd = 3_500,
        waterPriceVnd = 100_000,
        waterUnit = "person",
        services = new[] { new { label = "Wifi", amountVnd = 100_000 } },
        extraTerms = "Không nuôi thú cưng.",
        version,
    };

    private async Task<(Guid Id, int Version)> DraftAsync(TestUser owner, long room, params string[] tenants)
    {
        var response = await Client().SendAsync(HttpMethod.Post, "/api/v1/rental/leases", owner.Token, LeaseBody(room, tenants));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await AuthClient.ReadJsonAsync(response);
        return (json.GetProperty("leaseId").GetGuid(), json.GetProperty("version").GetInt32());
    }

    private async Task<(string Sha, int Version)> SendAsync(TestUser owner, Guid lease, int version)
    {
        var response = await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/send", owner.Token, new { version });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var json = await AuthClient.ReadJsonAsync(response);
        return (json.GetProperty("contentSha256").GetString()!, json.GetProperty("version").GetInt32());
    }

    private async Task<string> WaitForEmailAsync(string to, string marker)
    {
        var client = Client();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var hit = (await client.EmailsToAsync(to)).FirstOrDefault(b => b.Contains(marker, StringComparison.Ordinal));
            if (hit is not null) return hit;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new InvalidOperationException($"no email containing '{marker}'");
    }

    private async Task<string> InviteTokenAsync(string email) =>
        InviteLink().Match(await WaitForEmailAsync(email, "/loi-moi/")).Groups[1].Value;

    private async Task<HttpResponseMessage> ConfirmAsync(TestUser user, Guid lease, string sha)
    {
        var client = Client();
        // Old code emails would otherwise satisfy the wait; clear them for this user first.
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/otp", user.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var body = await WaitForEmailAsync(user.Email, "Mã xác nhận hợp đồng của bạn là");
        var code = Regex.Match(body, @"là: (\d{6})").Groups[1].Value;
        return await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/confirm", user.Token, new { code, contentSha256 = sha });
    }

    private Task<TestUser> RenterAsync() => TestUsers.CreateAsync(Client(), infra, ["renter"]);

    // ---- draft and send (US-1101, US-1102) -----------------------------------------------------------------

    [Fact]
    public async Task Send_LandlordNotReady_ListsMissingSteps()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"], phoneVerified: false);
        var client = Client();
        var property = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/rental/properties", owner.Token,
            new { name = "Nhà", wardCode = "90001", streetAddress = "Số 1", location = new { lat = 21.05, lng = 105.73 } }));
        var room = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Post,
            $"/api/v1/rental/properties/{property.GetProperty("id").GetInt64()}/rooms", owner.Token,
            new { code = "A1", areaM2 = 15.0m, isSelfContained = false, listRentVnd = 1_500_000 }));
        var (lease, version) = await DraftAsync(owner, room.GetProperty("id").GetInt64(), "t@example.com");

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/send", owner.Token, new { version });
        var json = await AuthClient.ReadJsonAsync(response);

        json.GetProperty("code").GetString().ShouldBe("rental.landlord_not_ready");
        json.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(
            ["phone_verification", "owner_verification", "payout_account"], ignoreOrder: true);
    }

    [Fact]
    public async Task Send_FreezesHashReservesRoomAndInvitesEveryEmail()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var registered = await RenterAsync();
        var unknown = $"new-{Guid.NewGuid():N}@example.com";
        var (lease, version) = await DraftAsync(owner, room, registered.Email, unknown);

        var (sha, _) = await SendAsync(owner, lease, version);
        var detail = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/rental/leases/{lease}", owner.Token));

        detail.GetProperty("status").GetString().ShouldBe("awaiting_signatures");
        detail.GetProperty("contentSha256").GetString().ShouldBe(sha);
        (await ScalarAsync<string>("SELECT status::text FROM app.rooms WHERE id = @Room", new { Room = room })).ShouldBe("reserved");
        (await InviteTokenAsync(registered.Email)).ShouldNotBeNullOrEmpty();
        (await InviteTokenAsync(unknown)).ShouldNotBeNullOrEmpty();   // BR-27: same email whether an account exists or not
        (await ScalarAsync<int>("SELECT count(*)::int FROM app.lease_parties lp JOIN app.leases l ON l.id = lp.lease_id WHERE l.public_id = @Lease AND invite_token_hash IS NOT NULL",
            new { Lease = lease })).ShouldBe(2);
    }

    [Fact]
    public async Task Draft_EditOnlyWhileDraft_AndVersionChecked()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var (lease, version) = await DraftAsync(owner, room, "a@example.com");
        var client = Client();

        var edit = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/leases/{lease}", owner.Token,
            LeaseBody(room, ["a@example.com", "b@example.com"], version: version));
        var stale = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/leases/{lease}", owner.Token,
            LeaseBody(room, ["a@example.com"], version: version));
        var newVersion = (await AuthClient.ReadJsonAsync(edit)).GetProperty("version").GetInt32();
        await SendAsync(owner, lease, newVersion);
        var afterSend = await client.SendAsync(HttpMethod.Put, $"/api/v1/rental/leases/{lease}", owner.Token,
            LeaseBody(room, ["a@example.com"], version: newVersion + 1));

        edit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ProblemCodeAsync(stale)).ShouldBe("rental.version_conflict");
        (await AuthClient.ProblemCodeAsync(afterSend)).ShouldBe("rental.lease_not_editable");
    }

    [Fact]
    public async Task Draft_LandlordInvitesOwnEmail_RaisesInvalidParty()
    {
        var (owner, room) = await ReadyOwnerAsync();

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, "/api/v1/rental/leases", owner.Token,
            LeaseBody(room, [owner.Email.ToUpperInvariant()])))).ShouldBe("rental.invalid_party");
    }

    [Fact]
    public async Task Send_OverlappingLeaseOnSameRoom_Returns409RoomAlreadyLeased()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var (first, v1) = await DraftAsync(owner, room, "a@example.com");
        await SendAsync(owner, first, v1);
        var overlapping = await Client().SendAsync(HttpMethod.Post, "/api/v1/rental/leases", owner.Token,
            LeaseBody(room, ["b@example.com"], "2027-03-01", "2027-09-01"));
        var json = await AuthClient.ReadJsonAsync(overlapping);

        var response = await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{json.GetProperty("leaseId").GetGuid()}/send",
            owner.Token, new { version = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("rental.room_already_leased");
    }

    [Fact]
    public async Task Leases_OfAnotherLandlord_Return404()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var (other, _) = await ReadyOwnerAsync();
        var (lease, version) = await DraftAsync(owner, room, "a@example.com");
        var client = Client();

        (await client.SendAsync(HttpMethod.Get, $"/api/v1/rental/leases/{lease}", other.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/send", other.Token, new { version }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/cancel", other.Token, new { version }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/rental/leases", other.Token,
            LeaseBody(room, ["x@example.com"])))).ShouldBe("validation.failed");   // someone else's room
    }

    // ---- invites (US-1103) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Invite_WrongEmailIs404_ExpiredIs410()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var tenant = await RenterAsync();
        var stranger = await RenterAsync();
        var (lease, version) = await DraftAsync(owner, room, tenant.Email);
        await SendAsync(owner, lease, version);
        var token = await InviteTokenAsync(tenant.Email);
        var client = Client();

        (await client.SendAsync(HttpMethod.Get, $"/api/v1/tenancy/invites/{token}", stranger.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/tenancy/invites/{token}/claim", stranger.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        var view = await client.SendAsync(HttpMethod.Get, $"/api/v1/tenancy/invites/{token}", tenant.Token);
        view.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ReadJsonAsync(view)).GetProperty("roomCode").GetString().ShouldBe("P101");

        _clock.Advance(LeaseEndpoints.InviteLifetime + TimeSpan.FromMinutes(1));
        var expired = await client.SendAsync(HttpMethod.Get, $"/api/v1/tenancy/invites/{token}", tenant.Token);
        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await AuthClient.ProblemCodeAsync(expired)).ShouldBe("rental.invite_expired");
    }

    // ---- confirmation and activation (US-1104) --------------------------------------------------------------

    [Fact]
    public async Task Confirm_AllPartiesActivateLease_RoomOccupied_ListingBackToDraft()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var tenant = await RenterAsync();
        var client = Client();
        // A verified listing generated from the room.
        var listing = (await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/rooms/{room}/listing", owner.Token)))
            .GetProperty("listingId").GetGuid();
        await ExecAsync("UPDATE app.listings SET status = 'verified', expires_at = now() + interval '90 days' WHERE public_id = @Id", new { Id = listing });
        var (lease, version) = await DraftAsync(owner, room, tenant.Email);
        var (sha, _) = await SendAsync(owner, lease, version);

        var token = await InviteTokenAsync(tenant.Email);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/tenancy/invites/{token}/claim", tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = await AuthClient.ReadJsonAsync(await ConfirmAsync(tenant, lease, sha));
        var again = await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/otp", tenant.Token);
        var last = await AuthClient.ReadJsonAsync(await ConfirmAsync(owner, lease, sha));

        first.GetProperty("status").GetString().ShouldBe("awaiting_signatures");
        (await AuthClient.ProblemCodeAsync(again)).ShouldBe("rental.lease_already_confirmed");
        last.GetProperty("status").GetString().ShouldBe("active");
        (await ScalarAsync<string>("SELECT status::text FROM app.rooms WHERE id = @Room", new { Room = room })).ShouldBe("occupied");
        (await ScalarAsync<string>("SELECT status::text FROM app.listings WHERE public_id = @Id", new { Id = listing })).ShouldBe("draft");
        (await ScalarAsync<string>("""
            SELECT t.reason FROM app.listing_transitions t JOIN app.listings l ON l.id = t.listing_id
            WHERE l.public_id = @Id ORDER BY t.id DESC LIMIT 1
            """, new { Id = listing })).ShouldBe(LeaseEndpoints.RoomLeasedReason);
        var tenantView = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, $"/api/v1/tenancy/leases/{lease}", tenant.Token));
        tenantView.GetProperty("parties").EnumerateArray().ShouldAllBe(p => p.GetProperty("confirmedAt").ValueKind != JsonValueKind.Null);
        (await WaitForEmailAsync(tenant.Email, "Hợp đồng có hiệu lực")).ShouldContain(sha);
    }

    [Fact]
    public async Task Confirm_WrongHashOrCode_IsRejected_OthersCannotConfirm()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var stranger = await RenterAsync();
        var (lease, version) = await DraftAsync(owner, room, "nobody@example.com");
        var (sha, _) = await SendAsync(owner, lease, version);
        var client = Client();

        var changed = await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/confirm", owner.Token,
            new { code = "000000", contentSha256 = new string('0', 64) });
        var wrongCode = await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/confirm", owner.Token,
            new { code = "000000", contentSha256 = sha });
        var notParty = await client.SendAsync(HttpMethod.Post, $"/api/v1/leases/{lease}/otp", stranger.Token);

        (await AuthClient.ProblemCodeAsync(changed)).ShouldBe("rental.lease_content_changed");
        (await AuthClient.ProblemCodeAsync(wrongCode)).ShouldBe("auth.code_invalid");
        notParty.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- cancel and end (US-1105, BR-33, BR-34) -------------------------------------------------------------

    [Fact]
    public async Task Cancel_AwaitingLease_FreesRoomAndKillsInvite()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var tenant = await RenterAsync();
        var (lease, version) = await DraftAsync(owner, room, tenant.Email);
        var (_, sent) = await SendAsync(owner, lease, version);
        var token = await InviteTokenAsync(tenant.Email);

        var cancel = await Client().SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/cancel", owner.Token, new { version = sent });

        cancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ScalarAsync<string>("SELECT status::text FROM app.rooms WHERE id = @Room", new { Room = room })).ShouldBe("vacant");
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/tenancy/invites/{token}", tenant.Token)))
            .ShouldBe("rental.invite_expired");
    }

    [Fact]
    public async Task End_ActiveLease_FreesRoom_AndDeleteAccountBlockedWhileActive()
    {
        var (owner, room) = await ReadyOwnerAsync();
        var tenant = await RenterAsync();
        var (lease, version) = await DraftAsync(owner, room, tenant.Email);
        var (sha, _) = await SendAsync(owner, lease, version);
        var client = Client();
        await client.SendAsync(HttpMethod.Post, $"/api/v1/tenancy/invites/{await InviteTokenAsync(tenant.Email)}/claim", tenant.Token);
        await ConfirmAsync(tenant, lease, sha);
        await ConfirmAsync(owner, lease, sha);

        var delete = await client.SendAsync(HttpMethod.Delete, "/api/v1/me", owner.Token, new { password = AuthClient.Password });
        var current = (await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, $"/api/v1/rental/leases/{lease}", owner.Token)))
            .GetProperty("version").GetInt32();
        var end = await client.SendAsync(HttpMethod.Post, $"/api/v1/rental/leases/{lease}/end", owner.Token, new { version = current });

        (await AuthClient.ProblemCodeAsync(delete)).ShouldBe("me.active_lease");
        end.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ScalarAsync<string>("SELECT status::text FROM app.rooms WHERE id = @Room", new { Room = room })).ShouldBe("vacant");
    }

    // ---- content hash (BR-24) -------------------------------------------------------------------------------

    [Fact]
    public void ContentHash_IsCanonical_OrderOfTenantsAndCaseDoNotMatter()
    {
        var a = new LeaseContent.Input(Guid.Parse("8f2a1c3e-0000-4000-8000-000000000001"), "Nha A", "So 1", "90001", "P101",
            "Owner@Example.com", ["b@example.com", "A@example.com"], new DateOnly(2027, 1, 1), new DateOnly(2027, 7, 1),
            2_500_000, 2_500_000, 5, 3_500, 100_000, "person", [new LeaseContent.Service("Wifi", 100_000)], null);
        var b = a with { TenantEmails = ["a@example.com", "B@example.com"], LandlordEmail = "owner@example.com" };

        LeaseContent.Canonical(a).ShouldBe(
            """{"billingDay":5,"depositVnd":2500000,"electricityPriceVnd":3500,"endDate":"2027-07-01","extraTerms":null,"landlordEmail":"owner@example.com","leaseId":"8f2a1c3e-0000-4000-8000-000000000001","property":{"name":"Nha A","streetAddress":"So 1","wardCode":"90001"},"rentVnd":2500000,"roomCode":"P101","services":[{"amountVnd":100000,"label":"Wifi"}],"startDate":"2027-01-01","templateVersion":1,"tenantEmails":["a@example.com","b@example.com"],"waterPriceVnd":100000,"waterUnit":"person"}""");
        LeaseContent.Hash(b).ShouldBe(LeaseContent.Hash(a));
        LeaseContent.Hash(a with { RentVnd = 2_600_000 }).ShouldNotBe(LeaseContent.Hash(a));
    }
}
