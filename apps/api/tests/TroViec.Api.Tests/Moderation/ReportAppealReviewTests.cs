using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;
using TroViec.Api.Tests.Scans;

namespace TroViec.Api.Tests.Moderation;

/// <summary>T3.5: reports, appeals, reviews, saved listings, audit (US-404–406, 606–609, 701–702; D13–D17 at API level).</summary>
[Collection(nameof(InfraCollection))]
public sealed class ReportAppealReviewTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private Task<TestUser> User(params string[] roles) => TestUsers.CreateAsync(Client(), infra, roles);

    private async Task<T> Db<T>(string sql, object? args = null)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>A published listing (verified 30 days) whose last transition is the approval.</summary>
    private async Task<Guid> PublishedAsync(long posterId)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        var (id, publicId) = await connection.QuerySingleAsync<(long, Guid)>(
            """
            INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
            VALUES (@Poster, 'room', 'Phòng công khai', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                    ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, 'verified', now() + interval '30 days')
            RETURNING id, public_id
            """, new { Poster = posterId });
        await connection.ExecuteAsync(
            "INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) VALUES (@Id, 'pending_review', 'verified', app.system_user_id())",
            new { Id = id });
        return publicId;
    }

    private Task<HttpResponseMessage> Post(TestUser user, string path, object body) => Client().SendAsync(HttpMethod.Post, path, user.Token, body);

    private async Task<(string Status, int Version)> ListingState(Guid id)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.QuerySingleAsync<(string, int)>("SELECT status::text, version FROM app.listings WHERE public_id = @Id", new { Id = id });
    }

    [Fact]
    public async Task Report_Hide_Appeal_Accept_EndToEnd()
    {
        var poster = await User("owner");
        var student = await User("student");
        var hider = await User("moderator");
        var other = await User("moderator");
        var listing = await PublishedAsync(poster.Id);

        var report = await Post(student, $"/api/v1/listings/{listing}/reports", new { category = "scam", description = "Đòi chuyển cọc trước" });
        report.StatusCode.ShouldBe(HttpStatusCode.Created);
        var reportId = (await AuthClient.ReadJsonAsync(report)).GetProperty("id").GetInt64();
        (await AuthClient.ProblemCodeAsync(await Post(student, $"/api/v1/listings/{listing}/reports", new { category = "other" })))
            .ShouldBe("report.already_open");

        var detail = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/moderation/reports/{reportId}", hider.Token));
        detail.GetProperty("listing").GetProperty("listing").GetProperty("status").GetString().ShouldBe("verified");
        (await Post(hider, $"/api/v1/moderation/reports/{reportId}/confirm", new { note = " ", hideListing = true, listingVersion = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(hider, $"/api/v1/moderation/reports/{reportId}/confirm", new { note = "Xác nhận đòi cọc", hideListing = true, listingVersion = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListingState(listing)).ShouldBe(("hidden", 2));

        var appeal = await Post(poster, $"/api/v1/listings/{listing}/appeals", new { reason = "Tôi đã sửa nội dung, không đòi cọc trước" });
        appeal.StatusCode.ShouldBe(HttpStatusCode.Created);
        var appealId = (await AuthClient.ReadJsonAsync(appeal)).GetProperty("id").GetInt64();
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, $"/api/v1/listings/{listing}/unpublish", poster.Token, new { version = 2 })))
            .ShouldBe("appeal.pending");

        // D16: the moderator who hid the listing cannot decide the appeal.
        (await AuthClient.ProblemCodeAsync(await Post(hider, $"/api/v1/moderation/appeals/{appealId}/accept", new { note = "OK" })))
            .ShouldBe("appeal.decider_conflict");
        var accepted = await Post(other, $"/api/v1/moderation/appeals/{appealId}/accept", new { note = "Đã kiểm tra lại" });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListingState(listing)).ShouldBe(("verified", 3));

        var mine = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/me/appeals", poster.Token));
        var item = mine.EnumerateArray().Single();
        (item.GetProperty("status").GetString(), item.GetProperty("decisionNote").GetString()).ShouldBe(("accepted", "Đã kiểm tra lại"));

        var audit = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/moderation/audit/me", hider.Token));
        audit.EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ShouldContain("listing_hidden");
        audit.EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ShouldContain("report_confirmed");
    }

    [Fact]
    public async Task AppealRejected_HiddenBecomesRemoved_RejectedListingAppealedAgainIsAlreadyUsed()
    {
        var poster = await User("owner");
        var moderator = await User("moderator");
        var decider = await User("moderator");

        var hidden = await PublishedAsync(poster.Id);
        (await Post(moderator, $"/api/v1/moderation/listings/{hidden}/hide", new { version = 1, reason = "Ảnh lấy từ tin khác" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var a1 = (await AuthClient.ReadJsonAsync(await Post(poster, $"/api/v1/listings/{hidden}/appeals", new { reason = "Ảnh của tôi" }))).GetProperty("id").GetInt64();
        (await Post(decider, $"/api/v1/moderation/appeals/{a1}/reject", new { note = "Ảnh trùng tin đã gỡ" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListingState(hidden)).Status.ShouldBe("removed");

        // A rejected listing keeps its status when the appeal is rejected; the same decision cannot be appealed twice (D15).
        var pending = await PublishedAsync(poster.Id);
        await Db<int>("UPDATE app.listings SET status = 'pending_review', expires_at = NULL WHERE public_id = @Id", new { Id = pending });
        (await Post(moderator, $"/api/v1/moderation/listings/{pending}/reject", new { version = 1, reason = "Thiếu ảnh" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var a2 = (await AuthClient.ReadJsonAsync(await Post(poster, $"/api/v1/listings/{pending}/appeals", new { reason = "Đã đủ ảnh" }))).GetProperty("id").GetInt64();
        (await Post(decider, $"/api/v1/moderation/appeals/{a2}/reject", new { note = "Vẫn thiếu" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListingState(pending)).Status.ShouldBe("rejected");
        (await AuthClient.ProblemCodeAsync(await Post(poster, $"/api/v1/listings/{pending}/appeals", new { reason = "Lần nữa" }))).ShouldBe("appeal.already_used");
    }

    [Fact]
    public async Task Appeal_OnApprovalDecision_IsTargetInvalid()   // D14
    {
        var poster = await User("owner");
        var listing = await PublishedAsync(poster.Id);

        (await AuthClient.ProblemCodeAsync(await Post(poster, $"/api/v1/listings/{listing}/appeals", new { reason = "?" }))).ShouldBe("appeal.target_invalid");
    }

    [Fact]
    public async Task ConfirmScanReport_IncrementsIdentifierCounter()   // D13
    {
        var reporter = await User("student");
        var moderator = await User("moderator");
        var scanId = (await AuthClient.ReadJsonAsync(await ScanClient.PostAsync(_factory.CreateClient(), ScanClient.NewIp(), text: "phòng " + Guid.NewGuid())))
            .GetProperty("scanId").GetGuid();
        var identifier = await Db<long>(
            """
            WITH i AS (INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES ('phone', sha256(gen_random_uuid()::text::bytea), '•••999') RETURNING id)
            INSERT INTO app.scan_identifiers (scan_id, identifier_id) SELECT s.id, i.id FROM app.scans s, i WHERE s.public_id = @Id RETURNING identifier_id
            """, new { Id = scanId });
        var reportId = (await AuthClient.ReadJsonAsync(await Post(reporter, $"/api/v1/scans/{scanId}/reports", new { category = "scam" }))).GetProperty("id").GetInt64();

        var detail = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/moderation/reports/{reportId}", moderator.Token));
        detail.GetProperty("scan").GetProperty("inputText").GetString()!.ShouldStartWith("phòng ");
        (await AuthClient.ProblemCodeAsync(await Post(reporter, $"/api/v1/moderation/reports/{reportId}/dismiss", new { note = "x" }))).ShouldBe("auth.forbidden");
        (await Post(moderator, $"/api/v1/moderation/reports/{reportId}/confirm", new { note = "Đúng là đòi tiền", hideListing = false })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Db<int>("SELECT confirmed_report_count FROM app.identifier_stats WHERE identifier_id = @Id", new { Id = identifier })).ShouldBe(1);
        (await AuthClient.ProblemCodeAsync(await Post(moderator, $"/api/v1/moderation/reports/{reportId}/dismiss", new { note = "x" }))).ShouldBe("report.already_resolved");
    }

    [Fact]
    public async Task Review_StudentOnce_OwnerNotStudent_PosterNotOwn_HiddenNotListed()   // D17
    {
        var poster = await User("owner", "student");
        var student = await User("student");
        var owner = await User("owner");
        var moderator = await User("moderator");
        var listing = await PublishedAsync(poster.Id);

        (await Post(student, $"/api/v1/listings/{listing}/reviews", new { rating = 5, body = "Phòng đúng mô tả" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await AuthClient.ProblemCodeAsync(await Post(student, $"/api/v1/listings/{listing}/reviews", new { rating = 4 }))).ShouldBe("review.already_exists");
        (await Post(owner, $"/api/v1/listings/{listing}/reviews", new { rating = 1 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthClient.ProblemCodeAsync(await Post(poster, $"/api/v1/listings/{listing}/reviews", new { rating = 5 }))).ShouldBe("review.own_listing");
        (await Post(student, $"/api/v1/listings/{listing}/reviews", new { rating = 6 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var reviews = await AuthClient.ReadJsonAsync(await _factory.CreateClient().GetAsync($"/api/v1/listings/{listing}/reviews", TestContext.Current.CancellationToken));
        var reviewId = reviews.EnumerateArray().Single().GetProperty("id").GetInt64();
        (await Post(moderator, $"/api/v1/moderation/reviews/{reviewId}/hide", new { reason = "Nội dung quảng cáo" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuthClient.ReadJsonAsync(await _factory.CreateClient().GetAsync($"/api/v1/listings/{listing}/reviews", TestContext.Current.CancellationToken)))
            .GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Review_ByOwnerWithoutStudentRole_TriggerAnswersNotStudent()
    {
        // review.create is a student permission, so the trigger path is only reachable for a student account whose
        // student role was revoked after its token was issued: the database still refuses.
        var poster = await User("owner");
        var former = await User("student");
        var listing = await PublishedAsync(poster.Id);
        await Db<int>("DELETE FROM app.user_roles WHERE user_id = @Id", new { former.Id });

        (await AuthClient.ProblemCodeAsync(await Post(former, $"/api/v1/listings/{listing}/reviews", new { rating = 3 }))).ShouldBe("review.not_student");
    }

    [Fact]
    public async Task Saved_PublicExpiredAndUnavailableStates_Idempotent()
    {
        var poster = await User("owner");
        var student = await User("student");
        var a = await PublishedAsync(poster.Id);
        var b = await PublishedAsync(poster.Id);
        var c = await PublishedAsync(poster.Id);
        foreach (var id in new[] { a, b, c, a })
            (await Client().SendAsync(HttpMethod.Put, $"/api/v1/me/saved-listings/{id}", student.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Db<int>("UPDATE app.listings SET status = 'expired' WHERE public_id = @Id", new { Id = b });
        await Db<int>("UPDATE app.listings SET status = 'hidden' WHERE public_id = @Id", new { Id = c });

        var saved = (await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/me/saved-listings", student.Token)))
            .EnumerateArray().ToDictionary(i => i.GetProperty("listingId").GetGuid(), i => i);
        saved.Count.ShouldBe(3);
        saved[a].GetProperty("state").GetString().ShouldBe("public");
        saved[b].GetProperty("state").GetString().ShouldBe("expired");
        saved[c].GetProperty("state").GetString().ShouldBe("unavailable");
        saved[c].GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);   // no content leaks

        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/me/saved-listings/{a}", student.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/me/saved-listings/{a}", student.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client().SendAsync(HttpMethod.Put, $"/api/v1/me/saved-listings/{c}", student.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
