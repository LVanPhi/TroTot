using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TroViec.Api.Features.Search;

/// <summary>Hard filters understood from a natural-language query (docs/04-api.md, appliedFilters).</summary>
public sealed record AppliedFilters(string? Type, int? MinRent, int? MaxRent, IReadOnlyList<string> Amenities, bool? SelfContained,
    bool NearCampus);

public sealed record ParsedQuery(AppliedFilters Filters, string Remainder);

/// <summary>
/// Rule-based parser (docs/02-architecture.md, Tìm bằng câu tự nhiên): price ("dưới 2,5 triệu", "2tr5", "1.500.000đ",
/// "từ 2 đến 3 triệu"), listing type, amenities by keyword, "khép kín" → selfContained, "gần trường". Matching works on
/// an accent-free, lower-case copy; whatever is not understood stays in Remainder for full-text and vector ranking.
/// </summary>
public static partial class QueryParser
{
    private static readonly (string Code, string[] Phrases)[] AmenityWords =
    [
        ("parking", ["cho de xe", "cho gui xe", "de xe", "gui xe", "bai xe", "nha xe", "de xe may"]),
        ("air_conditioner", ["dieu hoa", "may lanh"]),
        ("water_heater", ["binh nong lanh", "nong lanh", "nuoc nong"]),
        ("washing_machine", ["may giat"]),
        ("fridge", ["tu lanh"]),
        ("kitchen", ["bep rieng", "cho nau an", "nau an", "bep"]),
        ("wifi", ["wifi", "wi-fi", "internet", "mang"]),
        ("balcony", ["ban cong", "cua so"]),
        ("free_hours", ["gio giac tu do", "khong chung chu", "tu do gio giac", "gio tu do"]),
        ("security_camera", ["camera an ninh", "camera"]),
        ("elevator", ["thang may"]),
    ];

    private static readonly string[] SelfContainedWords = ["khep kin", "ve sinh rieng", "wc rieng", "vs rieng", "nha ve sinh rieng"];
    private static readonly string[] RoomWords = ["phong tro", "nha tro", "can ho mini", "chung cu mini", "phong", "tro"];
    private static readonly string[] JobWords = ["viec lam them", "lam them", "viec lam", "part time", "parttime", "gia su", "phuc vu", "tuyen", "viec"];
    private static readonly string[] NearCampusWords = ["gan truong", "gan dai hoc", "gan dh", "gan cong truong"];

    private static readonly HashSet<string> StopWords =
    [
        "co", "va", "can", "tim", "muon", "o", "mot", "cac", "nhung", "thue", "gia", "khoang", "tam", "voi", "la",
        "de", "duoc", "minh", "em", "ban", "nao", "nhe", "a", "oi", "khu", "vuc", "sinh", "vien", "sv", "re", "moi",
        "/", "-", ",", ".",
    ];

    private const string Amount = @"(?<n>\d{1,3}(?:[.,]\d{3})+|\d+(?:[.,]\d+)?)\s*(?<u>trieu|tr|cu|k|nghin|ngan)?\s*(?<d>\d)?(?:\s*(?:d|dong|vnd))?";

    public static ParsedQuery Parse(string query, IReadOnlySet<string> activeAmenities)
    {
        // "/tháng", "một tháng" after a price carry no meaning for the search.
        var text = " " + PerMonth().Replace(Fold(query), " ") + " ";
        string? type = null;
        int? min = null, max = null;
        bool? selfContained = null;
        var near = false;
        var amenities = new List<string>();

        // Price ranges first, then one-sided bounds, then "khoảng X" (±20 %).
        text = Range.Replace(text, m =>
        {
            if (ToVnd(m, "a") is { } a && ToVnd(m, "b") is { } b) { (min, max) = (Math.Min(a, b), Math.Max(a, b)); return " "; }
            return m.Value;
        });
        text = Below.Replace(text, m => ToVnd(m, "a") is { } v ? Set(ref max, v) : m.Value);
        text = Above.Replace(text, m => ToVnd(m, "a") is { } v ? Set(ref min, v) : m.Value);
        text = About.Replace(text, m =>
        {
            if (ToVnd(m, "a") is not { } v) return m.Value;
            min ??= (int)(v * 0.8);
            max ??= (int)(v * 1.2);
            return " ";
        });

        if (Take(ref text, SelfContainedWords)) selfContained = true;
        if (Take(ref text, NearCampusWords)) near = true;
        foreach (var (code, phrases) in AmenityWords)
            if (activeAmenities.Contains(code) && Take(ref text, phrases)) amenities.Add(code);
        if (Take(ref text, JobWords)) type = "job";
        if (Take(ref text, RoomWords)) type ??= "room";
        if (selfContained is not null || amenities.Count > 0 || min is not null || max is not null) type ??= "room";

        var remainder = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !StopWords.Contains(w)));
        return new ParsedQuery(new AppliedFilters(type, min, max, amenities, selfContained, near), remainder);
    }

    /// <summary>Lower-case, no diacritics, đ → d, single spaces.</summary>
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        // Punctuation ending a word becomes a space; "2,5" and "1.800.000" keep theirs.
        var plain = WordPunctuation().Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ");
        return Spaces().Replace(plain, " ").Trim();
    }

    private static string Set(ref int? target, int value)
    {
        target = value;
        return " ";
    }

    private static bool Take(ref string text, IEnumerable<string> phrases)
    {
        var found = false;
        foreach (var p in phrases.OrderByDescending(p => p.Length))
        {
            var needle = " " + p + " ";
            while (text.Contains(needle, StringComparison.Ordinal))
            {
                text = text.Replace(needle, " ", StringComparison.Ordinal);
                found = true;
            }
        }
        return found;
    }

    /// <summary>"2,5 triệu" / "2tr5" / "1500k" / "1.500.000đ" → VND; amounts without a unit under 1,000 are millions.</summary>
    private static int? ToVnd(Match m, string prefix)
    {
        var n = m.Groups[prefix + "n"].Value;
        var unit = m.Groups[prefix + "u"].Value;
        var tail = m.Groups[prefix + "d"].Value;
        if (n.Length == 0) return null;
        decimal value;
        if (unit is "" && GroupedThousands().IsMatch(n)) value = decimal.Parse(n.Replace(".", "").Replace(",", ""), CultureInfo.InvariantCulture);
        else
        {
            value = decimal.Parse(n.Replace(',', '.'), CultureInfo.InvariantCulture);
            value *= unit switch
            {
                "trieu" or "tr" or "cu" => 1_000_000m,
                "k" or "nghin" or "ngan" => 1_000m,
                _ => value < 1000 ? 1_000_000m : 1m,
            };
            if (tail.Length == 1 && unit is "tr" or "trieu" or "cu") value += int.Parse(tail, CultureInfo.InvariantCulture) * 100_000m;
        }
        return value is >= 100_000 and <= 100_000_000 ? (int)value : null;
    }

    private static string Amt(string p) => Amount.Replace("?<n>", $"?<{p}n>").Replace("?<u>", $"?<{p}u>").Replace("?<d>", $"?<{p}d>");

    [GeneratedRegex(@"[,.;:!?()""]+(?=\s|$)|(?<=\s|^)[,.;:!?()""]+")]
    private static partial Regex WordPunctuation();

    [GeneratedRegex(@"\s*/\s*thang\b|\b(?:mot|1|moi) thang\b")]
    private static partial Regex PerMonth();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})+$")]
    private static partial Regex GroupedThousands();

    private static readonly Regex Range = new(
        $@" (?:tu |gia tu |khoang tu )?{Amt("a")}\s*(?:den|toi|-|~)\s*{Amt("b")} ", RegexOptions.CultureInvariant);

    private static readonly Regex Below = new(
        $@" (?:duoi|<|toi da|khong qua|max|re hon|it hon) {Amt("a")} ", RegexOptions.CultureInvariant);

    private static readonly Regex Above = new(
        $@" (?:tren|>|tu|it nhat|toi thieu|min) {Amt("a")} ", RegexOptions.CultureInvariant);

    /// <summary>"khoảng 2 triệu", "giá 2tr", or any amount that carries a money unit.</summary>
    private static readonly Regex About = new(
        $@" (?:(?:khoang|tam|gia|tam gia|khoang gia) {Amt("a")}|(?<an>\d+(?:[.,]\d+)?)\s*(?<au>trieu|tr|cu|k|nghin|ngan)\s*(?<ad>\d)?|(?<an>\d{{1,3}}(?:[.,]\d{{3}})+)\s*(?:d|dong|vnd)) ",
        RegexOptions.CultureInvariant);
}
