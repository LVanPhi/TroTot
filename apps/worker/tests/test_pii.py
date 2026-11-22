"""docs/09-testing.md: >= 30 real-style Vietnamese sentences (anonymised).

Mandatory before any text goes to an LLM.
"""

import pytest

from troviec_worker.pipeline.pii import mask

CASES = [
    # phones
    ("Liên hệ 0912 345 678 để xem phòng", "Liên hệ [SĐT] để xem phòng"),
    ("Gọi ngay 0987.654.321 (chính chủ)", "Gọi ngay [SĐT] (chính chủ)"),
    ("Zalo +84 912 345 678 nhắn trước nha", "Zalo [SĐT] nhắn trước nha"),
    ("SĐT/Zalo: 0388-111-222", "SĐT/Zalo: [SĐT]"),
    ("ib 0353123456 hoặc 0912345678", "ib [SĐT] hoặc [SĐT]"),
    # bank accounts
    ("Chuyển cọc 500k vào STK 1903 5678 9012 Techcombank", "Chuyển cọc 500k vào STK [STK] Techcombank"),
    ("Số tài khoản: 0011004455667 Vietcombank chủ TK", "Số tài khoản: [STK] Vietcombank chủ TK"),
    ("MB 9704229912345 nhé em", "MB [STK] nhé em"),
    ("ck qua tk 123456789 agribank trước 12h", "ck qua tk [STK] agribank trước 12h"),
    # emails
    ("Gửi CV về tuyendung.quan@gmail.com", "Gửi CV về [EMAIL]"),
    ("email: chu.nha_99@yahoo.com.vn", "email: [EMAIL]"),
    # links
    ("Xem thêm ảnh tại https://www.facebook.com/nguyen.van.tuan.123", "Xem thêm ảnh tại [LINK]"),
    ("Nhắn zalo.me/0912345678 để giữ phòng", "Nhắn [LINK] để giữ phòng"),
    ("Đăng ký tại bit.ly/vieclam-tet", "Đăng ký tại [LINK]"),
    ("Group: https://t.me/vieclamthem_hn", "Group: [LINK]"),
    # names after a title
    ("Liên hệ anh Tuấn để xem phòng", "Liên hệ anh [TÊN] để xem phòng"),
    ("Chị Nguyễn Thị Lan cho thuê phòng", "Chị [TÊN] cho thuê phòng"),
    ("Gặp chú Ba ở tầng 1", "Gặp chú [TÊN] ở tầng 1"),
    ("Chủ nhà Trần Văn Hùng rất dễ tính", "Chủ nhà [TÊN] rất dễ tính"),
    ("Em là Mai, bạn Hoa giới thiệu", "Em là Mai, bạn [TÊN] giới thiệu"),
    ("a Đức nhận cọc trực tiếp", "a [TÊN] nhận cọc trực tiếp"),
    ("c Hằng quản lý tiệm", "c [TÊN] quản lý tiệm"),
    ("ông Lê Minh Quân chủ trọ", "ông [TÊN] chủ trọ"),
    # ID card numbers
    ("Gửi ảnh CCCD 001203004567 để giữ chỗ", "Gửi ảnh CCCD [CCCD] để giữ chỗ"),
    ("Số căn cước: 079200012345", "Số căn cước: [CCCD]"),
    # several at once
    (
        "Chị Lan, 0912345678, STK 1903567890 TPBank, email lan@gmail.com",
        "Chị [TÊN], [SĐT], STK [STK] TPBank, email [EMAIL]",
    ),
    # must stay untouched: streets, schools, places, prices, sizes, dates
    (
        "Phòng ở đường Lê Văn Lương, gần Đại học Bách Khoa",
        "Phòng ở đường Lê Văn Lương, gần Đại học Bách Khoa",
    ),
    ("Cách trường Đại học Quốc gia Hà Nội 500m", "Cách trường Đại học Quốc gia Hà Nội 500m"),
    ("Giá 2.500.000 đồng/tháng, diện tích 25 m2", "Giá 2.500.000 đồng/tháng, diện tích 25 m2"),
    ("Ngõ 165 Cầu Giấy, phường Dịch Vọng", "Ngõ 165 Cầu Giấy, phường Dịch Vọng"),
    ("Nhận việc từ 20/11/2026, lương 30.000đ/giờ", "Nhận việc từ 20/11/2026, lương 30.000đ/giờ"),
    ("Mã phòng 190356789012 tầng 4", "Mã phòng 190356789012 tầng 4"),
    ("Hệ thống điều hòa mới, có thang máy", "Hệ thống điều hòa mới, có thang máy"),
]


def test_has_at_least_30_cases() -> None:
    assert len(CASES) >= 30


@pytest.mark.parametrize(("text", "expected"), CASES)
def test_mask(text: str, expected: str) -> None:
    assert mask(text) == expected


def test_ner_layer_masks_names_without_title() -> None:
    text = "Liên hệ Nguyễn Văn An để xem phòng"

    masked = mask(text, ner=lambda t: [(t.index("Nguyễn"), t.index("Nguyễn") + len("Nguyễn Văn An"))])

    assert masked == "Liên hệ [TÊN] để xem phòng"


def test_ner_failure_keeps_regex_masking() -> None:
    def broken(_: str) -> list[tuple[int, int]]:
        raise RuntimeError("model missing")

    assert mask("Gọi anh Tuấn 0912345678", ner=broken) == "Gọi anh [TÊN] [SĐT]"
