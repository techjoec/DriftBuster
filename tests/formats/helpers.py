from __future__ import annotations

from pathlib import Path
from typing import Iterable, Optional, Tuple

from driftbuster.core.detector import Detector


def run_detection_on_text(plugin_names: Iterable[str], text: str, *, path: str = "sample.txt"):
    """Run detector against a text blob using only the requested plugins.

    Returns a list of (path, match) for any detections.
    """
    det = Detector(enabled_plugins=tuple(plugin_names))
    tmp = Path(path)
    sample = text.encode("utf-8", errors="ignore")
    match = det._detect(tmp, sample, text)  # type: ignore[attr-defined]
    return match

