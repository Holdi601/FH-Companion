from __future__ import annotations

import argparse
import csv
import json
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from scapy.layers.inet import IP, TCP, UDP
from scapy.layers.inet6 import IPv6
from scapy.utils import PcapNgReader


def read_timeline(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    return [
        json.loads(line)
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if line.strip()
    ]


def forza_endpoints(timeline: list[dict[str, Any]]) -> set[tuple[str, int]]:
    result: set[tuple[str, int]] = set()
    for sample in timeline:
        for connection in sample.get("tcp", []):
            address = str(connection.get("remote_address") or connection.get("RemoteAddress") or "")
            port = int(connection.get("remote_port") or connection.get("RemotePort") or 0)
            state = str(connection.get("state") or connection.get("State") or "")
            if address not in {"", "0.0.0.0", "::"} and port > 0 and "bound" not in state.lower():
                result.add((address, port))
    return result


def packet_endpoint(src: str, sport: int, dst: str, dport: int, local_addresses: set[str]) -> tuple[str, int, str]:
    if src in local_addresses:
        return dst, dport, "out"
    return src, sport, "in"


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze a Forza VM pktmon network probe.")
    parser.add_argument("--probe-dir", required=True, type=Path)
    args = parser.parse_args()

    probe_dir = args.probe_dir.resolve()
    pcap_path = probe_dir / "forza_scroll.pcapng"
    timeline_path = probe_dir / "timeline.jsonl"
    if not pcap_path.exists():
        raise FileNotFoundError(pcap_path)

    timeline = read_timeline(timeline_path)
    endpoints = forza_endpoints(timeline)
    local_addresses = {
        str(connection.get("local_address") or connection.get("LocalAddress") or "")
        for sample in timeline
        for connection in sample.get("tcp", [])
    }
    local_addresses.discard("")
    local_addresses.discard("0.0.0.0")
    local_addresses.discard("::")

    flows: dict[tuple[str, str, int, str, int], dict[str, Any]] = defaultdict(
        lambda: {
            "packets": 0,
            "bytes": 0,
            "payload_bytes": 0,
            "first_timestamp": None,
            "last_timestamp": None,
            "payload_sizes": Counter(),
            "tls_records": Counter(),
        }
    )
    per_second: dict[int, dict[str, int]] = defaultdict(
        lambda: {
            "forza_in_bytes": 0,
            "forza_out_bytes": 0,
            "forza_in_payload": 0,
            "forza_out_payload": 0,
            "all_bytes": 0,
            "packets": 0,
        }
    )
    protocol_counts: Counter[str] = Counter()
    first_timestamp: float | None = None
    last_timestamp: float | None = None

    for packet in PcapNgReader(str(pcap_path)):
        timestamp = float(packet.time)
        first_timestamp = timestamp if first_timestamp is None else min(first_timestamp, timestamp)
        last_timestamp = timestamp if last_timestamp is None else max(last_timestamp, timestamp)
        second = int(timestamp)
        per_second[second]["all_bytes"] += len(packet)
        per_second[second]["packets"] += 1

        if IP in packet:
            network = packet[IP]
            src, dst = network.src, network.dst
        elif IPv6 in packet:
            network = packet[IPv6]
            src, dst = network.src, network.dst
        else:
            protocol_counts["non_ip"] += 1
            continue

        if TCP in packet:
            transport = packet[TCP]
            protocol = "tcp"
        elif UDP in packet:
            transport = packet[UDP]
            protocol = "udp"
        else:
            protocol_counts["other_ip"] += 1
            continue

        protocol_counts[protocol] += 1
        sport, dport = int(transport.sport), int(transport.dport)
        key = (protocol, src, sport, dst, dport)
        flow = flows[key]
        flow["packets"] += 1
        flow["bytes"] += len(packet)
        flow["first_timestamp"] = timestamp if flow["first_timestamp"] is None else min(flow["first_timestamp"], timestamp)
        flow["last_timestamp"] = timestamp if flow["last_timestamp"] is None else max(flow["last_timestamp"], timestamp)

        payload = bytes(transport.payload)
        flow["payload_bytes"] += len(payload)
        if payload:
            flow["payload_sizes"][len(payload)] += 1
            if (
                len(payload) >= 5
                and payload[0] in {20, 21, 22, 23}
                and payload[1:3] in {b"\x03\x01", b"\x03\x02", b"\x03\x03", b"\x03\x04"}
            ):
                flow["tls_records"][(payload[0], int.from_bytes(payload[3:5], "big"))] += 1

        remote_address, remote_port, direction = packet_endpoint(src, sport, dst, dport, local_addresses)
        if (remote_address, remote_port) in endpoints:
            per_second[second][f"forza_{direction}_bytes"] += len(packet)
            per_second[second][f"forza_{direction}_payload"] += len(payload)

    flow_rows: list[dict[str, Any]] = []
    for key, data in flows.items():
        protocol, src, sport, dst, dport = key
        remote_address, remote_port, direction = packet_endpoint(src, sport, dst, dport, local_addresses)
        flow_rows.append(
            {
                "protocol": protocol,
                "src": src,
                "src_port": sport,
                "dst": dst,
                "dst_port": dport,
                "direction": direction,
                "remote_address": remote_address,
                "remote_port": remote_port,
                "is_forza_endpoint": (remote_address, remote_port) in endpoints,
                "packets": data["packets"],
                "bytes": data["bytes"],
                "payload_bytes": data["payload_bytes"],
                "duration_seconds": round(data["last_timestamp"] - data["first_timestamp"], 3),
                "common_payload_sizes": json.dumps(data["payload_sizes"].most_common(8)),
                "tls_record_starts": json.dumps(
                    [
                        {"content_type": content_type, "record_length": length, "count": count}
                        for (content_type, length), count in data["tls_records"].most_common()
                    ]
                ),
            }
        )
    flow_rows.sort(key=lambda item: item["bytes"], reverse=True)

    endpoint_data: dict[tuple[str, int], dict[str, Any]] = defaultdict(
        lambda: {
            "in_packets": 0,
            "out_packets": 0,
            "in_bytes": 0,
            "out_bytes": 0,
            "in_payload_bytes": 0,
            "out_payload_bytes": 0,
            "in_tls_records": Counter(),
            "out_tls_records": Counter(),
        }
    )
    for key, data in flows.items():
        _protocol, src, sport, dst, dport = key
        remote_address, remote_port, direction = packet_endpoint(src, sport, dst, dport, local_addresses)
        if (remote_address, remote_port) not in endpoints:
            continue
        aggregate = endpoint_data[(remote_address, remote_port)]
        aggregate[f"{direction}_packets"] += data["packets"]
        aggregate[f"{direction}_bytes"] += data["bytes"]
        aggregate[f"{direction}_payload_bytes"] += data["payload_bytes"]
        aggregate[f"{direction}_tls_records"].update(data["tls_records"])

    endpoint_rows: list[dict[str, Any]] = []
    for (address, port), data in endpoint_data.items():
        repeated_outbound_app_records = sum(
            count
            for (content_type, _length), count in data["out_tls_records"].items()
            if content_type == 23 and count >= 3
        )
        endpoint_rows.append(
            {
                "address": address,
                "port": port,
                "in_packets": data["in_packets"],
                "out_packets": data["out_packets"],
                "in_bytes": data["in_bytes"],
                "out_bytes": data["out_bytes"],
                "in_payload_bytes": data["in_payload_bytes"],
                "out_payload_bytes": data["out_payload_bytes"],
                "repeated_outbound_tls_app_records": repeated_outbound_app_records,
                "outbound_tls_records": json.dumps(
                    [
                        {"content_type": content_type, "record_length": length, "count": count}
                        for (content_type, length), count in data["out_tls_records"].most_common()
                    ]
                ),
                "inbound_tls_records": json.dumps(
                    [
                        {"content_type": content_type, "record_length": length, "count": count}
                        for (content_type, length), count in data["in_tls_records"].most_common()
                    ]
                ),
            }
        )
    endpoint_rows.sort(
        key=lambda item: (item["repeated_outbound_tls_app_records"], item["in_payload_bytes"] + item["out_payload_bytes"]),
        reverse=True,
    )

    with (probe_dir / "flows.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(flow_rows[0].keys()) if flow_rows else ["protocol"])
        writer.writeheader()
        writer.writerows(flow_rows)

    with (probe_dir / "per_second.csv").open("w", newline="", encoding="utf-8") as handle:
        fieldnames = ["timestamp", "forza_in_bytes", "forza_out_bytes", "forza_in_payload", "forza_out_payload", "all_bytes", "packets"]
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for second, data in sorted(per_second.items()):
            writer.writerow(
                {
                    "timestamp": datetime.fromtimestamp(second, timezone.utc).isoformat(),
                    **data,
                }
            )

    with (probe_dir / "endpoints.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(endpoint_rows[0].keys()) if endpoint_rows else ["address"])
        writer.writeheader()
        writer.writerows(endpoint_rows)

    forza_flows = [row for row in flow_rows if row["is_forza_endpoint"]]
    dominant = max(forza_flows, key=lambda row: row["payload_bytes"], default=None)
    leaderboard_candidate = endpoint_rows[0] if endpoint_rows else None
    rank_start = timeline[0].get("verified_rank", 0) if timeline else 0
    rank_end = timeline[-1].get("verified_rank", 0) if timeline else 0
    report = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "pcap_path": str(pcap_path),
        "duration_seconds": round((last_timestamp or 0) - (first_timestamp or 0), 3),
        "protocol_counts": dict(protocol_counts),
        "forza_endpoints": [
            {"address": address, "port": port}
            for address, port in sorted(endpoints)
        ],
        "timeline": {
            "sample_count": len(timeline),
            "rank_start": rank_start,
            "rank_end": rank_end,
            "rank_delta": rank_end - rank_start,
        },
        "dominant_forza_flow": dominant,
        "leaderboard_candidate_endpoint": leaderboard_candidate,
        "observations": [
            "TCP payloads beginning with TLS content type 23 are encrypted TLS application data.",
            "Repeated TLS record lengths can fingerprint leaderboard requests but do not reveal row contents.",
            "Plaintext extraction requires supported TLS session secrets, an authorized debug proxy, or instrumentation at the HTTP API boundary.",
        ],
    }
    (probe_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
