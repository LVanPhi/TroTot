"""Per-scan time budget (docs/05-ai-worker.md, step 1): 8 s from when the worker takes the job."""

import time
from collections.abc import Callable

SCAN_BUDGET_SECONDS = 8.0


class Budget:
    def __init__(
        self, seconds: float = SCAN_BUDGET_SECONDS, clock: Callable[[], float] = time.monotonic
    ) -> None:
        self._clock = clock
        self._deadline = clock() + seconds

    def remaining(self) -> float:
        return max(0.0, self._deadline - self._clock())

    def ocr_timeout(self) -> float:
        """min(10 s, remaining - 3 s): keeps 3 s for the later steps."""
        return max(0.0, min(10.0, self.remaining() - 3.0))

    def llm_timeout(self) -> float:
        """min(7 s, remaining - 0.5 s)."""
        return max(0.0, min(7.0, self.remaining() - 0.5))
