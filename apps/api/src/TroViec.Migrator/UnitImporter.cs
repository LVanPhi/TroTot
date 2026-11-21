using System.Text;
using Dapper;
using Npgsql;

namespace TroViec.Migrator;

public sealed record AdministrativeUnit(string Code, string Name, string UnitLevel, string? ParentCode);

/// <summary>Loads administrative units from CSV (db/seed/administrative_units/README.md), upsert by code.</summary>
public static class UnitImporter
{
    private const string ExpectedHeader = "code,name,unit_level,parent_code";

    public static async Task<int> ImportFileAsync(string connectionString, string csvPath, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(csvPath))
            throw new MigratorException($"file not found: {csvPath}");
        var units = Parse(await File.ReadAllLinesAsync(csvPath, Encoding.UTF8, ct));
        return await ImportAsync(connectionString, units, log, ct);
    }

    public static IReadOnlyList<AdministrativeUnit> Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].Trim().TrimStart('﻿') != ExpectedHeader)
            throw new MigratorException($"line 1: header must be exactly '{ExpectedHeader}'");

        var units = new List<AdministrativeUnit>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var lineNo = i + 1;
            var fields = SplitCsvLine(lines[i], lineNo);
            if (fields.Count != 4)
                throw new MigratorException($"line {lineNo}: expected 4 columns, got {fields.Count}");

            var code = fields[0].Trim();
            var name = fields[1].Trim();
            var level = fields[2].Trim();
            var parent = fields[3].Trim();
            if (level is not ("province" or "commune"))
                throw new MigratorException($"line {lineNo}: unit_level must be 'province' or 'commune'");
            if ((level == "province") != (parent.Length == 0))
                throw new MigratorException($"line {lineNo}: provinces have no parent_code, communes must have one");

            units.Add(new AdministrativeUnit(code, name, level, parent.Length == 0 ? null : parent));
        }
        return units;
    }

    public static async Task<int> ImportAsync(
        string connectionString, IReadOnlyList<AdministrativeUnit> units, Action<string> log, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        const string upsert = """
            INSERT INTO app.administrative_units (code, name, unit_level, parent_code)
            VALUES (@Code, @Name, @UnitLevel::app.admin_unit_level, @ParentCode)
            ON CONFLICT (code) DO UPDATE
                SET name = EXCLUDED.name, unit_level = EXCLUDED.unit_level, parent_code = EXCLUDED.parent_code
            """;
        // Provinces first so communes' foreign keys resolve.
        var ordered = units.OrderBy(u => u.UnitLevel == "province" ? 0 : 1).ToList();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(upsert, ordered, tx, cancellationToken: ct));
        }
        catch (PostgresException ex)
        {
            throw new MigratorException($"import failed ({ex.SqlState} {ex.ConstraintName}): {ex.MessageText}");
        }
        await tx.CommitAsync(ct);

        log($"imported {ordered.Count(u => u.UnitLevel == "province")} province(s), " +
            $"{ordered.Count(u => u.UnitLevel == "commune")} commune(s)");
        return ordered.Count;
    }

    /// <summary>Splits one CSV line; supports double-quoted fields with "" escapes.</summary>
    private static List<string> SplitCsvLine(string line, int lineNo)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (inQuotes) throw new MigratorException($"line {lineNo}: unterminated quote");
        fields.Add(current.ToString());
        return fields;
    }
}
