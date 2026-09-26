from __future__ import annotations

import argparse
import json

import frida


def main() -> int:
    parser = argparse.ArgumentParser(description="Attach to a local process and detach.")
    parser.add_argument("--pid", required=True, type=int)
    args = parser.parse_args()

    session = frida.attach(args.pid)
    try:
        script = session.create_script(
            """
            rpc.exports = {
              inspect() {
                const modules = Process.enumerateModules();
                return {
                  pid: Process.id,
                  arch: Process.arch,
                  platform: Process.platform,
                  pointerSize: Process.pointerSize,
                  modules: modules.slice(0, 20).map(m => ({
                    name: m.name,
                    base: m.base.toString(),
                    size: m.size,
                    path: m.path
                  }))
                };
              }
            };
            """
        )
        script.load()
        print(json.dumps(script.exports_sync.inspect(), indent=2))
        script.unload()
    finally:
        session.detach()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
