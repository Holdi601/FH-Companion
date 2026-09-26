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
const dispatchSites = [
  {
    name: 'extended-write-callback',
    rva: 0x205e4,
    contextSource: 'stack20',
    signature: 'extended'
  },
  {
    name: 'response-body-write-callback',
    rva: 0x2060d,
    contextSource: 'r9',
    signature: 'body'
  },
  {
    name: 'response-string-callback',
    rva: 0x20636,
    contextSource: 'r8',
    signature: 'string'
  }
];
const maxBodyBytes = %MAX_BODY_BYTES%;
const hookedCallbacks = new Set();
const snapshottedCodePages = new Set();

function bytesToHex(buffer) {
  if (buffer === null)
    return null;
  const view = new Uint8Array(buffer);
  let out = '';
  for (let i = 0; i < view.length; i++)
    out += view[i].toString(16).padStart(2, '0');
  return out;
}

function readBuffer(address, length) {
  if (address.isNull() || length <= 0)
    return null;
  try {
    return address.readByteArray(Math.min(length, maxBodyBytes));
  } catch (_) {
    return null;
  }
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

function backtrace(context) {
  return Thread.backtrace(context, Backtracer.ACCURATE)
    .slice(0, 24)
    .map(describeAddress);
}

function emit(record, data) {
  if (data !== null)
    record.dataHex = bytesToHex(data);
  send(record);
}

function hookExport(module, name, callbacks) {
  try {
    const target = module.getExportByName(name);
    Interceptor.attach(target, callbacks(target));
    send({
      type: 'export-hooked',
      module: module.name,
      name: name,
      target: target.toString()
    });
    return 1;
  } catch (error) {
    send({
      type: 'hook-error',
      module: module.name,
      name: name,
      error: String(error)
    });
    return 0;
  }
}

function hookClientCallback(callback, signature) {
  const key = callback.toString() + ':' + signature;
  if (hookedCallbacks.has(key))
    return;
  hookedCallbacks.add(key);

  const range = Process.findRangeByAddress(callback);
  if (range === null || !range.protection.includes('x')) {
    send({
      type: 'callback-rejected',
      callback: describeAddress(callback),
      reason: range === null ? 'unmapped callback' : 'callback is not executable',
      protection: range === null ? '' : range.protection
    });
    return;
  }

  try {
    Interceptor.attach(callback, {
      onEnter(args) {
        this.body = args[1];
        if (signature === 'string') {
          try {
            this.size = this.body.readUtf8String(maxBodyBytes).length;
          } catch (_) {
            this.size = 0;
          }
          this.contextPointer = args[2];
          this.finalChunk = 1;
        } else {
          this.size = args[2].toUInt32();
          this.contextPointer = signature === 'body' ? args[3] : args[4];
          this.finalChunk = signature === 'body' ? 1 : args[3].toUInt32();
        }
        emit({
          type: 'client-callback-enter',
          callback: describeAddress(callback),
          signature: signature,
          callHandle: args[0].toString(),
          size: this.size,
          finalChunk: this.finalChunk,
          context: this.contextPointer.toString(),
          backtrace: backtrace(this.context)
        }, readBuffer(this.body, this.size));
      },
      onLeave(retval) {
        emit({
          type: 'client-callback-leave',
          callback: describeAddress(callback),
          signature: signature,
          result: retval.toString(),
          size: this.size,
          context: this.contextPointer.toString()
        }, readBuffer(this.body, this.size));
      }
    });
    send({
      type: 'callback-hooked',
      callback: describeAddress(callback),
      signature: signature
    });
  } catch (error) {
    send({
      type: 'callback-hook-error',
      callback: describeAddress(callback),
      signature: signature,
      error: String(error)
    });
  }
}

function install(module) {
  let installed = 0;
  installed += hookExport(
    module,
    'HCHttpCallResponseAppendResponseBodyBytes',
    appendAddress => ({
      onEnter(args) {
        const size = args[2].toUInt32();
        emit({
          type: 'response-append',
          target: describeAddress(appendAddress),
          callHandle: args[0].toString(),
          size: size,
          backtrace: backtrace(this.context)
        }, readBuffer(args[1], size));
      }
    })
  );

  installed += hookExport(
    module,
    'HCHttpCallResponseGetResponseBodyBytesSize',
    target => ({
      onEnter(args) {
        this.callHandle = args[0].toString();
        this.sizePointer = args[1];
        this.trace = backtrace(this.context);
      },
      onLeave(retval) {
        let size = 0;
        try {
          if (!this.sizePointer.isNull())
            size = this.sizePointer.readU64().toNumber();
        } catch (_) {
        }
        send({
          type: 'response-body-size',
          target: describeAddress(target),
          callHandle: this.callHandle,
          result: retval.toString(),
          size: size,
          backtrace: this.trace
        });
      }
    })
  );

  installed += hookExport(
    module,
    'HCHttpCallResponseGetResponseBodyBytes',
    target => ({
      onEnter(args) {
        this.callHandle = args[0].toString();
        this.bufferSize = args[1].toUInt32();
        this.buffer = args[2];
        this.bytesUsedPointer = args[3];
        this.trace = backtrace(this.context);
        if (this.bufferSize >= 16 * 1024) {
          const caller = this.returnAddress;
          const page = caller.and(ptr('0xfffffffffffff000'));
          const key = page.toString();
          if (!snapshottedCodePages.has(key)) {
            snapshottedCodePages.add(key);
            const owner = Process.findModuleByAddress(page);
            emit({
              type: 'caller-code-page',
              caller: describeAddress(caller),
              page: describeAddress(page),
              pageRva: owner === null ? '' : page.sub(owner.base).toString(),
              size: 4096
            }, readBuffer(page, 4096));
          }
        }
      },
      onLeave(retval) {
        let bytesUsed = 0;
        try {
          if (!this.bytesUsedPointer.isNull())
            bytesUsed = this.bytesUsedPointer.readU64().toNumber();
        } catch (_) {
        }
        emit({
          type: 'response-body-complete',
          target: describeAddress(target),
          callHandle: this.callHandle,
          result: retval.toString(),
          bufferSize: this.bufferSize,
          size: bytesUsed,
          backtrace: this.trace
        }, readBuffer(this.buffer, bytesUsed));
      }
    })
  );

  send({
    type: 'ready',
    module: module.name,
    base: module.base.toString(),
    installed: installed
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
        description="Capture complete response bodies and the client callback at libHttpClient dispatch."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--max-body-bytes", type=int, default=2 * 1024 * 1024)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

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
        if isinstance(payload, dict) and payload.get("type") in {
            "waiting-for-module",
            "ready",
            "hook-error",
            "hook-rejected",
            "dispatch-hooked",
            "export-hooked",
            "callback-hooked",
            "callback-hook-error",
            "callback-rejected",
            "response-dispatch",
            "response-body-size",
            "response-body-complete",
            "caller-code-page",
        }:
            printable = dict(payload)
            printable.pop("dataHex", None)
            if isinstance(printable.get("backtrace"), list):
                printable["backtrace"] = [
                    frame
                    for frame in printable["backtrace"]
                    if frame.get("module") == "forzahorizon6.exe"
                ][:8]
            print(json.dumps(printable, indent=2), flush=True)

    source = AGENT_SOURCE.replace("%MAX_BODY_BYTES%", str(args.max_body_bytes))
    session = frida.attach(args.pid)
    session.on("detached", lambda reason, crash: done.set())
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
