"""CLIP image embeddings (docs/05-ai-worker.md, step 8): open_clip ViT-B-32 / laion2b_s34b_b79k,
L2-normalised, 512 dimensions. torch and open_clip come with the `ml` extra and are imported lazily, so
the rest of the worker (and its tests) run without them. Weights are cached in MODEL_DIR.
"""

import io
import logging
import math
import threading
from pathlib import Path
from typing import Protocol

log = logging.getLogger(__name__)

MODEL_NAME = "open_clip/ViT-B-32"
PRETRAINED = "laion2b_s34b_b79k"
DIM = 512


class ImageEmbedder(Protocol):
    def embed(self, images: list[bytes]) -> list[list[float]]: ...


def l2_normalize(vector: list[float]) -> list[float]:
    norm = math.sqrt(sum(v * v for v in vector))
    return [v / norm for v in vector] if norm > 0 else vector


class ClipEmbedder:
    """Loads the model on first use (a few seconds), then embeds on CPU."""

    def __init__(self, cache_dir: Path | None = None) -> None:
        """cache_dir: MODEL_DIR in production; None uses the Hugging Face default cache."""
        self._cache_dir = cache_dir
        self._lock = threading.Lock()
        self._model = None
        self._preprocess = None

    def _load(self) -> None:
        import open_clip  # noqa: PLC0415 - heavy optional dependency
        import torch  # noqa: PLC0415

        torch.set_num_threads(2)
        model, _, preprocess = open_clip.create_model_and_transforms(
            "ViT-B-32", pretrained=PRETRAINED, cache_dir=str(self._cache_dir) if self._cache_dir else None
        )
        model.eval()
        self._model, self._preprocess = model, preprocess
        log.info("clip model loaded")

    def embed(self, images: list[bytes]) -> list[list[float]]:
        import torch  # noqa: PLC0415
        from PIL import Image  # noqa: PLC0415

        with self._lock:
            if self._model is None:
                self._load()
        batch = torch.stack(
            [self._preprocess(Image.open(io.BytesIO(b)).convert("RGB")) for b in images]  # type: ignore[misc]
        )
        with torch.no_grad():
            features = self._model.encode_image(batch)  # type: ignore[union-attr]
            features = features / features.norm(dim=-1, keepdim=True)
        return [row.tolist() for row in features]


def available() -> bool:
    """True when the ml extra is installed."""
    try:
        import open_clip  # noqa: F401, PLC0415
    except ImportError:
        return False
    return True
