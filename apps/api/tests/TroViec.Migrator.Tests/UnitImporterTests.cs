using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Migrator.Tests;

[Collection(nameof(PostgresCollection))]
public sealed class UnitImporterTests(PostgresFixture db)
{
    private static readonly string SampleCsv =
        Path.Combine(RepoRoot.Find(), "db", "seed", "administrative_units", "sample.csv");

    [Fact]
    public async Task ImportFileAsync_SampleCsv_LoadsProvincesAndCommunes()
    {
        var ct = TestContext.Current.CancellationToken;

        var count = await UnitImporter.ImportFileAsync(db.MigratorConnectionString, SampleCsv, _ => { }, ct);

        count.ShouldBe(5);
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        var quoted = await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM app.administrative_units WHERE code = '91001'");
        quoted.ShouldBe("Phường Mẫu C, khu 1");
    }

    [Fact]
    public async Task ImportAsync_SameCodeTwice_UpdatesNameWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        AdministrativeUnit[] original = [new("92", "Tỉnh Cũ", "province", null), new("92001", "Xã Cũ", "commune", "92")];
        AdministrativeUnit[] renamed = [new("92", "Tỉnh Mới", "province", null), new("92001", "Xã Cũ", "commune", "92")];

        await UnitImporter.ImportAsync(db.MigratorConnectionString, original, _ => { }, ct);
        await UnitImporter.ImportAsync(db.MigratorConnectionString, renamed, _ => { }, ct);

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        var rows = (await connection.QueryAsync<string>(
            "SELECT name FROM app.administrative_units WHERE code = '92'")).ToList();
        rows.ShouldBe(["Tỉnh Mới"]);
    }

    [Fact]
    public async Task ImportAsync_CommuneWithUnknownParent_ThrowsAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        AdministrativeUnit[] units = [new("93", "Tỉnh Ba", "province", null), new("94001", "Xã Lạc", "commune", "94")];

        await Should.ThrowAsync<MigratorException>(
            () => UnitImporter.ImportAsync(db.MigratorConnectionString, units, _ => { }, ct));

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM app.administrative_units WHERE code = '93'")).ShouldBe(0);
    }
}

public sealed class UnitImporterParseTests
{
    [Fact]
    public void Parse_WrongHeader_Throws()
    {
        Should.Throw<MigratorException>(() => UnitImporter.Parse(["code,name,level,parent", "01,A,province,"]))
            .Message.ShouldContain("line 1");
    }

    [Theory]
    [InlineData("01,Tỉnh A,district,")]
    [InlineData("01,Tỉnh A,province,02")]
    [InlineData("01001,Xã A,commune,")]
    [InlineData("01,Tỉnh A,province")]
    public void Parse_InvalidRow_ThrowsWithLineNumber(string row)
    {
        Should.Throw<MigratorException>(() => UnitImporter.Parse(["code,name,unit_level,parent_code", row]))
            .Message.ShouldContain("line 2");
    }
}
