using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.OwnerProfiles;

/// <summary>T3.1: owner profile, encrypted documents, moderator review/verify, signed document links (E5, BR-10, BR-11).</summary>
[Collection(nameof(InfraCollection))]
public sealed class OwnerDocumentTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly MutableClock _clock = new();
    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ??= new ApiFactory(infra) { Clock = _clock };

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    private AuthClient Client() => new(Factory.CreateClient(), infra);

    private static byte[] Png(int extra = 64) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. RandomNumberGenerator.GetBytes(extra)];

    private static byte[] Pdf() => [.. "%PDF-1.7\n"u8, .. RandomNumberGenerator.GetBytes(100)];

    private Task<HttpResponseMessage> UploadAsync(TestUser user, byte[] file, string docType = "id_card", string? consent = "true")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", "giay-to.bin");
        form.Add(new StringContent(docType), "docType");
        if (consent is not null) form.Add(new StringContent(consent), "consent");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/owner-documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<TestUser> OwnerWithProfileAsync()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        (await Client().SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", owner.Token, new { ownerType = "individual" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return owner;
    }

    private async Task<long> UploadOkAsync(TestUser owner, byte[]? file = null)
    {
        var response = await UploadAsync(owner, file ?? Png());
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AuthClient.ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private async Task<int> AccessLogCountAsync(long documentId)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM app.document_access_logs WHERE owner_document_id = @Id", new { Id = documentId });
    }

    // ---- profile (US-501) ----------------------------------------------------------------------------

    [Fact]
    public async Task Profile_BusinessNeedsNameAndTaxCode_IndividualSavesAndReads()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var client = Client();

        (await client.SendAsync(HttpMethod.Get, "/api/v1/me/owner-profile", owner.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", owner.Token,
            new { ownerType = "business", businessName = "Nhà trọ A" }))).ShouldBe("validation.failed");
        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", owner.Token,
            new { ownerType = "business", businessName = "Nhà trọ A", taxCode = "12345" }))).ShouldBe("validation.failed");

        var ok = await client.SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", owner.Token,
            new { ownerType = "business", businessName = "Nhà trọ A", taxCode = "0101234567-001" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me/owner-profile", owner.Token));
        (body.GetProperty("ownerType").GetString(), body.GetProperty("taxCode").GetString()).ShouldBe(("business", "0101234567-001"));
    }

    [Fact]
    public async Task Profile_AccountWithoutListingCreate_IsForbidden()
    {
        var moderatorOnly = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        (await Client().SendAsync(HttpMethod.Get, "/api/v1/me/owner-profile", moderatorOnly.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- upload (US-502) -----------------------------------------------------------------------------

    [Fact]
    public async Task Upload_RequiresConsentProfileSupportedTypeAndSize()
    {
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, Png()))).ShouldBe("owner.profile_required");
        await Client().SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", owner.Token, new { ownerType = "individual" });

        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, Png(), consent: null))).ShouldBe("document.consent_required");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, Png(), consent: "false"))).ShouldBe("document.consent_required");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, "GIF89a-not-allowed"u8.ToArray()))).ShouldBe("document.unsupported_type");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, Png(10 * 1024 * 1024)))).ShouldBe("document.too_large");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(owner, Png(), docType: "passport"))).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Upload_StoresEncryptedCopyAndRecordsKeyAndConsent()
    {
        var owner = await OwnerWithProfileAsync();
        var file = Pdf();

        var id = await UploadOkAsync(owner, file);

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var row = await connection.QuerySingleAsync<(string Key, string Mime, int Size, string KeyId, bool Consent, byte[] Sha)>(
            "SELECT object_key, mime_type, byte_size, key_id, consent_at IS NOT NULL, sha256 FROM app.owner_documents WHERE id = @Id",
            new { Id = id });
        (row.Mime, row.Size, row.KeyId, row.Consent).ShouldBe(("application/pdf", file.Length, "k1", true));
        row.Sha.ShouldBe(SHA256.HashData(file));
        var stored = await Factory.Services.GetRequiredService<IObjectStorage>().GetAsync("owner-documents", row.Key, CancellationToken.None);
        stored!.Length.ShouldBe(file.Length + 28);   // nonce 12 + tag 16
        stored.AsSpan().IndexOf(file.AsSpan(0, 9)).ShouldBe(-1);   // no plaintext at rest
        Factory.Services.GetRequiredService<DocumentCipher>().Decrypt("k1", stored).ShouldBe(file);
    }

    [Fact]
    public async Task Upload_SameFileTwice_IsDuplicateForSameAndOtherAccount()
    {
        var first = await OwnerWithProfileAsync();
        var second = await OwnerWithProfileAsync();
        var file = Png();
        await UploadOkAsync(first, file);

        (await AuthClient.ProblemCodeAsync(await UploadAsync(first, file))).ShouldBe("document.duplicate");
        (await AuthClient.ProblemCodeAsync(await UploadAsync(second, file))).ShouldBe("document.duplicate");
    }

    // ---- list and delete (US-504) --------------------------------------------------------------------

    [Fact]
    public async Task Delete_OwnDocumentErasesFileKeepsRow_OthersGet404()
    {
        var owner = await OwnerWithProfileAsync();
        var stranger = await OwnerWithProfileAsync();
        var id = await UploadOkAsync(owner);
        string key;
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
            key = await connection.ExecuteScalarAsync<string>("SELECT object_key FROM app.owner_documents WHERE id = @Id", new { Id = id }) ?? "";

        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/me/owner-documents/{id}", stranger.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/me/owner-documents/{id}", owner.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Client().SendAsync(HttpMethod.Delete, $"/api/v1/me/owner-documents/{id}", owner.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var list = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/me/owner-documents", owner.Token));
        list.EnumerateArray().Single().GetProperty("erasedAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
        (await Factory.Services.GetRequiredService<IObjectStorage>().ExistsAsync("owner-documents", key, CancellationToken.None)).ShouldBeFalse();
    }

    // ---- moderation: review, verify (US-503, BR-11, BR-19) -----------------------------------------

    [Fact]
    public async Task Verify_NeedsAcceptedDocument_SetsTwelveMonths_NotForOwnProfile()
    {
        var owner = await OwnerWithProfileAsync();
        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator", "owner"]);
        var docId = await UploadOkAsync(owner);
        var client = Client();

        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-profiles/{owner.Id}/verify", moderator.Token)))
            .ShouldBe("owner.no_accepted_document");
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{docId}/review", moderator.Token, new { decision = "accepted" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var verify = await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-profiles/{owner.Id}/verify", moderator.Token);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var until = (await AuthClient.ReadJsonAsync(verify)).GetProperty("verifiedUntil").GetDateTimeOffset();
        until.ShouldBe(_clock.GetUtcNow().AddMonths(12), TimeSpan.FromSeconds(1));

        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-profiles/{moderator.Id}/verify", moderator.Token)))
            .ShouldBe("moderation.conflict_of_interest");
    }

    [Fact]
    public async Task Review_OwnDocument_IsConflictOfInterest_AndPendingListShowsProfile()
    {
        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator", "owner"]);
        await Client().SendAsync(HttpMethod.Put, "/api/v1/me/owner-profile", moderator.Token, new { ownerType = "individual" });
        var ownDoc = await UploadOkAsync(moderator);

        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{ownDoc}/review",
            moderator.Token, new { decision = "accepted" }))).ShouldBe("moderation.conflict_of_interest");

        var other = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        var pending = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/moderation/owner-profiles?pending=true&pageSize=50", other.Token));
        pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetInt64()).ShouldContain(moderator.Id);
        var detail = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/moderation/owner-profiles/{moderator.Id}", other.Token));
        detail.GetProperty("documents").GetArrayLength().ShouldBe(1);
    }

    // ---- document links (US-605, BR-10) --------------------------------------------------------------

    [Fact]
    public async Task Access_EachLinkLogsOnce_FileIsNoStore_ReopenWithinFiveMinutes_ThenExpires()
    {
        var owner = await OwnerWithProfileAsync();
        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        var file = Pdf();
        var id = await UploadOkAsync(owner, file);
        var client = Client();

        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access",
            moderator.Token, new { purpose = " " }))).ShouldBe("validation.failed");
        var access = await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access", moderator.Token,
            new { purpose = "Đối chiếu tên chủ hộ" });
        access.StatusCode.ShouldBe(HttpStatusCode.OK);
        var url = (await AuthClient.ReadJsonAsync(access)).GetProperty("url").GetString()!;
        (await AccessLogCountAsync(id)).ShouldBe(1);

        var http = Factory.CreateClient();
        var opened = await http.GetAsync(url, TestContext.Current.CancellationToken);
        opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await opened.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(file);
        opened.Headers.CacheControl!.NoStore.ShouldBeTrue();
        opened.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");

        _clock.Advance(TimeSpan.FromMinutes(4));
        (await http.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AccessLogCountAsync(id)).ShouldBe(1);   // reopening the same link writes nothing

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await AuthClient.ProblemCodeAsync(await http.GetAsync(url, TestContext.Current.CancellationToken))).ShouldBe("document.token_invalid");

        await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access", moderator.Token, new { purpose = "Xem lại" });
        (await AccessLogCountAsync(id)).ShouldBe(2);   // every link issued is logged
    }

    [Fact]
    public async Task Access_TamperedTokenIs403_AdminLacksDocumentView_ErasedIs410()
    {
        var owner = await OwnerWithProfileAsync();
        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        var admin = await TestUsers.CreateAsync(Client(), infra, ["admin"]);
        var id = await UploadOkAsync(owner);
        var client = Client();

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access", admin.Token, new { purpose = "Xem" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var url = (await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access",
            moderator.Token, new { purpose = "Xem" }))).GetProperty("url").GetString()!;
        var tampered = url[..^2] + (url[^2] == 'A' ? "B" : "A") + url[^1];
        var http = Factory.CreateClient();
        (await AuthClient.ProblemCodeAsync(await http.GetAsync(tampered, TestContext.Current.CancellationToken))).ShouldBe("document.token_invalid");
        (await AuthClient.ProblemCodeAsync(await http.GetAsync("/api/v1/files/documents/abc", TestContext.Current.CancellationToken))).ShouldBe("document.token_invalid");

        await client.SendAsync(HttpMethod.Delete, $"/api/v1/me/owner-documents/{id}", owner.Token);
        (await http.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await AuthClient.ProblemCodeAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/owner-documents/{id}/access",
            moderator.Token, new { purpose = "Xem" }))).ShouldBe("document.erased");
    }

    // ---- account deletion (US-106 AC3) ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAccount_ErasesOwnerDocumentFiles()
    {
        var owner = await OwnerWithProfileAsync();
        var id = await UploadOkAsync(owner);

        var response = await Client().SendAsync(HttpMethod.Delete, "/api/v1/me", owner.Token, new { password = AuthClient.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        (await connection.QuerySingleAsync<(bool Erased, bool KeyNull)>(
            "SELECT erased_at IS NOT NULL, object_key IS NULL FROM app.owner_documents WHERE id = @Id", new { Id = id }))
            .ShouldBe((true, true));
    }

    // ---- crypto units ---------------------------------------------------------------------------------

    [Fact]
    public void DocumentCipher_RotatedKeyStillOpensOldFiles_TamperingFails()
    {
        var keys = $"{{\"k1\":\"{ApiFactory.TestKey(1)}\",\"k2\":\"{ApiFactory.TestKey(2)}\"}}";
        var old = new DocumentCipher(keys, "k1");
        var rotated = new DocumentCipher(keys, "k2");
        var blob = old.Encrypt("giấy tờ"u8.ToArray());

        rotated.Decrypt("k1", blob).ShouldBe("giấy tờ"u8.ToArray());
        blob[^1] ^= 1;
        Should.Throw<CryptographicException>(() => rotated.Decrypt("k1", blob));
        Should.Throw<InvalidOperationException>(() => new DocumentCipher(keys, "k9"));
    }

    [Fact]
    public void SignedUrl_ValidUntilFiveMinutes()
    {
        var clock = new MutableClock();
        var links = SignedUrlService.FromBase64(ApiFactory.TestKey(5), clock);
        var (token, _) = links.Create(42, 7);

        links.Validate(token).ShouldBe((42L, 7L));
        clock.Advance(TimeSpan.FromMinutes(5));
        Should.Throw<ProblemException>(() => links.Validate(token)).Code.ShouldBe("document.token_invalid");
        Should.Throw<ProblemException>(() => SignedUrlService.FromBase64(ApiFactory.TestKey(6), new MutableClock()).Validate(token))
            .Code.ShouldBe("document.token_invalid");
    }
}
