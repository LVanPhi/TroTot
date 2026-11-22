using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using SkiaSharp;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;
using TroViec.Api.Tests.Storage;

namespace TroViec.Api.Tests.Listings;

/// <summary>T3.2: create/edit drafts, photos, my listings, history, detail visibility (US-301–303, US-307, IDOR).</summary>
[Collection(nameof(InfraCollection))]
public sealed class ListingTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private Task<TestUser> OwnerAsync() => TestUsers.CreateAsync(Client(), infra, ["owner"]);

    private static object Room(string title = "Phòng trọ khép kín gần ĐH Công nghiệp", int rent = 2_500_000, string[]? amenities = null,
        int? version = null) => new
    {
        listingType = "room",
        title,
        description = "Phòng 20m2 khép kín, có điều hòa, nóng lạnh, giờ giấc tự do, gần trường.",
        wardCode = "90001",
        streetAddress = "Số 1 ngõ 2 đường Cầu Diễn",
        location = new { lat = 21.0537, lng = 105.7353 },
        room = new { monthlyRentVnd = rent, depositVnd = (int?)null, areaM2 = 20.0m, isSelfContained = true, amenityCodes = amenities ?? ["wifi", "air_conditioner"] },
        job = (object?)null,
        version,
    };

    private static object Job() => new
    {
        listingType = "job",
        title = "Phục vụ quán cà phê ca tối",
        description = "Quán cần 2 bạn phục vụ ca tối 18h-22h, làm tại quán, có đào tạo, trả lương theo tuần.",
        wardCode = "90001",
        streetAddress = "Số 5 đường Hồ Tùng Mậu",
        location = new { lat = 21.04, lng = 105.77 },
        room = (object?)null,
        job = new { employerName = "Quán Cà phê A", payVnd = 25_000, payUnit = "hour", hoursPerWeek = 20 },
    };

    private async Task<(Guid Id, int Version)> CreateAsync(TestUser owner, object? body = null)
    {
        var response = await Client().SendAsync(HttpMethod.Post, "/api/v1/listings", owner.Token, body ?? Room());
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await AuthClient.ReadJsonAsync(response);
        return (json.GetProperty("listingId").GetGuid(), json.GetProperty("version").GetInt32());
    }

    private async Task SetStatusAsync(Guid id, string status)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        await connection.ExecuteAsync(
            "UPDATE app.listings SET status = @S::app.listing_status, expires_at = CASE WHEN @S = 'verified' THEN now() + interval '90 days' END WHERE public_id = @Id",
            new { S = status, Id = id });
    }

    private Task<HttpResponseMessage> UploadAsync(TestUser owner, Guid id, byte[] image)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "anh.png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/listings/{id}/images") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        return _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static byte[] Png(int w) => TestImages.Encode(w, 24, SKEncodedImageFormat.Png);

    private async Task<JsonElement> DetailAsync(Guid id, string? token = null) =>
        await AuthClient.ReadJsonAsync(token is null
            ? await _factory.CreateClient().GetAsync($"/api/v1/listings/{id}", TestContext.Current.CancellationToken)
            : await Client().SendAsync(HttpMethod.Get, $"/api/v1/listings/{id}", token));

    // ---- create (US-301, US-302) -----------------------------------------------------------------------

    [Fact]
    public async Task Create_Room_WritesChildAmenitiesAndFirstTransition()
    {
        var owner = await OwnerAsync();

        var (id, version) = await CreateAsync(owner);

        version.ShouldBe(1);
        var detail = await DetailAsync(id, owner.Token);
        detail.GetProperty("status").GetString().ShouldBe("draft");
        detail.GetProperty("room").GetProperty("monthlyRentVnd").GetInt32().ShouldBe(2_500_000);
        detail.GetProperty("room").GetProperty("depositVnd").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("amenities").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ShouldBe(["air_conditioner", "wifi"]);
        detail.GetProperty("location").GetProperty("lat").GetDouble().ShouldBe(21.0537, 1e-6);
        var history = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/listings/{id}/history", owner.Token));
        var first = history.EnumerateArray().Single();
        (first.GetProperty("fromStatus").ValueKind, first.GetProperty("toStatus").GetString(), first.GetProperty("actor").GetString())
            .ShouldBe((JsonValueKind.Null, "draft", "Bạn"));
    }

    [Fact]
    public async Task Create_Job_Works()
    {
        var owner = await OwnerAsync();
        var (id, _) = await CreateAsync(owner, Job());

        var job = (await DetailAsync(id, owner.Token)).GetProperty("job");
        (job.GetProperty("payUnit").GetString(), job.GetProperty("hoursPerWeek").GetInt32()).ShouldBe(("hour", 20));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("room.monthlyRentVnd")]
    [InlineData("listingType")]
    [InlineData("location")]
    [InlineData("wardCode")]
    [InlineData("room.amenityCodes")]
    public async Task Create_InvalidField_IsValidationFailedOnThatField(string field)
    {
        var owner = await OwnerAsync();
        var body = JsonSerializer.SerializeToNode(Room(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        switch (field)
        {
            case "title": body["title"] = "   "; break;
            case "description": body["description"] = "quá ngắn"; break;
            case "room.monthlyRentVnd": body["room"]!["monthlyRentVnd"] = 50_000; break;
            case "listingType": body["listingType"] = "job"; break;   // room filled but type job
            case "location": body["location"] = JsonSerializer.SerializeToNode(new { lat = 48.85, lng = 2.35 }); break;
            case "wardCode": body["wardCode"] = "90"; break;
            case "room.amenityCodes": body["room"]!["amenityCodes"] = JsonSerializer.SerializeToNode(new[] { "private_bathroom" }); break;
        }

        var response = await Client().SendAsync(HttpMethod.Post, "/api/v1/listings", owner.Token, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await AuthClient.ReadJsonAsync(response)).GetProperty("errors");
        errors.EnumerateObject().Select(p => p.Name).ShouldContain(field);
    }

    // ---- edit (US-307) ---------------------------------------------------------------------------------

    [Fact]
    public async Task Update_Draft_BumpsVersion_StaleVersionConflicts_NonDraftNotEditable()
    {
        var owner = await OwnerAsync();
        var (id, version) = await CreateAsync(owner);

        var ok = await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", owner.Token, Room("Phòng mới sửa", 2_000_000, ["wifi"], version));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ReadJsonAsync(ok)).GetProperty("version").GetInt32().ShouldBe(2);
        var detail = await DetailAsync(id, owner.Token);
        (detail.GetProperty("title").GetString(), detail.GetProperty("amenities").GetArrayLength()).ShouldBe(("Phòng mới sửa", 1));

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", owner.Token, Room(version: version))))
            .ShouldBe("listing.version_conflict");

        await SetStatusAsync(id, "pending_review");
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", owner.Token, Room(version: 2))))
            .ShouldBe("listing.not_editable");
    }

    [Fact]
    public async Task Update_CannotChangeType()
    {
        var owner = await OwnerAsync();
        var (id, version) = await CreateAsync(owner);
        var job = JsonSerializer.SerializeToNode(Job(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        job["version"] = version;

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", owner.Token, job)))
            .ShouldBe("validation.failed");
    }

    // ---- IDOR ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OtherUser_HiddenListingIs404_PublicListingIs403_GuestSeesOnlyPublic()
    {
        var owner = await OwnerAsync();
        var stranger = await OwnerAsync();
        var (id, version) = await CreateAsync(owner);

        (await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", stranger.Token, Room(version: version))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client().SendAsync(HttpMethod.Get, $"/api/v1/listings/{id}", stranger.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client().SendAsync(HttpMethod.Get, $"/api/v1/listings/{id}/history", stranger.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.CreateClient().GetAsync($"/api/v1/listings/{id}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(stranger, id, Png(30))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await SetStatusAsync(id, "verified");
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}", stranger.Token, Room(version: version))))
            .ShouldBe("listing.not_owner");
        var publicView = await DetailAsync(id);
        publicView.GetProperty("title").GetString().ShouldNotBeNull();
        publicView.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);   // status/version only for poster and moderators

        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        await SetStatusAsync(id, "pending_review");
        (await DetailAsync(id, moderator.Token)).GetProperty("status").GetString().ShouldBe("pending_review");
    }

    // ---- photos (US-303) -----------------------------------------------------------------------------

    [Fact]
    public async Task Images_AddDuplicateLimitDeleteReorder_OnlyInDraft()
    {
        var owner = await OwnerAsync();
        var (id, _) = await CreateAsync(owner);

        var ids = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var response = await UploadAsync(owner, id, Png(20 + i));
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            var json = await AuthClient.ReadJsonAsync(response);
            json.GetProperty("sortOrder").GetInt32().ShouldBe(i + 1);
            json.GetProperty("url").GetString()!.ShouldContain($"{id}/");
            ids.Add(json.GetProperty("imageId").GetInt64());
        }
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, id, Png(50)))).ShouldBe("image.too_many");

        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/listings/{id}/images/{ids[0]}", owner.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, id, Png(21)))).ShouldBe("image.duplicate");   // same file as #2
        ids.RemoveAt(0);
        (await AuthClient.ReadJsonAsync(await UploadAsync(owner, id, Png(60)))).GetProperty("sortOrder").GetInt32().ShouldBe(10);

        var images = (await DetailAsync(id, owner.Token)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("imageId").GetInt64()).ToList();
        images.Reverse();
        (await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}/images/order", owner.Token, new { imageIds = images }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DetailAsync(id, owner.Token)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("imageId").GetInt64()).ShouldBe(images);
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Put, $"/api/v1/listings/{id}/images/order", owner.Token,
            new { imageIds = images.Take(3) }))).ShouldBe("validation.failed");

        await SetStatusAsync(id, "rejected");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, id, Png(70)))).ShouldBe("listing.not_editable");
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Delete, $"/api/v1/listings/{id}/images/{images[0]}", owner.Token)))
            .ShouldBe("listing.not_editable");
    }

    // ---- my listings -----------------------------------------------------------------------------------

    [Fact]
    public async Task MyListings_OnlyMine_FilterByStatus()
    {
        var owner = await OwnerAsync();
        var (a, _) = await CreateAsync(owner);
        var (b, _) = await CreateAsync(owner, Job());
        await SetStatusAsync(b, "verified");
        await CreateAsync(await OwnerAsync());

        var all = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/me/listings", owner.Token));
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("listingId").GetGuid()).ShouldBe([b, a], ignoreOrder: true);
        var drafts = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/me/listings?status=draft", owner.Token));
        drafts.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("listingId").GetGuid()).ShouldBe([a]);
    }
}
