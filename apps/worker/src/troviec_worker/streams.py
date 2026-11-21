"""Redis Streams queue contract (docs/05-ai-worker.md, Hợp đồng hàng đợi).

Each entry has two fields: `type` and `payload` (JSON). Streams are trimmed with MAXLEN ~ 10000.
"""

import json
import os
import socket
from typing import Any

import redis
from pydantic import BaseModel

SCAN_REQUESTED = "troviec:scan-requested"
LISTING_EMBED = "troviec:listing-embed"
SCAN_COMPLETED = "troviec:scan-completed"
DEAD_LETTER = "troviec:dead-letter"
GROUP = "ai-workers"
MAXLEN = 10_000


class Message(BaseModel):
    id: str
    type: str
    payload: dict[str, Any]
    deliveries: int = 1


def make_client(redis_url: str, block_ms: int) -> redis.Redis:
    """Socket timeout must outlast XREADGROUP BLOCK, or an idle read raises TimeoutError."""
    return redis.Redis.from_url(
        redis_url, decode_responses=True, socket_timeout=block_ms / 1000 + 5, socket_connect_timeout=5
    )


def consumer_name() -> str:
    return f"{socket.gethostname()}-{os.getpid()}"


def publish(client: redis.Redis, stream: str, event_type: str, payload: dict[str, Any]) -> str:
    return client.xadd(
        stream, {"type": event_type, "payload": json.dumps(payload)}, maxlen=MAXLEN, approximate=True
    )


class StreamConsumer:
    """One consumer of one stream inside the `ai-workers` group."""

    def __init__(self, client: redis.Redis, stream: str, consumer: str | None = None) -> None:
        self.client = client
        self.stream = stream
        self.consumer = consumer or consumer_name()

    def ensure_group(self) -> None:
        """Create the consumer group (and the stream) if missing; safe to call on every start."""
        try:
            self.client.xgroup_create(self.stream, GROUP, id="0", mkstream=True)
        except redis.ResponseError as error:
            if "BUSYGROUP" not in str(error):
                raise

    def read(self, block_ms: int) -> Message | None:
        """XREADGROUP COUNT 1: one job at a time per process."""
        response = self.client.xreadgroup(GROUP, self.consumer, {self.stream: ">"}, count=1, block=block_ms)
        if not response:
            return None
        _, entries = response[0]
        entry_id, fields = entries[0]
        return _to_message(entry_id, fields, deliveries=1)

    def reclaim(self, min_idle_ms: int) -> list[Message]:
        """XAUTOCLAIM entries stuck at a dead consumer; each comes back with its delivery count."""
        claimed: list[Message] = []
        cursor = "0-0"
        while True:
            cursor, entries, _deleted = self.client.xautoclaim(
                self.stream, GROUP, self.consumer, min_idle_time=min_idle_ms, start_id=cursor, count=10
            )
            for entry_id, fields in entries:
                claimed.append(_to_message(entry_id, fields, deliveries=self._deliveries(entry_id)))
            if cursor == "0-0":
                return claimed

    def ack(self, message_id: str) -> None:
        self.client.xack(self.stream, GROUP, message_id)

    def dead_letter(self, message: Message, error: str) -> None:
        publish(
            self.client,
            DEAD_LETTER,
            "dead_letter",
            {
                "stream": self.stream,
                "messageId": message.id,
                "payload": message.payload,
                "error": error,
                "deliveries": message.deliveries,
            },
        )

    def _deliveries(self, entry_id: str) -> int:
        pending = self.client.xpending_range(self.stream, GROUP, min=entry_id, max=entry_id, count=1)
        return int(pending[0]["times_delivered"]) if pending else 1


def _to_message(entry_id: str, fields: dict[str, str], deliveries: int) -> Message:
    return Message(
        id=entry_id,
        type=fields.get("type", ""),
        payload=json.loads(fields.get("payload", "{}")),
        deliveries=deliveries,
    )
