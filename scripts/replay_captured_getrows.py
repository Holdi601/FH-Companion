from __future__ import annotations

import argparse
import hashlib
import json
import ssl
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def child(element: ET.Element, name: str) -> ET.Element | None:
    return next((item for item in element if local_name(item.tag) == name), None)


def event_id(event: ET.Element) -> str:
    system = child(event, "System")
    if system is None:
        return ""
    return next((item.text or "" for item in system if local_name(item.tag) == "EventID"), "")


def event_data(event: ET.Element) -> dict[str, str]:
    data = child(event, "EventData")
    if data is None:
        return {}
    return {
        item.attrib.get("Name", ""): item.text or ""
        for item in data
        if item.attrib.get("Name")
    }


def parse_headers(raw: str) -> dict[str, str]:
    headers: dict[str, str] = {}
    for line in raw.replace("\r\n", "\n").split("\n"):
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        headers[name.strip()] = value.strip()
    return headers


def parse_hex_data(value: str) -> bytes:
    text = value.strip()
    if text.lower().startswith("0x"):
        text = text[2:]
    return bytes.fromhex(text)


def read_getrows_requests(trace_path: Path) -> list[dict[str, Any]]:
    requests: list[dict[str, Any]] = []
    active: dict[str, dict[str, Any]] = {}
    for _event, element in ET.iterparse(trace_path, events=("end",)):
        if local_name(element.tag) != "Event":
            continue
        data = event_data(element)
        request_key = data.get("Request", "")
        current_event = event_id(element)
        if not request_key:
            element.clear()
            continue

        if current_event == "17":
            record = {
                "method": data.get("Method", ""),
                "uri": data.get("URI", ""),
                "headers": {},
                "body_chunks": [],
            }
            requests.append(record)
            active[request_key] = record
        else:
            record = active.get(request_key)
            if record is None:
                element.clear()
                continue

        if current_event == "100":
            record["headers"] = parse_headers(data.get("Headers", ""))
        elif current_event == "111":
            record["body_chunks"].append(parse_hex_data(data.get("Data", "")))
        element.clear()

    result = []
    for record in requests:
        headers = record["headers"]
        if headers.get("X-Method", "").lower() != "getrows":
            continue
        if "gameservices.fh6.forzamotorsport.net" not in record["uri"].lower():
            continue
        record["body"] = b"".join(record.pop("body_chunks"))
        result.append(record)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Replay exactly one captured FH6 Scoreboard.GetRows request without exporting its credentials."
    )
    parser.add_argument("--trace-xml", required=True, type=Path)
    parser.add_argument(
        "--confirm-live-request",
        action="store_true",
        help="Required acknowledgement that this sends one real request to the FH6 service.",
    )
    parser.add_argument("--timeout-seconds", type=int, default=30)
    args = parser.parse_args()

    if not args.confirm_live_request:
        parser.error("--confirm-live-request is required")

    requests = read_getrows_requests(args.trace_xml.resolve())
    if not requests:
        raise RuntimeError("No Scoreboard.GetRows request was found in the trace.")
    captured = requests[-1]
    body = captured["body"]
    if not body:
        raise RuntimeError("The selected captured request has no body.")

    excluded = {"host", "content-length", "connection", "accept-encoding"}
    headers = {
        name: value
        for name, value in captured["headers"].items()
        if name.lower() not in excluded and value
    }
    request = urllib.request.Request(
        captured["uri"],
        data=body,
        headers=headers,
        method=captured["method"] or "POST",
    )

    status = 0
    response_headers: Any = {}
    response_body = b""
    error = ""
    try:
        with urllib.request.urlopen(
            request,
            timeout=args.timeout_seconds,
            context=ssl.create_default_context(),
        ) as response:
            status = int(response.status)
            response_headers = response.headers
            response_body = response.read()
    except urllib.error.HTTPError as exc:
        status = int(exc.code)
        response_headers = exc.headers
        response_body = exc.read()
        error = str(exc.reason)
    except urllib.error.URLError as exc:
        error = str(exc.reason)

    report = {
        "sent_requests": 1,
        "captured_request_size": len(body),
        "captured_request_sha256": hashlib.sha256(body).hexdigest(),
        "http_status": status,
        "response_size": len(response_body),
        "response_sha256": hashlib.sha256(response_body).hexdigest() if response_body else "",
        "content_type": response_headers.get("Content-Type", "") if response_headers else "",
        "xls_error_code": response_headers.get("X-Error-Code", "") if response_headers else "",
        "error": error,
        "credentials_exported": False,
        "response_body_saved": False,
    }
    print(json.dumps(report, indent=2))
    return 0 if 200 <= status < 300 else 1


if __name__ == "__main__":
    raise SystemExit(main())
