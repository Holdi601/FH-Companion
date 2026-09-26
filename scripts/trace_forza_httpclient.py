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
const calls = new Map();
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

function getCall(handle) {
  const key = handle.toString();
  let state = calls.get(key);
  if (state === undefined) {
    state = {
      handle: key,
      headers: {},
      method: '',
      url: '',
      requestBody: null,
      requestBodySize: 0,
      responseChunkIndex: 0
    };
    calls.set(key, state);
  }
  return state;
}

function readCString(address, maxLength = 8192) {
  if (address.isNull())
    return null;
  try {
    return address.readUtf8String(maxLength);
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

function readBuffer(address, length) {
  if (address.isNull() || length <= 0)
    return null;
  try {
    return address.readByteArray(Math.min(length, maxBodyBytes));
  } catch (_) {
    return null;
  }
}

function sanitizeHeader(name, value) {
  const lower = name.toLowerCase();
  if (safeHeaderNames.has(lower))
    return value;
  if (sensitiveHeaderNames.has(lower))
    return { present: true, length: value.length };
  return null;
}

function isXls(state) {
  const value = String(state.headers['content-type'] || '').toLowerCase();
  return value === 'bin/xtsw';
}

function describeAddress(address) {
  const module = Process.findModuleByAddress(address);
  if (module === null)
    return { address: address.toString(), module: '', offset: '' };
  return {
    address: address.toString(),
    module: module.name,
    offset: address.sub(module.base).toString()
  };
}

function backtrace(context) {
  return Thread.backtrace(context, Backtracer.ACCURATE)
    .slice(0, 20)
    .map(describeAddress);
}

function emit(record, data) {
  if (data !== null)
    record.dataHex = bytesToHex(data);
  send(record);
}

function hook(module, name, callbacks) {
  let address = null;
  try {
    address = module.getExportByName(name);
  } catch (_) {
  }
  if (address === null) {
    send({ type: 'hook-error', api: name, error: 'export not found' });
    return;
  }
  Interceptor.attach(address, callbacks(address));
  send({ type: 'hooked', api: name, address: address.toString() });
}

function install(module) {
  hook(module, 'HCHttpCallRequestSetUrl', address => ({
    onEnter(args) {
      const state = getCall(args[0]);
      state.method = readCString(args[1]) || '';
      state.url = readCString(args[2]) || '';
    }
  }));

  hook(module, 'HCHttpCallRequestSetHeader', address => ({
    onEnter(args) {
      const state = getCall(args[0]);
      const name = readCString(args[1]) || '';
      const value = readCString(args[2]) || '';
      const safeValue = sanitizeHeader(name, value);
      if (safeValue !== null)
        state.headers[name.toLowerCase()] = safeValue;
    }
  }));

  hook(module, 'HCHttpCallRequestSetRequestBodyBytes', address => ({
    onEnter(args) {
      const state = getCall(args[0]);
      const size = args[2].toUInt32();
      state.requestBodySize = size;
      state.requestBody = readBuffer(args[1], size);
      state.requestBodyTrace = backtrace(this.context);
    }
  }));

  hook(module, 'HCHttpCallPerformAsync', address => ({
    onEnter(args) {
      const state = getCall(args[0]);
      if (!isXls(state))
        return;
      emit({
        type: 'httpclient',
        api: 'HCHttpCallPerformAsync',
        handle: state.handle,
        method: state.method,
        url: state.url,
        headers: state.headers,
        requestBodySize: state.requestBodySize,
        requestBodyTrace: state.requestBodyTrace || [],
        backtrace: backtrace(this.context)
      }, state.requestBody);
    }
  }));

  hook(module, 'HCHttpCallResponseAppendResponseBodyBytes', address => ({
    onEnter(args) {
      const state = getCall(args[0]);
      const size = args[2].toUInt32();
      if (!isXls(state))
        return;
      emit({
        type: 'httpclient',
        api: 'HCHttpCallResponseAppendResponseBodyBytes',
        handle: state.handle,
        method: state.method,
        url: state.url,
        headers: state.headers,
        chunkIndex: state.responseChunkIndex++,
        size: size,
        backtrace: backtrace(this.context)
      }, readBuffer(args[1], size));
    }
  }));

  hook(module, 'HCHttpCallResponseGetResponseBodyBytes', address => ({
    onEnter(args) {
      this.state = getCall(args[0]);
      this.traceThis = isXls(this.state);
      if (!this.traceThis)
        return;
      this.bufferSize = args[1].toUInt32();
      this.buffer = args[2];
      this.bufferUsed = args[3];
      this.trace = backtrace(this.context);
    },
    onLeave(retval) {
      if (!this.traceThis)
        return;
      let used = 0;
      try {
        if (!this.bufferUsed.isNull())
          used = this.bufferUsed.readU64().toNumber();
      } catch (_) {
      }
      emit({
        type: 'httpclient',
        api: 'HCHttpCallResponseGetResponseBodyBytes',
        handle: this.state.handle,
        method: this.state.method,
        url: this.state.url,
        headers: this.state.headers,
        result: retval.toString(),
        bufferSize: this.bufferSize,
        bytesUsed: used,
        backtrace: this.trace
      }, readBuffer(this.buffer, used));
    }
  }));

  hook(module, 'HCHttpCallCloseHandle', address => ({
    onEnter(args) {
      calls.delete(args[0].toString());
    }
  }));

  send({
    type: 'ready',
    module: module.name,
    hooks: [
      'HCHttpCallRequestSetUrl',
      'HCHttpCallRequestSetHeader',
      'HCHttpCallRequestSetRequestBodyBytes',
      'HCHttpCallPerformAsync',
      'HCHttpCallResponseAppendResponseBodyBytes',
      'HCHttpCallResponseGetResponseBodyBytes',
      'HCHttpCallCloseHandle'
    ]
  });
}

const existing = Process.findModuleByName('libHttpClient.dll');
if (existing !== null) {
  install(existing);
} else {
  send({ type: 'waiting-for-module', module: 'libHttpClient.dll' });
  Process.attachModuleObserver({
    onAdded(module) {
      if (module.name.toLowerCase() === 'libhttpclient.dll')
        install(module);
    }
  });
}
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Trace Xls request and response bodies at libHttpClient's public C API."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--timeout-seconds", type=int, default=180)
    parser.add_argument("--max-body-bytes", type=int, default=2 * 1024 * 1024)
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
        else:
            payload = message.get("payload", {})
            if payload.get("type") in {
                "ready",
                "hooked",
                "hook-error",
                "waiting-for-module",
            }:
                print(json.dumps(payload, indent=2), flush=True)
            elif payload.get("type") == "httpclient":
                compact = dict(payload)
                compact.pop("dataHex", None)
                compact["backtrace"] = [
                    frame
                    for frame in compact.get("backtrace", [])
                    if frame.get("module") == "forzahorizon6.exe"
                ][:10]
                print(json.dumps(compact, indent=2), flush=True)
        records += 1

    source = AGENT_SOURCE.replace("%MAX_BODY_BYTES%", str(args.max_body_bytes))
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
