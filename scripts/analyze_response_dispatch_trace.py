from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter
from pathlib import Path
from typing import Any


def shannon_entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = Counter(data)
    length = len(data)
    return -sum(
        (count / length) * math.log2(count / length)
        for count in counts.values()
    )


def classify_body(data: bytes) -> str:
    stripped = data.lstrip()
    if stripped.startswith((b"{", b"[")):
        return "json"
    if data.startswith(b"\x1f\x8b"):
        return "gzip"
    if data.startswith(b"PK\x03\x04"):
        return "zip"
    return "binary"


def payload_from_record(record: dict[str, Any]) -> dict[str, Any]:
    message = record.get("message", {})
    payload = message.get("payload", {})
    return payload if isinstance(payload, dict) else {}


def forza_frames(payload: dict[str, Any]) -> list[str]:
    result: list[str] = []
    for frame in payload.get("backtrace", []):
        module = str(frame.get("module", ""))
        if module.lower() != "forzahorizon6.exe":
            continue
        value = f"{module}+{frame.get('offset', '')}"
        if value not in result:
            result.append(value)
    return result


def body_summary(
    data: bytes,
    *,
    call_handle: str,
    event_type: str,
    timestamp: float,
    chunk_count: int,
    frames: list[str],
) -> dict[str, Any]:
    return {
        "type": event_type,
        "call_handle": call_handle,
        "received_at": timestamp,
        "size": len(data),
        "chunk_count": chunk_count,
        "classification": classify_body(data),
        "entropy": round(shannon_entropy(data), 4),
        "multiple_of_16": len(data) % 16 == 0,
        "sha256": hashlib.sha256(data).hexdigest(),
        "prefix_hex": data[:16].hex(),
        "forza_frames": frames,
    }


def read_records(path: Path) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid JSON on line {line_number}: {exc}") from exc
    return records


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Summarize libHttpClient response traces without exposing body data."
    )
    parser.add_argument("trace", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--code-dir",
        type=Path,
        help="Write captured caller code pages as binary files.",
    )
    parser.add_argument(
        "--append-gap-seconds",
        type=float,
        default=2.0,
        help="Start a new inferred response after this idle gap on a reused handle.",
    )
    args = parser.parse_args()

    records = read_records(args.trace)
    complete: list[dict[str, Any]] = []
    append_segments: list[dict[str, Any]] = []
    code_pages: list[dict[str, Any]] = []
    open_segments: dict[str, dict[str, Any]] = {}

    for record in records:
        payload = payload_from_record(record)
        event_type = payload.get("type")
        timestamp = float(record.get("received_at", 0))
        call_handle = str(payload.get("callHandle", ""))
        data_hex = payload.get("dataHex")

        if event_type == "caller-code-page":
            data = bytes.fromhex(data_hex) if isinstance(data_hex, str) else b""
            page_rva = str(payload.get("pageRva", "unknown"))
            filename = f"caller_code_{page_rva.replace('0x', '')}.bin"
            if args.code_dir:
                args.code_dir.mkdir(parents=True, exist_ok=True)
                (args.code_dir / filename).write_bytes(data)
            code_pages.append(
                {
                    "page_rva": page_rva,
                    "caller": payload.get("caller", {}),
                    "size": len(data),
                    "sha256": hashlib.sha256(data).hexdigest(),
                    "output": (
                        str((args.code_dir / filename).resolve())
                        if args.code_dir
                        else None
                    ),
                }
            )
            continue

        if event_type in {"response-body-complete", "response-string-complete"}:
            data = bytes.fromhex(data_hex) if isinstance(data_hex, str) else b""
            complete.append(
                body_summary(
                    data,
                    call_handle=call_handle,
                    event_type=event_type,
                    timestamp=timestamp,
                    chunk_count=1,
                    frames=forza_frames(payload),
                )
            )
            continue

        if event_type != "response-append":
            continue

        data = bytes.fromhex(data_hex) if isinstance(data_hex, str) else b""
        segment = open_segments.get(call_handle)
        if (
            segment is None
            or timestamp - float(segment["last_at"]) > args.append_gap_seconds
        ):
            segment = {
                "call_handle": call_handle,
                "first_at": timestamp,
                "last_at": timestamp,
                "chunks": [],
                "frames": [],
            }
            open_segments[call_handle] = segment
            append_segments.append(segment)

        segment["last_at"] = timestamp
        segment["chunks"].append(data)
        for frame in forza_frames(payload):
            if frame not in segment["frames"]:
                segment["frames"].append(frame)

    inferred = [
        body_summary(
            b"".join(segment["chunks"]),
            call_handle=segment["call_handle"],
            event_type="inferred-response-from-appends",
            timestamp=float(segment["first_at"]),
            chunk_count=len(segment["chunks"]),
            frames=segment["frames"],
        )
        for segment in append_segments
    ]

    report = {
        "trace": str(args.trace.resolve()),
        "records": len(records),
        "complete_responses": complete,
        "inferred_responses": inferred,
        "code_pages": code_pages,
    }
    text = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
        print(args.output.resolve())
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
