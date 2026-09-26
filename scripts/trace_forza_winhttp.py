from __future__ import annotations

import argparse
import json
import threading
import time
from pathlib import Path
from typing import Any

import frida


AGENT_SOURCE = r"""
const maxBodyBytes = %MAX_BODY_BYTES%;
const xlsOnly = %XLS_ONLY%;
const requestState = new Map();
const safeHeaderNames = new Set([
  'content-type',
  'user-agent',
  'x-classname',
  'x-cmsinstance',
  'x-contractversion',
  'x-languagecodeid',
  'x-method',
  'x-version'
]);
const sensitiveHeaderNames = new Set([
  'authorization',
  'signature',
  'x-authorization',
  'x-entitytoken',
  'x-validation'
]);

function bytesToHex(buffer) {
  if (buffer === null)
    return null;
  const view = new Uint8Array(buffer);
  let out = '';
  for (let i = 0; i < view.length; i++)
    out += view[i].toString(16).padStart(2, '0');
  return out;
}

function readBuffer(pointer, length) {
  if (pointer.isNull() || length <= 0)
    return null;
  try {
    return pointer.readByteArray(Math.min(length, maxBodyBytes));
  } catch (_) {
    return null;
  }
}

function readWide(pointer, chars) {
  if (pointer.isNull())
    return null;
  try {
    if (chars < 0 || chars > 8192)
      return pointer.readUtf16String();
    return pointer.readUtf16String(chars);
  } catch (_) {
    return null;
  }
}

function sanitizeHeaders(text) {
  const result = {};
  if (text === null)
    return result;
  for (const line of text.split(/\r?\n/)) {
    const separator = line.indexOf(':');
    if (separator < 1)
      continue;
    const name = line.slice(0, separator).trim();
    const value = line.slice(separator + 1).trim();
    const lower = name.toLowerCase();
    if (safeHeaderNames.has(lower)) {
      result[name] = value;
    } else if (sensitiveHeaderNames.has(lower)) {
      result[name] = { present: true, length: value.length };
    }
  }
  return result;
}

function mergeHeaders(target, incoming) {
  for (const key of Object.keys(incoming))
    target[key] = incoming[key];
}

function isXlsRequest(state) {
  const contentType = String(state.headers['Content-Type'] || state.headers['content-type'] || '').toLowerCase();
  return contentType === 'bin/xtsw';
}

function shouldTrace(state) {
  return !xlsOnly || isXlsRequest(state);
}

function stateFor(handle) {
  let state = requestState.get(handle);
  if (state === undefined) {
    state = { headers: {} };
    requestState.set(handle, state);
  }
  return state;
}

function describeAddress(addr) {
  const mod = Process.findModuleByAddress(addr);
  if (mod === null)
    return { address: addr.toString(), module: '', offset: '' };
  return {
    address: addr.toString(),
    module: mod.name,
    offset: addr.sub(mod.base).toString()
  };
}

function backtrace(context) {
  return Thread.backtrace(context, Backtracer.ACCURATE)
    .slice(0, 16)
    .map(describeAddress);
}

function sendRecord(record, data) {
  if (data !== null)
    record.dataHex = bytesToHex(data);
  send(record);
}

function getExport(module, name) {
  try {
    return module.getExportByName(name);
  } catch (_) {
    try {
      return module.findExportByName(name);
    } catch (_) {
      return null;
    }
  }
}

function hookExport(module, name, callbacks) {
  const address = getExport(module, name);
  if (address === null) {
    send({ type: 'hook-error', api: name, error: 'export not found' });
    return;
  }
  Interceptor.attach(address, callbacks(address));
  send({ type: 'hooked', api: name, address: address.toString() });
}

function installWinHttpHooks(module) {
hookExport(module, 'WinHttpAddRequestHeaders', address => ({
  onEnter(args) {
    const handle = args[0].toString();
    const headerChars = args[2].toInt32();
    const headers = sanitizeHeaders(readWide(args[1], headerChars));
    const state = stateFor(handle);
    mergeHeaders(state.headers, headers);
    if (!shouldTrace(state))
      return;
    sendRecord({
      type: 'winhttp',
      api: 'WinHttpAddRequestHeaders',
      hRequest: handle,
      headers: headers,
      backtrace: backtrace(this.context)
    }, null);
  }
}));

hookExport(module, 'WinHttpSendRequest', address => ({
  onEnter(args) {
    const handle = args[0].toString();
    const state = stateFor(handle);
    mergeHeaders(
      state.headers,
      sanitizeHeaders(readWide(args[1], args[2].toInt32()))
    );
    if (!shouldTrace(state))
      return;
    const optionalLength = args[4].toUInt32();
    sendRecord({
      type: 'winhttp',
      api: 'WinHttpSendRequest',
      hRequest: handle,
      headers: state.headers,
      optionalLength: optionalLength,
      totalLength: args[5].toUInt32(),
      backtrace: backtrace(this.context)
    }, readBuffer(args[3], optionalLength));
  }
}));

hookExport(module, 'WinHttpWriteData', address => ({
  onEnter(args) {
    const handle = args[0].toString();
    const state = stateFor(handle);
    if (!shouldTrace(state))
      return;
    const length = args[2].toUInt32();
    sendRecord({
      type: 'winhttp',
      api: 'WinHttpWriteData',
      hRequest: handle,
      headers: state.headers,
      length: length,
      backtrace: backtrace(this.context)
    }, readBuffer(args[1], length));
  }
}));

hookExport(module, 'WinHttpReceiveResponse', address => ({
  onEnter(args) {
    const handle = args[0].toString();
    const state = stateFor(handle);
    if (!shouldTrace(state))
      return;
    sendRecord({
      type: 'winhttp',
      api: 'WinHttpReceiveResponse',
      hRequest: handle,
      headers: state.headers,
      backtrace: backtrace(this.context)
    }, null);
  }
}));

hookExport(module, 'WinHttpReadData', address => ({
  onEnter(args) {
    this.hRequest = args[0].toString();
    this.state = stateFor(this.hRequest);
    this.traceThis = shouldTrace(this.state);
    if (!this.traceThis)
      return;
    this.buffer = args[1];
    this.requested = args[2].toUInt32();
    this.readPointer = args[3];
    this.trace = backtrace(this.context);
  },
  onLeave(retval) {
    if (!this.traceThis)
      return;
    let bytesRead = 0;
    try {
      if (!this.readPointer.isNull())
        bytesRead = this.readPointer.readU32();
    } catch (_) {
    }
    sendRecord({
      type: 'winhttp',
      api: 'WinHttpReadData',
      hRequest: this.hRequest,
      headers: this.state.headers,
      requested: this.requested,
      bytesRead: bytesRead,
      result: retval.toString(),
      backtrace: this.trace
    }, readBuffer(this.buffer, bytesRead));
  }
}));

send({ type: 'ready', module: module.name, hooks: ['WinHttpAddRequestHeaders', 'WinHttpSendRequest', 'WinHttpWriteData', 'WinHttpReceiveResponse', 'WinHttpReadData'] });
}

const existing = Process.findModuleByName('winhttp.dll');
if (existing !== null) {
  installWinHttpHooks(existing);
} else {
  send({ type: 'waiting-for-module', module: 'winhttp.dll' });
  Process.attachModuleObserver({
    onAdded(module) {
      if (module.name.toLowerCase() === 'winhttp.dll')
        installWinHttpHooks(module);
    }
  });
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Trace WinHTTP payloads and Forza caller stacks from a running FH6 process."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--max-body-bytes", type=int, default=4096)
    parser.add_argument(
        "--all-http",
        action="store_true",
        help="Trace non-Xls WinHTTP calls too. Default is bin/xtsw only.",
    )
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        output.unlink()

    records = 0
    done = threading.Event()

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
        if message.get("type") == "error":
            print(json.dumps(message, indent=2), flush=True)
            records += 1
            return
        payload = message.get("payload", {})
        if payload.get("type") in {"ready", "hooked", "hook-error", "waiting-for-module"}:
            print(json.dumps(payload, indent=2), flush=True)
        elif payload.get("type") == "winhttp":
            compact = dict(payload)
            compact.pop("dataHex", None)
            compact["backtrace"] = [
                item for item in compact.get("backtrace", [])
                if item.get("module") in {"forzahorizon6.exe", "winhttp.dll"}
            ][:8]
            print(json.dumps(compact, indent=2), flush=True)
        records += 1

    source = (
        AGENT_SOURCE.replace("%MAX_BODY_BYTES%", str(args.max_body_bytes))
        .replace("%XLS_ONLY%", "false" if args.all_http else "true")
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

    print(json.dumps({"pid": args.pid, "records": records, "output": str(output)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
