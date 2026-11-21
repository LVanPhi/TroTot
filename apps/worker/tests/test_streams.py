import redis

from troviec_worker.streams import GROUP, SCAN_REQUESTED, StreamConsumer, make_client, publish


def test_ensure_group_called_twice_does_not_fail(redis_client: redis.Redis) -> None:
    consumer = StreamConsumer(redis_client, SCAN_REQUESTED, "c1")

    consumer.ensure_group()
    consumer.ensure_group()

    groups = redis_client.xinfo_groups(SCAN_REQUESTED)
    assert [g["name"] for g in groups] == [GROUP]


def test_read_then_ack_leaves_nothing_pending(redis_client: redis.Redis) -> None:
    consumer = StreamConsumer(redis_client, SCAN_REQUESTED, "c1")
    consumer.ensure_group()
    entry_id = publish(redis_client, SCAN_REQUESTED, "scan.requested", {"scanId": 7})

    message = consumer.read(block_ms=1000)

    assert message is not None
    assert (message.id, message.type, message.payload, message.deliveries) == (
        entry_id,
        "scan.requested",
        {"scanId": 7},
        1,
    )
    consumer.ack(message.id)
    assert redis_client.xpending(SCAN_REQUESTED, GROUP)["pending"] == 0


def test_read_empty_stream_returns_none(redis_client: redis.Redis) -> None:
    consumer = StreamConsumer(redis_client, SCAN_REQUESTED, "c1")
    consumer.ensure_group()

    assert consumer.read(block_ms=100) is None


def test_reclaim_idle_entry_moves_it_with_delivery_count(redis_client: redis.Redis) -> None:
    dead = StreamConsumer(redis_client, SCAN_REQUESTED, "dead")
    alive = StreamConsumer(redis_client, SCAN_REQUESTED, "alive")
    dead.ensure_group()
    publish(redis_client, SCAN_REQUESTED, "scan.requested", {"scanId": 8})
    assert dead.read(block_ms=1000) is not None  # delivered, never acked

    claimed = alive.reclaim(min_idle_ms=0)

    assert [(m.payload["scanId"], m.deliveries) for m in claimed] == [(8, 2)]
    consumers = {c["name"]: c["pending"] for c in redis_client.xinfo_consumers(SCAN_REQUESTED, GROUP)}
    assert consumers["alive"] == 1


def test_reclaim_not_idle_long_enough_returns_nothing(redis_client: redis.Redis) -> None:
    dead = StreamConsumer(redis_client, SCAN_REQUESTED, "dead")
    dead.ensure_group()
    publish(redis_client, SCAN_REQUESTED, "scan.requested", {"scanId": 9})
    dead.read(block_ms=1000)

    assert StreamConsumer(redis_client, SCAN_REQUESTED, "alive").reclaim(min_idle_ms=120_000) == []


def test_make_client_idle_read_longer_than_default_timeout_returns_none(redis_url: str) -> None:
    client = make_client(redis_url, block_ms=6000)
    consumer = StreamConsumer(client, SCAN_REQUESTED, "c1")
    consumer.ensure_group()

    assert consumer.read(block_ms=6000) is None
    client.close()
