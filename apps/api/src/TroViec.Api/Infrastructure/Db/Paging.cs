namespace TroViec.Api.Infrastructure.Db;

/// <summary>Paged list shape shared by every list endpoint (docs/04-api.md, Quy ước chung).</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>Clamps ?page= and ?pageSize= to 1.. and 1..50 instead of failing on odd values.</summary>
    public static (int Page, int PageSize, int Offset) Normalize(int? page, int? pageSize)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        return (p, size, (p - 1) * size);
    }

    /// <summary>Escapes % and _ so user text is matched literally inside ILIKE patterns.</summary>
    public static string LikeContains(string text) =>
        "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}
