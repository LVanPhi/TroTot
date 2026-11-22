"""Embedding (docs/05-ai-worker.md, Embedding): listing text, listing.embed upsert, backfill, /internal/embed."""

import uuid

import pytest
from fastapi.testclient import TestClient
from psycopg_pool import ConnectionPool

from troviec_worker.embed_api import create_app
from troviec_worker.jobs import embed_job
from troviec_worker.registry import Registry
from troviec_worker.streams import LISTING_EMBED, StreamConsumer, publish


class FakeEmbedder:
    def __init__(self) -> None:
        self.texts: list[str] = []

    def embed(self, texts: list[str]) -> list[list[float]]:
        self.texts += texts
        return [[1.0] + [0.0] * 1023 for _ in texts]


def test_listing_text_room_has_summary_sentence() -> None:
    row = {
        "listing_type": "room",
        "title": "Phòng gần trường",
        "description": "Phòng sạch",
        "monthly_rent_vnd": 2_500_000,
        "area_m2": 20.0,
        "is_self_contained": True,
        "amenities": ["Wifi", "Điều hòa"],
    }
    assert embed_job.listing_text(row) == (
        "Phòng gần trường\nPhòng sạch\nPhòng khép kín 20 m², 2.500.000 đồng/tháng, tiện nghi: wifi, điều hòa."
    )


def test_listing_text_job() -> None:
    row = {
        "listing_type": "job",
        "title": "Phục vụ",
        "description": "Ca tối",
        "employer_name": "Quán A",
        "pay_vnd": 25_000,
        "pay_unit": "hour",
        "hours_per_week": 20,
    }
    assert embed_job.listing_text(row).endswith("Việc làm thêm tại Quán A, 25.000 đồng/giờ, 20 giờ/tuần.")


@pytest.fixture
def listing(sql) -> int:  # noqa: ANN001
    sql(
        "INSERT INTO app.administrative_units (code, name, unit_level, parent_code) VALUES "
        "('82', 'Tỉnh E', 'province', NULL), ('82001', 'Phường E', 'commune', '82') ON CONFLICT DO NOTHING"
    )
    ((user,),) = sql(
        "INSERT INTO app.users (email, password_hash, full_name) VALUES (%s, '!', 'E') RETURNING id",
        (f"{uuid.uuid4().hex}@test.vn",),
    )
    ((lid,),) = sql(
        """
        INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location,
                                  status, expires_at)
        VALUES (%s, 'room', 'Phòng gần trường', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '82001', 'Số 1',
                ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, 'verified', now() + interval '30 days')
        RETURNING id
        """,
        (user,),
    )
    sql(
        "INSERT INTO app.room_listings (listing_id, monthly_rent_vnd, area_m2, is_self_contained) VALUES (%s, 2500000, 20, true)",
        (lid,),
    )
    sql(
        "INSERT INTO app.room_amenities (listing_id, amenity_id) SELECT %s, id FROM app.amenities WHERE code = 'wifi'",
        (lid,),
    )
    return lid


def test_listing_embed_job_upserts_vector_with_running_model_version(
    redis_client,
    worker_pool: ConnectionPool,
    sql,
    listing: int,  # noqa: ANN001
) -> None:
    embedder = FakeEmbedder()
    consumer = StreamConsumer(redis_client, LISTING_EMBED, "embed-1")
    consumer.ensure_group()
    publish(redis_client, LISTING_EMBED, "listing.embed", {"listingId": listing})
    publish(
        redis_client, LISTING_EMBED, "listing.embed", {"listingId": listing}
    )  # re-approval: upsert, no error

    for _ in range(2):
        embed_job.handle(consumer.read(1000), consumer, worker_pool, Registry(worker_pool), embedder)

    ((model,),) = sql("SELECT id FROM app.model_versions WHERE component = 'text_embedding' AND is_active")
    assert sql("SELECT model_version_id FROM app.listing_embeddings WHERE listing_id = %s", (listing,)) == [
        (model,)
    ]
    assert "tiện nghi: wifi" in embedder.texts[0]
    assert int(redis_client.xpending(LISTING_EMBED, "ai-workers")["pending"]) == 0


def test_listing_embed_without_model_acks_and_writes_nothing(
    redis_client,
    worker_pool: ConnectionPool,
    sql,
    listing: int,  # noqa: ANN001
) -> None:
    consumer = StreamConsumer(redis_client, LISTING_EMBED, "embed-2")
    consumer.ensure_group()
    publish(redis_client, LISTING_EMBED, "listing.embed", {"listingId": listing})

    embed_job.handle(consumer.read(1000), consumer, worker_pool, Registry(worker_pool), None)

    assert sql("SELECT count(*) FROM app.listing_embeddings WHERE listing_id = %s", (listing,)) == [(0,)]
    assert int(redis_client.xpending(LISTING_EMBED, "ai-workers")["pending"]) == 0


def test_backfill_embeds_listings_missing_a_vector(worker_pool: ConnectionPool, sql, listing: int) -> None:  # noqa: ANN001
    count = embed_job.backfill(worker_pool, FakeEmbedder(), Registry(worker_pool))

    assert count >= 1
    assert sql("SELECT count(*) FROM app.listing_embeddings WHERE listing_id = %s", (listing,)) == [(1,)]
    assert embed_job.backfill(worker_pool, FakeEmbedder(), Registry(worker_pool)) == 0


def test_embed_api_returns_vectors_and_model_version() -> None:
    client = TestClient(create_app("secret-token", FakeEmbedder(), lambda: 3))

    response = client.post(
        "/internal/embed", json={"texts": ["phòng khép kín"]}, headers={"X-Internal-Token": "secret-token"}
    )

    assert response.status_code == 200
    body = response.json()
    assert (body["model"], body["modelVersionId"], body["dim"], len(body["vectors"][0])) == (
        "BAAI/bge-m3",
        3,
        1024,
        1024,
    )


def test_embed_api_other_model_version_running_is_503() -> None:
    client = TestClient(create_app("secret-token", FakeEmbedder(), lambda: None))

    response = client.post(
        "/internal/embed", json={"texts": ["a"]}, headers={"X-Internal-Token": "secret-token"}
    )

    assert response.status_code == 503
