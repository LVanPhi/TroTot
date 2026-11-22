using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Migrator.Tests;

/// <summary>
/// Constraints, triggers and grants of 0005_rental_core.sql (docs/12-rental.md, Kiểm thử — CSDL).
/// Every test runs as the role that does the work (app_api / app_maintenance) inside a transaction that is
/// rolled back, so tests do not see each other's rows.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RentalSchemaTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Ward = "98001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var migrator = new NpgsqlConnection(db.MigratorConnectionString);
        await migrator.ExecuteAsync("""
            INSERT INTO app.administrative_units (code, name, unit_level, parent_code)
            VALUES ('98', 'Tỉnh thử', 'province', NULL), ('98001', 'Xã thử', 'commune', '98')
            ON CONFLICT (code) DO NOTHING
            """);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private NpgsqlConnection Connect(string role) =>
        new(new NpgsqlConnectionStringBuilder(db.MigratorConnectionString)
        {
            Username = role,
            Password = role switch
            {
                "app_api" => "test-api",
                "app_maintenance" => "test-maintenance",
                _ => throw new ArgumentOutOfRangeException(nameof(role)),
            },
        }.ConnectionString);

    private sealed record Seed(long Owner, long Tenant, long Room, long Lease);

    /// <summary>Owner with one property and room, a tenant account, and a draft lease (landlord party added).</summary>
    private static async Task<Seed> SeedAsync(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var owner = await c.ExecuteScalarAsync<long>(
            "INSERT INTO app.users (email, password_hash, full_name) VALUES (@E, 'x', 'Chủ trọ') RETURNING id",
            new { E = $"owner-{suffix}@example.com" }, tx);
        var tenant = await c.ExecuteScalarAsync<long>(
            "INSERT INTO app.users (email, password_hash, full_name) VALUES (@E, 'x', 'Người thuê') RETURNING id",
            new { E = $"tenant-{suffix}@example.com" }, tx);
        var property = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app.properties (owner_id, name, ward_code, street_address, location)
            VALUES (@Owner, 'Nhà A', @Ward, 'Số 1', ST_SetSRID(ST_MakePoint(105.73, 21.05), 4326)::geography)
            RETURNING id
            """, new { Owner = owner, Ward }, tx);
        var room = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app.rooms (property_id, code, area_m2, is_self_contained, list_rent_vnd)
            VALUES (@Property, 'P101', 20, true, 2500000) RETURNING id
            """, new { Property = property }, tx);
        var lease = await InsertLeaseAsync(c, tx, room, "2027-01-01", "2027-07-01");
        await c.ExecuteAsync(
            "INSERT INTO app.lease_parties (lease_id, role, user_id) VALUES (@Lease, 'landlord', @Owner)",
            new { Lease = lease, Owner = owner }, tx);
        return new Seed(owner, tenant, room, lease);
    }

    private static Task<long> InsertLeaseAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, long room, string start, string end) =>
        c.ExecuteScalarAsync<long>("""
            INSERT INTO app.leases (room_id, start_date, end_date, rent_vnd, deposit_vnd, billing_day,
                                    electricity_price_vnd, water_price_vnd, water_unit)
            VALUES (@Room, @Start::date, @End::date, 2500000, 2500000, 5, 3500, 100000, 'person')
            RETURNING id
            """, new { Room = room, Start = start, End = end }, tx);

    private static Task SendAsync(NpgsqlConnection c, NpgsqlTransaction tx, long lease) =>
        c.ExecuteAsync("""
            UPDATE app.leases SET status = 'awaiting_signatures', content_sha256 = sha256('x'::bytea),
                   sent_at = now(), invite_expires_at = now() + interval '7 days'
            WHERE id = @Lease
            """, new { Lease = lease }, tx);

    /// <summary>Runs SQL that must fail, inside a savepoint so the transaction stays usable.</summary>
    private static async Task<PostgresException> FailsAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, string sql, object? args = null)
    {
        await tx.SaveAsync("expect_failure", Ct);
        var ex = await Should.ThrowAsync<PostgresException>(() => c.ExecuteAsync(sql, args, tx));
        await tx.RollbackAsync("expect_failure", Ct);
        return ex;
    }

    /// <summary>Issued invoice with one rent line of 2,500,000 ₫.</summary>
    private static async Task<long> IssuedInvoiceAsync(NpgsqlConnection c, NpgsqlTransaction tx, long lease, string period)
    {
        var bank = await c.ExecuteScalarAsync<short>("SELECT id FROM app.banks WHERE bin = '970436'", transaction: tx);
        var invoice = await c.ExecuteScalarAsync<long>(
            "INSERT INTO app.invoices (lease_id, period, total_vnd) VALUES (@Lease, @Period, 2500000) RETURNING id",
            new { Lease = lease, Period = period }, tx);
        await c.ExecuteAsync("""
            INSERT INTO app.invoice_lines (invoice_id, kind, label, quantity, unit_price_vnd, amount_vnd, sort_order)
            VALUES (@Invoice, 'rent', 'Tiền phòng', 1, 2500000, 2500000, 1)
            """, new { Invoice = invoice }, tx);
        await c.ExecuteAsync("""
            UPDATE app.invoices SET status = 'issued', issued_at = now(), due_date = current_date + 7,
                   transfer_code = @Code, bank_id = @Bank, account_ciphertext = '\x00', key_id = 'k1',
                   account_hint = '•••1234', holder_name = 'NGUYEN VAN A'
            WHERE id = @Invoice
            """, // Hex digits are a subset of Crockford base32, so this is a valid transfer code.
            new { Invoice = invoice, Bank = bank, Code = "TT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant() }, tx);
        return invoice;
    }

    [Fact]
    public async Task Leases_OverlappingSentLeaseOnSameRoom_ViolatesExclusion()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        await SendAsync(c, tx, s.Lease);

        var overlapping = await InsertLeaseAsync(c, tx, s.Room, "2027-06-01", "2027-12-01");
        var ex = await FailsAsync(c, tx, """
            UPDATE app.leases SET status = 'awaiting_signatures', content_sha256 = sha256('y'::bytea),
                   sent_at = now(), invite_expires_at = now() WHERE id = @Id
            """, new { Id = overlapping });

        ex.ConstraintName.ShouldBe("ex_leases_room_period");
    }

    [Fact]
    public async Task Leases_BackToBackOrCancelled_DoNotConflict()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        await SendAsync(c, tx, s.Lease);

        // [) ranges: a lease starting on the previous end date does not overlap.
        var next = await InsertLeaseAsync(c, tx, s.Room, "2027-07-01", "2028-01-01");
        await SendAsync(c, tx, next);
        // Cancelling frees the period for another lease.
        await c.ExecuteAsync("UPDATE app.leases SET status = 'cancelled', cancelled_at = now() WHERE id = @Id",
            new { Id = s.Lease }, tx);
        var replacement = await InsertLeaseAsync(c, tx, s.Room, "2027-02-01", "2027-06-01");
        await SendAsync(c, tx, replacement);

        (await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM app.leases WHERE room_id = @Room AND status = 'awaiting_signatures'",
            new { s.Room }, tx)).ShouldBe(2);
    }

    [Fact]
    public async Task Leases_DurationOutsideOneToThirtySixMonths_ViolatesCheck()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);

        var tooShort = await FailsAsync(c, tx, """
            INSERT INTO app.leases (room_id, start_date, end_date, rent_vnd, deposit_vnd, billing_day,
                                    electricity_price_vnd, water_price_vnd, water_unit)
            VALUES (@Room, '2027-01-01', '2027-01-20', 2500000, 0, 5, 3500, 100000, 'm3')
            """, new { s.Room });
        var tooLong = await FailsAsync(c, tx, """
            INSERT INTO app.leases (room_id, start_date, end_date, rent_vnd, deposit_vnd, billing_day,
                                    electricity_price_vnd, water_price_vnd, water_unit)
            VALUES (@Room, '2027-01-01', '2030-02-01', 2500000, 0, 5, 3500, 100000, 'm3')
            """, new { s.Room });

        tooShort.ConstraintName.ShouldBe("ck_leases_duration");
        tooLong.ConstraintName.ShouldBe("ck_leases_duration");
    }

    [Fact]
    public async Task LeaseParties_LandlordNotOwnerOrTenantIsOwner_RaisesInvalidParty()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var other = await SeedAsync(c, tx);

        var wrongLandlord = await FailsAsync(c, tx,
            "INSERT INTO app.lease_parties (lease_id, role, user_id) VALUES (@Lease, 'landlord', @User)",
            new { Lease = other.Lease, User = s.Owner });
        var ownerAsTenant = await FailsAsync(c, tx, """
            INSERT INTO app.lease_parties (lease_id, role, user_id, invite_email, claimed_at)
            VALUES (@Lease, 'tenant', @Owner, 'owner@example.com', now())
            """, new { s.Lease, s.Owner });

        wrongLandlord.MessageText.ShouldBe("rental.invalid_party");
        ownerAsTenant.MessageText.ShouldBe("rental.invalid_party");
    }

    [Fact]
    public async Task LeaseParties_FifthTenant_RaisesInvalidParty()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        for (var i = 1; i <= 4; i++)
        {
            await c.ExecuteAsync(
                "INSERT INTO app.lease_parties (lease_id, role, invite_email) VALUES (@Lease, 'tenant', @Email)",
                new { s.Lease, Email = $"t{i}@example.com" }, tx);
        }

        var ex = await FailsAsync(c, tx,
            "INSERT INTO app.lease_parties (lease_id, role, invite_email) VALUES (@Lease, 'tenant', 't5@example.com')",
            new { s.Lease });

        ex.MessageText.ShouldBe("rental.invalid_party");
    }

    [Fact]
    public async Task LeaseParties_SameEmailTwiceCaseInsensitive_ViolatesUnique()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        await c.ExecuteAsync(
            "INSERT INTO app.lease_parties (lease_id, role, invite_email) VALUES (@Lease, 'tenant', 'a@example.com')",
            new { s.Lease }, tx);

        var ex = await FailsAsync(c, tx,
            "INSERT INTO app.lease_parties (lease_id, role, invite_email) VALUES (@Lease, 'tenant', 'A@Example.com')",
            new { s.Lease });

        ex.ConstraintName.ShouldBe("uq_lease_parties_lease_email");
    }

    [Fact]
    public async Task Invoices_IssuedWithTotalNotEqualToLines_FailsAtCommit()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await IssuedInvoiceAsync(c, tx, s.Lease, "2027-01");
        await c.ExecuteAsync("SET CONSTRAINTS ALL IMMEDIATE", transaction: tx);   // totals match: passes

        await tx.SaveAsync("mismatch", Ct);
        await c.ExecuteAsync("SET CONSTRAINTS ALL DEFERRED", transaction: tx);
        await c.ExecuteAsync("UPDATE app.invoices SET total_vnd = 2600000 WHERE id = @Invoice", new { Invoice = invoice }, tx);
        var ex = await Should.ThrowAsync<PostgresException>(
            () => c.ExecuteAsync("SET CONSTRAINTS ALL IMMEDIATE", transaction: tx));
        await tx.RollbackAsync("mismatch", Ct);

        ex.MessageText.ShouldBe("rental.invoice_total_mismatch");
    }

    [Fact]
    public async Task InvoiceLines_ChangedAfterIssue_RaisesNotEditable()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await IssuedInvoiceAsync(c, tx, s.Lease, "2027-01");

        var insert = await FailsAsync(c, tx, """
            INSERT INTO app.invoice_lines (invoice_id, kind, label, quantity, unit_price_vnd, amount_vnd, sort_order)
            VALUES (@Invoice, 'other', 'Thêm', 1, 1000, 1000, 2)
            """, new { Invoice = invoice });
        var delete = await FailsAsync(c, tx, "DELETE FROM app.invoice_lines WHERE invoice_id = @Invoice",
            new { Invoice = invoice });

        insert.MessageText.ShouldBe("rental.invoice_not_editable");
        delete.MessageText.ShouldBe("rental.invoice_not_editable");
    }

    [Fact]
    public async Task InvoiceLines_MeterEndBelowStart_ViolatesCheck()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await c.ExecuteScalarAsync<long>(
            "INSERT INTO app.invoices (lease_id, period, total_vnd) VALUES (@Lease, '2027-01', 0) RETURNING id",
            new { s.Lease }, tx);

        var ex = await FailsAsync(c, tx, """
            INSERT INTO app.invoice_lines (invoice_id, kind, label, meter_start, meter_end, quantity, unit_price_vnd,
                                           amount_vnd, sort_order)
            VALUES (@Invoice, 'electricity', 'Điện', 120, 100, 0, 3500, 0, 1)
            """, new { Invoice = invoice });

        ex.ConstraintName.ShouldBe("ck_invoice_lines_meter");
    }

    [Fact]
    public async Task Invoices_SamePeriodAllowedOnlyAfterVoid()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var first = await IssuedInvoiceAsync(c, tx, s.Lease, "2027-02");

        var duplicate = await FailsAsync(c, tx,
            "INSERT INTO app.invoices (lease_id, period, total_vnd) VALUES (@Lease, '2027-02', 0)", new { s.Lease });
        await c.ExecuteAsync(
            "UPDATE app.invoices SET status = 'void', voided_at = now(), void_reason = 'Sai chỉ số' WHERE id = @Id",
            new { Id = first }, tx);
        await c.ExecuteAsync(
            "INSERT INTO app.invoices (lease_id, period, total_vnd) VALUES (@Lease, '2027-02', 0)", new { s.Lease }, tx);

        duplicate.ConstraintName.ShouldBe("uq_invoices_lease_period");
    }

    [Fact]
    public async Task Payments_NetAboveTotal_RaisesExceedsTotal()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await IssuedInvoiceAsync(c, tx, s.Lease, "2027-01");
        await c.ExecuteAsync("""
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by)
            VALUES (@Invoice, 2000000, current_date, 'cash', @Owner)
            """, new { Invoice = invoice, s.Owner }, tx);

        var ex = await FailsAsync(c, tx, """
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by)
            VALUES (@Invoice, 600000, current_date, 'bank_transfer', @Owner)
            """, new { Invoice = invoice, s.Owner });

        ex.MessageText.ShouldBe("rental.payment_exceeds_total");
    }

    [Fact]
    public async Task Payments_ReversalRules_EnforcedAndAppendOnly()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await IssuedInvoiceAsync(c, tx, s.Lease, "2027-01");
        var payment = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by)
            VALUES (@Invoice, 2500000, current_date, 'cash', @Owner) RETURNING id
            """, new { Invoice = invoice, s.Owner }, tx);

        var wrongAmount = await FailsAsync(c, tx, """
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by, reverses_payment_id)
            VALUES (@Invoice, 100000, current_date, 'cash', @Owner, @Payment)
            """, new { Invoice = invoice, s.Owner, Payment = payment });
        var reversal = await c.ExecuteScalarAsync<long>("""
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by, reverses_payment_id, note)
            VALUES (@Invoice, 2500000, current_date, 'cash', @Owner, @Payment, 'Ghi nhầm') RETURNING id
            """, new { Invoice = invoice, s.Owner, Payment = payment }, tx);
        var twice = await FailsAsync(c, tx, """
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by, reverses_payment_id)
            VALUES (@Invoice, 2500000, current_date, 'cash', @Owner, @Payment)
            """, new { Invoice = invoice, s.Owner, Payment = payment });
        var reverseReversal = await FailsAsync(c, tx, """
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by, reverses_payment_id)
            VALUES (@Invoice, 2500000, current_date, 'cash', @Owner, @Reversal)
            """, new { Invoice = invoice, s.Owner, Reversal = reversal });
        var update = await FailsAsync(c, tx, "UPDATE app.payments SET amount_vnd = 1 WHERE id = @Payment",
            new { Payment = payment });
        var delete = await FailsAsync(c, tx, "DELETE FROM app.payments WHERE id = @Payment", new { Payment = payment });

        wrongAmount.MessageText.ShouldBe("rental.invalid_reversal");
        twice.ConstraintName.ShouldBe("uq_payments_reverses_payment_id");
        reverseReversal.MessageText.ShouldBe("rental.invalid_reversal");
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Payments_OnDraftInvoice_RaisesNotPayable()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        var invoice = await c.ExecuteScalarAsync<long>(
            "INSERT INTO app.invoices (lease_id, period, total_vnd) VALUES (@Lease, '2027-03', 0) RETURNING id",
            new { s.Lease }, tx);

        var ex = await FailsAsync(c, tx, """
            INSERT INTO app.payments (invoice_id, amount_vnd, paid_on, method, recorded_by)
            VALUES (@Invoice, 1000, current_date, 'cash', @Owner)
            """, new { Invoice = invoice, s.Owner });

        ex.MessageText.ShouldBe("rental.invoice_not_payable");
    }

    [Fact]
    public async Task PayoutAccounts_SelfReviewOrBadHolderName_ViolatesChecks()
    {
        await using var c = Connect("app_api");
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var s = await SeedAsync(c, tx);
        const string insert = """
            INSERT INTO app.payout_accounts (owner_id, bank_id, account_ciphertext, key_id, account_hmac, account_hint,
                                             holder_name, reviewed_by, reviewed_at, review_note)
            VALUES (@Owner, (SELECT id FROM app.banks WHERE bin = '970436'), '\x00', 'k1', sha256('1'::bytea), '•••1234',
                    @Holder, @Reviewer, @At, @Note)
            """;

        var lowercase = await FailsAsync(c, tx, insert,
            new { s.Owner, Holder = "Nguyen Van A", Reviewer = (long?)null, At = (DateTime?)null, Note = (string?)null });
        var selfReview = await FailsAsync(c, tx, insert,
            new { s.Owner, Holder = "NGUYEN VAN A", Reviewer = (long?)s.Owner, At = (DateTime?)DateTime.UtcNow, Note = "ok" });

        lowercase.ConstraintName.ShouldBe("ck_payout_accounts_holder");
        selfReview.ConstraintName.ShouldBe("ck_payout_accounts_not_self_reviewed");
    }

    [Fact]
    public async Task Maintenance_EndsLeaseFreesRoomAndDeletesInvoices()
    {
        long lease, room, invoice;
        await using (var api = Connect("app_api"))
        {
            await api.OpenAsync(Ct);
            await using var tx = await api.BeginTransactionAsync(Ct);
            var s = await SeedAsync(api, tx);
            await SendAsync(api, tx, s.Lease);
            await api.ExecuteAsync(
                "UPDATE app.leases SET status = 'active', activated_at = now() WHERE id = @Lease", new { s.Lease }, tx);
            await api.ExecuteAsync("UPDATE app.rooms SET status = 'occupied' WHERE id = @Room", new { s.Room }, tx);
            invoice = await IssuedInvoiceAsync(api, tx, s.Lease, "2027-01");
            await tx.CommitAsync(Ct);
            (lease, room) = (s.Lease, s.Room);
        }

        await using var m = Connect("app_maintenance");
        await m.OpenAsync(Ct);
        await using var mtx = await m.BeginTransactionAsync(Ct);
        await m.ExecuteAsync(
            "UPDATE app.leases SET status = 'ended', ended_at = now(), version = version + 1 WHERE id = @Lease",
            new { Lease = lease }, mtx);
        await m.ExecuteAsync("UPDATE app.rooms SET status = 'vacant', version = version + 1 WHERE id = @Room",
            new { Room = room }, mtx);
        (await m.ExecuteAsync("DELETE FROM app.invoices WHERE id = @Invoice", new { Invoice = invoice }, mtx)).ShouldBe(1);
        (await m.ExecuteScalarAsync<int>("SELECT count(*) FROM app.invoice_lines WHERE invoice_id = @Invoice",
            new { Invoice = invoice }, mtx)).ShouldBe(0);
        (await m.ExecuteAsync("DELETE FROM app.leases WHERE id = @Lease", new { Lease = lease }, mtx)).ShouldBe(1);
        await mtx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Seed_RenterRoleAndRentalPermissions()
    {
        await using var c = Connect("app_api");
        var grants = (await c.QueryAsync<string>("""
            SELECT r.code || ':' || p.code
            FROM app.role_permissions rp
            JOIN app.roles r ON r.id = rp.role_id
            JOIN app.permissions p ON p.id = rp.permission_id
            WHERE p.code LIKE 'rental.%' OR r.code = 'renter'
            """)).ToList();
        var banks = await c.ExecuteScalarAsync<int>("SELECT count(*) FROM app.banks WHERE is_active");

        grants.ShouldBe(
            ["owner:rental.manage", "student:rental.tenant", "renter:rental.tenant", "renter:scan.create",
             "renter:report.create", "renter:listing.save"],
            ignoreOrder: true);
        banks.ShouldBe(10);
    }
}
