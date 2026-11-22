"""Text embeddings for listings and search queries (docs/05-ai-worker.md, Embedding): BAAI/bge-m3 dense
vectors, 1024 dimensions, L2-normalised. sentence-transformers comes with the `ml` extra and is imported
lazily.
"""

import logging
import threading
from pathlib import Path
from typing import Protocol

log = logging.getLogger(__name__)

MODEL_NAME = "BAAI/bge-m3"
MODEL_VERSION = "1"
DIM = 1024


class TextEmbedder(Protocol):
    def embed(self, texts: list[str]) -> list[list[float]]: ...


class BgeM3Embedder:
    """Loads the model on first use (about 2 GB), then embeds on CPU."""

    def __init__(self, cache_dir: Path | None = None) -> None:
        self._cache_dir = cache_dir
        self._lock = threading.Lock()
        self._model = None

    def embed(self, texts: list[str]) -> list[list[float]]:
        with self._lock:
            if self._model is None:
                from sentence_transformers import SentenceTransformer  # noqa: PLC0415 - optional dependency

                self._model = SentenceTransformer(
                    MODEL_NAME, device="cpu", cache_folder=str(self._cache_dir) if self._cache_dir else None
                )
                log.info("text embedding model loaded")
            vectors = self._model.encode(texts, normalize_embeddings=True, convert_to_numpy=True)
        return [row.tolist() for row in vectors]


def available() -> bool:
    try:
        import sentence_transformers  # noqa: F401, PLC0415
    except ImportError:
        return False
    return True
