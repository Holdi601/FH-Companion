"""Shared carId -> codename lookup, backed by config/fh6_car_catalogue.json.

The catalogue is produced by dump_car_catalogue.py / update_car_catalogue.ps1
and accumulates across runs. This helper is the single place the extraction and
backfill code turns a numeric carId into its human-readable codename
(MAKE_model_year), so every export uses the same mapping.

A carId not yet in the catalogue returns "" -- honest blank rather than a guess.
Coverage grows as the catalogue is refreshed on more boards.
"""

from __future__ import annotations

import json
from pathlib import Path

_DEFAULT = Path(__file__).resolve().parent.parent / "config" / "fh6_car_catalogue.json"


def load_catalogue(path: Path | None = None) -> dict[int, str]:
    """carId -> codename, or {} if the catalogue file is absent/unreadable."""
    p = path or _DEFAULT
    if not p.exists():
        return {}
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
    except Exception:
        return {}
    return {int(k): v for k, v in data.get("by_car_id", {}).items()}


def codename_for(car_id: int, catalogue: dict[int, str]) -> str:
    return catalogue.get(int(car_id), "")
