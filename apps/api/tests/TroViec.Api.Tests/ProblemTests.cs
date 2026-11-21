using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Tests;

[Collection(nameof(InfraCollection))]
public sealed class ProblemTests(InfraFixture infra)
{
    [Fact]
    public async Task Handler_DuplicateUserEmail_Writes409EmailTakenProblem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        const string insert = "INSERT INTO app.users (email, password_hash, full_name) VALUES (@Email, '!', 'Test')";

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(insert, new { Email = email });
        var violation = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync(insert, new { Email = email.ToUpperInvariant() }));

        violation.ConstraintName.ShouldBe("uq_users_email");
        var (status, body) = await WriteThroughHandlerAsync(factory.Services, violation, ct);

        status.ShouldBe(409);
        body.GetProperty("code").GetString().ShouldBe("auth.email_taken");
        body.GetProperty("type").GetString().ShouldBe("https://troviec.example.com/errors/auth.email_taken");
        body.GetProperty("title").GetString().ShouldBe("Email đã được đăng ký");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("errors").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Handler_ProblemExceptionWithErrors_WritesValidationProblem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra);
        var errors = new Dictionary<string, string[]> { ["monthlyRentVnd"] = ["Giá phải lớn hơn 0"] };

        var (status, body) = await WriteThroughHandlerAsync(
            factory.Services, new ProblemException("validation.failed", errors: errors), ct);

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").GetProperty("monthlyRentVnd")[0].GetString().ShouldBe("Giá phải lớn hơn 0");
    }

    [Fact]
    public async Task Handler_UnexpectedException_Writes500WithoutMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra);

        var (status, body) = await WriteThroughHandlerAsync(
            factory.Services, new InvalidOperationException("secret user data"), ct);

        status.ShouldBe(500);
        body.GetProperty("code").GetString().ShouldBe("system.error");
        body.GetRawText().ShouldNotContain("secret user data");
    }

    private static async Task<(int Status, JsonElement Body)> WriteThroughHandlerAsync(
        IServiceProvider services, Exception exception, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        var handler = scope.ServiceProvider.GetServices<IExceptionHandler>().Single();

        (await handler.TryHandleAsync(context, exception, ct)).ShouldBeTrue();

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: ct);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }
}
