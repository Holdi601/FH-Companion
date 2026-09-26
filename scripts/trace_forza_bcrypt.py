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
const minBufferBytes = %MIN_BUFFER_BYTES%;
const keyInfo = new Map();
const algorithmInfo = new Map();

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

function readWide(address, maxChars = 512) {
  if (address.isNull())
    return null;
  try {
    return address.readUtf16String(maxChars);
  } catch (_) {
    return null;
  }
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

function getTrace(context) {
  return Thread.backtrace(context, Backtracer.ACCURATE)
    .slice(0, 24)
    .map(describeAddress);
}

function readU32(address, offset) {
  try {
    return address.add(offset).readU32();
  } catch (_) {
    return 0;
  }
}

function readU64Number(address, offset) {
  try {
    return address.add(offset).readU64().toNumber();
  } catch (_) {
    return 0;
  }
}

function readPointerAt(address, offset) {
  try {
    return address.add(offset).readPointer();
  } catch (_) {
    return ptr(0);
  }
}

function readAuthenticatedCipherInfo(address) {
  if (address.isNull())
    return null;
  const cbSize = readU32(address, 0);
  const version = readU32(address, 4);
  if (cbSize < 80 || cbSize > 256)
    return null;
  const nonce = readPointerAt(address, 8);
  const nonceSize = readU32(address, 16);
  const authData = readPointerAt(address, 24);
  const authDataSize = readU32(address, 32);
  const tag = readPointerAt(address, 40);
  const tagSize = readU32(address, 48);
  const macContext = readPointerAt(address, 56);
  const macContextSize = readU32(address, 64);
  return {
    cbSize: cbSize,
    version: version,
    nonceSize: nonceSize,
    nonceHex: bytesToHex(readBuffer(nonce, nonceSize)),
    authDataSize: authDataSize,
    authDataHex: bytesToHex(readBuffer(authData, authDataSize)),
    tagSize: tagSize,
    tagHex: bytesToHex(readBuffer(tag, tagSize)),
    macContextSize: macContextSize,
    macContextHex: bytesToHex(readBuffer(macContext, macContextSize)),
    aadProcessed: readU32(address, 68),
    dataProcessed: readU64Number(address, 72),
    flags: readU32(address, 80)
  };
}

function isForzaTrace(trace) {
  return trace.some(frame => frame.module.toLowerCase() === 'forzahorizon6.exe');
}

function emit(record, data) {
  if (data !== null)
    record.dataHex = bytesToHex(data);
  send(record);
}

function hook(name, callbacks) {
  const address = Module.findGlobalExportByName(name);
  if (address === null) {
    send({ type: 'hook-error', api: name, error: 'export not found' });
    return;
  }
  Interceptor.attach(address, callbacks(address));
  send({ type: 'hooked', api: name, address: address.toString() });
}

hook('BCryptOpenAlgorithmProvider', address => ({
  onEnter(args) {
    this.output = args[0];
    this.algorithm = readWide(args[1]) || '';
    this.implementation = readWide(args[2]) || '';
    this.traceThis = this.algorithm === 'AES';
    if (this.traceThis)
      this.trace = getTrace(this.context);
  },
  onLeave(retval) {
    if (
      !this.traceThis ||
      retval.toInt32() !== 0 ||
      !isForzaTrace(this.trace) ||
      this.output.isNull()
    )
      return;
    let handle = null;
    try {
      handle = this.output.readPointer();
    } catch (_) {
      return;
    }
    algorithmInfo.set(handle.toString(), {
      algorithm: this.algorithm,
      implementation: this.implementation
    });
    send({
      type: 'bcrypt',
      api: 'BCryptOpenAlgorithmProvider',
      result: retval.toString(),
      algorithmHandle: handle.toString(),
      algorithm: this.algorithm,
      implementation: this.implementation,
      backtrace: this.trace
    });
  }
}));

hook('BCryptGenerateSymmetricKey', address => ({
  onEnter(args) {
    this.algorithmHandle = args[0];
    this.outputKey = args[1];
    this.secret = args[4];
    this.secretSize = args[5].toUInt32();
    this.trace = getTrace(this.context);
    this.traceThis = isForzaTrace(this.trace);
  },
  onLeave(retval) {
    if (!this.traceThis || retval.toInt32() !== 0 || this.outputKey.isNull())
      return;
    let handle = null;
    try {
      handle = this.outputKey.readPointer();
    } catch (_) {
      return;
    }
    const algorithm = algorithmInfo.get(this.algorithmHandle.toString()) || {};
    keyInfo.set(handle.toString(), {
      algorithm: algorithm.algorithm || '',
      secretSize: this.secretSize
    });
    emit({
      type: 'bcrypt',
      api: 'BCryptGenerateSymmetricKey',
      result: retval.toString(),
      algorithmHandle: this.algorithmHandle.toString(),
      keyHandle: handle.toString(),
      algorithm: algorithm.algorithm || '',
      secretSize: this.secretSize,
      backtrace: this.trace
    }, readBuffer(this.secret, this.secretSize));
  }
}));

hook('BCryptSetProperty', address => ({
  onEnter(args) {
    const name = readWide(args[1]) || '';
    if (name !== 'ChainingMode')
      return;
    const trace = getTrace(this.context);
    if (!isForzaTrace(trace))
      return;
    const size = args[3].toUInt32();
    let value = '';
    if (name === 'ChainingMode')
      value = readWide(args[2], Math.floor(size / 2)) || '';
    send({
      type: 'bcrypt',
      api: 'BCryptSetProperty',
      objectHandle: args[0].toString(),
      property: name,
      value: value,
      size: size,
      backtrace: trace
    });
  }
}));

function cryptCallbacks(apiName) {
  return address => ({
    onEnter(args) {
      this.keyHandle = args[0];
      this.input = args[1];
      this.inputSize = args[2].toUInt32();
      this.paddingInfo = args[3];
      this.iv = args[4];
      this.ivSize = args[5].toUInt32();
      this.output = args[6];
      this.outputCapacity = args[7].toUInt32();
      this.outputSize = args[8];
      this.flags = args[9].toUInt32();
      this.trace = getTrace(this.context);
      this.traceThis =
        this.inputSize >= minBufferBytes &&
        this.inputSize <= maxBodyBytes &&
        isForzaTrace(this.trace);
      if (this.traceThis) {
        this.inputData = readBuffer(this.input, this.inputSize);
        this.ivData = readBuffer(this.iv, this.ivSize);
        this.authInfo = readAuthenticatedCipherInfo(this.paddingInfo);
      }
    },
    onLeave(retval) {
      if (!this.traceThis)
        return;
      let outputSize = 0;
      try {
        if (!this.outputSize.isNull())
          outputSize = this.outputSize.readU32();
      } catch (_) {
      }
      const knownKey = keyInfo.get(this.keyHandle.toString()) || {};
      const record = {
        type: 'bcrypt',
        api: apiName,
        result: retval.toString(),
        keyHandle: this.keyHandle.toString(),
        algorithm: knownKey.algorithm || '',
        knownSecretSize: knownKey.secretSize || 0,
        inputSize: this.inputSize,
        outputCapacity: this.outputCapacity,
        outputSize: outputSize,
        ivSize: this.ivSize,
        ivHex: bytesToHex(this.ivData),
        authenticatedCipherInfo: this.authInfo,
        flags: this.flags,
        inputHex: bytesToHex(this.inputData),
        backtrace: this.trace
      };
      emit(record, readBuffer(this.output, outputSize));
    }
  });
}

hook('BCryptEncrypt', cryptCallbacks('BCryptEncrypt'));
hook('BCryptDecrypt', cryptCallbacks('BCryptDecrypt'));

send({
  type: 'ready',
  module: 'bcrypt.dll',
  hooks: [
    'BCryptOpenAlgorithmProvider',
    'BCryptGenerateSymmetricKey',
    'BCryptSetProperty',
    'BCryptEncrypt',
    'BCryptDecrypt'
  ]
});
"""


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Trace Forza calls to stable Windows BCrypt exports."
    )
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--timeout-seconds", type=int, default=180)
    parser.add_argument("--min-buffer-bytes", type=int, default=256)
    parser.add_argument("--max-body-bytes", type=int, default=2 * 1024 * 1024)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    args.output.parent.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, Any]] = []
    stop_event = threading.Event()

    def write_record(record: dict[str, Any]) -> None:
        records.append(record)
        with args.output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, separators=(",", ":")) + "\n")

    def on_message(message: dict[str, Any], data: Any) -> None:
        record = {"received_at": time.time(), "message": message}
        write_record(record)
        payload = message.get("payload", {})
        if isinstance(payload, dict) and payload.get("type") in {
            "hooked",
            "hook-error",
            "ready",
        }:
            print(json.dumps(payload, indent=2), flush=True)

    device = frida.get_local_device()
    session = device.attach(args.pid)
    script = session.create_script(
        AGENT_SOURCE.replace("%MAX_BODY_BYTES%", str(args.max_body_bytes)).replace(
            "%MIN_BUFFER_BYTES%", str(args.min_buffer_bytes)
        )
    )
    script.on("message", on_message)
    script.load()

    try:
        stop_event.wait(args.timeout_seconds)
    except KeyboardInterrupt:
        pass
    finally:
        script.unload()
        session.detach()

    print(
        json.dumps(
            {"pid": args.pid, "records": len(records), "output": str(args.output)},
            indent=2,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
