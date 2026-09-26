from __future__ import annotations

import argparse
import threading
from pathlib import Path
from typing import Any

import frida


AGENT_SOURCE = r"""
const moduleName = %MODULE_NAME%;
const rva = %RVA%;
const size = %SIZE%;
const module = Process.findModuleByName(moduleName);
if (module === null) {
  send({ type: 'error', error: 'module not found', module: moduleName });
} else {
  const address = module.base.add(rva);
  try {
    const data = address.readByteArray(size);
    send({
      type: 'dump',
      module: module.name,
      base: module.base.toString(),
      rva: '0x' + rva.toString(16),
      address: address.toString(),
      size: size
    }, data);
  } catch (error) {
    send({ type: 'error', error: String(error), address: address.toString() });
  }
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="Read a small module RVA range.")
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--module", default="forzahorizon6.exe")
    parser.add_argument("--rva", required=True, type=lambda value: int(value, 0))
    parser.add_argument("--size", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    if args.size <= 0 or args.size > 16 * 1024 * 1024:
        raise ValueError("size must be between 1 and 16777216")

    done = threading.Event()
    failure: list[str] = []

    def on_message(message: dict[str, Any], data: bytes | None) -> None:
        payload = message.get("payload", {})
        if message.get("type") == "error":
            failure.append(str(message))
        elif payload.get("type") == "error":
            failure.append(str(payload.get("error")))
        elif payload.get("type") == "dump" and data is not None:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_bytes(bytes(data))
            print(payload, flush=True)
        done.set()

    source = (
        AGENT_SOURCE.replace("%MODULE_NAME%", repr(args.module))
        .replace("%RVA%", str(args.rva))
        .replace("%SIZE%", str(args.size))
    )
    session = frida.attach(args.pid)
    try:
        script = session.create_script(source)
        script.on("message", on_message)
        script.load()
        if not done.wait(10):
            raise TimeoutError("Timed out waiting for memory dump")
        if failure:
            raise RuntimeError(failure[0])
    finally:
        try:
            session.detach()
        except frida.InvalidOperationError:
            pass

    if not args.output.exists() or args.output.stat().st_size != args.size:
        raise RuntimeError("Memory dump was not written at the requested size")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
