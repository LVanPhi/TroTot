using System.Net;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Admin;

/// <summary>
/// docs/09-testing.md, Ma trận phân quyền. Every endpoint × 6 kinds of caller, expectations read from <see cref="Matrix"/>.
/// Adding an endpoint without a row here fails <see cref="Matrix_CoversEveryApiEndpoint"/>.
/// </summary>
[Collection(nameof(InfraCollection))]
public sealed class AuthorizationMatrixTests(InfraFixture infra, AuthorizationMatrixTests.Callers6 users)
    : IClassFixture<AuthorizationMatrixTests.Callers6>
{
    // Callers, in column order of the Expect strings.
    private static readonly string[] Callers = ["guest", "student", "owner", "moderator", "admin", "student_no_phone"];

    /// <summary>
    /// "ok"  = reached the handler (not rejected by authentication/authorization);
    /// "401" = auth.unauthenticated; "403" = auth.forbidden. Sample requests never change shared state:
    /// unknown ids, wrong passwords, invalid bodies.
    /// </summary>
    private sealed record Row(string Method, string Route, string Path, object? Body, string Expect);

    private const string Missing = "999999999";

    private static readonly Row[] Matrix =
    [
        //                                                                                                      guest student owner moderator admin student_no_phone
        new("POST", "/api/v1/auth/register", "/api/v1/auth/register", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/email/verify", "/api/v1/auth/email/verify", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/email/resend", "/api/v1/auth/email/resend", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/login", "/api/v1/auth/login", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/refresh", "/api/v1/auth/refresh", null, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/logout", "/api/v1/auth/logout", null, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/password/forgot", "/api/v1/auth/password/forgot", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/password/reset", "/api/v1/auth/password/reset", new { }, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/auth/phone/otp", "/api/v1/auth/phone/otp", new { phone = "bad" }, "401 ok ok ok ok ok"),
        new("POST", "/api/v1/auth/phone/verify", "/api/v1/auth/phone/verify", new { phone = "bad", code = "x" }, "401 ok ok ok ok ok"),
        new("GET", "/api/v1/me/", "/api/v1/me", null, "401 ok ok ok ok ok"),
        new("PATCH", "/api/v1/me/", "/api/v1/me", new { fullName = "" }, "401 ok ok ok ok ok"),
        new("DELETE", "/api/v1/me/", "/api/v1/me", new { password = "" }, "401 ok ok ok ok ok"),
        new("GET", "/api/v1/admin/users/", "/api/v1/admin/users", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/users/{id:long}/roles", $"/api/v1/admin/users/{Missing}/roles", new { roleCode = "student" }, "401 403 403 403 ok 403"),
        new("DELETE", "/api/v1/admin/users/{id:long}/roles/{roleCode}", $"/api/v1/admin/users/{Missing}/roles/student", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/users/{id:long}/lock", $"/api/v1/admin/users/{Missing}/lock", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/users/{id:long}/unlock", $"/api/v1/admin/users/{Missing}/unlock", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/users/{id:long}/phone", $"/api/v1/admin/users/{Missing}/phone", new { phone = "0912345678" }, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/scans/", "/api/v1/scans", new { }, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/scans/quota", "/api/v1/scans/quota", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/scans/{scanId:guid}", $"/api/v1/scans/{Guid.Empty}", null, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/scans/{scanId:guid}/reports", $"/api/v1/scans/{Guid.Empty}/reports", new { category = "scam" }, "401 ok 403 ok ok ok"),
        new("GET", "/api/v1/me/scans", "/api/v1/me/scans", null, "401 ok ok ok ok ok"),
        new("GET", "/api/v1/meta/provinces", "/api/v1/meta/provinces", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/meta/provinces/{code}/communes", "/api/v1/meta/provinces/90/communes", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/meta/communes", "/api/v1/meta/communes?q=mau", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/meta/amenities", "/api/v1/meta/amenities", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/meta/banks", "/api/v1/meta/banks", null, "ok ok ok ok ok ok"),
        // Rental (docs/12-rental.md): rental.manage = owner; owner.verify = moderator.
        new("GET", "/api/v1/rental/properties", "/api/v1/rental/properties", null, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/properties", "/api/v1/rental/properties", new { }, "401 403 ok 403 403 403"),
        new("GET", "/api/v1/rental/properties/{id:long}", $"/api/v1/rental/properties/{Missing}", null, "401 403 ok 403 403 403"),
        new("PUT", "/api/v1/rental/properties/{id:long}", $"/api/v1/rental/properties/{Missing}", new { }, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/properties/{id:long}/rooms", $"/api/v1/rental/properties/{Missing}/rooms", new { }, "401 403 ok 403 403 403"),
        new("PUT", "/api/v1/rental/rooms/{id:long}", $"/api/v1/rental/rooms/{Missing}", new { }, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/rooms/{id:long}/listing", $"/api/v1/rental/rooms/{Missing}/listing", null, "401 403 ok 403 403 403"),
        new("GET", "/api/v1/me/payout-account", "/api/v1/me/payout-account", null, "401 403 ok 403 403 403"),
        new("PUT", "/api/v1/me/payout-account", "/api/v1/me/payout-account", new { }, "401 403 ok 403 403 403"),
        new("GET", "/api/v1/moderation/payout-accounts", "/api/v1/moderation/payout-accounts", null, "401 403 403 ok 403 403"),
        new("GET", "/api/v1/rental/leases", "/api/v1/rental/leases", null, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/leases", "/api/v1/rental/leases", new { }, "401 403 ok 403 403 403"),
        new("GET", "/api/v1/rental/leases/{leaseId:guid}", $"/api/v1/rental/leases/{Guid.Empty}", null, "401 403 ok 403 403 403"),
        new("PUT", "/api/v1/rental/leases/{leaseId:guid}", $"/api/v1/rental/leases/{Guid.Empty}", new { }, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/leases/{leaseId:guid}/send", $"/api/v1/rental/leases/{Guid.Empty}/send", new { }, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/leases/{leaseId:guid}/cancel", $"/api/v1/rental/leases/{Guid.Empty}/cancel", new { }, "401 403 ok 403 403 403"),
        new("POST", "/api/v1/rental/leases/{leaseId:guid}/end", $"/api/v1/rental/leases/{Guid.Empty}/end", new { }, "401 403 ok 403 403 403"),
        // Any party of a lease (signed in; the party check is the authorization, others get 404).
        new("POST", "/api/v1/leases/{leaseId:guid}/otp", $"/api/v1/leases/{Guid.Empty}/otp", null, "401 ok ok ok ok ok"),
        new("POST", "/api/v1/leases/{leaseId:guid}/confirm", $"/api/v1/leases/{Guid.Empty}/confirm", new { }, "401 ok ok ok ok ok"),
        new("GET", "/api/v1/tenancy/invites/{token}", "/api/v1/tenancy/invites/unknown-token-0000000000", null, "401 ok ok ok ok ok"),
        new("POST", "/api/v1/tenancy/invites/{token}/claim", "/api/v1/tenancy/invites/unknown-token-0000000000/claim", null, "401 ok 403 403 403 ok"),
        new("GET", "/api/v1/tenancy/leases", "/api/v1/tenancy/leases", null, "401 ok 403 403 403 ok"),
        new("GET", "/api/v1/tenancy/leases/{leaseId:guid}", $"/api/v1/tenancy/leases/{Guid.Empty}", null, "401 ok 403 403 403 ok"),
        new("POST", "/api/v1/moderation/payout-accounts/{ownerId:long}/review", $"/api/v1/moderation/payout-accounts/{Missing}/review", new { }, "401 403 403 ok 403 403"),
        new("GET", "/api/v1/meta/criteria", "/api/v1/meta/criteria", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/meta/campuses", "/api/v1/meta/campuses", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/me/owner-profile", "/api/v1/me/owner-profile", null, "401 ok ok 403 403 ok"),
        new("PUT", "/api/v1/me/owner-profile", "/api/v1/me/owner-profile", new { }, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/me/owner-documents", "/api/v1/me/owner-documents", null, "401 ok ok 403 403 ok"),
        new("POST", "/api/v1/me/owner-documents", "/api/v1/me/owner-documents", null, "401 ok ok 403 403 ok"),
        new("DELETE", "/api/v1/me/owner-documents/{id:long}", $"/api/v1/me/owner-documents/{Missing}", null, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/moderation/owner-profiles", "/api/v1/moderation/owner-profiles", null, "401 403 403 ok 403 403"),
        new("GET", "/api/v1/moderation/owner-profiles/{userId:long}", $"/api/v1/moderation/owner-profiles/{Missing}", null, "401 403 403 ok 403 403"),
        new("POST", "/api/v1/moderation/owner-profiles/{userId:long}/verify", $"/api/v1/moderation/owner-profiles/{Missing}/verify", null, "401 403 403 ok 403 403"),
        new("POST", "/api/v1/moderation/owner-documents/{id:long}/review", $"/api/v1/moderation/owner-documents/{Missing}/review", new { }, "401 403 403 ok 403 403"),
        new("POST", "/api/v1/moderation/owner-documents/{id:long}/access", $"/api/v1/moderation/owner-documents/{Missing}/access", new { }, "401 403 403 ok 403 403"),
        new("GET", "/api/v1/files/documents/{token}", "/api/v1/files/documents/abc", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/listings/{listingId:guid}", $"/api/v1/listings/{Guid.Empty}", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/listings/", "/api/v1/listings?lat=21&lng=105.8", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/listings/search", "/api/v1/listings/search?q=phong&lat=21&lng=105.8", null, "ok ok ok ok ok ok"),
        new("GET", "/api/v1/listings/{listingId:guid}/history", $"/api/v1/listings/{Guid.Empty}/history", null, "401 ok ok ok ok ok"),
        new("POST", "/api/v1/listings/", "/api/v1/listings", new { }, "401 ok ok 403 403 ok"),
        new("PUT", "/api/v1/listings/{listingId:guid}", $"/api/v1/listings/{Guid.Empty}", new { }, "401 ok ok 403 403 ok"),
        new("POST", "/api/v1/listings/{listingId:guid}/images", $"/api/v1/listings/{Guid.Empty}/images", null, "401 ok ok 403 403 ok"),
        new("DELETE", "/api/v1/listings/{listingId:guid}/images/{imageId:long}", $"/api/v1/listings/{Guid.Empty}/images/{Missing}", null, "401 ok ok 403 403 ok"),
        new("PUT", "/api/v1/listings/{listingId:guid}/images/order", $"/api/v1/listings/{Guid.Empty}/images/order", new { }, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/me/listings", "/api/v1/me/listings", null, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/moderation/queue", "/api/v1/moderation/queue", null, "401 403 403 ok ok 403"),
        new("GET", "/api/v1/admin/criteria", "/api/v1/admin/criteria", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/criteria", "/api/v1/admin/criteria", new { }, "401 403 403 403 ok 403"),
        new("PUT", "/api/v1/admin/criteria/{id:int}", "/api/v1/admin/criteria/32000", new { }, "401 403 403 403 ok 403"),
        new("GET", "/api/v1/admin/model-versions", "/api/v1/admin/model-versions", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/admin/model-versions/{id:int}/activate", "/api/v1/admin/model-versions/32000/activate", null, "401 403 403 403 ok 403"),
        new("GET", "/api/v1/admin/stats", "/api/v1/admin/stats", null, "401 403 403 403 ok 403"),
        new("GET", "/api/v1/admin/audit/transitions", "/api/v1/admin/audit/transitions", null, "401 403 403 403 ok 403"),
        new("GET", "/api/v1/admin/audit/document-access", "/api/v1/admin/audit/document-access", null, "401 403 403 403 ok 403"),
        new("POST", "/api/v1/listings/{listingId:guid}/reports", $"/api/v1/listings/{Guid.Empty}/reports", new { }, "401 ok 403 ok ok ok"),
        new("GET", "/api/v1/moderation/reports/", "/api/v1/moderation/reports", null, "401 403 403 ok ok 403"),
        new("GET", "/api/v1/moderation/reports/{id:long}", $"/api/v1/moderation/reports/{Missing}", null, "401 403 403 ok ok 403"),
        new("GET", "/api/v1/moderation/reports/{id:long}/scan-images/{imageNo:int}", $"/api/v1/moderation/reports/{Missing}/scan-images/1", null, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/reports/{id:long}/confirm", $"/api/v1/moderation/reports/{Missing}/confirm", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/reports/{id:long}/dismiss", $"/api/v1/moderation/reports/{Missing}/dismiss", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/listings/{listingId:guid}/appeals", $"/api/v1/listings/{Guid.Empty}/appeals", new { }, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/me/appeals", "/api/v1/me/appeals", null, "401 ok ok 403 403 ok"),
        new("GET", "/api/v1/moderation/appeals/", "/api/v1/moderation/appeals", null, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/appeals/{id:long}/accept", $"/api/v1/moderation/appeals/{Missing}/accept", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/appeals/{id:long}/reject", $"/api/v1/moderation/appeals/{Missing}/reject", new { }, "401 403 403 ok ok 403"),
        new("GET", "/api/v1/listings/{listingId:guid}/reviews/", $"/api/v1/listings/{Guid.Empty}/reviews", null, "ok ok ok ok ok ok"),
        new("POST", "/api/v1/listings/{listingId:guid}/reviews/", $"/api/v1/listings/{Guid.Empty}/reviews", new { }, "401 ok 403 403 403 ok"),
        new("POST", "/api/v1/moderation/reviews/{id:long}/hide", $"/api/v1/moderation/reviews/{Missing}/hide", new { }, "401 403 403 ok ok 403"),
        new("GET", "/api/v1/me/saved-listings/", "/api/v1/me/saved-listings", null, "401 ok 403 403 403 ok"),
        new("PUT", "/api/v1/me/saved-listings/{listingId:guid}", $"/api/v1/me/saved-listings/{Guid.Empty}", null, "401 ok 403 403 403 ok"),
        new("DELETE", "/api/v1/me/saved-listings/{listingId:guid}", $"/api/v1/me/saved-listings/{Guid.Empty}", null, "401 ok 403 403 403 ok"),
        new("GET", "/api/v1/moderation/audit/me", "/api/v1/moderation/audit/me", null, "401 403 403 ok 403 403"),
        new("GET", "/api/v1/moderation/listings/{listingId:guid}", $"/api/v1/moderation/listings/{Guid.Empty}", null, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/listings/{listingId:guid}/approve", $"/api/v1/moderation/listings/{Guid.Empty}/approve", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/listings/{listingId:guid}/reject", $"/api/v1/moderation/listings/{Guid.Empty}/reject", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/listings/{listingId:guid}/hide", $"/api/v1/moderation/listings/{Guid.Empty}/hide", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/moderation/listings/{listingId:guid}/restore", $"/api/v1/moderation/listings/{Guid.Empty}/restore", new { }, "401 403 403 ok ok 403"),
        new("POST", "/api/v1/listings/{listingId:guid}/submit", $"/api/v1/listings/{Guid.Empty}/submit", new { }, "401 ok ok 403 403 ok"),
        new("POST", "/api/v1/listings/{listingId:guid}/renew", $"/api/v1/listings/{Guid.Empty}/renew", new { }, "401 ok ok 403 403 ok"),
        new("POST", "/api/v1/listings/{listingId:guid}/unpublish", $"/api/v1/listings/{Guid.Empty}/unpublish", new { }, "401 ok ok 403 403 ok"),
        new("POST", "/api/v1/listings/{listingId:guid}/remove", $"/api/v1/listings/{Guid.Empty}/remove", new { }, "401 ok ok 403 403 ok"),
    ];

    /// <summary>One factory and six signed-in callers shared by every case in this class.</summary>
    public sealed class Callers6(InfraFixture infra) : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new(infra);
        public Dictionary<string, string?> Tokens { get; } = [];

        public async ValueTask InitializeAsync()
        {
            var client = new AuthClient(Factory.CreateClient(), infra);
            Tokens["guest"] = null;
            Tokens["student"] = (await TestUsers.CreateAsync(client, infra, ["student"])).Token;
            Tokens["owner"] = (await TestUsers.CreateAsync(client, infra, ["owner"])).Token;
            Tokens["moderator"] = (await TestUsers.CreateAsync(client, infra, ["moderator"])).Token;
            Tokens["admin"] = (await TestUsers.CreateAsync(client, infra, ["admin"])).Token;
            Tokens["student_no_phone"] = (await TestUsers.CreateAsync(client, infra, ["student"], phoneVerified: false)).Token;
        }

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private ApiFactory _factory => users.Factory;
    private Dictionary<string, string?> _tokens => users.Tokens;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Matrix)
            foreach (var caller in Callers)
                data.Add($"{row.Method} {row.Route}", caller);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Endpoint_Caller_GetsExpectedAccess(string endpoint, string caller)
    {
        var row = Matrix.Single(r => $"{r.Method} {r.Route}" == endpoint);
        var expected = row.Expect.Split(' ')[Array.IndexOf(Callers, caller)];
        var request = new HttpRequestMessage(new HttpMethod(row.Method), row.Path)
        {
            Content = row.Body is null ? null : System.Net.Http.Json.JsonContent.Create(row.Body),
        };
        request.Headers.Add(ApiFactory.TestIpHeader, $"10.200.{Array.IndexOf(Callers, caller)}.{Random.Shared.Next(1, 255)}");
        request.Headers.Add("X-Requested-With", "troviec");
        if (_tokens[caller] is { } token) request.Headers.Authorization = new("Bearer", token);

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        var code = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await AuthClient.ProblemCodeAsync(response)
            : null;

        switch (expected)
        {
            case "401":
                ((response.StatusCode, code)).ShouldBe((HttpStatusCode.Unauthorized, "auth.unauthenticated"));
                break;
            case "403":
                ((response.StatusCode, code)).ShouldBe((HttpStatusCode.Forbidden, "auth.forbidden"));
                break;
            default:
                // Reached the handler: whatever it answers, it is not the authorization layer saying no.
                code.ShouldNotBe("auth.unauthenticated", $"{endpoint} as {caller}");
                code.ShouldNotBe("auth.forbidden", $"{endpoint} as {caller}");
                break;
        }
    }

    [Fact]
    public void Matrix_CoversEveryApiEndpoint()
    {
        var mapped = ApiEndpoints().Select(e => $"{e.Method} {e.Route}").Order().ToList();

        mapped.ShouldBe(Matrix.Select(r => $"{r.Method} {r.Route}").Order().ToList(),
            "add a row to AuthorizationMatrixTests.Matrix for every new endpoint");
    }

    /// <summary>AGENTS.md rule 11: every endpoint states its access: a permission policy, signed-in only, or anonymous.</summary>
    [Fact]
    public void EveryEndpoint_DeclaresAuthorizationExplicitly()
    {
        var missing = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null && e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void PermissionPolicies_UseOnlyKnownPermissionCodes()
    {
        var policies = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .OfType<string>()
            .Distinct();

        policies.Except(Permissions.All).ShouldBeEmpty();
    }

    [Fact]
    public async Task PermissionConstants_MatchDatabaseSeed()
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var codes = (await connection.QueryAsync<string>("SELECT code FROM app.permissions")).Order().ToList();

        Permissions.All.Order().ShouldBe(codes);
    }

    private IEnumerable<(string Method, string Route)> ApiEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => (m, e.RoutePattern.RawText!)));
}
