import base64

import pytest

from troviec_worker.pipeline.identifiers import decode_key, extract, normalize_url, value_hmac

# Shared test vector with the API (TroViec.Api.Tests IdentifierHasherTests): if either side changes how
# the key is
# decoded or how the input string is built, both tests fail instead of hashes silently diverging.
SHARED_KEY_B64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
SHARED_HMAC_HEX = "b0654f42d896f81d38b1e33a1170f472472115a2f8d99196dc8866737f8d85ab"


def test_value_hmac_shared_vector_with_api() -> None:
    key = decode_key(SHARED_KEY_B64)

    assert key == bytes(range(32))
    assert value_hmac(key, "phone", "+84912345678").hex() == SHARED_HMAC_HEX


def test_decode_key_uses_bytes_not_the_base64_text() -> None:
    key = decode_key(SHARED_KEY_B64)

    assert value_hmac(key, "phone", "+84912345678") != value_hmac(
        SHARED_KEY_B64.encode(), "phone", "+84912345678"
    )


def test_decode_key_rejects_non_base64() -> None:
    with pytest.raises(ValueError):
        decode_key("not base64 !!")


@pytest.mark.parametrize(
    "raw", ["0912 345 678", "0912.345.678", "+84 912345678", "84912345678", "0912-345-678"]
)
def test_phone_variants_normalize_to_same_value(raw: str) -> None:
    found = extract(f"Liên hệ {raw} để xem phòng")

    assert [(i.kind, i.normalized, i.hint) for i in found] == [("phone", "+84912345678", "•••678")]


@pytest.mark.parametrize(
    "text",
    [
        "Số bàn 024 3869 2222 giờ hành chính",  # landline
        "Giá 2500000000 đồng",  # money, 10 digits
        "Mã tin 1234567890123",
        "Diện tích 25 m2, tầng 3",
    ],
)
def test_landlines_money_and_ids_are_not_phones(text: str) -> None:
    assert [i for i in extract(text) if i.kind == "phone"] == []


def test_bank_account_only_near_keyword() -> None:
    near = extract("Chuyển cọc vào STK 1903 5678 9012 Techcombank nhé")
    far = extract("Phòng số 190356789012, gần chợ, yên tĩnh, an ninh tốt, chủ dễ tính, có chỗ để xe máy")

    assert [(i.kind, i.normalized, i.hint) for i in near] == [("bank_account", "190356789012", "•••9012")]
    assert [i for i in far if i.kind == "bank_account"] == []


def test_bank_account_does_not_capture_phone_numbers() -> None:
    found = extract("STK hay số 0912345678 đều được")

    assert [i.kind for i in found] == ["phone"]


@pytest.mark.parametrize(
    ("raw", "normalized", "host"),
    [
        ("https://Zalo.me/0912345678/", "zalo.me/0912345678", "zalo.me"),
        ("zalo.me/g/abcdef", "zalo.me/g/abcdef", "zalo.me"),
        (
            "https://www.facebook.com/nguyen.van.a?utm_source=x&id=5",
            "facebook.com/nguyen.van.a?id=5",
            "facebook.com",
        ),
        ("bit.ly/3xYz9", "bit.ly/3xYz9", "bit.ly"),
        ("http://example.com/phong.", "example.com/phong", "example.com"),
    ],
)
def test_urls_normalized(raw: str, normalized: str, host: str) -> None:
    found = extract(f"Xem ảnh tại {raw} nhé")

    assert [(i.kind, i.normalized, i.hint) for i in found] == [("url", normalized, host)]


def test_url_with_phone_in_path_is_one_url() -> None:
    found = extract("Nhắn zalo.me/0912345678 hoặc gọi 0987654321")

    assert sorted((i.kind, i.hint) for i in found) == [("phone", "•••321"), ("url", "zalo.me")]


def test_duplicates_are_reported_once() -> None:
    found = extract("Gọi 0912345678 hoặc 0912.345.678")

    assert len(found) == 1


def test_normalize_url_drops_all_utm_parameters() -> None:
    assert normalize_url("https://t.me/abc?utm_medium=a&UTM_campaign=b") == ("t.me/abc", "t.me")


def test_hint_never_contains_full_value() -> None:
    for identifier in extract("STK 0011223344556 Vietcombank, gọi 0912345678"):
        assert identifier.normalized not in identifier.hint or identifier.kind == "url"


def test_base64_round_trip_sanity() -> None:
    assert base64.b64encode(bytes(range(32))).decode() == SHARED_KEY_B64
