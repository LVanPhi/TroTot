using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SkiaSharp;
using StackExchange.Redis;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;
using TroViec.Api.Tests.Storage;

namespace TroViec.Api.Tests.Listings;

/// <summary>T3.3: submit, renew, unpublish, remove (US-304–306, BR-02, BR-03, BR-09) and US-106 AC2.</summary>
[Collection(nameof(InfraCollection))]
public sealed class ListingLifecycleTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private static readonly object Room = new
    {
        listingType = "room",
        title = "Phòng trọ khép kín gần trường",
        description = "Phòng 20m2 khép kín, có điều hòa, nóng lạnh, giờ giấc tự do, gần trường.",
        wardCode = "90001",
        streetAddress = "Số 1 ngõ 2",
        location = new { lat = 21.05, lng = 105.73 },
        room = new { monthlyRentVnd = 2_500_000, areaM2 = 20.0m, isSelfContained = true, amenityCodes = new[] { "wifi" } },
    };

    /// <summary>An owner who passes BR-02: verified phone (TestUsers) + owner profile verified now.</summary>
    private async Task<TestUser> VerifiedOwnerAsync()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        await Sql("""
            INSERT INTO app.owner_profiles (user_id, owner_type, verified_until, verifier_id)
            VALUES (@Id, 'individual', now() + interval '1 year', app.system_user_id())
            """, new { owner.Id });
        return owner;
    }

    private async Task Sql(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        await connection.ExecuteAsync(sql, args);
    }

    private async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<Guid> DraftAsync(TestUser owner, bool withImage = true)
    {
        var created = await Client().SendAsync(HttpMethod.Post, "/api/v1/listings", owner.Token, Room);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await AuthClient.ReadJsonAsync(created)).GetProperty("listingId").GetGuid();
        if (withImage)
        {
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(TestImages.Encode(30, 20, SKEncodedImageFormat.Png));
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "file", "a.png");
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/listings/{id}/images") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
            (await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        return id;
    }

    private Task<HttpResponseMessage> PostAsync(TestUser user, Guid id, string action, object body) =>
        Client().SendAsync(HttpMethod.Post, $"/api/v1/listings/{id}/{action}", user.Token, body);

    private Task<(string Status, int Version)> StateAsync(Guid id) =>
        QueryState(id);

    private async Task<(string Status, int Version)> QueryState(Guid id)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.QuerySingleAsync<(string, int)>("SELECT status::text, version FROM app.listings WHERE public_id = @Id", new { Id = id });
    }

    private Task SetAsync(Guid id, string status, string? expiresIn = null) =>
        Sql($"UPDATE app.listings SET status = @S::app.listing_status, expires_at = {(expiresIn is null ? "expires_at" : $"now() + interval '{expiresIn}'")} WHERE public_id = @Id",
            new { S = status, Id = id });

    // ---- submit (US-304) ------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_NotVerifiedPoster_ListsWhatIsMissing()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"], phoneVerified: false);
        var id = await DraftAsync(owner);

        var response = await PostAsync(owner, id, "submit", new { version = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await AuthClient.ReadJsonAsync(response);
        body.GetProperty("code").GetString().ShouldBe("listing.poster_not_verified");
        body.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["phone_verification", "owner_verification"]);
        (await StateAsync(id)).ShouldBe(("draft", 1));
    }

    [Fact]
    public async Task Submit_RoomWithoutPhoto_IsImagesRequired()
    {
        var owner = await VerifiedOwnerAsync();
        var id = await DraftAsync(owner, withImage: false);

        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, id, "submit", new { version = 1 }))).ShouldBe("listing.images_required");
    }

    [Fact]
    public async Task Submit_CreatesListingScanWithPosterPhoneAndOutbox_ThenWorkerResultMovesToPendingReview()
    {
        var owner = await VerifiedOwnerAsync();
        var id = await DraftAsync(owner);

        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, id, "submit", new { version = 7 }))).ShouldBe("listing.version_conflict");
        var response = await PostAsync(owner, id, "submit", new { version = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await AuthClient.ReadJsonAsync(response);
        (body.GetProperty("status").GetString(), body.GetProperty("version").GetInt32()).ShouldBe(("screening", 2));
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        var scan = await connection.QuerySingleAsync<(long Id, Guid PublicId, long Requester, string Text, int Outbox, byte[] Phone)>(
            """
            SELECT s.id, s.public_id, s.requester_id, s.input_text,
                   (SELECT count(*)::int FROM app.outbox_messages o WHERE o.scan_id = s.id),
                   (SELECT i.value_hmac FROM app.scan_identifiers si JOIN app.identifiers i ON i.id = si.identifier_id WHERE si.scan_id = s.id)
            FROM app.scans s JOIN app.listings l ON l.id = s.listing_id
            WHERE l.public_id = @Id AND s.source = 'listing'
            """, new { Id = id });
        (scan.Requester, scan.Outbox).ShouldBe((owner.Id, 1));
        scan.Text.ShouldStartWith("Phòng trọ khép kín gần trường\n");
        var phone = await connection.ExecuteScalarAsync<string>("SELECT phone_e164 FROM app.users WHERE id = @Id", new { owner.Id });
        scan.Phone.ShouldBe(_factory.Services.GetRequiredService<IdentifierHasher>().Hash("phone", phone!));

        // The worker finishes the scan and publishes scan.completed (docs/02-architecture.md, Gửi tin đi duyệt, bước 4).
        using (var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString))
            await redis.GetDatabase().StreamAddAsync(Streams.ScanCompleted, Streams.Entry("scan.completed", new
            {
                scanId = scan.Id, publicId = scan.PublicId, status = "completed", source = "listing",
                listingId = await connection.ExecuteScalarAsync<long>("SELECT id FROM app.listings WHERE public_id = @Id", new { Id = id }),
            }));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await StateAsync(id)).Status != "pending_review" && DateTime.UtcNow < deadline)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        (await StateAsync(id)).ShouldBe(("pending_review", 3));
    }

    // ---- renew (US-305, BR-03) -------------------------------------------------------------------------

    [Fact]
    public async Task Renew_OnlyWithinSevenDaysOrExpired()
    {
        var owner = await VerifiedOwnerAsync();
        var id = await DraftAsync(owner);
        await SetAsync(id, "verified", "30 days");

        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, id, "renew", new { version = 1 }))).ShouldBe("listing.renew_too_early");
        await SetAsync(id, "verified", "5 days");
        (await PostAsync(owner, id, "renew", new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await StateAsync(id)).ShouldBe(("screening", 2));

        var expired = await DraftAsync(owner);
        await SetAsync(expired, "expired", "-1 day");
        (await PostAsync(owner, expired, "renew", new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var draft = await DraftAsync(owner);
        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, draft, "renew", new { version = 1 }))).ShouldBe("listing.invalid_transition");
    }

    // ---- unpublish and remove (US-306, US-307) --------------------------------------------------------

    [Fact]
    public async Task Unpublish_VerifiedRejectedExpiredToDraft_NotFromDraft()
    {
        var owner = await VerifiedOwnerAsync();
        foreach (var status in new[] { "verified", "rejected", "expired" })
        {
            var id = await DraftAsync(owner);
            await SetAsync(id, status, status == "verified" ? "30 days" : null);
            var response = await PostAsync(owner, id, "unpublish", new { version = 1 });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, status);
            (await StateAsync(id)).ShouldBe(("draft", 2));
        }
        var draft = await DraftAsync(owner);
        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, draft, "unpublish", new { version = 1 }))).ShouldBe("listing.invalid_transition");
    }

    [Fact]
    public async Task Remove_DefaultReason_NotWhileScreening_NotWithOpenAppeal()
    {
        var owner = await VerifiedOwnerAsync();
        var id = await DraftAsync(owner);

        (await PostAsync(owner, id, "remove", new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar<string>("SELECT t.reason FROM app.listing_transitions t JOIN app.listings l ON l.id = t.listing_id WHERE l.public_id = @Id AND t.to_status = 'removed'",
            new { Id = id })).ShouldBe("Người đăng gỡ");

        var screening = await DraftAsync(owner);
        await SetAsync(screening, "screening");
        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, screening, "remove", new { version = 1 }))).ShouldBe("listing.invalid_transition");

        var rejected = await DraftAsync(owner);
        await SetAsync(rejected, "rejected");
        await Sql("""
            WITH l AS (SELECT id FROM app.listings WHERE public_id = @Id),
                 t AS (INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
                       SELECT id, 'pending_review', 'rejected', app.system_user_id(), 'Thiếu ảnh' FROM l RETURNING id, listing_id)
            INSERT INTO app.appeals (listing_id, listing_transition_id, reason) SELECT listing_id, id, 'Đã bổ sung' FROM t
            """, new { Id = rejected });
        (await AuthClient.ProblemCodeAsync(await PostAsync(owner, rejected, "remove", new { version = 1 }))).ShouldBe("appeal.pending");
    }

    [Fact]
    public async Task OtherUser_CannotChangeStatus()
    {
        var owner = await VerifiedOwnerAsync();
        var stranger = await VerifiedOwnerAsync();
        var id = await DraftAsync(owner);

        (await PostAsync(stranger, id, "submit", new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(stranger, id, "remove", new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- account deletion (US-106 AC2) -----------------------------------------------------------------

    [Fact]
    public async Task DeleteAccount_RemovesEveryListing_ButNotWhileOneIsScreening()
    {
        var owner = await VerifiedOwnerAsync();
        var draft = await DraftAsync(owner);
        var pending = await DraftAsync(owner);
        await SetAsync(pending, "screening");

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Delete, "/api/v1/me", owner.Token, new { password = AuthClient.Password })))
            .ShouldBe("me.listing_in_screening");

        await SetAsync(pending, "pending_review");
        (await Client().SendAsync(HttpMethod.Delete, "/api/v1/me", owner.Token, new { password = AuthClient.Password }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StateAsync(draft)).Status.ShouldBe("removed");
        (await StateAsync(pending)).Status.ShouldBe("removed");
        (await Scalar<int>("SELECT count(*)::int FROM app.listing_transitions WHERE reason = 'Người dùng xóa tài khoản' AND listing_id IN (SELECT id FROM app.listings WHERE poster_id = @Id)",
            new { owner.Id })).ShouldBe(2);
    }
}
