from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


AGENT_SOURCE = r"""
const target = ptr('%ADDRESS%');
const watchSize = %SIZE%;
const watchId = 0;
const installed = new Set();
let hit = false;

function describeAddress(address) {
  const module = Process.findModuleByAddress(address);
  if (module === null) {
    return { address: address.toString(), module: '', moduleOffset: '' };
  }
  return {
    address: address.toString(),
    module: module.name,
    moduleBase: module.base.toString(),
    moduleOffset: address.sub(module.base).toString()
  };
}

function install(thread) {
  if (hit || installed.has(thread.id))
    return;
  try {
    thread.setHardwareWatchpoint(watchId, target, watchSize, 'w');
    installed.add(thread.id);
  } catch (error) {
    send({
      type: 'watchpoint-install-error',
      threadId: thread.id,
      error: String(error)
    });
  }
}

Process.setExceptionHandler(exception => {
  const threadId = Process.getCurrentThreadId();
  if (!['single-step', 'breakpoint'].includes(exception.type))
    return false;
  if (!installed.has(threadId))
    return false;

  hit = true;
  const thread = Process.enumerateThreads().find(item => item.id === threadId);
  if (thread !== undefined) {
    try {
      thread.unsetHardwareWatchpoint(watchId);
    } catch (_) {
    }
  }

  const pc = exception.context.pc;
  let bytes = '';
  try {
    bytes = target.sub(16).readByteArray(48);
  } catch (_) {
    bytes = null;
  }
  const backtrace = Thread.backtrace(
    exception.context,
    Backtracer.FUZZY
  ).slice(0, 32).map(describeAddress);
  const registers = {};
  for (const name of [
    'rax', 'rbx', 'rcx', 'rdx', 'rsi', 'rdi',
    'r8', 'r9', 'r10', 'r11', 'r12', 'r13',
    'r14', 'r15', 'rsp', 'rbp', 'rip'
  ]) {
    if (exception.context[name] !== undefined)
      registers[name] = exception.context[name].toString();
  }

  send({
    type: 'watchpoint-hit',
    exceptionType: exception.type,
    threadId: threadId,
    target: target.toString(),
    pc: describeAddress(pc),
    backtrace: backtrace,
    registers: registers,
    targetWindow: bytes
  }, bytes);
  return true;
});

const observer = Process.attachThreadObserver({
  onAdded(thread) {
    install(thread);
  },
  onRemoved(thread) {
    installed.delete(thread.id);
  }
});

for (const thread of Process.enumerateThreads())
  install(thread);

send({
  type: 'watchpoint-ready',
  target: target.toString(),
  size: watchSize,
  threadCount: installed.size
});
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Trace the first native write to an address using Frida hardware watchpoints."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--address", required=True)
    parser.add_argument("--size", type=int, default=4, choices=(1, 2, 4, 8))
    parser.add_argument("--timeout-seconds", type=int, default=120)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    completed = threading.Event()
    records: list[dict[str, Any]] = []

    def record(message: dict[str, Any], data: bytes | None) -> None:
        item: dict[str, Any] = {
            "received_at": time.time(),
            "message": message,
        }
        if data is not None:
            item["data_hex"] = bytes(data).hex()
        records.append(item)
        with output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(item, separators=(",", ":")) + "\n")
        payload = message.get("payload", {})
        print(json.dumps(payload, indent=2), flush=True)
        if payload.get("type") == "watchpoint-hit":
            completed.set()

    if output.exists():
        output.unlink()

    session = frida.attach(args.pid)
    try:
        source = (
            AGENT_SOURCE.replace("%ADDRESS%", args.address)
            .replace("%SIZE%", str(args.size))
        )
        script = session.create_script(source)
        script.on("message", record)
        script.load()
        completed.wait(args.timeout_seconds)
        time.sleep(1)
        try:
            script.unload()
        except frida.InvalidOperationError:
            # The target process may have exited while the watchpoint was armed.
            pass
    finally:
        try:
            session.detach()
        except frida.InvalidOperationError:
            pass

    hits = [
        item
        for item in records
        if item.get("message", {}).get("payload", {}).get("type") == "watchpoint-hit"
    ]
    print(
        json.dumps(
            {
                "pid": args.pid,
                "address": args.address,
                "records": len(records),
                "hits": len(hits),
                "output": str(output),
            },
            indent=2,
        )
    )
    return 0 if hits else 2


if __name__ == "__main__":
    raise SystemExit(main())
