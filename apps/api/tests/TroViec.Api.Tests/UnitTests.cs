using Npgsql;
using Shouldly;
using TroViec.Api.Infrastructure.Config;
using TroViec.Api.Infrastructure.Db;

namespace TroViec.Api.Tests;

public sealed class PgErrorMapperTests
{
    [Theory]
    [InlineData("23505", "uq_users_email", null, "auth.email_taken")]
    [InlineData("23505", "uq_users_phone_e164", null, "auth.phone_taken")]
    [InlineData("23505", "uq_appeals_listing_transition_id", null, "appeal.already_used")]
    [InlineData("23505", "uq_invoices_lease_period", null, "rental.invoice_period_exists")]
    [InlineData("23505", "uq_payments_reverses_payment_id", null, "rental.payment_already_reversed")]
    [InlineData("23P01", "ex_leases_room_period", null, "rental.room_already_leased")]
    [InlineData("P0001", null, "rental.payment_exceeds_total", "rental.payment_exceeds_total")]
    [InlineData("23503", "fk_listings_ward_code", null, "validation.reference_not_found")]
    [InlineData("23514", "ck_users_phone_format", null, "validation.constraint_violated")]
    [InlineData("P0001", null, "appeal.window_closed", "appeal.window_closed")]
    [InlineData("40001", null, null, "system.retry_later")]
    [InlineData("40P01", null, null, "system.retry_later")]
    public void Map_KnownError_ReturnsCode(string sqlState, string? constraint, string? message, string expected) =>
        PgErrorMapper.Map(Pg(sqlState, constraint, message), includeConstraintName: false)!.Code.ShouldBe(expected);

    [Theory]
    [InlineData("23505", "uq_some_other_index", null)]
    [InlineData("23P01", "ex_some_other_exclusion", null)]
    [InlineData("P0001", null, "free text from a trigger")]
    [InlineData("42P01", null, null)]
    public void Map_UnknownError_ReturnsNull(string sqlState, string? constraint, string? message) =>
        PgErrorMapper.Map(Pg(sqlState, constraint, message), includeConstraintName: false).ShouldBeNull();

    [Fact]
    public void Map_CheckViolationInDevelopment_IncludesConstraintName()
    {
        var problem = PgErrorMapper.Map(Pg("23514", "ck_users_phone_format", null), includeConstraintName: true)!;

        problem.Extensions!["constraint"].ShouldBe("ck_users_phone_format");
    }

    [Fact]
    public void Map_CheckViolationInProduction_HidesConstraintName() =>
        PgErrorMapper.Map(Pg("23514", "ck_users_phone_format", null), includeConstraintName: false)!.Extensions.ShouldBeNull();

    private static PostgresException Pg(string sqlState, string? constraint, string? message) =>
        new(messageText: message ?? "msg", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState,
            constraintName: constraint);
}

public sealed class DbEnumTests
{
    public enum SampleStatus { Draft, PendingReview, Verified }

    [Theory]
    [InlineData(SampleStatus.Draft, "draft")]
    [InlineData(SampleStatus.PendingReview, "pending_review")]
    public void ToDb_EnumValue_ReturnsSnakeCaseLabel(SampleStatus value, string label)
    {
        DbEnum.ToDb(value).ShouldBe(label);
        DbEnum.FromDb<SampleStatus>(label).ShouldBe(value);
    }

    [Fact]
    public void FromDb_UnknownLabel_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => DbEnum.FromDb<SampleStatus>("pendingreview"));
}

public sealed class TrustedProxyTests
{
    [Fact]
    public void ParseNetworks_Unset_DefaultsToLoopback() =>
        ServiceSetup.ParseNetworks(null).Select(n => n.ToString()).ShouldBe(["127.0.0.1/32", "::1/128"]);

    [Fact]
    public void ParseNetworks_CommaSeparated_ParsesEach() =>
        ServiceSetup.ParseNetworks(" 172.18.0.0/16 , 10.0.0.0/8").Select(n => n.ToString())
            .ShouldBe(["172.18.0.0/16", "10.0.0.0/8"]);
}
