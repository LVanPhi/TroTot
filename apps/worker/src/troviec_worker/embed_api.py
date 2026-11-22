"""POST /internal/embed (port 8001, internal Docker network only, X-Internal-Token; docs/05-ai-worker.md,
Embedding).  Answers 503 when the model is not installed or the running text_embedding version is not the
one this worker implements; the API then falls back to keyword search.
"""

import hmac
from collections.abc import Callable
from typing import Annotated

from fastapi import FastAPI, Header, HTTPException, status
from pydantic import BaseModel, Field, StringConstraints

from troviec_worker.ml.text_embed import DIM, MODEL_NAME, TextEmbedder

Text = Annotated[str, StringConstraints(min_length=1, max_length=2000)]


class EmbedRequest(BaseModel):
    texts: list[Text] = Field(min_length=1, max_length=16)


class EmbedResponse(BaseModel):
    model: str
    modelVersionId: int  # noqa: N815 - JSON contract in docs/05-ai-worker.md
    dim: int
    vectors: list[list[float]]


def create_app(
    internal_token: str,
    embedder: TextEmbedder | None = None,
    model_version_id: Callable[[], int | None] = lambda: None,
) -> FastAPI:
    app = FastAPI(title="troviec-worker-embed", docs_url=None, redoc_url=None, openapi_url=None)

    @app.post("/internal/embed")
    def embed(
        request: EmbedRequest,
        x_internal_token: Annotated[str, Header()] = "",
    ) -> EmbedResponse:
        if not internal_token or not hmac.compare_digest(x_internal_token, internal_token):
            raise HTTPException(status.HTTP_401_UNAUTHORIZED)
        version_id = model_version_id()
        if embedder is None or version_id is None:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "embedding model not available")
        return EmbedResponse(
            model=MODEL_NAME, modelVersionId=version_id, dim=DIM, vectors=embedder.embed(request.texts)
        )

    @app.get("/health/live")
    def live() -> dict[str, str]:
        return {"status": "ok"}

    return app
