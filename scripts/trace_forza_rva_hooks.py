from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


AGENT_SOURCE = r"""
const moduleName = '%MODULE_NAME%';
const hooks = %HOOKS_JSON%;
const maxBytes = %MAX_BYTES%;
const module = Process.getModuleByName(moduleName);

function asAddress(value) {
  try {
    return ptr(value).toString();
  } catch (_) {
    return '';
  }
}

function readBytes(address, length) {
  if (address.isNull())
    return null;
  try {
    return address.readByteArray(length);
  } catch (_) {
    return null;
  }
}

function bytesToHex(buffer) {
  if (buffer === null)
    return null;
  const view = new Uint8Array(buffer);
  let out = '';
  for (let i = 0; i < view.length; i++)
    out += view[i].toString(16).padStart(2, '0');
  return out;
}

function readUtf8(address) {
  if (address.isNull())
    return null;
  try {
    return address.readUtf8String(128);
  } catch (_) {
    return null;
  }
}

function describePointer(address) {
  const item = {
    address: address.toString(),
    utf8: readUtf8(address)
  };
  const bytes = readBytes(address, maxBytes);
  if (bytes !== null)
    item.bytesHex = bytesToHex(bytes);
  return item;
}

function threadBacktrace(context) {
  return Thread.backtrace(context, Backtracer.FUZZY)
    .slice(0, 24)
    .map(addr => {
      const mod = Process.findModuleByAddress(addr);
      if (mod === null)
        return { address: addr.toString(), module: '', offset: '' };
      return {
        address: addr.toString(),
        module: mod.name,
        offset: addr.sub(mod.base).toString()
      };
    });
}

send({
  type: 'module',
  module: moduleName,
  base: module.base.toString(),
  size: module.size
});

function parseRva(value) {
  if (typeof value === 'number')
    return value;
  const text = String(value).trim();
  if (text.startsWith('0x') || text.startsWith('0X'))
    return parseInt(text.slice(2), 16);
  return parseInt(text, 10);
}

for (const hook of hooks) {
  const target = module.base.add(parseRva(hook.rva));
  const range = Process.findRangeByAddress(target);
  if (range === null || !range.protection.includes('x')) {
    send({
      type: 'hook-rejected',
      name: hook.name,
      rva: hook.rva,
      target: target.toString(),
      reason: range === null ? 'unmapped address' : 'address is not executable',
      protection: range === null ? '' : range.protection
    });
    continue;
  }
  try {
    Interceptor.attach(target, {
      onEnter(args) {
        const context = this.context;
        const record = {
          type: 'hit',
          name: hook.name,
          rva: hook.rva,
          target: target.toString(),
          threadId: Process.getCurrentThreadId(),
          registers: {
            rcx: asAddress(context.rcx),
            rdx: asAddress(context.rdx),
            r8: asAddress(context.r8),
            r9: asAddress(context.r9),
            rax: asAddress(context.rax),
            rsp: asAddress(context.rsp)
          },
          pointed: {
            rcx: describePointer(context.rcx),
            rdx: describePointer(context.rdx),
            r8: describePointer(context.r8),
            r9: describePointer(context.r9)
          },
          backtrace: threadBacktrace(context)
        };
        send(record);
      }
    });
    send({
      type: 'hooked',
      name: hook.name,
      rva: hook.rva,
      target: target.toString()
    });
  } catch (error) {
    send({
      type: 'hook-error',
      name: hook.name,
      rva: hook.rva,
      target: target.toString(),
      error: String(error)
    });
  }
}
"""


def parse_hook(value: str) -> dict[str, str]:
    if "=" in value:
        name, rva = value.split("=", 1)
    else:
        name, rva = value, value
    return {"name": name, "rva": rva}


def expand_hook_values(values: list[str]) -> list[dict[str, str]]:
    hooks: list[dict[str, str]] = []
    for value in values:
        for part in value.split(","):
            part = part.strip()
            if part:
                hooks.append(parse_hook(part))
    return hooks


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Attach to Forza and log calls to selected module RVAs."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--module", default="forzahorizon6.exe")
    parser.add_argument("--rva", action="append", default=[])
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--max-bytes", type=int, default=96)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    hooks = expand_hook_values(args.rva)
    if not hooks:
        parser.error(
            "At least one explicit --rva name=0xOFFSET is required. "
            "There are intentionally no automatic hooks."
        )

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        output.unlink()

    done = threading.Event()
    records = 0

    def write_record(message: dict[str, Any], data: bytes | None) -> None:
        nonlocal records
        item: dict[str, Any] = {
            "received_at": time.time(),
            "message": message,
        }
        if data is not None:
            item["data_hex"] = bytes(data).hex()
        with output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(item, separators=(",", ":")) + "\n")
        payload = message.get("payload", {})
        if payload.get("type") in {
            "hooked",
            "hook-error",
            "hook-rejected",
            "module",
            "hit",
        }:
            print(json.dumps(payload, indent=2), flush=True)
        records += 1

    source = (
        AGENT_SOURCE.replace("%MODULE_NAME%", args.module)
        .replace("%HOOKS_JSON%", json.dumps(hooks))
        .replace("%MAX_BYTES%", str(args.max_bytes))
    )

    session = frida.attach(args.pid)
    try:
        script = session.create_script(source)
        script.on("message", write_record)
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
            {
                "pid": args.pid,
                "records": records,
                "output": str(output),
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
