"""Steps 8-10 (docs/09-testing.md: price, image_signal, community): pure functions and DB integration."""

import io
import math
import uuid
from datetime import UTC, datetime, timedelta

import pytest
from PIL import Image, ImageDraw, ImageFilter
from psycopg_pool import ConnectionPool

from troviec_worker.jobs import signal_steps
from troviec_worker.jobs.text_steps import TextAnalysis
from troviec_worker.pipeline import community_signal, image_signal, price
from troviec_worker.pipeline.image_signal import Candidate

# ---- helpers ------------------------------------------------------------------------------------


def photo(seed: int, size: tuple[int, int] = (800, 600)) -> Image.Image:
    """A blurred mosaic: enough structure for pHash and CLIP to tell photos apart."""
    import random

    rnd = random.Random(seed)
    img = Image.new("RGB", size, (200, 190, 170))
    draw = ImageDraw.Draw(img)
    for _ in range(25):
        x, y = rnd.randrange(size[0] - 100), rnd.randrange(size[1] - 100)
        w, h = rnd.randrange(40, 200), rnd.randrange(40, 200)
        draw.rectangle(
            [x, y, x + w, y + h], fill=(rnd.randrange(256), rnd.randrange(256), rnd.randrange(256))
        )
    return img.filter(ImageFilter.GaussianBlur(3))


def jpeg(img: Image.Image, quality: int = 90) -> bytes:
    out = io.BytesIO()
    img.save(out, "JPEG", quality=quality)
    return out.getvalue()


def stripes() -> Image.Image:
    img = Image.new("RGB", (800, 600), "white")
    draw = ImageDraw.Draw(img)
    for x in range(0, 800, 40):
        draw.rectangle([x, 0, x + 20, 600], fill=(20, 60, 160))
    draw.ellipse([250, 150, 550, 450], fill=(230, 40, 40))
    return img


def crop20(img: Image.Image) -> Image.Image:
    w, h = img.size
    return img.crop((int(w * 0.1), int(h * 0.1), int(w * 0.9), int(h * 0.9)))


def cand(
    status: str = "verified", poster: int = 2, ham: int = 3, sim: float = 0.0, image_id: int = 1
) -> Candidate:
    return Candidate(1, image_id, 10, status, poster, ham, sim)


# ---- price (step 9) -------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("Cho thuê phòng trọ 1tr8 gần ĐH", 1_800_000),
        ("Phòng trọ giá 1,8 triệu/tháng", 1_800_000),
        ("Phòng khép kín 1.800.000đ một tháng", 1_800_000),
        ("Phòng trọ 1800k bao điện nước", 1_800_000),
        ("Cho thuê phòng 2tr5/tháng", 2_500_000),
        ("Phòng trọ sạch, cọc 200k, giá thuê 2tr", 2_000_000),  # 200k is out of range, "giá thuê" preferred
        ("Phòng trọ 50 triệu", None),  # outside 300k-20tr
        ("Tuyển nhân viên bán hàng, lương 5 triệu/tháng", None),  # job text: no room keyword
        ("Phòng trọ đẹp, liên hệ để biết giá", None),
    ],
)
def test_extract_rent(text: str, expected: int | None) -> None:
    assert price.extract_rent(text) == expected


def test_price_signal_needs_ten_samples() -> None:
    assert price.compute(1_000_000, [2_500_000] * 9) is None


def test_price_signal_far_below_median_is_one_and_above_is_zero() -> None:
    samples = [
        2_400_000,
        2_500_000,
        2_600_000,
        2_500_000,
        2_700_000,
        2_300_000,
        2_500_000,
        2_600_000,
        2_400_000,
        2_500_000,
    ]

    low = price.compute(1_000_000, samples)
    high = price.compute(3_000_000, samples)

    assert low is not None and (low.signal, low.median_vnd, low.sample_size, low.rent_vnd) == (
        1.0,
        2_500_000,
        10,
        1_000_000,
    )
    assert high is not None and high.signal == 0.0


def test_price_signal_mad_floor_is_five_percent_of_median() -> None:
    # MAD 0 → MAD' = 125.000; 2.25M is 2 MAD' below → 2 / 2.5 = 0.8
    result = price.compute(2_250_000, [2_500_000] * 10)
    assert result is not None and math.isclose(result.signal, 0.8)


# ---- community (step 10) ------------------------------------------------------------------------

NOW = datetime(2026, 11, 22, tzinfo=UTC)


@pytest.mark.parametrize(
    ("reports", "ids", "source", "created", "expected"),
    [
        (1, 1, "quick_check", None, 0.39),
        (3, 2, "quick_check", None, 0.78),
        (0, 1, "quick_check", None, 0.0),
        (0, 0, "quick_check", None, None),
        (0, 0, "listing", NOW - timedelta(days=2), 0.2),
        (0, 0, "listing", NOW - timedelta(days=30), 0.0),
        (10, 1, "listing", NOW - timedelta(days=1), 1.0),
    ],
)
def test_community_signal(
    reports: int, ids: int, source: str, created: datetime | None, expected: float | None
) -> None:
    value = community_signal.compute(reports, ids, source, created, NOW)
    assert (round(value, 2) if value is not None else None) == expected


# ---- image (step 8) -----------------------------------------------------------------------------


@pytest.mark.parametrize("unsigned", [0, 1, 2**63 - 1, 2**63, 2**64 - 1])
def test_phash_signed_conversion_round_trips(unsigned: int) -> None:
    signed = image_signal.to_signed(unsigned)
    assert -(2**63) <= signed < 2**63
    assert image_signal.to_unsigned(signed) == unsigned


def test_phash_matches_resized_recompressed_copy_not_other_photo() -> None:
    original = photo(1)
    copy = jpeg(original.resize((400, 300)), quality=60)

    same = image_signal.hamming(image_signal.phash(jpeg(original)), image_signal.phash(copy))
    other = image_signal.hamming(image_signal.phash(jpeg(original)), image_signal.phash(jpeg(photo(2))))

    assert same <= image_signal.MAX_HAMMING < other


@pytest.mark.parametrize(
    ("source", "matches", "expected"),
    [
        ("quick_check", [cand()], 0.5),
        ("listing", [cand(poster=2)], 0.8),
        ("listing", [cand(poster=1)], 0.0),  # only the poster's own other listing
        ("listing", [cand(poster=1), cand("removed", poster=1, image_id=2)], 1.0),
        ("quick_check", [cand("hidden")], 1.0),
        ("quick_check", [], 0.0),
    ],
)
def test_image_signal_table(source: str, matches: list[Candidate], expected: float) -> None:
    assert image_signal.signal(matches, 1, source, poster_id=1) == expected


def test_image_signal_null_without_images() -> None:
    assert image_signal.signal([], 0, "quick_check", None) is None


def test_best_matches_keeps_real_matches_once_per_photo() -> None:
    candidates = [
        cand(ham=30, sim=0.5),
        cand(ham=30, sim=0.95, image_id=2),
        cand(ham=8, image_id=2),
        cand(ham=12, image_id=3),
    ]

    best = image_signal.best_matches(candidates)

    assert [(c.listing_image_id, c.hamming_distance) for c in best] == [(2, 8)]


# ---- DB integration ---------------------------------------------------------------------------------


class FakeClip:
    """Deterministic stand-in for CLIP: the same picture (by seed tag) gives the same unit vector."""

    def __init__(self, vectors: dict[bytes, list[float]]) -> None:
        self.vectors = vectors

    def embed(self, images: list[bytes]) -> list[list[float]]:
        return [self.vectors[b] for b in images]


def unit(i: int) -> list[float]:
    v = [0.0] * 512
    v[i] = 1.0
    return v


@pytest.fixture
def world(sql):  # noqa: ANN201
    """Ward, two posters, and a helper to add room listings with photos."""
    sql(
        "INSERT INTO app.administrative_units (code, name, unit_level, parent_code) VALUES "
        "('81', 'Tỉnh Ảnh', 'province', NULL), ('81001', 'Phường Ảnh', 'commune', '81') ON CONFLICT DO NOTHING"
    )

    def user(days_old: int = 30) -> int:
        ((uid,),) = sql(
            "INSERT INTO app.users (email, password_hash, full_name, created_at) VALUES (%s, '!', 'Thử', now() - make_interval(days => %s)) RETURNING id",
            (f"{uuid.uuid4().hex}@test.vn", days_old),
        )
        return uid

    def listing(
        poster: int,
        status: str = "verified",
        rent: int = 2_500_000,
        ward: str = "81001",
        photos: tuple[bytes, ...] = (),
    ) -> int:
        ((lid,),) = sql(
            """
            INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
            VALUES (%s, 'room', 'Phòng thử', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', %s, 'Số 1',
                    ST_SetSRID(ST_MakePoint(105.70, 21.00), 4326)::geography, %s::app.listing_status, now() + interval '30 days')
            RETURNING id
            """,
            (poster, ward, status),
        )
        sql(
            "INSERT INTO app.room_listings (listing_id, monthly_rent_vnd, area_m2, is_self_contained) VALUES (%s, %s, 20, true)",
            (lid, rent),
        )
        for i, blob in enumerate(photos, start=1):
            sql(
                """
                INSERT INTO app.listing_images (listing_id, object_key, sha256, width_px, height_px, sort_order)
                VALUES (%s, %s, sha256(%s), 800, 600, %s)
                """,
                (lid, f"t/{uuid.uuid4().hex}.jpg", blob, i),
            )
        return lid

    return {"user": user, "listing": listing}


def _object_store(sql, listing_id: int, photos: tuple[bytes, ...]) -> dict[str, bytes]:
    keys = sql(
        "SELECT object_key FROM app.listing_images WHERE listing_id = %s ORDER BY sort_order", (listing_id,)
    )
    return {key: blob for (key,), blob in zip(keys, photos, strict=True)}


def _listing_scan(sql, listing_id: int) -> dict:
    ((scan_id,),) = sql(
        "INSERT INTO app.scans (source, listing_id, input_text, input_sha256) VALUES ('listing', %s, 'Phòng thử', sha256(gen_random_uuid()::text::bytea)) RETURNING id",
        (listing_id,),
    )
    return {"id": scan_id, "source": "listing", "listing_id": listing_id}


def _clip_model_id(sql) -> int:
    ((mid,),) = sql("SELECT id FROM app.model_versions WHERE component = 'image_embedding' AND is_active")
    return mid


def test_listing_scan_matches_other_posters_photo_fills_hashes_and_records_match(
    worker_pool: ConnectionPool, sql, world
) -> None:
    room = jpeg(photo(11))
    other_poster, poster = world["user"](), world["user"]()
    other = world["listing"](other_poster, photos=(room,))
    sql("UPDATE app.listing_images SET phash = %s WHERE listing_id = %s", (image_signal.phash(room), other))
    mine = world["listing"](poster, status="screening", photos=(jpeg(photo(11).resize((400, 300)), 70),))
    store = _object_store(sql, mine, (jpeg(photo(11).resize((400, 300)), 70),))
    deps = signal_steps.SignalDeps(load_image=lambda _bucket, key: store[key])

    result = signal_steps.compute(
        worker_pool, _listing_scan(sql, mine), TextAnalysis("Phòng thử"), deps, None
    )

    assert result.image == 0.8
    assert [(m.listing_id, m.source_image_no) for m in result.matches] == [(other, 1)]
    assert sql("SELECT phash IS NOT NULL FROM app.listing_images WHERE listing_id = %s", (mine,)) == [(True,)]


def test_listing_scan_matching_only_own_listing_scores_zero_and_removed_listing_one(
    worker_pool: ConnectionPool, sql, world
) -> None:
    poster = world["user"]()
    room = jpeg(photo(12))
    own = world["listing"](poster, photos=(room,))
    sql("UPDATE app.listing_images SET phash = %s WHERE listing_id = %s", (image_signal.phash(room), own))
    mine = world["listing"](poster, status="screening", photos=(room,))
    store = _object_store(sql, mine, (room,))
    deps = signal_steps.SignalDeps(load_image=lambda _b, key: store[key])

    assert (
        signal_steps.compute(worker_pool, _listing_scan(sql, mine), TextAnalysis(""), deps, None).image == 0.0
    )

    removed = world["listing"](world["user"](), status="removed", photos=(room,))
    sql("UPDATE app.listing_images SET phash = %s WHERE listing_id = %s", (image_signal.phash(room), removed))
    assert (
        signal_steps.compute(worker_pool, _listing_scan(sql, mine), TextAnalysis(""), deps, None).image == 1.0
    )


def test_clip_finds_cropped_photo_only_with_the_same_clip_model(
    worker_pool: ConnectionPool, sql, world
) -> None:
    original = jpeg(photo(13))
    cropped = jpeg(crop20(photo(13)))
    assert (
        image_signal.hamming(image_signal.phash(original), image_signal.phash(cropped))
        > image_signal.MAX_HAMMING
    )

    running = _clip_model_id(sql)
    ((old_model,),) = sql(
        "INSERT INTO app.model_versions (component, name, version) VALUES ('image_embedding', 'old/clip', %s) RETURNING id",
        (uuid.uuid4().hex[:8],),
    )
    same_model = world["listing"](world["user"](), photos=(original,))
    other_model = world["listing"](world["user"](), photos=(original,))
    vector = image_signal.vector_literal(unit(5))
    sql(
        "UPDATE app.listing_images SET phash = %s, clip_embedding = %s::vector, clip_model_id = %s WHERE listing_id = %s",
        (image_signal.phash(original), vector, running, same_model),
    )
    sql(
        "UPDATE app.listing_images SET phash = %s, clip_embedding = %s::vector, clip_model_id = %s WHERE listing_id = %s",
        (image_signal.phash(original), vector, old_model, other_model),
    )
    ((scan_id,),) = sql(
        "INSERT INTO app.scans (source, input_text, input_sha256, expires_at) VALUES ('quick_check', 'x', sha256(gen_random_uuid()::text::bytea), now() + interval '30 days') RETURNING id"
    )
    deps = signal_steps.SignalDeps(load_image=lambda _b, _k: b"", clip=FakeClip({cropped: unit(5)}))
    scan = {"id": scan_id, "source": "quick_check", "listing_id": None}

    result = signal_steps.compute(worker_pool, scan, TextAnalysis("", images=[(1, cropped)]), deps, running)

    assert result.image == 0.5
    assert {m.listing_id for m in result.matches} == {same_model}
    assert result.matches[0].clip_similarity == pytest.approx(1.0)


def test_quick_check_price_compares_claimed_rent_with_rooms_in_ward(
    worker_pool: ConnectionPool, sql, world, new_scan
) -> None:
    ward = "81002"
    sql(
        "INSERT INTO app.administrative_units (code, name, unit_level, parent_code) VALUES (%s, 'Phường Giá', 'commune', '81') ON CONFLICT DO NOTHING",
        (ward,),
    )
    poster = world["user"]()
    for rent in [
        2_400_000,
        2_500_000,
        2_600_000,
        2_500_000,
        2_700_000,
        2_300_000,
        2_500_000,
        2_600_000,
        2_400_000,
        2_500_000,
    ]:
        world["listing"](poster, rent=rent, ward=ward)
    world["listing"](poster, status="removed", rent=100_000, ward=ward)  # not a comparable sample
    scan_id = new_scan()
    sql(
        "UPDATE app.scans SET claimed_rent_vnd = 1000000, claimed_ward_code = %s WHERE id = %s",
        (ward, scan_id),
    )
    deps = signal_steps.SignalDeps(load_image=lambda _b, _k: b"")

    result = signal_steps.compute(
        worker_pool,
        {"id": scan_id, "source": "quick_check", "listing_id": None},
        TextAnalysis(""),
        deps,
        None,
    )

    assert result.price is not None
    assert (
        result.price.signal,
        result.price.median_vnd,
        result.price.sample_size,
        result.price.rent_vnd,
    ) == (1.0, 2_500_000, 10, 1_000_000)
    assert result.image is None and result.community is None


def test_quick_check_community_sums_confirmed_reports(worker_pool: ConnectionPool, sql, new_scan) -> None:
    scan_id = new_scan()
    ((ident,),) = sql(
        "INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES ('phone', sha256(gen_random_uuid()::text::bytea), '•••321') RETURNING id"
    )
    sql(
        "INSERT INTO app.identifier_stats (identifier_id, confirmed_report_count, last_confirmed_at) VALUES (%s, 3, now())",
        (ident,),
    )
    sql("INSERT INTO app.scan_identifiers (scan_id, identifier_id) VALUES (%s, %s)", (scan_id, ident))
    deps = signal_steps.SignalDeps(load_image=lambda _b, _k: b"")

    result = signal_steps.compute(
        worker_pool,
        {"id": scan_id, "source": "quick_check", "listing_id": None},
        TextAnalysis(""),
        deps,
        None,
    )

    assert round(result.community, 2) == 0.78


# ---- real CLIP (CI job worker-ml) -----------------------------------------------------------------


@pytest.mark.ml
def test_real_clip_matches_20_percent_crop_but_not_other_photo() -> None:
    from troviec_worker.ml.clip import ClipEmbedder

    clip = ClipEmbedder()  # default Hugging Face cache (cached in CI)
    # The "other" picture must look different: two random mosaics are already ~0.96 apart for CLIP.
    base, cropped, other = clip.embed([jpeg(photo(21)), jpeg(crop20(photo(21))), jpeg(stripes())])

    def cosine(a: list[float], b: list[float]) -> float:
        return sum(x * y for x, y in zip(a, b, strict=True))

    assert len(base) == 512 and math.isclose(math.sqrt(cosine(base, base)), 1.0, rel_tol=1e-4)
    assert cosine(base, cropped) >= image_signal.MIN_COSINE
    assert cosine(base, other) < image_signal.MIN_COSINE
