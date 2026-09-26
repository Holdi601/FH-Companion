from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import pandas as pd


CRITICAL_FIELDS = ["gamertag", "car", "car_class", "pi", "drivetrain", "lap_time", "gear"]
TEXT_COLUMNS = [
    "game",
    "event_type",
    "rivals_mode",
    "track",
    "performance_class",
    "pi_class",
    "gamertag",
    "car",
    "car_class",
    "class_pi",
    "drivetrain",
    "lap_time",
    "gear",
    "raw_text",
    "source_file",
    "image_file",
    "extracted_at",
    "row_key",
    "chunk_csv",
]
INTEGER_COLUMNS = ["rank", "visible_row_index", "source_page_index", "pi"]
FLOAT_COLUMNS = ["confidence", "timestamp_seconds"]
BOOLEAN_COLUMNS = ["abs", "tcs", "stm", "is_dirty"]


def clean_text(value: Any) -> str:
    if value is None:
        return ""
    if pd.isna(value):
        return ""
    return " ".join(str(value).split())


def read_chunk_csvs(input_root: Path) -> pd.DataFrame:
    paths = sorted(input_root.rglob("leaderboard_entries.csv"))
    frames: list[pd.DataFrame] = []
    for path in paths:
        if "combined" in path.parts:
            continue
        frame = pd.read_csv(path, keep_default_na=False)
        frame["chunk_csv"] = str(path)
        frames.append(frame)
    if not frames:
        return pd.DataFrame()
    return pd.concat(frames, ignore_index=True)


def row_quality(row: pd.Series) -> float:
    score = float(row.get("confidence") or 0)
    for field in CRITICAL_FIELDS:
        if clean_text(row.get(field)):
            score += 0.02
    gamertag = clean_text(row.get("gamertag"))
    score += min(sum(1 for char in gamertag if char.isalnum()), 12) * 0.005
    return round(score, 4)


def dedupe_rows(frame: pd.DataFrame) -> pd.DataFrame:
    if frame.empty:
        return frame

    frame = frame.copy()
    if "rank" in frame.columns:
        frame["rank"] = pd.to_numeric(frame["rank"], errors="coerce").astype("Int64")
    frame["_quality"] = frame.apply(row_quality, axis=1)

    key_columns = ["game", "event_type", "rivals_mode", "track", "performance_class", "pi_class", "rank"]
    for column in key_columns:
        if column not in frame.columns:
            frame[column] = ""

    ranked = frame[frame["rank"].notna()].sort_values(
        by=["_quality", "extracted_at"],
        ascending=[False, False],
        na_position="last",
    )
    ranked = ranked.drop_duplicates(subset=key_columns, keep="first")

    unranked = frame[frame["rank"].isna()].sort_values(
        by=["_quality", "extracted_at"],
        ascending=[False, False],
        na_position="last",
    )
    fallback_columns = ["game", "event_type", "rivals_mode", "track", "performance_class", "pi_class", "gamertag", "car", "lap_time"]
    for column in fallback_columns:
        if column not in unranked.columns:
            unranked[column] = ""
    unranked = unranked.drop_duplicates(subset=fallback_columns, keep="first")

    merged = pd.concat([ranked, unranked], ignore_index=True)
    merged = merged.sort_values(
        by=["rank", "lap_time", "gamertag"],
        ascending=[True, True, True],
        na_position="last",
    )
    return merged.drop(columns=["_quality"], errors="ignore").reset_index(drop=True)


def normalize_bool(value: Any) -> bool | None:
    if value is None or pd.isna(value):
        return None
    text = str(value).strip().lower()
    if text in {"true", "1", "yes", "y"}:
        return True
    if text in {"false", "0", "no", "n"}:
        return False
    return None


def normalize_for_output(frame: pd.DataFrame) -> pd.DataFrame:
    normalized = frame.copy()
    for column in TEXT_COLUMNS:
        if column in normalized.columns:
            normalized[column] = normalized[column].map(clean_text).astype("string")
    for column in INTEGER_COLUMNS:
        if column in normalized.columns:
            normalized[column] = pd.to_numeric(normalized[column], errors="coerce").astype("Int64")
    for column in FLOAT_COLUMNS:
        if column in normalized.columns:
            normalized[column] = pd.to_numeric(normalized[column], errors="coerce")
    for column in BOOLEAN_COLUMNS:
        if column in normalized.columns:
            normalized[column] = normalized[column].map(normalize_bool).astype("boolean")
    return normalized


def is_complete_row(row: pd.Series) -> bool:
    if pd.isna(row.get("rank")):
        return False
    return all(clean_text(row.get(field)) for field in CRITICAL_FIELDS)


def write_outputs(frame: pd.DataFrame, output_dir: Path, parquet_path: Path | None, total_ranks: int | None, max_scanned_rank: int | None) -> dict[str, Any]:
    output_dir.mkdir(parents=True, exist_ok=True)
    csv_path = output_dir / "leaderboard_entries.csv"
    json_path = output_dir / "leaderboard_entries.json"
    local_parquet_path = output_dir / "leaderboard_entries.parquet"

    frame = normalize_for_output(frame)
    frame.to_csv(csv_path, index=False, encoding="utf-8")
    frame.to_json(json_path, orient="records", indent=2, force_ascii=False)
    frame.to_parquet(local_parquet_path, engine="pyarrow", index=False)
    if parquet_path:
        parquet_path.parent.mkdir(parents=True, exist_ok=True)
        frame.to_parquet(parquet_path, engine="pyarrow", index=False)

    ranked = frame[frame["rank"].notna()].copy() if "rank" in frame.columns else pd.DataFrame()
    if not ranked.empty:
        ranked["rank"] = pd.to_numeric(ranked["rank"], errors="coerce").astype("Int64")
    complete = ranked[ranked.apply(is_complete_row, axis=1)] if not ranked.empty else ranked
    complete_ranks = sorted(int(rank) for rank in complete["rank"].dropna().unique()) if not complete.empty else []

    if max_scanned_rank is None:
        max_scanned_rank = max(complete_ranks) if complete_ranks else 0
    coverage_end = max_scanned_rank
    if total_ranks is not None:
        coverage_end = min(total_ranks, coverage_end)

    expected = set(range(1, coverage_end + 1)) if coverage_end > 0 else set()
    present_complete = set(complete_ranks)
    missing_ranks = sorted(expected - present_complete)

    incomplete_ranks: list[int] = []
    missing_critical_by_rank: dict[str, list[str]] = {}
    for _index, row in ranked.iterrows():
        rank_value = row.get("rank")
        if pd.isna(rank_value):
            continue
        rank = int(rank_value)
        missing_fields = [field for field in CRITICAL_FIELDS if not clean_text(row.get(field))]
        if missing_fields:
            incomplete_ranks.append(rank)
            missing_critical_by_rank[str(rank)] = missing_fields

    duplicate_ranks: dict[str, int] = {}
    if not ranked.empty:
        counts = ranked["rank"].value_counts()
        duplicate_ranks = {str(int(rank)): int(count) for rank, count in counts.items() if count > 1}

    duplicate_content_across_ranks: dict[str, list[int]] = {}
    duplicate_content_ranks: set[int] = set()
    if not ranked.empty:
        content_groups: dict[str, set[int]] = {}
        for _index, row in ranked.iterrows():
            rank_value = row.get("rank")
            if pd.isna(rank_value):
                continue
            signature = "|".join(
                [
                    clean_text(row.get("gamertag")).lower(),
                    clean_text(row.get("car")).lower(),
                    clean_text(row.get("lap_time")),
                ]
            )
            if not signature.strip("|"):
                continue
            content_groups.setdefault(signature, set()).add(int(rank_value))
        duplicate_content_across_ranks = {
            signature: sorted(ranks)
            for signature, ranks in content_groups.items()
            if len(ranks) > 1
        }
        for ranks in duplicate_content_across_ranks.values():
            duplicate_content_ranks.update(ranks)

    report = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "row_count": int(len(frame)),
        "complete_rank_count": int(len(complete_ranks)),
        "total_ranks": total_ranks,
        "max_scanned_rank": max_scanned_rank,
        "coverage_end": coverage_end,
        "missing_ranks": missing_ranks,
        "incomplete_ranks": sorted(set(incomplete_ranks)),
        "missing_critical_by_rank": missing_critical_by_rank,
        "duplicate_ranks": duplicate_ranks,
        "duplicate_content_across_ranks": duplicate_content_across_ranks,
        "duplicate_content_ranks": sorted(duplicate_content_ranks),
        "csv_path": str(csv_path),
        "parquet_path": str(parquet_path or local_parquet_path),
    }
    with (output_dir / "rank_coverage_report.json").open("w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2, ensure_ascii=False)
    return report


def parse_optional_int(value: str) -> int | None:
    text = str(value or "").strip()
    if not text or text.lower() in {"none", "auto", "null"}:
        return None
    parsed = int(text)
    return parsed if parsed > 0 else None


def main() -> int:
    parser = argparse.ArgumentParser(description="Merge chunked Forza rank scan extractor outputs and write coverage report.")
    parser.add_argument("--input-root", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--parquet-path", type=Path, default=None)
    parser.add_argument("--total-ranks", default="")
    parser.add_argument("--max-scanned-rank", default="")
    args = parser.parse_args()

    input_root = args.input_root.resolve()
    output_dir = args.output_dir.resolve()
    parquet_path = args.parquet_path.resolve() if args.parquet_path else None
    total_ranks = parse_optional_int(args.total_ranks)
    max_scanned_rank = parse_optional_int(args.max_scanned_rank)

    frame = dedupe_rows(read_chunk_csvs(input_root))
    report = write_outputs(frame, output_dir, parquet_path, total_ranks, max_scanned_rank)
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
