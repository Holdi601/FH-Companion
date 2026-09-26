from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


DEFAULT_STRINGS = [
    "X-Encryption",
    "EncryptionSecret",
    "X-Validation",
    "X-Renegotiate",
    "RenegotiatedRequired",
    "bin/xtsw",
]


AGENT_SOURCE = r"""
const moduleName = '%MODULE_NAME%';
const labels = %LABELS_JSON%;
const module = Process.getModuleByName(moduleName);
const pageSize = Process.pageSize;
const pages = new Map();

function patternForAscii(text) {
  return Array.from(text)
    .map(character => character.charCodeAt(0).toString(16).padStart(2, '0'))
    .join(' ');
}

function describeAddress(address) {
  const owner = Process.findModuleByAddress(address);
  if (owner === null)
    return { address: address.toString(), module: '', offset: '' };
  return {
    address: address.toString(),
    module: owner.name,
    offset: address.sub(owner.base).toString()
  };
}

function pointerSnapshot(address) {
  if (address.isNull())
    return null;
  const result = { address: address.toString() };
  try {
    result.utf8 = address.readUtf8String(128);
  } catch (_) {
    result.utf8 = null;
  }
  return result;
}

function stackSnapshot(stackPointer, count = 16) {
  const frames = [];
  for (let index = 0; index < count; index++) {
    try {
      const value = stackPointer.add(index * Process.pointerSize).readPointer();
      frames.push({
        index: index,
        stackAddress: stackPointer.add(index * Process.pointerSize).toString(),
        value: describeAddress(value)
      });
    } catch (_) {
      break;
    }
  }
  return frames;
}

for (const label of labels) {
  const matches = Memory.scanSync(module.base, module.size, patternForAscii(label));
  for (const match of matches) {
    const pageOffset = match.address.toUInt32() & (pageSize - 1);
    const pageBase = match.address.sub(pageOffset);
    const key = pageBase.toString();
    let item = pages.get(key);
    if (item === undefined) {
      item = { base: pageBase, size: pageSize, labels: [] };
      pages.set(key, item);
    }
    item.labels.push({
      label: label,
      address: match.address.toString(),
      offset: match.address.sub(module.base).toString()
    });
  }
}

const monitoredPages = Array.from(pages.values());
send({
  type: 'string-pages',
  module: module.name,
  base: module.base.toString(),
  pages: monitoredPages.map(page => ({
    base: page.base.toString(),
    size: page.size,
    labels: page.labels
  }))
});

if (monitoredPages.length === 0) {
  send({ type: 'ready', pages: 0 });
} else {
  MemoryAccessMonitor.enable(
    monitoredPages.map(page => ({ base: page.base, size: page.size })),
    {
      onAccess(details) {
        const page = monitoredPages[details.rangeIndex];
        const context = details.context;
        send({
          type: 'string-access',
          operation: details.operation,
          threadId: details.threadId,
          from: describeAddress(details.from),
          address: describeAddress(details.address),
          pageIndex: details.pageIndex,
          pagesCompleted: details.pagesCompleted,
          pagesTotal: details.pagesTotal,
          labels: page.labels,
          registers: {
            rcx: context.rcx.toString(),
            rdx: context.rdx.toString(),
            r8: context.r8.toString(),
            r9: context.r9.toString(),
            rax: context.rax.toString(),
            rsp: context.rsp.toString()
          },
          pointed: {
            rcx: pointerSnapshot(context.rcx),
            rdx: pointerSnapshot(context.rdx),
            r8: pointerSnapshot(context.r8),
            r9: pointerSnapshot(context.r9)
          },
          stack: stackSnapshot(context.rsp)
        });
      }
    }
  );
  send({ type: 'ready', pages: monitoredPages.length });
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find Xls transport strings and report the code that first reads their pages."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--module", default="forzahorizon6.exe")
    parser.add_argument("--string", action="append", dest="strings", default=[])
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    labels = args.strings or DEFAULT_STRINGS
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.output.exists():
        args.output.unlink()

    records = 0
    done = threading.Event()

    def on_message(message: dict[str, Any], data: Any) -> None:
        nonlocal records
        record = {"received_at": time.time(), "message": message}
        with args.output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, separators=(",", ":")) + "\n")
        records += 1
        payload = message.get("payload", {})
        if isinstance(payload, dict):
            print(json.dumps(payload, indent=2), flush=True)

    source = (
        AGENT_SOURCE.replace("%MODULE_NAME%", args.module)
        .replace("%LABELS_JSON%", json.dumps(labels))
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
