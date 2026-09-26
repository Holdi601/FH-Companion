from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Hold a Frida session without loading hooks or modifying target code."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--hold-seconds", type=int, default=45)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    detached = threading.Event()
    detached_info: dict[str, Any] = {}
    started_at = time.time()

    session = frida.attach(args.pid)
    try:
        def on_detached(reason: str, crash: Any) -> None:
            detached_info["reason"] = reason
            detached_info["crash"] = str(crash) if crash is not None else None
            detached.set()

        session.on("detached", on_detached)
        detached.wait(args.hold_seconds)
        detached_early = detached.is_set()

        result = {
            "pid": args.pid,
            "hold_seconds": args.hold_seconds,
            "elapsed_seconds": round(time.time() - started_at, 3),
            "detached_early": detached_early,
            "detached": detached_info,
        }
    finally:
        if not result.get("detached_early", False):
            try:
                session.detach()
            except frida.InvalidOperationError:
                result["detached_early"] = True
                result["detached"] = {"reason": "target unavailable during detach"}

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 2 if result["detached_early"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
