from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.util
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import cv2


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PROFILE = ROOT / "config" / "fh6_rivals_1080p.json"
DEFAULT_OUTPUT_DIR = ROOT / "data" / "processed"
DEFAULT_FRAMES_DIR = ROOT / "data" / "frames"
OCR_SCRIPT = ROOT / "scripts" / "windows_ocr.ps1"
OCR_BATCH_SCRIPT = ROOT / "scripts" / "windows_ocr_batch.ps1"
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"}
VIDEO_EXTENSIONS = {".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"}
PERFORMANCE_CLASSES = ("D", "C", "B", "A", "S1", "S2", "R", "X")
INVALID_JSON_CONTROL_CHARS = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
NVIDIA_DLL_HANDLES: list[Any] = []


@dataclass(frozen=True)
class SourceImage:
    path: Path
    source: Path
    timestamp_seconds: float | None = None


@dataclass(frozen=True)
class PreparedImage:
    source_image: SourceImage
    ocr_path: Path


def positive_int_env(name: str) -> int | None:
    value = os.environ.get(name, "").strip()
    if not value:
        return None
    try:
        parsed = int(value)
    except ValueError:
        return None
    return parsed if parsed > 0 else None


def configure_native_thread_budget() -> int | None:
    threads = positive_int_env("FORZA_OCR_THREADS_PER_PROCESS")
    if not threads:
        return None
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
        os.environ.setdefault(name, str(threads))
    try:
        cv2.setNumThreads(threads)
    except cv2.error:
        pass
    return threads


def load_profile(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def normalized_output_path(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path


def collect_inputs(input_path: Path, frames_dir: Path, frame_every: float, include_glob: str = "") -> list[SourceImage]:
    if input_path.is_dir():
        items: list[SourceImage] = []
        for child in sorted(input_path.iterdir()):
            if include_glob and not child.match(include_glob):
                continue
            if child.suffix.lower() in IMAGE_EXTENSIONS:
                items.append(SourceImage(path=child, source=child))
            elif child.suffix.lower() in VIDEO_EXTENSIONS:
                items.extend(extract_video_frames(child, frames_dir, frame_every))
        return items

    if input_path.suffix.lower() in IMAGE_EXTENSIONS:
        return [SourceImage(path=input_path, source=input_path)]

    if input_path.suffix.lower() in VIDEO_EXTENSIONS:
        return extract_video_frames(input_path, frames_dir, frame_every)

    raise ValueError(f"Unsupported input: {input_path}")


def extract_video_frames(video_path: Path, frames_dir: Path, frame_every: float) -> list[SourceImage]:
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise RuntimeError("ffmpeg is required for video input, but it was not found on PATH.")

    frames_dir.mkdir(parents=True, exist_ok=True)
    safe_stem = re.sub(r"[^A-Za-z0-9_.-]+", "_", video_path.stem)
    output_pattern = frames_dir / f"{safe_stem}_%06d.png"
    fps = 1 / frame_every

    subprocess.run(
        [
            ffmpeg,
            "-y",
            "-i",
            str(video_path),
            "-vf",
            f"fps={fps:.6f}",
            str(output_pattern),
        ],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    frames = sorted(frames_dir.glob(f"{safe_stem}_*.png"))
    return [
        SourceImage(path=frame, source=video_path, timestamp_seconds=index * frame_every)
        for index, frame in enumerate(frames)
    ]


def crop_for_ocr(image_path: Path, profile: dict[str, Any], tmp_dir: Path) -> tuple[Path, dict[str, int]]:
    image = cv2.imread(str(image_path))
    if image is None:
        raise RuntimeError(f"Could not read image: {image_path}")

    height, width = image.shape[:2]
    crop = profile.get("leaderboard_crop", {})
    left = int(width * float(crop.get("left", 0)))
    top = int(height * float(crop.get("top", 0)))
    right = int(width * float(crop.get("right", 1)))
    bottom = int(height * float(crop.get("bottom", 1)))

    left = max(0, min(left, width - 1))
    top = max(0, min(top, height - 1))
    right = max(left + 1, min(right, width))
    bottom = max(top + 1, min(bottom, height))

    cropped = image[top:bottom, left:right]
    cropped = preprocess(cropped)

    digest = hashlib.sha1(str(image_path).encode("utf-8")).hexdigest()[:12]
    out_path = tmp_dir / f"{image_path.stem}_{digest}_crop.png"
    cv2.imwrite(str(out_path), cropped)
    return out_path, {"left": left, "top": top, "right": right, "bottom": bottom}


def preprocess(image: Any) -> Any:
    scale = 2
    enlarged = cv2.resize(image, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    gray = cv2.cvtColor(enlarged, cv2.COLOR_BGR2GRAY)
    denoised = cv2.fastNlMeansDenoising(gray, h=6)
    sharpened = cv2.addWeighted(denoised, 1.45, cv2.GaussianBlur(denoised, (0, 0), 1.2), -0.45, 0)
    return cv2.cvtColor(sharpened, cv2.COLOR_GRAY2BGR)


def ocr_path_key(path: Path | str) -> str:
    return str(Path(path).resolve()).casefold()


def load_ocr_json(text: str, context: str) -> dict[str, Any]:
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        sanitized = INVALID_JSON_CONTROL_CHARS.sub(" ", text)
        try:
            return json.loads(sanitized)
        except json.JSONDecodeError:
            preview = text[:500].replace("\r", "\\r").replace("\n", "\\n")
            raise RuntimeError(f"Windows OCR returned invalid JSON for {context}: {exc}. Preview: {preview}") from exc


def run_windows_ocr(image_path: Path, language: str) -> dict[str, Any]:
    command = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(OCR_SCRIPT),
        "-ImagePath",
        str(image_path),
        "-Language",
        language,
    ]
    try:
        completed = subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as exc:
        stdout = (exc.stdout or b"").decode("utf-8-sig", errors="replace")
        stderr = (exc.stderr or b"").decode("utf-8-sig", errors="replace")
        details = (stderr or stdout).strip()
        raise RuntimeError(f"Windows OCR failed for {image_path}:\n{details}") from exc

    stdout = (completed.stdout or b"").decode("utf-8-sig", errors="replace").strip()
    if not stdout:
        stderr = (completed.stderr or b"").decode("utf-8-sig", errors="replace").strip()
        raise RuntimeError(f"Windows OCR returned no JSON for {image_path}.\n{stderr}")
    return load_ocr_json(stdout, str(image_path))


def run_windows_ocr_batch(image_paths: list[Path], language: str) -> dict[str, dict[str, Any]]:
    if not image_paths:
        return {}

    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as handle:
        list_path = Path(handle.name)
        for image_path in image_paths:
            handle.write(str(image_path.resolve()))
            handle.write("\n")

    command = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(OCR_BATCH_SCRIPT),
        "-ImageListPath",
        str(list_path),
        "-Language",
        language,
    ]

    try:
        completed = subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as exc:
        stdout = (exc.stdout or b"").decode("utf-8-sig", errors="replace")
        stderr = (exc.stderr or b"").decode("utf-8-sig", errors="replace")
        details = (stderr or stdout).strip()
        print(f"[forza-extract] batch OCR failed, falling back to single-image OCR:\n{details}", file=sys.stderr)
        return {ocr_path_key(image_path): run_windows_ocr(image_path, language) for image_path in image_paths}
    finally:
        try:
            list_path.unlink()
        except OSError:
            pass

    stdout = (completed.stdout or b"").decode("utf-8-sig", errors="replace")
    stderr = (completed.stderr or b"").decode("utf-8-sig", errors="replace").strip()
    lines = [line.strip() for line in stdout.splitlines() if line.strip()]
    if not lines:
        raise RuntimeError(f"Windows batch OCR returned no JSON lines.\n{stderr}")

    expected = {ocr_path_key(image_path): image_path for image_path in image_paths}
    results: dict[str, dict[str, Any]] = {}
    for line_number, line in enumerate(lines, start=1):
        item = load_ocr_json(line, f"{OCR_BATCH_SCRIPT} line {line_number}")
        item_path_text = str(item.get("image", ""))
        item_key = ocr_path_key(item_path_text) if item_path_text else ""
        original_path = expected.get(item_key)
        if original_path is None:
            continue

        error = str(item.get("error", "")).strip()
        if error:
            print(f"[forza-extract] batch OCR failed for {original_path.name}, retrying single-image OCR: {error}", file=sys.stderr)
            results[item_key] = run_windows_ocr(original_path, language)
        else:
            results[item_key] = item

    missing = [path for key, path in expected.items() if key not in results]
    for image_path in missing:
        print(f"[forza-extract] batch OCR omitted {image_path.name}, retrying single-image OCR", file=sys.stderr)
        results[ocr_path_key(image_path)] = run_windows_ocr(image_path, language)

    return results


def register_nvidia_cuda_dll_dirs() -> list[Path]:
    spec = importlib.util.find_spec("nvidia")
    if spec is None or not spec.submodule_search_locations:
        return []

    dll_dirs: list[Path] = []
    for base_text in spec.submodule_search_locations:
        base = Path(base_text)
        if not base.exists():
            continue
        for child in base.iterdir():
            bin_dir = child / "bin"
            if bin_dir.exists() and any(bin_dir.glob("*.dll")):
                dll_dirs.append(bin_dir)

    unique_dirs = sorted(set(dll_dirs), key=lambda item: str(item).casefold())
    for dll_dir in unique_dirs:
        try:
            NVIDIA_DLL_HANDLES.append(os.add_dll_directory(str(dll_dir)))
        except (AttributeError, OSError):
            pass

    if unique_dirs:
        os.environ["PATH"] = ";".join(str(item) for item in unique_dirs) + ";" + os.environ.get("PATH", "")
    return unique_dirs


def create_rapidocr_engine(use_gpu: bool) -> Any:
    try:
        if use_gpu:
            register_nvidia_cuda_dll_dirs()
        from rapidocr import RapidOCR
    except ImportError as exc:
        raise RuntimeError(
            "RapidOCR backend is not installed. Install it with: "
            "python -m pip install rapidocr onnxruntime-gpu"
        ) from exc

    params = {
        "Global.log_level": "warning",
        "EngineConfig.onnxruntime.use_cuda": bool(use_gpu),
        "EngineConfig.onnxruntime.cuda_ep_cfg.cudnn_conv_algo_search": "HEURISTIC",
    }
    worker_threads = positive_int_env("FORZA_OCR_THREADS_PER_PROCESS")
    if worker_threads:
        params["EngineConfig.onnxruntime.intra_op_num_threads"] = worker_threads
        params["EngineConfig.onnxruntime.inter_op_num_threads"] = 1
    engine = RapidOCR(params=params)
    if use_gpu:
        providers = get_rapidocr_providers(engine)
        flattened = [provider for _name, provider_list in providers for provider in provider_list]
        if "CUDAExecutionProvider" not in flattened:
            raise RuntimeError(
                "RapidOCR GPU backend was requested, but ONNXRuntime fell back to CPU. "
                f"Providers: {providers}. Check CUDA/cuDNN DLL paths."
            )
    return engine


def get_rapidocr_providers(engine: Any) -> list[tuple[str, list[str]]]:
    providers: list[tuple[str, list[str]]] = []
    for part_name in ("text_det", "text_cls", "text_rec"):
        part = getattr(engine, part_name, None)
        wrapper_session = getattr(part, "session", None)
        session = getattr(wrapper_session, "session", None)
        if session is None:
            continue
        get_providers = getattr(session, "get_providers", None)
        if callable(get_providers):
            providers.append((part_name, list(get_providers())))
    return providers


def rapidocr_output_to_ocr(result: Any, image_path: Path) -> dict[str, Any]:
    image = cv2.imread(str(image_path))
    if image is None:
        raise RuntimeError(f"Could not read OCR image: {image_path}")
    height, width = image.shape[:2]

    txts_attr = getattr(result, "txts", None)
    boxes_attr = getattr(result, "boxes", None)
    scores_attr = getattr(result, "scores", None)
    txts = list(txts_attr) if txts_attr is not None else []
    boxes = list(boxes_attr) if boxes_attr is not None else []
    scores = list(scores_attr) if scores_attr is not None else []
    lines: list[dict[str, Any]] = []

    for index, text in enumerate(txts):
        clean = str(text).strip()
        if not clean or index >= len(boxes):
            continue
        box = boxes[index]
        points = box.tolist() if hasattr(box, "tolist") else box
        xs = [float(point[0]) for point in points]
        ys = [float(point[1]) for point in points]
        left = min(xs)
        top = min(ys)
        right = max(xs)
        bottom = max(ys)
        word = {
            "text": clean,
            "x": round(left, 2),
            "y": round(top, 2),
            "width": round(max(1.0, right - left), 2),
            "height": round(max(1.0, bottom - top), 2),
        }
        line = {
            "text": clean,
            "x": word["x"],
            "y": word["y"],
            "width": word["width"],
            "height": word["height"],
            "words": [word],
        }
        if index < len(scores):
            line["score"] = float(scores[index])
        lines.append(line)

    return {
        "image": str(image_path.resolve()),
        "language": "rapidocr",
        "width": width,
        "height": height,
        "text_angle": 0,
        "lines": lines,
    }


def run_rapidocr_batch(image_paths: list[Path], use_gpu: bool) -> dict[str, dict[str, Any]]:
    if not image_paths:
        return {}
    engine = create_rapidocr_engine(use_gpu=use_gpu)
    backend_name = "rapidocr-gpu" if use_gpu else "rapidocr-cpu"
    print(f"[forza-extract] {backend_name} OCR batch {len(image_paths)} screenshots")
    results: dict[str, dict[str, Any]] = {}
    for index, image_path in enumerate(image_paths, start=1):
        print(f"[{index}/{len(image_paths)}] OCR {image_path.name} [{backend_name}]")
        result = engine(str(image_path))
        results[ocr_path_key(image_path)] = rapidocr_output_to_ocr(result, image_path)
    return results


def parse_ocr(
    ocr: dict[str, Any],
    profile: dict[str, Any],
    source_image: SourceImage,
    metadata: dict[str, str],
    ocr_image_path: Path | None = None,
    cell_tmp_dir: Path | None = None,
    language: str = "en",
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    columns = profile.get("columns", {})
    min_row_height = int(profile.get("min_row_height_px", 0))
    width = max(float(ocr.get("width", 1)), 1.0)
    time_regexes = [re.compile(pattern) for pattern in profile.get("time_patterns", [])]
    dirty_markers = [marker.lower() for marker in profile.get("dirty_markers", [])]
    ocr_image = cv2.imread(str(ocr_image_path), cv2.IMREAD_GRAYSCALE) if ocr_image_path else None
    rows_per_page = int(profile.get("rows_per_page", 11) or 11)
    source_page_index = parse_source_page_index(source_image.path)
    infer_rank = bool(profile.get("infer_rank_from_page", True))
    cell_ocr_values: dict[int, dict[str, str]] = {}

    table_rows = build_table_rows(ocr, profile)
    for visible_index, line in enumerate(table_rows, start=1):
        if float(line.get("height", 0)) < min_row_height:
            continue

        words = line.get("words", [])
        if not words:
            continue

        buckets: dict[str, list[str]] = {name: [] for name in columns}
        for word in words:
            text = str(word.get("text", "")).strip()
            if not text:
                continue

            center_x = (float(word.get("x", 0)) + float(word.get("width", 0)) / 2) / width
            for column_name, bounds in columns.items():
                start, end = float(bounds[0]), float(bounds[1])
                if start <= center_x < end:
                    buckets[column_name].append(text)
                    break

        car_class = parse_car_class(" ".join(buckets.get("car_class", []))) or metadata["performance_class"]
        pi = parse_pi(" ".join(buckets.get("pi", [])))
        class_pi = clean_text(f"{car_class} {pi}") if car_class and pi else parse_class_pi(" ".join(buckets.get("class_pi", [])))
        inferred_rank = source_page_index * rows_per_page + visible_index if source_page_index is not None else None
        parsed_rank = parse_rank(" ".join(buckets.get("rank", [])))
        rank = parsed_rank
        if rank is None and infer_rank and inferred_rank is not None:
            rank = inferred_rank

        abs_enabled = parse_checkbox_value(" ".join(buckets.get("abs", [])), ocr_image, line, profile, "abs")
        tcs_enabled = parse_checkbox_value(" ".join(buckets.get("tcs", [])), ocr_image, line, profile, "tcs")
        stm_enabled = parse_checkbox_value(" ".join(buckets.get("stm", [])), ocr_image, line, profile, "stm")
        bucket_drivetrain = normalize_drivetrain(" ".join(buckets.get("drivetrain", [])))
        bucket_gear = normalize_gear(" ".join(buckets.get("gear", [])))
        pixel_gear = classify_gear_from_pixels(ocr_image, line, profile)
        fallback_columns: set[str] = set()
        if not bucket_drivetrain:
            fallback_columns.add("drivetrain")
        if not (bucket_gear or pixel_gear):
            fallback_columns.add("gear")
        bucket_gamertag = normalize_gamertag(" ".join(buckets.get("gamertag", [])))
        cell_values = extract_row_cell_ocr(
            ocr_image=ocr_image,
            line=line,
            profile=profile,
            source_image=source_image,
            visible_index=visible_index,
            tmp_dir=cell_tmp_dir,
            language=language,
            skip_columns={"gamertag"},
            only_columns=fallback_columns,
        )
        cell_ocr_values[visible_index] = cell_values
        gamertag_candidates = [bucket_gamertag]
        if should_run_gamertag_fallback(bucket_gamertag, profile):
            gamertag_candidates.extend(
                extract_gamertag_candidates(
                    ocr_image=ocr_image,
                    line=line,
                    profile=profile,
                    source_image=source_image,
                    visible_index=visible_index,
                    tmp_dir=cell_tmp_dir,
                    language=language,
                )
            )
        gamertag = choose_best_gamertag(gamertag_candidates)
        drivetrain = normalize_drivetrain(cell_values.get("drivetrain") or bucket_drivetrain)
        gear = normalize_gear(cell_values.get("gear") or bucket_gear or pixel_gear)

        row = {
            "game": metadata["game"],
            "event_type": metadata["event_type"],
            "rivals_mode": metadata["rivals_mode"],
            "track": metadata["track"],
            "performance_class": metadata["performance_class"],
            "pi_class": metadata["pi_class"],
            "rank": rank,
            "visible_row_index": visible_index,
            "source_page_index": source_page_index,
            "gamertag": gamertag,
            "car": normalize_car_text(" ".join(buckets.get("car", []))),
            "car_class": car_class,
            "pi": pi,
            "class_pi": class_pi,
            "drivetrain": drivetrain,
            "lap_time": parse_time(" ".join(buckets.get("time", [])), time_regexes),
            "abs": abs_enabled,
            "tcs": tcs_enabled,
            "stm": stm_enabled,
            "gear": gear,
            "is_dirty": detect_dirty(line.get("text", ""), [], dirty_markers),
            "raw_text": clean_text(str(line.get("text", ""))),
            "source_file": str(source_image.source),
            "image_file": str(source_image.path),
            "timestamp_seconds": source_image.timestamp_seconds,
            "extracted_at": datetime.now(timezone.utc).isoformat(),
        }

        row = repair_from_raw_text(row, time_regexes)
        if looks_like_leaderboard_row(row):
            rows.append(row)

    return fill_missing_visible_ranks(rows)


def fill_missing_visible_ranks(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    starts: dict[int, int] = {}
    for row in rows:
        rank = row.get("rank")
        visible_index = row.get("visible_row_index")
        if rank is None or visible_index is None:
            continue
        try:
            start = int(rank) - int(visible_index) + 1
        except (TypeError, ValueError):
            continue
        if start <= 0:
            continue
        starts[start] = starts.get(start, 0) + 1

    if not starts:
        return rows

    start_rank = sorted(starts.items(), key=lambda item: (-item[1], item[0]))[0][0]
    for row in rows:
        if row.get("rank") is not None:
            continue
        visible_index = row.get("visible_row_index")
        if visible_index is None:
            continue
        row["rank"] = start_rank + int(visible_index) - 1
    return rows


def build_table_rows(ocr: dict[str, Any], profile: dict[str, Any]) -> list[dict[str, Any]]:
    words: list[dict[str, Any]] = []
    for line in ocr.get("lines", []):
        for word in line.get("words", []):
            text = str(word.get("text", "")).strip()
            if not text:
                continue
            words.append(
                {
                    "text": text,
                    "x": float(word.get("x", 0)),
                    "y": float(word.get("y", 0)),
                    "width": float(word.get("width", 0)),
                    "height": float(word.get("height", 0)),
                }
            )

    if not words:
        return []

    ocr_height = max(float(ocr.get("height", 1)), 1.0)
    body = profile.get("table_body", {})
    body_top = ocr_height * float(body.get("top", 0.0))
    body_bottom = ocr_height * float(body.get("bottom", 1.0))
    words = [
        word
        for word in words
        if body_top <= word["y"] + word["height"] / 2 <= body_bottom
    ]
    if not words:
        return []

    heights = sorted(word["height"] for word in words if word["height"] > 0)
    median_height = heights[len(heights) // 2] if heights else 24.0
    configured_tolerance = profile.get("row_merge_tolerance_px")
    tolerance = float(configured_tolerance) if configured_tolerance else max(18.0, median_height * 0.85)

    sorted_words = sorted(words, key=lambda word: (word["y"] + word["height"] / 2, word["x"]))
    row_groups: list[dict[str, Any]] = []

    for word in sorted_words:
        center_y = word["y"] + word["height"] / 2
        if not row_groups or abs(center_y - row_groups[-1]["center_y"]) > tolerance:
            row_groups.append({"center_y": center_y, "words": [word]})
            continue

        group = row_groups[-1]
        group["words"].append(word)
        group["center_y"] = sum(item["y"] + item["height"] / 2 for item in group["words"]) / len(group["words"])

    rows: list[dict[str, Any]] = []
    for group in row_groups:
        row_words = sorted(group["words"], key=lambda word: word["x"])
        if not looks_like_table_body_words(row_words, profile, float(ocr.get("width", 1))):
            continue
        left = min(word["x"] for word in row_words)
        top = min(word["y"] for word in row_words)
        right = max(word["x"] + word["width"] for word in row_words)
        bottom = max(word["y"] + word["height"] for word in row_words)
        rows.append(
            {
                "text": clean_text(" ".join(word["text"] for word in row_words)),
                "x": round(left, 2),
                "y": round(top, 2),
                "center_y": round(float(group["center_y"]), 2),
                "width": round(right - left, 2),
                "height": round(bottom - top, 2),
                "words": row_words,
            }
        )

    return rows


def extract_row_cell_ocr(
    ocr_image: Any,
    line: dict[str, Any],
    profile: dict[str, Any],
    source_image: SourceImage,
    visible_index: int,
    tmp_dir: Path | None,
    language: str,
    skip_columns: set[str] | None = None,
    only_columns: set[str] | None = None,
) -> dict[str, str]:
    cell_config = profile.get("cell_ocr", {})
    if not cell_config.get("enabled", False):
        return {}
    if ocr_image is None or tmp_dir is None:
        return {}

    columns = [str(name) for name in cell_config.get("columns", [])]
    if not columns:
        return {}

    values: dict[str, str] = {}
    tmp_dir.mkdir(parents=True, exist_ok=True)
    skip_columns = skip_columns or set()
    for column_name in columns:
        if column_name in skip_columns:
            continue
        if only_columns is not None and column_name not in only_columns:
            continue
        variants = ["threshold", "raw"] if column_name == "drivetrain" else ["raw"]
        for variant in variants:
            cell_path = crop_cell_for_ocr(
                ocr_image=ocr_image,
                line=line,
                profile=profile,
                column_name=column_name,
                tmp_dir=tmp_dir,
                source_image=source_image,
                visible_index=visible_index,
                variant=variant,
            )
            if cell_path is None:
                continue
            try:
                cell_ocr = run_windows_ocr(cell_path, language)
            except RuntimeError:
                continue
            text = clean_text(" ".join(str(item.get("text", "")) for item in cell_ocr.get("lines", [])))
            if text:
                values[column_name] = text
                break
    return values


def should_run_gamertag_fallback(value: str, profile: dict[str, Any]) -> bool:
    config = profile.get("gamertag_ocr", {})
    if not config.get("enabled", True):
        return False
    trigger_score = int(config.get("trigger_score_below", 6))
    if gamertag_score(value) < trigger_score:
        return True
    if config.get("trigger_on_icon_suffix", True) and looks_like_gamertag_has_icon_suffix(value):
        return True
    return False


def looks_like_gamertag_has_icon_suffix(value: str) -> bool:
    text = clean_text(value)
    if not text:
        return False
    if re.search(r"[^\x00-\x7F]\s*$", text):
        return True
    if re.search(r"[\*@+~^|!?.:;_\-]+\s*$", text):
        return True
    tokens = text.split()
    return len(tokens) > 1 and tokens[-1] in {"O", "U", "P"}


def extract_gamertag_candidates(
    ocr_image: Any,
    line: dict[str, Any],
    profile: dict[str, Any],
    source_image: SourceImage,
    visible_index: int,
    tmp_dir: Path | None,
    language: str,
) -> list[str]:
    if ocr_image is None or tmp_dir is None:
        return []

    config = profile.get("gamertag_ocr", {})
    variants = config.get("variants") or default_gamertag_variants()
    candidates: list[str] = []
    seen: set[str] = set()
    accept_score = int(config.get("accept_score", 10))
    tmp_dir.mkdir(parents=True, exist_ok=True)

    for index, variant_config in enumerate(variants):
        bounds = variant_config.get("bounds")
        preprocess_name = str(variant_config.get("preprocess", "text_raw"))
        scale = int(variant_config.get("scale", config.get("scale", 5)))
        trim = bool(variant_config.get("trim", True))
        name = str(variant_config.get("name", f"gamertag_{index:02d}"))
        cell_path = crop_cell_for_ocr(
            ocr_image=ocr_image,
            line=line,
            profile=profile,
            column_name="gamertag",
            tmp_dir=tmp_dir,
            source_image=source_image,
            visible_index=visible_index,
            variant=preprocess_name,
            bounds_override=bounds,
            scale_override=scale,
            trim_text=trim,
            name_suffix=name,
        )
        if cell_path is None:
            continue
        try:
            cell_ocr = run_windows_ocr(cell_path, language)
        except RuntimeError:
            continue
        text = normalize_gamertag(clean_text(" ".join(str(item.get("text", "")) for item in cell_ocr.get("lines", []))))
        if not text or text in seen:
            continue
        seen.add(text)
        candidates.append(text)
        if gamertag_score(text) >= accept_score and not looks_like_gamertag_has_icon_suffix(text):
            break

    return candidates


def default_gamertag_variants() -> list[dict[str, Any]]:
    return [
        {"name": "short_raw", "bounds": [0.108, 0.155], "preprocess": "text_raw", "scale": 5, "trim": True},
        {"name": "short_threshold", "bounds": [0.108, 0.155], "preprocess": "text_threshold", "scale": 5, "trim": True},
        {"name": "medium_raw", "bounds": [0.108, 0.170], "preprocess": "text_raw", "scale": 5, "trim": True},
        {"name": "medium_threshold", "bounds": [0.108, 0.170], "preprocess": "text_threshold", "scale": 5, "trim": True},
        {"name": "wide_raw", "bounds": [0.105, 0.240], "preprocess": "text_raw", "scale": 4, "trim": True},
        {"name": "wide_threshold", "bounds": [0.105, 0.240], "preprocess": "text_threshold", "scale": 4, "trim": True},
        {"name": "full_raw", "bounds": [0.105, 0.300], "preprocess": "text_raw", "scale": 4, "trim": True},
    ]


def crop_cell_for_ocr(
    ocr_image: Any,
    line: dict[str, Any],
    profile: dict[str, Any],
    column_name: str,
    tmp_dir: Path,
    source_image: SourceImage,
    visible_index: int,
    variant: str = "raw",
    bounds_override: Any | None = None,
    scale_override: int | None = None,
    trim_text: bool = False,
    name_suffix: str = "",
) -> Path | None:
    columns = profile.get("columns", {})
    if column_name not in columns:
        return None

    height, width = ocr_image.shape[:2]
    cell_config = profile.get("cell_ocr", {})
    bounds = bounds_override or cell_config.get("bounds", {}).get(column_name, columns[column_name])
    start, end = float(bounds[0]), float(bounds[1])
    pad_x = int(cell_config.get("padding_x_px", 8))
    pad_y = int(cell_config.get("padding_y_px", 8))
    scale = max(1, int(scale_override or cell_config.get("scale", 1)))

    x1 = max(0, int(width * start) - pad_x)
    x2 = min(width, int(width * end) + pad_x)
    center_y = float(line.get("center_y", line.get("y", 0) + line.get("height", 0) / 2))
    row_height = max(float(line.get("height", 0)), 44.0)
    y1 = max(0, int(center_y - row_height / 2) - pad_y)
    y2 = min(height, int(center_y + row_height / 2) + pad_y)
    if x2 <= x1 or y2 <= y1:
        return None

    crop = ocr_image[y1:y2, x1:x2]
    if crop.size == 0:
        return None
    if variant in {"norm", "text_norm"}:
        crop = cv2.normalize(crop, None, 0, 255, cv2.NORM_MINMAX)
        interpolation = cv2.INTER_CUBIC
    elif variant in {"threshold", "text_threshold"}:
        crop = cv2.normalize(crop, None, 0, 255, cv2.NORM_MINMAX)
        _threshold, crop = cv2.threshold(crop, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
        interpolation = cv2.INTER_NEAREST
    else:
        interpolation = cv2.INTER_CUBIC
    if trim_text or variant.startswith("text_"):
        crop = trim_text_crop(crop)
    if scale > 1:
        crop = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=interpolation)

    crop = cv2.copyMakeBorder(crop, 10, 10, 10, 10, cv2.BORDER_CONSTANT, value=255)
    safe_stem = re.sub(r"[^A-Za-z0-9_.-]+", "_", source_image.path.stem)
    suffix = f"_{name_suffix}" if name_suffix else ""
    out_path = tmp_dir / f"{safe_stem}_row{visible_index:02d}_{column_name}_{variant}{suffix}.png"
    cv2.imwrite(str(out_path), crop)
    return out_path


def trim_text_crop(crop: Any) -> Any:
    if crop.size == 0:
        return crop
    if float(crop.mean()) < 128:
        mask = crop > 150
    else:
        mask = crop < 120
    if not mask.any():
        return crop

    col_density = mask.mean(axis=0)
    row_density = mask.mean(axis=1)
    cols = [index for index, value in enumerate(col_density) if value > 0.025]
    rows = [index for index, value in enumerate(row_density) if value > 0.025]
    if not cols or not rows:
        return crop

    x1 = max(0, cols[0] - 2)
    x2 = min(crop.shape[1], cols[-1] + 3)
    y1 = max(0, rows[0] - 2)
    y2 = min(crop.shape[0], rows[-1] + 3)
    if x2 <= x1 or y2 <= y1:
        return crop
    return crop[y1:y2, x1:x2]


def clean_text(value: str) -> str:
    value = value.replace("\ufffd", " ")
    value = value.replace("ï¿½", " ")
    return re.sub(r"\s+", " ", value).strip()


def parse_source_page_index(path: Path) -> int | None:
    match = re.search(r"leaderboard_(\d+)", path.stem, flags=re.IGNORECASE)
    if not match:
        return None
    return int(match.group(1))


def looks_like_table_body_words(words: list[dict[str, Any]], profile: dict[str, Any], width: float) -> bool:
    text = clean_text(" ".join(str(word.get("text", "")) for word in words)).lower()
    if not text:
        return False
    if "filter:" in text or "players" in text or "change rival" in text:
        return False
    if any(header in text for header in ["driver", "drivetrain", "class", "time", "gear"]):
        return False

    columns = profile.get("columns", {})
    has_rank_or_driver = False
    has_car_or_time = False
    for word in words:
        center_x = (float(word.get("x", 0)) + float(word.get("width", 0)) / 2) / max(width, 1.0)
        word_text = clean_text(str(word.get("text", "")))
        if not word_text:
            continue
        if column_contains(columns, "rank", center_x) or column_contains(columns, "gamertag", center_x):
            has_rank_or_driver = True
        if column_contains(columns, "car", center_x) or column_contains(columns, "time", center_x):
            has_car_or_time = True
    return has_rank_or_driver and has_car_or_time


def column_contains(columns: dict[str, Any], name: str, center_x: float) -> bool:
    if name not in columns:
        return False
    start, end = float(columns[name][0]), float(columns[name][1])
    return start <= center_x < end


def normalize_gamertag(value: str) -> str:
    text = clean_text(value)
    text = re.sub(r"^[#*\s]*\d{1,4}\s+", "", text)
    tokens = text.split()
    while tokens and re.fullmatch(r"[*#@+%&~^|!?.:;_\-\[\]\(\){}]+|[\u00a0-\uffff]+|[Oo0]|[Pp]|U|S", tokens[-1]):
        tokens.pop()
    text = clean_text(" ".join(tokens))
    text = re.sub(r"\s+([@+%&~^|!?.:;_\-\*\[\]\(\){}]+)$", "", text)
    text = re.sub(r"([A-Za-z0-9])[\u00a0-\uffff]+$", r"\1", text)
    text = re.sub(r"([A-Za-z0-9])[\u00a0-\uffff]+[Oo0]$", r"\1", text)
    text = re.sub(r"([A-Za-z0-9])[\*@+%&~^|!?.:;_\-]+$", r"\1", text)
    return clean_text(text)


def choose_best_gamertag(values: list[str]) -> str:
    candidates = []
    seen: set[str] = set()
    for value in values:
        normalized = normalize_gamertag(value)
        if normalized and normalized not in seen:
            seen.add(normalized)
            candidates.append(normalized)
    if not candidates:
        return ""

    def sort_key(value: str) -> tuple[int, int, int]:
        icon_penalty = 1 if looks_like_gamertag_has_icon_suffix(value) else 0
        leading_artifact_penalty = 1 if re.match(r"^[I1l|/\\]\s+\S", value) else 0
        prefix_artifact_penalty = 1 if has_cleaner_prefix_candidate(value, candidates) else 0
        return (
            gamertag_score(value) - icon_penalty * 3 - leading_artifact_penalty * 4 - prefix_artifact_penalty * 4,
            len(value),
            -icon_penalty,
        )

    return max(candidates, key=sort_key)


def has_cleaner_prefix_candidate(value: str, candidates: list[str]) -> bool:
    if not value or value[-1].lower() not in {"e", "g"}:
        return False
    return any(value != candidate and value.startswith(candidate) and len(value) - len(candidate) <= 2 for candidate in candidates)


def gamertag_score(value: str) -> int:
    text = clean_text(value)
    if len(text) <= 1:
        return 0
    alnum = sum(1 for char in text if char.isalnum())
    if text.lower() in {"a", "p", "o", "u"}:
        return 0
    return alnum


def normalize_car_text(value: str) -> str:
    text = clean_text(value)
    text = re.sub(r"\bSOO\b", "S800", text, flags=re.IGNORECASE)
    text = re.sub(r"\bS0O\b", "S800", text, flags=re.IGNORECASE)
    text = re.sub(r"\bS8OO\b", "S800", text, flags=re.IGNORECASE)
    text = re.sub(r"\bpeel\b", "Peel", text, flags=re.IGNORECASE)
    text = re.sub(r"\bhonda\b", "Honda", text, flags=re.IGNORECASE)
    return clean_text(text)


def parse_car_class(value: str) -> str:
    normalized = clean_text(value).upper()
    normalized = normalized.replace("I", "1").replace("L", "1")
    match = re.search(r"\b(D|C|B|A|S1|S2|R|X)\b", normalized)
    return match.group(1) if match else ""


def parse_pi(value: str) -> int | None:
    normalized = clean_text(value).translate(str.maketrans({"O": "0", "o": "0", "I": "1", "l": "1"}))
    match = re.search(r"\b(\d{3})\b", normalized)
    if not match:
        return None
    pi = int(match.group(1))
    return pi if 100 <= pi <= 999 else None


def normalize_drivetrain(value: str) -> str:
    normalized = clean_text(value).upper()
    normalized = re.sub(r"[^A-Z0-9]", "", normalized)
    if normalized in {"RWD", "FWD", "AWD"}:
        return normalized
    if "RWD" in normalized:
        return "RWD"
    if "FWD" in normalized:
        return "FWD"
    if "AWD" in normalized:
        return "AWD"
    return ""


def normalize_gear(value: str) -> str:
    normalized = clean_text(value).upper()
    normalized = re.sub(r"[^A-Z0-9]", "", normalized)
    if normalized in {"M", "MC", "A"}:
        return normalized
    if "MC" in normalized:
        return "MC"
    if normalized == "M":
        return "M"
    if normalized == "A":
        return "A"
    return ""


def classify_gear_from_pixels(ocr_image: Any, line: dict[str, Any], profile: dict[str, Any]) -> str:
    if ocr_image is None:
        return ""

    bounds = profile.get("cell_ocr", {}).get("bounds", {}).get("gear")
    if not bounds:
        bounds = profile.get("columns", {}).get("gear")
    if not bounds:
        return ""

    height, width = ocr_image.shape[:2]
    x1 = max(0, int(width * float(bounds[0])) - 10)
    x2 = min(width, int(width * float(bounds[1])) + 10)
    center_y = float(line.get("center_y", line.get("y", 0) + line.get("height", 0) / 2))
    row_height = max(float(line.get("height", 0)), 44.0)
    y1 = max(0, int(center_y - row_height / 2) - 12)
    y2 = min(height, int(center_y + row_height / 2) + 12)
    if x2 <= x1 or y2 <= y1:
        return ""

    crop = ocr_image[y1:y2, x1:x2]
    if crop.size == 0:
        return ""

    if float(crop.mean()) < 128:
        mask = crop > 170
    else:
        mask = crop < 100

    mask_uint = mask.astype("uint8")
    component_count, labels, stats, _centroids = cv2.connectedComponentsWithStats(mask_uint, 8)
    components: list[tuple[int, int, int, int, int]] = []
    crop_height, crop_width = crop.shape[:2]
    for index in range(1, component_count):
        x, y, w, h, area = [int(value) for value in stats[index]]
        if area < 20 or w < 3 or h < 10:
            continue
        # Ignore vertical scrollbar fragments and row borders in the gear area.
        if h > crop_height * 0.82 and w < 8:
            continue
        if x > crop_width * 0.78 and h > crop_height * 0.65:
            continue
        components.append((x, y, w, h, area))

    if not components:
        return ""

    x1_text = min(component[0] for component in components)
    y1_text = min(component[1] for component in components)
    x2_text = max(component[0] + component[2] for component in components)
    y2_text = max(component[1] + component[3] for component in components)
    text_width = x2_text - x1_text
    text_height = y2_text - y1_text
    if text_width <= 0 or text_height <= 0:
        return ""

    if len(components) >= 2 or text_width / max(text_height, 1) > 1.15:
        return "MC"

    glyph = mask_uint[y1_text:y2_text, x1_text:x2_text]
    glyph = cv2.resize(glyph * 255, (32, 32), interpolation=cv2.INTER_NEAREST) > 0
    top = glyph[:12, :]
    left_top = int(top[:, :10].sum())
    center_top = int(top[:, 10:22].sum())
    right_top = int(top[:, 22:].sum())
    edge_top = left_top + right_top
    return "A" if center_top > edge_top * 1.3 else "M"


def parse_checkbox_value(
    value: str,
    ocr_image: Any,
    line: dict[str, Any],
    profile: dict[str, Any],
    name: str,
) -> bool | None:
    pixel_value = detect_checkbox_from_pixels(ocr_image, line, profile, name)
    if pixel_value is not None:
        return pixel_value
    normalized = clean_text(value).lower()
    if not normalized:
        return None
    if re.search(r"[o0●◉]", normalized):
        return True
    return None


def detect_checkbox_from_pixels(
    ocr_image: Any,
    line: dict[str, Any],
    profile: dict[str, Any],
    name: str,
) -> bool | None:
    if ocr_image is None:
        return None
    centers = profile.get("checkbox_centers", {})
    if name not in centers:
        return None

    height, width = ocr_image.shape[:2]
    x = int(width * float(centers[name]))
    y = int(float(line.get("center_y", line.get("y", 0) + line.get("height", 0) / 2)))
    if x <= 0 or y <= 0 or x >= width or y >= height:
        return None

    radius = 13
    inner = 5
    patch = ocr_image[max(0, y - radius):min(height, y + radius + 1), max(0, x - radius):min(width, x + radius + 1)]
    center = ocr_image[max(0, y - inner):min(height, y + inner + 1), max(0, x - inner):min(width, x + inner + 1)]
    if patch.size == 0 or center.size == 0:
        return None

    background = float(patch.mean())
    center_mean = float(center.mean())
    center_dark_ratio = float((center < 90).mean())
    center_bright_ratio = float((center > 170).mean())

    if background < 110:
        return center_bright_ratio > 0.30 or center_mean > 150
    return center_dark_ratio > 0.30 or center_mean < 120


def parse_rank(value: str) -> int | None:
    value = value.strip().replace("#", "")
    value = value.translate(str.maketrans({"O": "0", "o": "0", "I": "1", "l": "1"}))
    match = re.search(r"^\s*(\d{1,7})(?=\b|\s|$)", value)
    if not match:
        return None
    digits = match.group(1)
    if digits.startswith("0") and len(digits) >= 2:
        digits = digits[:2]
    rank = int(digits)
    return rank if rank > 0 else None


def parse_time(value: str, patterns: list[re.Pattern[str]]) -> str | None:
    normalized = value.replace(",", ".").replace(";", ":")
    normalized = re.sub(r"\s*:\s*", ":", normalized)
    normalized = re.sub(r"\s*\.\s*", ".", normalized)
    for pattern in patterns:
        match = pattern.search(normalized)
        if match:
            return match.group(0)
    compact = re.sub(r"\s+", "", normalized)
    for pattern in patterns:
        match = pattern.search(compact)
        if match:
            return match.group(0)
    return None


def parse_class_pi(value: str) -> str:
    normalized = clean_text(value)
    normalized = re.sub(r"\bS[lI]\b", "S1", normalized, flags=re.IGNORECASE)
    normalized = re.sub(r"\bSl\b", "S1", normalized, flags=re.IGNORECASE)
    normalized = re.sub(
        r"\b(D|C|B|A|S1|S2|R|X)\s*(\d{3})\b",
        lambda match: f"{match.group(1).upper()} {match.group(2)}",
        normalized,
        flags=re.IGNORECASE,
    )
    return normalized


def normalize_performance_class(value: str) -> str:
    normalized = clean_text(value).upper()
    if normalized in {"ALL", "*"}:
        return "All"
    return parse_class_pi(normalized)


def apply_ocr_mode(profile: dict[str, Any], mode: str) -> dict[str, Any]:
    normalized = (mode or "precise").strip().lower()
    if normalized == "precise":
        return profile
    if normalized != "fast":
        raise ValueError(f"Unsupported OCR mode: {mode}")

    profile = dict(profile)
    cell_ocr = dict(profile.get("cell_ocr", {}))
    cell_ocr["enabled"] = False
    profile["cell_ocr"] = cell_ocr

    gamertag_ocr = dict(profile.get("gamertag_ocr", {}))
    gamertag_ocr["enabled"] = False
    profile["gamertag_ocr"] = gamertag_ocr
    return profile


def detect_dirty(raw_text: str, flags: list[str], dirty_markers: list[str]) -> bool:
    haystack = f"{raw_text} {' '.join(flags)}".lower()
    return any(marker in haystack for marker in dirty_markers)


def repair_from_raw_text(row: dict[str, Any], patterns: list[re.Pattern[str]]) -> dict[str, Any]:
    raw = row["raw_text"]
    if row["lap_time"] is None:
        row["lap_time"] = parse_time(raw, patterns)
    if not row.get("pi"):
        pi = parse_pi(raw)
        if pi:
            row["pi"] = pi
    if not row.get("car_class"):
        row["car_class"] = parse_car_class(raw)
    if not row.get("class_pi"):
        class_match = re.search(r"\b(?:[DCBARX]|S[12Il])\s*\d{3}\b", raw, flags=re.IGNORECASE)
        if class_match:
            row["class_pi"] = parse_class_pi(class_match.group(0))
    if not row.get("class_pi") and row.get("car_class") and row.get("pi"):
        row["class_pi"] = f"{row['car_class']} {row['pi']}"
    if not row.get("drivetrain"):
        row["drivetrain"] = normalize_drivetrain(raw)
    return row


def looks_like_leaderboard_row(row: dict[str, Any]) -> bool:
    if row["rank"] is None and row["lap_time"] is None:
        return False
    if row["lap_time"] is None:
        return False
    if not row.get("gamertag") and not row.get("car"):
        return False
    raw = row["raw_text"].lower()
    header_words = {"rank", "player", "gamertag", "car", "time", "leaderboard"}
    if sum(1 for word in header_words if word in raw) >= 2:
        return False
    return True


def dedupe_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    best_by_key: dict[tuple[Any, ...], dict[str, Any]] = {}
    for row in rows:
        if row.get("rank") is not None:
            key = (
                row.get("event_type"),
                row.get("rivals_mode"),
                row.get("track"),
                row.get("performance_class"),
                row.get("pi_class"),
                row.get("rank"),
            )
        else:
            key = (
                row.get("event_type"),
                row.get("rivals_mode"),
                row.get("track"),
                row.get("performance_class"),
                row.get("pi_class"),
                row.get("gamertag").lower() if row.get("gamertag") else "",
                row.get("car").lower() if row.get("car") else "",
                row.get("lap_time"),
            )
        score = confidence_score(row)
        current = best_by_key.get(key)
        if current is None or score > confidence_score(current):
            row["confidence"] = score
            best_by_key[key] = row
    deduped = sorted(best_by_key.values(), key=lambda item: (item.get("rank") is None, item.get("rank") or 10**9, item.get("lap_time") or ""))
    for row in deduped:
        row["row_key"] = make_row_key(row)
    return deduped


def make_row_key(row: dict[str, Any]) -> str:
    parts = [
        row.get("game") or "",
        row.get("event_type") or "",
        row.get("rivals_mode") or "",
        row.get("track") or "",
        row.get("performance_class") or "",
        row.get("pi_class") or "",
        str(row.get("rank") or ""),
        row.get("gamertag") or "",
        row.get("car") or "",
        row.get("lap_time") or "",
    ]
    return hashlib.sha1("|".join(parts).lower().encode("utf-8")).hexdigest()


def confidence_score(row: dict[str, Any]) -> float:
    score = 0.0
    if row.get("rank") is not None:
        score += 0.25
    if row.get("gamertag"):
        score += 0.20
        score += min(gamertag_score(str(row.get("gamertag") or "")), 12) * 0.01
    if row.get("car"):
        score += 0.20
    if row.get("class_pi"):
        score += 0.10
    if row.get("lap_time"):
        score += 0.25
    if row.get("drivetrain"):
        score += 0.05
    if row.get("gear"):
        score += 0.03
    return round(score, 2)


def normalize_sqlite_row(row: dict[str, Any]) -> dict[str, Any]:
    sqlite_row = dict(row)
    sqlite_row["is_dirty"] = int(bool(row.get("is_dirty")))
    for key in ("abs", "tcs", "stm"):
        value = row.get(key)
        sqlite_row[key] = None if value is None else int(bool(value))
    return sqlite_row


def write_outputs(
    rows: list[dict[str, Any]],
    output_dir: Path,
    sqlite_path: Path | None = None,
    parquet_path: Path | None = None,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    csv_path = output_dir / "leaderboard_entries.csv"
    json_path = output_dir / "leaderboard_entries.json"
    local_parquet_path = output_dir / "leaderboard_entries.parquet"
    sqlite_path = sqlite_path or (output_dir / "leaderboard_entries.sqlite")
    sqlite_path.parent.mkdir(parents=True, exist_ok=True)

    fieldnames = [
        "game",
        "event_type",
        "rivals_mode",
        "track",
        "performance_class",
        "pi_class",
        "rank",
        "visible_row_index",
        "source_page_index",
        "gamertag",
        "car",
        "car_class",
        "pi",
        "class_pi",
        "drivetrain",
        "lap_time",
        "abs",
        "tcs",
        "stm",
        "gear",
        "is_dirty",
        "confidence",
        "raw_text",
        "source_file",
        "image_file",
        "timestamp_seconds",
        "extracted_at",
        "row_key",
    ]

    with csv_path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    with json_path.open("w", encoding="utf-8") as handle:
        json.dump(rows, handle, indent=2, ensure_ascii=False)

    write_parquet(rows, fieldnames, local_parquet_path, merge_existing=False)
    if parquet_path:
        write_parquet(rows, fieldnames, parquet_path, merge_existing=True)

    with sqlite3.connect(sqlite_path) as connection:
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS leaderboard_entries (
                row_key TEXT NOT NULL PRIMARY KEY,
                game TEXT NOT NULL,
                event_type TEXT NOT NULL,
                rivals_mode TEXT NOT NULL DEFAULT '',
                track TEXT NOT NULL,
                performance_class TEXT NOT NULL DEFAULT '',
                pi_class TEXT NOT NULL,
                rank INTEGER,
                visible_row_index INTEGER,
                source_page_index INTEGER,
                gamertag TEXT,
                car TEXT,
                car_class TEXT,
                pi INTEGER,
                class_pi TEXT,
                drivetrain TEXT,
                lap_time TEXT,
                abs INTEGER,
                tcs INTEGER,
                stm INTEGER,
                gear TEXT,
                is_dirty INTEGER NOT NULL,
                confidence REAL NOT NULL,
                raw_text TEXT NOT NULL,
                source_file TEXT NOT NULL,
                image_file TEXT NOT NULL,
                timestamp_seconds REAL,
                extracted_at TEXT NOT NULL
            )
            """
        )
        existing_columns = {
            row[1]
            for row in connection.execute("PRAGMA table_info(leaderboard_entries)")
        }
        if "rivals_mode" not in existing_columns:
            connection.execute("ALTER TABLE leaderboard_entries ADD COLUMN rivals_mode TEXT NOT NULL DEFAULT ''")
        if "performance_class" not in existing_columns:
            connection.execute("ALTER TABLE leaderboard_entries ADD COLUMN performance_class TEXT NOT NULL DEFAULT ''")
        for column_name, definition in {
            "visible_row_index": "INTEGER",
            "source_page_index": "INTEGER",
            "car_class": "TEXT",
            "pi": "INTEGER",
            "drivetrain": "TEXT",
            "abs": "INTEGER",
            "tcs": "INTEGER",
            "stm": "INTEGER",
            "gear": "TEXT",
        }.items():
            if column_name not in existing_columns:
                connection.execute(f"ALTER TABLE leaderboard_entries ADD COLUMN {column_name} {definition}")
        connection.executemany(
            """
            INSERT OR REPLACE INTO leaderboard_entries (
                row_key, game, event_type, rivals_mode, track, performance_class, pi_class, rank,
                visible_row_index, source_page_index, gamertag, car, car_class, pi, class_pi, drivetrain,
                lap_time, abs, tcs, stm, gear, is_dirty, confidence, raw_text, source_file, image_file,
                timestamp_seconds, extracted_at
            )
            VALUES (
                :row_key, :game, :event_type, :rivals_mode, :track, :performance_class, :pi_class, :rank,
                :visible_row_index, :source_page_index, :gamertag, :car, :car_class, :pi, :class_pi, :drivetrain,
                :lap_time, :abs, :tcs, :stm, :gear, :is_dirty, :confidence, :raw_text,
                :source_file, :image_file, :timestamp_seconds, :extracted_at
            )
            """,
            [normalize_sqlite_row(row) for row in rows],
        )


def write_parquet(rows: list[dict[str, Any]], fieldnames: list[str], path: Path, merge_existing: bool) -> None:
    try:
        import pandas as pd
    except ImportError as exc:
        raise RuntimeError("Parquet export requires pandas. Run: python -m pip install -r requirements.txt") from exc

    try:
        import pyarrow  # noqa: F401
    except ImportError as exc:
        raise RuntimeError("Parquet export requires pyarrow. Run: python -m pip install -r requirements.txt") from exc

    path.parent.mkdir(parents=True, exist_ok=True)
    frame = pd.DataFrame(rows, columns=fieldnames)
    if merge_existing and path.exists():
        existing = pd.read_parquet(path)
        frame = pd.concat([existing, frame], ignore_index=True)
        if "row_key" in frame.columns:
            frame = frame.drop_duplicates(subset=["row_key"], keep="last")
    frame.to_parquet(path, engine="pyarrow", index=False)


def write_rank_coverage_report(rows: list[dict[str, Any]], output_dir: Path) -> None:
    ranks = [int(row["rank"]) for row in rows if row.get("rank") is not None]
    counts_by_rank: dict[int, int] = {}
    for rank in ranks:
        counts_by_rank[rank] = counts_by_rank.get(rank, 0) + 1

    if ranks:
        min_rank = min(ranks)
        max_rank = max(ranks)
        expected = set(range(min_rank, max_rank + 1))
        missing = sorted(expected - set(ranks))
    else:
        min_rank = None
        max_rank = None
        missing = []

    duplicate_ranks = {
        str(rank): count
        for rank, count in sorted(counts_by_rank.items())
        if count > 1
    }

    source_counts: dict[str, int] = {}
    duplicate_content: dict[str, list[int]] = {}
    for row in rows:
        source_name = Path(str(row.get("source_file", ""))).name
        source_counts[source_name] = source_counts.get(source_name, 0) + 1
        signature = "|".join(
            [
                clean_text(str(row.get("gamertag") or "")).lower(),
                clean_text(str(row.get("car") or "")).lower(),
                str(row.get("lap_time") or ""),
            ]
        )
        if signature.strip("|"):
            duplicate_content.setdefault(signature, []).append(int(row["rank"]) if row.get("rank") is not None else -1)

    duplicate_content = {
        signature: sorted(set(ranks_for_signature))
        for signature, ranks_for_signature in duplicate_content.items()
        if len(set(ranks_for_signature)) > 1
    }
    critical_fields = ["gamertag", "car", "car_class", "pi", "drivetrain", "lap_time", "gear"]
    missing_fields = {
        field: sum(1 for row in rows if row.get(field) in {None, ""})
        for field in critical_fields
    }
    suspect_rows = [
        {
            "rank": row.get("rank"),
            "gamertag": row.get("gamertag"),
            "car": row.get("car"),
            "lap_time": row.get("lap_time"),
            "source_file": Path(str(row.get("source_file", ""))).name,
        }
        for row in rows
        if not row.get("gamertag") or gamertag_score(str(row.get("gamertag") or "")) < 3
    ]

    report = {
        "row_count": len(rows),
        "min_rank": min_rank,
        "max_rank": max_rank,
        "missing_ranks": missing,
        "duplicate_ranks": duplicate_ranks,
        "missing_critical_fields": missing_fields,
        "suspect_rows": suspect_rows,
        "rows_per_source_file": dict(sorted(source_counts.items())),
        "duplicate_content_across_ranks": duplicate_content,
    }

    with (output_dir / "rank_coverage_report.json").open("w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2, ensure_ascii=False)


def dump_ocr(ocr: dict[str, Any], output_dir: Path, source_image: SourceImage) -> None:
    dump_dir = output_dir / "ocr"
    dump_dir.mkdir(parents=True, exist_ok=True)
    safe_name = re.sub(r"[^A-Za-z0-9_.-]+", "_", source_image.path.stem)
    digest = hashlib.sha1(str(source_image.path).encode("utf-8")).hexdigest()[:10]
    with (dump_dir / f"{safe_name}_{digest}.json").open("w", encoding="utf-8") as handle:
        json.dump(ocr, handle, indent=2, ensure_ascii=False)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Extract Forza leaderboard rows from screenshots or video.")
    parser.add_argument("--input", required=True, type=Path, help="Screenshot, video, or folder containing both.")
    parser.add_argument("--track", required=True, help="Track/event name to attach to extracted rows.")
    parser.add_argument(
        "--pi-class",
        "--performance-class",
        dest="pi_class",
        required=True,
        help="Forza performance class, for example D, C, B, A, S1, S2, R, X, or All.",
    )
    parser.add_argument("--event-type", default="Rivals", help="Leaderboard type, defaults to Rivals.")
    parser.add_argument("--rivals-mode", default="", help="Rivals discipline/mode, for example Road Racing, Street, Dirt, Drag, Time Attack.")
    parser.add_argument("--game", default="Forza Horizon 6", help="Game name.")
    parser.add_argument("--profile", type=Path, default=DEFAULT_PROFILE, help="OCR crop/column profile JSON.")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR, help="Output folder.")
    parser.add_argument("--sqlite-path", type=Path, default=None, help="Optional shared SQLite database path.")
    parser.add_argument("--parquet-path", type=Path, default=None, help="Optional shared Parquet dataset path.")
    parser.add_argument("--frames-dir", type=Path, default=DEFAULT_FRAMES_DIR, help="Where extracted video frames are stored.")
    parser.add_argument("--frame-every", type=float, default=1.0, help="Seconds between sampled video frames.")
    parser.add_argument("--include-glob", default="", help="When input is a folder, only process matching direct child files, for example leaderboard_*.png.")
    parser.add_argument("--ocr-mode", choices=["fast", "precise"], default="precise", help="fast uses only page OCR; precise enables slower cell OCR fallback for repair passes.")
    parser.add_argument(
        "--ocr-backend",
        choices=["windows-batch", "windows-single", "rapidocr-cpu", "rapidocr-gpu"],
        default="windows-batch",
        help="windows-batch is stable; rapidocr-gpu uses ONNXRuntime CUDA when available.",
    )
    parser.add_argument("--dump-ocr", action="store_true", help="Write raw OCR JSON for calibration/debugging.")
    parser.add_argument("--keep-crops", action="store_true", help="Keep temporary OCR crop images in output_dir/crops.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    configured_threads = configure_native_thread_budget()
    if configured_threads:
        print(f"[forza-extract] native thread budget: {configured_threads}")
    profile = apply_ocr_mode(load_profile(args.profile), args.ocr_mode)
    output_dir = normalized_output_path(args.output_dir)
    frames_dir = normalized_output_path(args.frames_dir)
    metadata = {
        "game": args.game,
        "event_type": args.event_type,
        "rivals_mode": args.rivals_mode,
        "track": args.track,
        "performance_class": normalize_performance_class(args.pi_class),
        "pi_class": normalize_performance_class(args.pi_class),
    }

    input_path = args.input.resolve()
    if not input_path.exists():
        print(f"Input does not exist: {input_path}", file=sys.stderr)
        return 2

    source_images = collect_inputs(input_path, frames_dir, args.frame_every, args.include_glob)
    if not source_images:
        print(f"No supported images or videos found in {input_path}", file=sys.stderr)
        return 2

    all_rows: list[dict[str, Any]] = []
    language = profile.get("ocr_language", "en")

    with tempfile.TemporaryDirectory(prefix="forza_ocr_") as tmp:
        tmp_dir = Path(tmp)
        crops_dir = output_dir / "crops"
        if args.keep_crops:
            crops_dir.mkdir(parents=True, exist_ok=True)

        prepared_images: list[PreparedImage] = []
        for index, source_image in enumerate(source_images, start=1):
            print(f"[{index}/{len(source_images)}] prepare {source_image.path.name}")
            crop_path, _crop_pixels = crop_for_ocr(source_image.path, profile, tmp_dir)
            if args.keep_crops:
                kept_crop = crops_dir / crop_path.name
                shutil.copy2(crop_path, kept_crop)
                ocr_path = kept_crop
            else:
                ocr_path = crop_path
            prepared_images.append(PreparedImage(source_image=source_image, ocr_path=ocr_path))

        if args.ocr_backend == "rapidocr-gpu":
            ocr_results = run_rapidocr_batch([item.ocr_path for item in prepared_images], use_gpu=True)
        elif args.ocr_backend == "rapidocr-cpu":
            ocr_results = run_rapidocr_batch([item.ocr_path for item in prepared_images], use_gpu=False)
        elif args.ocr_backend == "windows-batch" and len(prepared_images) > 1:
            print(f"[forza-extract] OCR batch {len(prepared_images)} screenshots")
            ocr_results = run_windows_ocr_batch([item.ocr_path for item in prepared_images], language)
        else:
            ocr_results = {}
            for index, item in enumerate(prepared_images, start=1):
                print(f"[{index}/{len(prepared_images)}] OCR {item.source_image.path.name}")
                ocr_results[ocr_path_key(item.ocr_path)] = run_windows_ocr(item.ocr_path, language)

        for index, item in enumerate(prepared_images, start=1):
            print(f"[{index}/{len(prepared_images)}] parse {item.source_image.path.name}")
            ocr = ocr_results[ocr_path_key(item.ocr_path)]
            if args.dump_ocr:
                dump_ocr(ocr, output_dir, item.source_image)
            all_rows.extend(parse_ocr(ocr, profile, item.source_image, metadata, item.ocr_path, tmp_dir / "cells", language))

    rows = dedupe_rows(all_rows)
    sqlite_path = args.sqlite_path.resolve() if args.sqlite_path else None
    parquet_path = args.parquet_path.resolve() if args.parquet_path else None
    write_outputs(rows, output_dir, sqlite_path, parquet_path)
    write_rank_coverage_report(rows, output_dir)
    print(f"Extracted {len(rows)} unique leaderboard rows into {output_dir}")
    report_path = output_dir / "rank_coverage_report.json"
    print(f"Wrote rank coverage report at {report_path}")
    if sqlite_path:
        print(f"Updated shared SQLite database at {sqlite_path}")
    if parquet_path:
        print(f"Updated shared Parquet dataset at {parquet_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
