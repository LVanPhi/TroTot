"""Worker settings from environment variables (docs/08-deployment.md, .env.example section Worker)."""

from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


def find_repo_env(start: Path | None = None) -> Path | None:
    """Return the repo-root .env (folder holding AGENTS.md) if it exists; real env vars still win."""
    for folder in [(start or Path.cwd()).resolve(), *(start or Path.cwd()).resolve().parents]:
        if (folder / "AGENTS.md").exists():
            env = folder / ".env"
            return env if env.exists() else None
    return None


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=find_repo_env(), extra="ignore", env_file_encoding="utf-8")

    database_url: str
    redis_url: str

    s3_endpoint: str = "http://localhost:3900"
    s3_region: str = "garage"
    s3_access_key: str = ""
    s3_secret_key: str = ""
    s3_bucket_scan_images: str = "scan-images"
    s3_bucket_listing_images: str = "listing-images"

    identifier_hmac_key: str = ""
    llm_base_url: str = ""
    llm_api_key: str = ""
    llm_model: str = ""
    llm_timeout_seconds: float = 7
    llm_daily_call_limit: int = 800
    llm_fake: bool = False

    internal_api_token: str = ""
    model_dir: Path = Path("./.models")
    worker_fake_pipeline: bool = False
    log_level: str = "INFO"

    # Queue tuning (docs/05-ai-worker.md, Hợp đồng hàng đợi); not meant to change per environment.
    read_block_ms: int = Field(default=5000, ge=100)
    reclaim_every_seconds: float = Field(default=60, gt=0)
    reclaim_min_idle_ms: int = Field(default=120_000, ge=0)
    max_deliveries: int = Field(default=3, ge=1)
