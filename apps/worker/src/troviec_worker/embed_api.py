"""POST /internal/embed (port 8001, internal Docker network only, X-Internal-Token).

BGE-M3 is loaded in a later task; until then the endpoint validates the request and answers 503,
which the API treats as "fall back to keyword search".
"""

import hmac
from typing import Annotated

from fastapi import FastAPI, Header, HTTPException, status
from pydantic import BaseModel, Field, StringConstraints

Text = Annotated[str, StringConstraints(min_length=1, max_length=2000)]


class EmbedRequest(BaseModel):
    texts: list[Text] = Field(min_length=1, max_length=16)


class EmbedResponse(BaseModel):
    model: str
    modelVersionId: int  # noqa: N815 - JSON contract in docs/05-ai-worker.md
    dim: int
    vectors: list[list[float]]


def create_app(internal_token: str) -> FastAPI:
    app = FastAPI(title="troviec-worker-embed", docs_url=None, redoc_url=None, openapi_url=None)

    @app.post("/internal/embed")
    def embed(
        request: EmbedRequest,
        x_internal_token: Annotated[str, Header()] = "",
    ) -> EmbedResponse:
        if not internal_token or not hmac.compare_digest(x_internal_token, internal_token):
            raise HTTPException(status.HTTP_401_UNAUTHORIZED)
        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "embedding model not loaded")

    @app.get("/health/live")
    def live() -> dict[str, str]:
        return {"status": "ok"}

    return app
