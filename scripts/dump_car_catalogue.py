"""Dump the carId -> car codename catalogue from the live Forza process.

The catalogue is a contiguous array of 32-byte null-padded ASCII codename slots
indexed by carId (base + carId*0x20). Codenames look like LOT_00_ExigeWTA_18.
Discovered 2026-08-19; see docs/car_name_catalogue_research.md.

Standalone: it self-anchors by finding a known codename in memory and computing
the base, then verifies a second known codename lands on its expected carId
before dumping. Read-only (PROCESS_VM_READ, no injection). Re-runnable weekly;
new cars appear as new populated slots automatically.

    python dump_car_catalogue.py --process-name forzahorizon6 --output config/fh6_car_catalogue.json
"""
from __future__ import annotations
import argparse, ctypes, json, re, sys
from ctypes import wintypes
from pathlib import Path

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_INFORMATION=0x0400; PROCESS_VM_READ=0x0010
MEM_COMMIT=0x1000; PAGE_NOACCESS=0x01; PAGE_GUARD=0x100
STRIDE=0x20

# Two anchors that should exist in any recent build. carId is the array index.
DEFAULT_ANCHORS=[(4210,"LOT_00_ExigeWTA_18"),(4212,"NIS_32_SkylineWTA_93")]
CODENAME_RE=re.compile(rb"^[A-Za-z0-9]{2,}_.*_[0-9]{2}\x00")

class MBI(ctypes.Structure):
    _fields_=[("BaseAddress",ctypes.c_void_p),("AllocationBase",ctypes.c_void_p),
              ("AllocationProtect",wintypes.DWORD),("PartitionId",wintypes.WORD),
              ("RegionSize",ctypes.c_size_t),("State",wintypes.DWORD),
              ("Protect",wintypes.DWORD),("Type",wintypes.DWORD)]

def resolve_pid(name):
    import subprocess
    c=subprocess.run(["powershell.exe","-NoProfile","-Command",
        f"Get-Process -Name '{name}' -ErrorAction Stop | Sort-Object StartTime -Descending | Select-Object -First 1 -ExpandProperty Id"],
        capture_output=True,text=True,check=False)
    if not c.stdout.strip().isdigit(): raise RuntimeError(c.stderr.strip())
    return int(c.stdout.strip())

def regions(h):
    a=0; lim=0x00007FFFFFFFFFFF; info=MBI()
    while a<lim:
        if not kernel32.VirtualQueryEx(h,ctypes.c_void_p(a),ctypes.byref(info),ctypes.sizeof(info)): break
        base=int(info.BaseAddress or 0); size=int(info.RegionSize); nxt=base+size
        if nxt<=a: break
        if info.State==MEM_COMMIT and not(info.Protect&PAGE_NOACCESS) and not(info.Protect&PAGE_GUARD) and size:
            yield base,size
        a=nxt

def read(h,addr,length):
    buf=ctypes.create_string_buffer(length); act=ctypes.c_size_t()
    ok=kernel32.ReadProcessMemory(h,ctypes.c_void_p(addr),buf,length,ctypes.byref(act))
    if not ok and act.value==0: return b""
    return buf.raw[:act.value]

def find_all_needles(h, regs, needle):
    nb=needle.encode("ascii"); out=[]
    for base,size in regs:
        off=0
        while off<size:
            b=read(h,base+off,min(32*1024*1024,size-off))
            if not b: off+=32*1024*1024; continue
            i=0
            while True:
                j=b.find(nb,i)
                if j<0: break
                out.append(base+off+j); i=j+1
            # step back a little so a needle straddling the block edge is not missed
            off+=max(1,len(b)-len(nb))
    return out

def slot_name(h, base, carid):
    raw=read(h, base+carid*STRIDE, STRIDE)
    if not raw: return None
    z=raw.split(b"\x00",1)[0]
    try: return z.decode("ascii")
    except: return None

def main():
    ap=argparse.ArgumentParser()
    src=ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--pid",type=int); src.add_argument("--process-name")
    ap.add_argument("--output",type=Path,required=True)
    ap.add_argument("--max-carid",type=int,default=8192)
    args=ap.parse_args()
    if sys.platform!="win32": raise RuntimeError("Windows only")
    pid=args.pid or resolve_pid(args.process_name)
    h=kernel32.OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ,False,pid)
    if not h: raise ctypes.WinError(ctypes.get_last_error())
    regs=list(regions(h))

    # The codename occurs in several places (asset paths, other tables), so the
    # array copy is the occurrence of anchor1 whose derived base ALSO lands
    # anchor2 on its expected carId. Test every occurrence.
    cid0,name0=DEFAULT_ANCHORS[0]
    cid1,name1=DEFAULT_ANCHORS[1]
    candidates=find_all_needles(h,regs,name0)
    print(f"anchor '{name0}' occurrences: {len(candidates)}")
    base=None
    for addr0 in candidates:
        cand=addr0 - cid0*STRIDE
        if slot_name(h,cand,cid1)==name1:
            base=cand
            print(f"array base 0x{base:016x} (anchor1 @0x{addr0:016x}) -- anchor2 verified")
            break
    if base is None:
        print("no occurrence yields a base where anchor2 lands on its carId"); return 3
    print("verification OK: array is carId-indexed, stride 0x20")

    def valid(nm):
        return bool(nm) and bool(CODENAME_RE.match(nm.encode("ascii", "ignore") + b"\x00"))

    # Walk outward from the verified anchor to find the array's real extent. Slots
    # beyond the array are unrelated memory, so a run of invalid slots marks the
    # boundary; a few empties inside (unloaded cars) are tolerated.
    MAX_MISS = 48
    fresh = {}
    for direction in (1, -1):
        miss = 0
        cid = cid0
        while 0 <= cid < args.max_carid:
            nm = slot_name(h, base, cid)
            if valid(nm):
                fresh[cid] = nm
                miss = 0
            else:
                miss += 1
                if miss >= MAX_MISS:
                    break
            cid += direction
    print(f"array scan: {len(fresh)} codenames in carId {min(fresh)}..{max(fresh)}")

    # Merge into the persistent catalogue: keep everything seen before, add or
    # refresh what is loaded now. New weekly cars appear as new carIds across runs.
    existing = {}
    if args.output.exists():
        try:
            prev = json.loads(args.output.read_text(encoding="utf-8"))
            existing = {int(k): v for k, v in prev.get("by_car_id", {}).items()}
        except Exception:
            existing = {}
    added = [c for c in fresh if c not in existing]
    merged = dict(existing)
    merged.update(fresh)
    print(f"merge: {len(existing)} existing + {len(added)} new = {len(merged)} total")

    kernel32.CloseHandle(h)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "stride": STRIDE,
        "anchor": {str(cid0): name0},
        "count": len(merged),
        "note": "carId -> internal car codename (MAKE_model_year), from the live "
                "car catalogue array; accumulates across runs.",
        "by_car_id": {str(k): merged[k] for k in sorted(merged)},
    }
    args.output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(f"wrote {args.output}")
    for cid in (cid0, cid0 + 1, cid1):
        print(f"  {cid} -> {merged.get(cid)}")
    return 0

if __name__=="__main__":
    raise SystemExit(main())
