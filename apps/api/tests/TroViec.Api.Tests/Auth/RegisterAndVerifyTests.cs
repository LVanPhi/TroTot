using System.Net;
using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Api.Tests.Auth;

/// <summary>US-101 (student sign-up), US-102 (owner sign-up), email verification and resend.</summary>
[Collection(nameof(InfraCollection))]
public sealed class RegisterAndVerifyTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    [Fact]
    public async Task Register_StudentOutsideSchoolDomain_Returns422StudentEmailDomain()
    {
        var response = await Client().RegisterAsync("someone@gmail.test", "student");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.student_email_domain");
    }

    [Fact]
    public async Task Register_Student_CreatesUnverifiedStudentAndEmailsSixDigitCode()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();

        var response = await client.RegisterAsync(email);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (roles, verified, hash) = await UserAsync(email);
        roles.ShouldBe("student");
        verified.ShouldBeFalse();
        hash.ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        (await client.LatestCodeAsync(email)).ShouldMatch("^[0-9]{6}$");
    }

    [Fact]
    public async Task Register_OwnerWithAnyEmail_CreatesOwnerRole()
    {
        var email = AuthClient.NewOwnerEmail();

        (await Client().RegisterAsync(email, "owner")).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await UserAsync(email)).Roles.ShouldBe("owner");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1234567890")]
    public async Task Register_WeakPassword_Returns422WeakPassword(string password)
    {
        var response = await Client().RegisterAsync(AuthClient.NewStudentEmail(), password: password);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.weak_password");
    }

    [Fact]
    public async Task Register_EmailAlreadyUsedInOtherCase_Returns409EmailTaken()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);

        var response = await client.RegisterAsync(email.ToUpperInvariant().Replace(ApiFactory.StudentDomain.ToUpperInvariant(), ApiFactory.StudentDomain));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.email_taken");
    }

    [Fact]
    public async Task Register_MissingFields_Returns400WithFieldErrors()
    {
        var response = await Client().PostAsync("/api/v1/auth/register",
            new { email = "", password = "", fullName = "", accountType = "admin" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await AuthClient.ReadJsonAsync(response);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("fullName", out _).ShouldBeTrue();
        errors.TryGetProperty("accountType", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_CorrectCode_SetsEmailVerified()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsync("/api/v1/auth/email/verify", new { email, code = await client.LatestCodeAsync(email) });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UserAsync(email)).Verified.ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_FiveWrongCodes_CancelsCodeSoCorrectCodeNoLongerWorks()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);
        var code = await client.LatestCodeAsync(email);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 1; i <= 4; i++)
            (await AuthClient.ProblemCodeAsync(await client.PostAsync("/api/v1/auth/email/verify", new { email, code = wrong })))
                .ShouldBe("auth.code_invalid");
        var fifth = await client.PostAsync("/api/v1/auth/email/verify", new { email, code = wrong });
        var afterCancel = await client.PostAsync("/api/v1/auth/email/verify", new { email, code });

        fifth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AuthClient.ProblemCodeAsync(fifth)).ShouldBe("auth.code_attempts_exceeded");
        (await AuthClient.ProblemCodeAsync(afterCancel)).ShouldBe("auth.code_invalid");
        (await UserAsync(email)).Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Resend_SendsNewCodeThatVerifies()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);

        (await client.PostAsync("/api/v1/auth/email/resend", new { email })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.EmailsToAsync(email)).Count.ShouldBe(2);
        (await client.PostAsync("/api/v1/auth/email/verify", new { email, code = await client.LatestCodeAsync(email) }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Resend_FourthTimeInAnHour_Returns204ButSendsNothing()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);

        for (var i = 0; i < 4; i++)
            (await client.PostAsync("/api/v1/auth/email/resend", new { email })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.EmailsToAsync(email)).Count.ShouldBe(1 + 3);  // sign-up email + 3 allowed resends
    }

    [Fact]
    public async Task Resend_UnknownEmail_Returns204()
    {
        var response = await Client().PostAsync("/api/v1/auth/email/resend", new { email = AuthClient.NewStudentEmail() });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<(string Roles, bool Verified, string Hash)> UserAsync(string email)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        return await connection.QuerySingleAsync<(string, bool, string)>(
            """
            SELECT (SELECT string_agg(r.code, ',') FROM app.user_roles ur JOIN app.roles r ON r.id = ur.role_id
                    WHERE ur.user_id = u.id),
                   u.email_verified_at IS NOT NULL, u.password_hash
            FROM app.users u WHERE lower(u.email) = lower(@Email)
            """, new { Email = email });
    }
}
