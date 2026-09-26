from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


AGENT_SOURCE = r"""
const moduleName = 'libHttpClient.dll';
const gameModuleName = 'forzahorizon6.exe';
const triggerMinBody = %TRIGGER_MIN_BODY%;
const sampleRva = %SAMPLE_RVA%;
const sampleSize = %SAMPLE_SIZE%;
const intervalMs = %INTERVAL_MS%;
const durationMs = %DURATION_MS%;
const maxUnique = %MAX_UNIQUE%;
let samplingStarted = false;

function bytesToHex(buffer) {
  const view = new Uint8Array(buffer);
  let out = '';
  for (let i = 0; i < view.length; i++)
    out += view[i].toString(16).padStart(2, '0');
  return out;
}

function startSampling(triggerSize) {
  if (samplingStarted)
    return;
  samplingStarted = true;
  const game = Process.getModuleByName(gameModuleName);
  const target = game.base.add(sampleRva);
  const seen = new Set();
  let unique = 0;
  const startedAt = Date.now();

  send({
    type: 'sampling-started',
    triggerSize: triggerSize,
    module: game.name,
    base: game.base.toString(),
    rva: '0x' + sampleRva.toString(16),
    address: target.toString(),
    size: sampleSize,
    intervalMs: intervalMs,
    durationMs: durationMs
  });

  const timer = setInterval(function () {
    if (Date.now() - startedAt >= durationMs || unique >= maxUnique) {
      clearInterval(timer);
      send({ type: 'sampling-finished', unique: unique });
      return;
    }
    try {
      const data = target.readByteArray(sampleSize);
      const hex = bytesToHex(data);
      if (seen.has(hex))
        return;
      seen.add(hex);
      unique++;
      send({
        type: 'code-snapshot',
        index: unique - 1,
        elapsedMs: Date.now() - startedAt,
        rva: '0x' + sampleRva.toString(16),
        size: sampleSize
      }, data);
    } catch (error) {
      clearInterval(timer);
      send({ type: 'sampling-error', error: String(error) });
    }
  }, intervalMs);
}

function install(module) {
  const target = module.getExportByName(
    'HCHttpCallResponseGetResponseBodyBytes'
  );
  Interceptor.attach(target, {
    onEnter(args) {
      this.bufferSize = args[1].toUInt32();
    },
    onLeave() {
      if (this.bufferSize >= triggerMinBody)
        startSampling(this.bufferSize);
    }
  });
  send({
    type: 'ready',
    module: module.name,
    target: target.toString()
  });
}

const existing = Process.findModuleByName(moduleName);
if (existing !== null) {
  install(existing);
} else {
  send({ type: 'waiting-for-module', module: moduleName });
  Process.attachModuleObserver({
    onAdded(module) {
      if (module.name.toLowerCase() === moduleName.toLowerCase())
        install(module);
    }
  });
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Sample the protected EncryptionSession code after a large HTTP body."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--timeout-seconds", type=int, default=90)
    parser.add_argument("--trigger-min-body", type=int, default=16 * 1024)
    parser.add_argument("--sample-rva", type=lambda value: int(value, 0), default=0x5EEA000)
    parser.add_argument("--sample-size", type=int, default=0x1C00)
    parser.add_argument("--interval-ms", type=int, default=2)
    parser.add_argument("--duration-ms", type=int, default=3000)
    parser.add_argument("--max-unique", type=int, default=4)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.output.exists():
        args.output.unlink()

    done = threading.Event()
    records = 0

    def on_message(message: dict[str, Any], data: bytes | None) -> None:
        nonlocal records
        record: dict[str, Any] = {
            "received_at": time.time(),
            "message": message,
        }
        if data is not None:
            record["data_hex"] = bytes(data).hex()
        with args.output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, separators=(",", ":")) + "\n")
        records += 1
        payload = message.get("payload", {})
        if isinstance(payload, dict):
            print(json.dumps(payload, indent=2), flush=True)
            if payload.get("type") in {"sampling-finished", "sampling-error"}:
                done.set()

    source = (
        AGENT_SOURCE.replace("%TRIGGER_MIN_BODY%", str(args.trigger_min_body))
        .replace("%SAMPLE_RVA%", str(args.sample_rva))
        .replace("%SAMPLE_SIZE%", str(args.sample_size))
        .replace("%INTERVAL_MS%", str(args.interval_ms))
        .replace("%DURATION_MS%", str(args.duration_ms))
        .replace("%MAX_UNIQUE%", str(args.max_unique))
    )
    session = frida.attach(args.pid)
    try:
        script = session.create_script(source)
        script.on("message", on_message)
        script.load()
        done.wait(args.timeout_seconds)
        try:
            script.unload()
        except frida.InvalidOperationError:
            pass
    finally:
        try:
            session.detach()
        except frida.InvalidOperationError:
            pass

    print(
        json.dumps(
            {"pid": args.pid, "records": records, "output": str(args.output)},
            indent=2,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
