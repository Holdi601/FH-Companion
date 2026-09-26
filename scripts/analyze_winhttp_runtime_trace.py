from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


SAFE_HEADERS = {
    "content-type",
    "user-agent",
    "x-classname",
    "x-method",
    "x-cmsinstance",
    "x-contractversion",
    "x-languagecodeid",
    "x-version",
}

SENSITIVE_HEADERS = {
    "authorization",
    "signature",
    "x-authorization",
    "x-entitytoken",
    "x-validation",
}


def parse_headers(text: str | dict[str, Any] | None) -> dict[str, Any]:
    result: dict[str, Any] = {}
    if isinstance(text, dict):
        return dict(text)
    if not text:
        return result
    for line in text.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        result[key.strip()] = value.strip()
    return result


def sanitize_headers(headers: dict[str, Any]) -> dict[str, Any]:
    clean: dict[str, Any] = {}
    for key, value in headers.items():
        lower = key.lower()
        if isinstance(value, dict):
            clean[key] = value
            continue
        if lower in SAFE_HEADERS:
            clean[key] = value
        elif lower in SENSITIVE_HEADERS:
            clean[key] = {
                "present": True,
                "length": len(value),
                "sha256_12": hashlib.sha256(value.encode("utf-8")).hexdigest()[:12],
            }
        elif lower in {"traceparent", "x-requestid", "x-idempotencyid", "x-requesttimestamp", "x-userid"}:
            clean[key] = {
                "present": True,
                "length": len(value),
            }
    return clean


def forza_offsets(backtrace: list[dict[str, Any]]) -> list[str]:
    offsets: list[str] = []
    for frame in backtrace or []:
        if str(frame.get("module", "")).lower() == "forzahorizon6.exe":
            offsets.append(str(frame.get("offset", "")))
    return offsets


def first_text_from_hex(hex_text: str | None, limit: int = 128) -> str:
    if not hex_text:
        return ""
    try:
        raw = bytes.fromhex(hex_text[: limit * 2])
    except ValueError:
        return ""
    return "".join(chr(b) if 32 <= b < 127 else "." for b in raw)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Sanitize and summarize a Frida WinHTTP runtime trace."
    )
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-json", required=True, type=Path)
    parser.add_argument("--output-csv", required=True, type=Path)
    args = parser.parse_args()

    requests: list[dict[str, Any]] = []
    active_by_handle: dict[str, dict[str, Any]] = {}
    api_counts: Counter[str] = Counter()
    method_counts: Counter[str] = Counter()
    class_counts: Counter[str] = Counter()

    for line in args.input.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        item = json.loads(line)
        payload = item.get("message", {}).get("payload", {})
        if payload.get("type") != "winhttp":
            continue
        api = str(payload.get("api", ""))
        hrequest = str(payload.get("hRequest", ""))
        if api == "WinHttpAddRequestHeaders" or hrequest not in active_by_handle:
            request = {
                "sequence": len(requests) + 1,
                "hRequest": hrequest,
                "headers": {},
                "apis": Counter(),
                "send_optional_lengths": [],
                "write_lengths": [],
                "read_requests": [],
                "body_previews": [],
                "forza_offsets": Counter(),
            }
            requests.append(request)
            active_by_handle[hrequest] = request
        else:
            request = active_by_handle[hrequest]
        request["apis"][api] += 1
        api_counts[api] += 1

        for offset in forza_offsets(payload.get("backtrace", [])):
            request["forza_offsets"][offset] += 1

        if "headers" in payload:
            parsed = parse_headers(payload.get("headers"))
            request["headers"].update(sanitize_headers(parsed))
            method = parsed.get("X-Method")
            class_name = parsed.get("X-ClassName")
            if method:
                method_counts[method] += 1
            if class_name:
                class_counts[class_name] += 1

        if api == "WinHttpSendRequest":
            request["send_optional_lengths"].append(payload.get("optionalLength"))
        elif api == "WinHttpWriteData":
            request["write_lengths"].append(payload.get("length"))
        elif api == "WinHttpReadData":
            request["read_requests"].append(
                {
                    "requested": payload.get("requested"),
                    "bytesRead": payload.get("bytesRead"),
                }
            )

        content_type = str(request["headers"].get("Content-Type", "")).lower()
        if payload.get("dataHex") and content_type == "bin/xtsw":
            request["body_previews"].append(
                {
                    "api": api,
                    "hex_prefix": str(payload.get("dataHex", ""))[:96],
                    "ascii_prefix": first_text_from_hex(payload.get("dataHex")),
                }
            )

    records: list[dict[str, Any]] = []
    for request in requests:
        headers = request["headers"]
        records.append(
            {
                "sequence": request["sequence"],
                "hRequest": request["hRequest"],
                "class": headers.get("X-ClassName", ""),
                "method": headers.get("X-Method", ""),
                "content_type": headers.get("Content-Type", ""),
                "apis": dict(request["apis"]),
                "send_optional_lengths": request["send_optional_lengths"],
                "write_lengths": request["write_lengths"],
                "read_requests": request["read_requests"][:12],
                "forza_offsets": dict(request["forza_offsets"].most_common(12)),
                "headers": headers,
                "body_previews": request["body_previews"][:4],
            }
        )
    records.sort(key=lambda item: int(item["sequence"]))

    report = {
        "input": str(args.input.resolve()),
        "request_count": len(records),
        "api_counts": dict(api_counts),
        "method_counts": dict(method_counts),
        "class_counts": dict(class_counts),
        "interesting_offsets": dict(
            Counter(
                offset
                for record in records
                for offset, count in record["forza_offsets"].items()
                for _ in range(count)
            ).most_common(30)
        ),
        "requests": records,
    }

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    with args.output_csv.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=[
                "hRequest",
                "sequence",
                "class",
                "method",
                "content_type",
                "send_optional_lengths",
                "write_lengths",
                "top_forza_offsets",
            ],
        )
        writer.writeheader()
        for record in records:
            writer.writerow(
                {
                    "hRequest": record["hRequest"],
                    "sequence": record["sequence"],
                    "class": record["class"],
                    "method": record["method"],
                    "content_type": record["content_type"],
                    "send_optional_lengths": json.dumps(record["send_optional_lengths"]),
                    "write_lengths": json.dumps(record["write_lengths"]),
                    "top_forza_offsets": json.dumps(record["forza_offsets"]),
                }
            )

    print(args.output_json)
    print(args.output_csv)
    print(json.dumps({key: report[key] for key in ("request_count", "api_counts", "method_counts", "interesting_offsets")}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
