using Shouldly;
using TroViec.Api.Features.Search;

namespace TroViec.Api.Tests.Search;

/// <summary>Rule-based parser for natural-language search (US-402): ≥ 20 sample sentences.</summary>
public sealed class QueryParserTests
{
    private static readonly HashSet<string> Active =
    [
        "wifi", "air_conditioner", "water_heater", "kitchen", "fridge", "washing_machine", "parking", "balcony", "free_hours",
        "security_camera", "elevator",
    ];

    public static TheoryData<string, string?, int?, int?, string, bool?, string> Cases => new()
    {
        // query, type, min, max, amenities (comma), selfContained, remainder
        { "phòng khép kín dưới 2,5 triệu có chỗ để xe", "room", null, 2_500_000, "parking", true, "" },
        { "Phòng trọ dưới 2tr5 gần trường", "room", null, 2_500_000, "", null, "" },
        { "phòng từ 2 đến 3 triệu có điều hòa", "room", 2_000_000, 3_000_000, "air_conditioner", null, "" },
        { "phòng 2-3tr", "room", 2_000_000, 3_000_000, "", null, "" },
        { "trọ trên 1,5 triệu có máy giặt và tủ lạnh", "room", 1_500_000, null, "washing_machine,fridge", null, "" },
        { "phòng tối đa 1.800.000đ", "room", null, 1_800_000, "", null, "" },
        { "phòng không quá 1800k", "room", null, 1_800_000, "", null, "" },
        { "phòng khoảng 2 triệu", "room", 1_600_000, 2_400_000, "", null, "" },
        { "phong khep kin duoi 3tr co wifi", "room", null, 3_000_000, "wifi", true, "" },
        { "PHÒNG CÓ NÓNG LẠNH, BẾP RIÊNG", "room", null, null, "water_heater,kitchen", null, "" },
        { "phòng có ban công thoáng mát", "room", null, null, "balcony", null, "thoang mat" },
        { "phòng giờ giấc tự do không chung chủ", "room", null, null, "free_hours", null, "" },
        { "chung cư mini có thang máy và camera", "room", null, null, "elevator,security_camera", null, "" },
        { "phòng vệ sinh riêng chỗ gửi xe", "room", null, null, "parking", true, "" },
        { "việc làm thêm buổi tối", "job", null, null, "", null, "buoi toi" },
        { "tuyển phục vụ quán cà phê", "job", null, null, "", null, "quan ca phe" },
        { "gia sư toán lớp 9", "job", null, null, "", null, "toan lop 9" },
        { "part time cuối tuần", "job", null, null, "", null, "cuoi tuan" },
        { "phòng yên tĩnh gần chợ", "room", null, null, "", null, "yen tinh gan cho" },
        { "phòng rộng có cửa sổ dưới 4 triệu", "room", null, 4_000_000, "balcony", null, "rong" },
        { "nhà trọ máy lạnh internet", "room", null, null, "air_conditioner,wifi", null, "" },
        { "phòng dưới 50k", "room", null, null, "", null, "duoi 50k" },   // out of range: not a rent filter
        { "phòng 2tr5/tháng gần chợ", "room", 2_000_000, 3_000_000, "", null, "gan cho" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_SampleSentence_GivesExpectedFilters(string query, string? type, int? min, int? max, string amenities,
        bool? selfContained, string remainder)
    {
        var parsed = QueryParser.Parse(query, Active);

        parsed.Filters.Type.ShouldBe(type);
        (parsed.Filters.MinRent, parsed.Filters.MaxRent).ShouldBe((min, max));
        parsed.Filters.Amenities.ShouldBe(amenities.Length == 0 ? [] : amenities.Split(','), ignoreOrder: true);
        parsed.Filters.SelfContained.ShouldBe(selfContained);
        parsed.Remainder.ShouldBe(remainder);
    }

    [Fact]
    public void Parse_NearCampus_IsFlagged()
    {
        QueryParser.Parse("phòng gần trường", Active).Filters.NearCampus.ShouldBeTrue();
    }

    [Fact]
    public void Parse_InactiveAmenity_StaysInRemainder()
    {
        var parsed = QueryParser.Parse("phòng có thang máy", new HashSet<string> { "wifi" });

        parsed.Filters.Amenities.ShouldBeEmpty();
        parsed.Remainder.ShouldBe("thang may");
    }
}
