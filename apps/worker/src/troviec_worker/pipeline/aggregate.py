"""Step 11: combine signals into a 0-100 score and a risk level (docs/05-ai-worker.md, agg-v0 noisy-OR)."""

import json
import math
from dataclasses import dataclass
from pathlib import Path

MODELS_DIR = Path(__file__).resolve().parents[3] / "models" / "aggregator"
SIGNALS = ("text", "image", "price", "community")


@dataclass(frozen=True)
class AggregatorConfig:
    version: str
    max_contribution: dict[str, float]
    flag_bonus_per_extra_flag: float
    flag_bonus_cap: float
    injection_floor: float
    caution: int
    high: int


@dataclass(frozen=True)
class AggregateResult:
    score: int
    risk_level: str


def load(version: str, models_dir: Path = MODELS_DIR) -> AggregatorConfig:
    """models/aggregator/<version>.json; a missing file means worker.model_unavailable for the scan."""
    data = json.loads((models_dir / f"{version}.json").read_text(encoding="utf-8"))
    if data.get("type") != "noisy_or":
        raise ValueError(f"unsupported aggregator type {data.get('type')!r}")
    return AggregatorConfig(
        version=data["version"],
        max_contribution=data["maxContribution"],
        flag_bonus_per_extra_flag=data["flagBonusPerExtraFlag"],
        flag_bonus_cap=data["flagBonusCap"],
        injection_floor=data["injectionFloor"],
        caution=data["thresholds"]["caution"],
        high=data["thresholds"]["high"],
    )


def score(
    config: AggregatorConfig, signals: dict[str, float | None], flag_count: int, has_injection: bool
) -> AggregateResult:
    """risk = 1 − Π(1 − max_k × s_k) over non-null signals, + flag bonus, injection floor;
    score = floor(100 × min(1, risk) + 0.5) — half up, never Python's banker's round()."""
    keep = 1.0
    for name in SIGNALS:
        value = signals.get(name)
        if value is not None:
            keep *= 1 - config.max_contribution[name] * value
    risk = 1 - keep
    risk += min(config.flag_bonus_cap, config.flag_bonus_per_extra_flag * max(0, flag_count - 1))
    if has_injection:
        risk = max(risk, config.injection_floor)
    # round(…, 9) first: 100 × 0.765 is 76.49999… in binary floating point and must still give 77.
    points = math.floor(round(100 * min(1.0, risk), 9) + 0.5)
    level = "high" if points >= config.high else "caution" if points >= config.caution else "low"
    return AggregateResult(points, level)
