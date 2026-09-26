from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import re
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


SAFE_REQUEST_HEADERS = {
    "connection",
    "content-length",
    "content-type",
    "user-agent",
    "x-classname",
    "x-cmsinstance",
    "x-contractversion",
    "x-languagecodeid",
    "x-method",
    "x-version",
}
SAFE_RESPONSE_HEADERS = {
    "content-length",
    "content-type",
    "date",
    "server",
    "x-error-code",
    "xlsserver",
}
SENSITIVE_HEADER_FRAGMENTS = {
    "authorization",
    "cookie",
    "entitytoken",
    "idempotency",
    "requestid",
    "signature",
    "token",
    "traceparent",
    "userid",
    "validation",
}


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def child(element: ET.Element, name: str) -> ET.Element | None:
    return next((item for item in element if local_name(item.tag) == name), None)


def event_system(event: ET.Element) -> dict[str, Any]:
    system = child(event, "System")
    if system is None:
        return {}
    result: dict[str, Any] = {}
    for item in system:
        name = local_name(item.tag)
        result[name] = dict(item.attrib) if item.attrib else (item.text or "")
    return result


def event_data(event: ET.Element) -> dict[str, str]:
    data = child(event, "EventData")
    if data is None:
        return {}
    return {
        item.attrib.get("Name", ""): item.text or ""
        for item in data
        if item.attrib.get("Name")
    }


def parse_headers(raw: str, allowlist: set[str]) -> dict[str, str]:
    headers: dict[str, str] = {}
    for line in raw.replace("\r\n", "\n").split("\n"):
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        normalized = name.strip().lower()
        if normalized in allowlist:
            headers[name.strip()] = value.strip()
    return headers


def parse_hex_data(value: str) -> bytes:
    text = value.strip()
    if text.lower().startswith("0x"):
        text = text[2:]
    if not text:
        return b""
    if not re.fullmatch(r"[0-9a-fA-F]+", text):
        raise ValueError("ETW data field contains non-hexadecimal characters")
    if len(text) % 2:
        raise ValueError("ETW data field has an odd hexadecimal length")
    return bytes.fromhex(text)


def parse_number(value: str) -> int | None:
    text = value.strip()
    if not text:
        return None
    try:
        return int(text, 0)
    except ValueError:
        return None


def normalize_etw_timestamp(value: str) -> str:
    if not value:
        return ""
    # netsh trace convert on this VM emits +00:59 for a UTC+01:00 wall clock.
    normalized = re.sub(r"\+00:59$", "+01:00", value)
    try:
        return datetime.fromisoformat(normalized).astimezone(timezone.utc).isoformat()
    except ValueError:
        return ""


def read_rank_timeline(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    samples: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        sample = json.loads(line)
        timestamp = str(sample.get("timestamp", ""))
        try:
            sample["_timestamp"] = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        except ValueError:
            continue
        samples.append(sample)
    return samples


def nearest_rank_sample(created_at_utc: str, timeline: list[dict[str, Any]]) -> dict[str, Any] | None:
    if not created_at_utc or not timeline:
        return None
    try:
        request_time = datetime.fromisoformat(created_at_utc)
    except ValueError:
        return None
    sample = min(timeline, key=lambda item: abs((item["_timestamp"] - request_time).total_seconds()))
    return {
        "timestamp": sample.get("timestamp", ""),
        "delta_ms": round((sample["_timestamp"] - request_time).total_seconds() * 1000, 3),
        "status": sample.get("status", ""),
        "current_selected_rank": int(sample.get("current_selected_rank", 0) or 0),
        "navigation_verified_rank": int(sample.get("navigation_verified_rank", 0) or 0),
        "next_rank": int(sample.get("next_rank", 0) or 0),
        "max_scanned_rank": int(sample.get("max_scanned_rank", 0) or 0),
    }


def entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = Counter(data)
    length = len(data)
    return -sum((count / length) * math.log2(count / length) for count in counts.values())


def body_summary(data: bytes) -> dict[str, Any]:
    return {
        "size": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "entropy_bits_per_byte": round(entropy(data), 4),
        "prefix_hex": data[:32].hex(),
    }


def expected_length(headers: dict[str, str]) -> int | None:
    value = next(
        (header_value for name, header_value in headers.items() if name.lower() == "content-length"),
        None,
    )
    return parse_number(value or "")


def is_sensitive_name(name: str) -> bool:
    normalized = name.lower().replace("-", "")
    return any(fragment in normalized for fragment in SENSITIVE_HEADER_FRAGMENTS)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract sanitized Forza Scoreboard HTTP metadata and binary bodies from a netsh InternetClient XML trace."
    )
    parser.add_argument("--trace-xml", required=True, type=Path)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--process-id", type=int)
    parser.add_argument("--service-host", default="gameservices.fh6.forzamotorsport.net")
    parser.add_argument("--metadata-only", action="store_true")
    args = parser.parse_args()

    trace_path = args.trace_xml.resolve()
    output_dir = (args.output_dir or trace_path.parent / "winhttp_analysis").resolve()
    timeline = read_rank_timeline(trace_path.parent / "rank_timeline.jsonl")
    body_dir = output_dir / "http_bodies"
    output_dir.mkdir(parents=True, exist_ok=True)
    if not args.metadata_only:
        body_dir.mkdir(parents=True, exist_ok=True)
        for stale_body in body_dir.glob("scoreboard_*_*.bin"):
            stale_body.unlink()

    requests: list[dict[str, Any]] = []
    active_requests: dict[str, dict[str, Any]] = {}
    event_counts: Counter[str] = Counter()

    for _event, element in ET.iterparse(trace_path, events=("end",)):
        if local_name(element.tag) != "Event":
            continue

        system = event_system(element)
        execution = system.get("Execution")
        process_id = parse_number(str(execution.get("ProcessID", ""))) if isinstance(execution, dict) else None
        if args.process_id is not None and process_id != args.process_id:
            element.clear()
            continue

        event_id = str(system.get("EventID", ""))
        data = event_data(element)
        request_key = data.get("Request", "")
        if not request_key:
            element.clear()
            continue

        if event_id == "17":
            record = {
                "request_instance": len(requests) + 1,
                "process_id": process_id,
                "created_at_raw": "",
                "created_at_utc": "",
                "method": "",
                "uri": "",
                "request_headers": {},
                "response_headers": {},
                "request_chunks": [],
                "response_chunks": [],
                "request_chunk_lengths": [],
                "response_chunk_lengths": [],
            }
            requests.append(record)
            active_requests[request_key] = record
        else:
            record = active_requests.get(request_key)
            if record is None:
                element.clear()
                continue
        event_counts[event_id] += 1

        time_created = system.get("TimeCreated")
        timestamp = time_created.get("SystemTime", "") if isinstance(time_created, dict) else ""

        if event_id == "17":
            record["created_at_raw"] = timestamp
            record["created_at_utc"] = normalize_etw_timestamp(timestamp)
            record["method"] = data.get("Method", "")
            record["uri"] = data.get("URI", "")
        elif event_id == "100":
            record["request_headers"] = parse_headers(data.get("Headers", ""), SAFE_REQUEST_HEADERS)
        elif event_id == "111":
            chunk = parse_hex_data(data.get("Data", ""))
            record["request_chunks"].append(chunk)
            record["request_chunk_lengths"].append(parse_number(data.get("Length", "")))
        elif event_id == "101":
            record["response_headers"] = parse_headers(data.get("Headers", ""), SAFE_RESPONSE_HEADERS)
        elif event_id == "129":
            chunk = parse_hex_data(data.get("Data", ""))
            record["response_chunks"].append(chunk)
            record["response_chunk_lengths"].append(parse_number(data.get("Length", "")))

        element.clear()

    selected: list[dict[str, Any]] = []
    for record in requests:
        uri = str(record["uri"])
        request_headers = record["request_headers"]
        host = (urlparse(uri).hostname or "").lower()
        class_name = next(
            (value for name, value in request_headers.items() if name.lower() == "x-classname"),
            "",
        )
        method_name = next(
            (value for name, value in request_headers.items() if name.lower() == "x-method"),
            "",
        )
        if host != args.service_host.lower():
            continue
        if "scoreboard" not in class_name.lower() and method_name.lower() != "getrows":
            continue

        request_body = b"".join(record.pop("request_chunks"))
        response_body = b"".join(record.pop("response_chunks"))
        record.pop("request_chunk_lengths")
        record.pop("response_chunk_lengths")

        request_info = body_summary(request_body)
        response_info = body_summary(response_body)
        request_info["expected_size"] = expected_length(record["request_headers"])
        response_info["expected_size"] = expected_length(record["response_headers"])
        request_info["size_matches_header"] = (
            request_info["expected_size"] is None or request_info["expected_size"] == request_info["size"]
        )
        response_info["size_matches_header"] = (
            response_info["expected_size"] is None or response_info["expected_size"] == response_info["size"]
        )

        index = len(selected) + 1
        if not args.metadata_only:
            request_name = f"scoreboard_{index:04d}_request.bin"
            response_name = f"scoreboard_{index:04d}_response.bin"
            (body_dir / request_name).write_bytes(request_body)
            (body_dir / response_name).write_bytes(response_body)
            request_info["path"] = str(Path("http_bodies") / request_name)
            response_info["path"] = str(Path("http_bodies") / response_name)

        record["request_body"] = request_info
        record["response_body"] = response_info
        record["nearest_rank_sample"] = nearest_rank_sample(record["created_at_utc"], timeline)
        selected.append(record)

    for record in selected:
        for header_group in ("request_headers", "response_headers"):
            for header_name in record[header_group]:
                if is_sensitive_name(header_name):
                    raise RuntimeError(f"Sensitive header escaped sanitization: {header_name}")

    request_prefixes = Counter(
        record["request_body"]["prefix_hex"][:32]
        for record in selected
        if record["request_body"]["size"]
    )
    response_prefixes = Counter(
        record["response_body"]["prefix_hex"][:32]
        for record in selected
        if record["response_body"]["size"]
    )
    report = {
        "trace_xml": str(trace_path),
        "process_id_filter": args.process_id,
        "service_host": args.service_host,
        "scoreboard_request_count": len(selected),
        "event_counts": dict(event_counts),
        "request_sizes": dict(Counter(record["request_body"]["size"] for record in selected)),
        "response_sizes": dict(Counter(record["response_body"]["size"] for record in selected)),
        "request_prefixes_16_bytes": dict(request_prefixes),
        "response_prefixes_16_bytes": dict(response_prefixes),
        "rank_timeline_sample_count": len(timeline),
        "average_request_entropy": round(
            sum(record["request_body"]["entropy_bits_per_byte"] for record in selected) / len(selected),
            4,
        )
        if selected
        else 0.0,
        "average_response_entropy": round(
            sum(record["response_body"]["entropy_bits_per_byte"] for record in selected) / len(selected),
            4,
        )
        if selected
        else 0.0,
        "requests": selected,
        "security": {
            "raw_trace_contains_sensitive_session_headers": True,
            "sensitive_headers_written_to_this_report": False,
            "note": "Keep the raw ETL/XML private. This report uses strict header allowlists.",
        },
    }
    (output_dir / "scoreboard_http.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    rows = []
    for index, record in enumerate(selected, start=1):
        rank_sample = record.get("nearest_rank_sample") or {}
        rows.append(
            {
                "index": index,
                "created_at_utc": record["created_at_utc"],
                "created_at_raw": record["created_at_raw"],
                "process_id": record["process_id"],
                "method": record["method"],
                "uri": record["uri"],
                "x_classname": record["request_headers"].get("X-ClassName", ""),
                "x_method": record["request_headers"].get("X-Method", ""),
                "request_size": record["request_body"]["size"],
                "request_entropy": record["request_body"]["entropy_bits_per_byte"],
                "response_size": record["response_body"]["size"],
                "response_entropy": record["response_body"]["entropy_bits_per_byte"],
                "response_error_code": record["response_headers"].get("X-Error-Code", ""),
                "nearest_selected_rank": rank_sample.get("current_selected_rank", 0),
                "nearest_next_rank": rank_sample.get("next_rank", 0),
                "nearest_max_scanned_rank": rank_sample.get("max_scanned_rank", 0),
                "rank_sample_delta_ms": rank_sample.get("delta_ms", ""),
            }
        )
    with (output_dir / "scoreboard_http.csv").open("w", newline="", encoding="utf-8") as handle:
        fieldnames = list(rows[0]) if rows else ["index"]
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    (output_dir / "SENSITIVE_DATA_NOTICE.txt").write_text(
        "The source ETL/XML trace may contain live account and session headers.\n"
        "Do not publish or commit the raw trace. The generated JSON/CSV uses header allowlists.\n"
        "Extracted binary request bodies should also be treated as private until their format is understood.\n",
        encoding="ascii",
    )
    print(json.dumps({key: value for key, value in report.items() if key != "requests"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
