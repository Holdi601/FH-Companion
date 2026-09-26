"""Locate the carId -> car-name catalogue table in the live Forza process.

Given two known (carId, name) anchors, this:
  pass 1 -- finds the in-memory car-name string objects for each anchor name,
  pass 2 -- scans memory for 8-byte pointers to those string objects, and for
            each such pointer checks whether the anchor's carId sits within a
            small window -- i.e. a catalogue entry {..., name_ptr, ..., carId}.

When both anchors resolve to the same (name_ptr_offset, carId_offset) shape, the
table layout is proven and the whole catalogue can be walked. Read-only.

    python find_car_catalogue.py --process-name forzahorizon6 \
        --anchor 4210 "Exige WTAC" --anchor 4212 "Skyline WTAC"
"""
from __future__ import annotations
import argparse, ctypes, struct, sys
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_INFORMATION=0x0400; PROCESS_VM_READ=0x0010
MEM_COMMIT=0x1000; PAGE_NOACCESS=0x01; PAGE_GUARD=0x100

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
    t=c.stdout.strip()
    if not t.isdigit(): raise RuntimeError(c.stderr.strip())
    return int(t)

def regions(h):
    a=0; lim=0x00007FFFFFFFFFFF; info=MBI()
    while a<lim:
        if not kernel32.VirtualQueryEx(h,ctypes.c_void_p(a),ctypes.byref(info),ctypes.sizeof(info)): break
        base=int(info.BaseAddress or 0); size=int(info.RegionSize); nxt=base+size
        if nxt<=a: break
        if info.State==MEM_COMMIT and not (info.Protect&PAGE_NOACCESS) and not (info.Protect&PAGE_GUARD) and size:
            yield base,size
        a=nxt

def read(h,addr,length):
    buf=ctypes.create_string_buffer(length); act=ctypes.c_size_t()
    ok=kernel32.ReadProcessMemory(h,ctypes.c_void_p(addr),buf,length,ctypes.byref(act))
    if not ok and act.value==0: return b""
    return buf.raw[:act.value]

def find_all(block,needle):
    out=[];i=0
    while True:
        j=block.find(needle,i)
        if j<0: break
        out.append(j); i=j+1
    return out

def main():
    ap=argparse.ArgumentParser()
    src=ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--pid",type=int); src.add_argument("--process-name")
    ap.add_argument("--anchor",nargs=2,action="append",metavar=("CARID","NAME"),required=True)
    ap.add_argument("--window",type=int,default=128,help="bytes around a name-ptr to search for the carId")
    ap.add_argument("--block-mb",type=int,default=64)
    args=ap.parse_args()
    if sys.platform!="win32": raise RuntimeError("Windows only")
    pid=args.pid or resolve_pid(args.process_name)
    h=kernel32.OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ,False,pid)
    if not h: raise ctypes.WinError(ctypes.get_last_error())
    anchors=[(int(c),n) for c,n in args.anchor]
    regs=list(regions(h))
    blk=args.block_mb*1024*1024

    # pass 1: string-object char addresses per anchor name
    name_addrs={n:set() for _,n in anchors}
    for base,size in regs:
        off=0
        while off<size:
            b=read(h,base+off,min(blk,size-off))
            if not b: off+=blk; continue
            for _,n in anchors:
                nb=n.encode("ascii")
                for i in find_all(b,nb):
                    # require the interned-string header just before: len(u32)+0xffffffff
                    hdr=b[max(0,i-8):i]
                    if len(hdr)==8 and hdr[4:8]==b"\xff\xff\xff\xff":
                        name_addrs[n].add(base+off+i)
            off+=len(b)
    for n in name_addrs: print(f"pass1: '{n}' string objects: {len(name_addrs[n])}")

    # candidate pointer targets: char addr and object base (charaddr-8)
    targets={}  # target_addr -> (carid,name)
    for cid,n in anchors:
        for a in name_addrs[n]:
            targets[a]=(cid,n); targets[a-8]=(cid,n)
    tset=set(targets)
    if not tset:
        print("no name strings found; aborting"); return 2

    # pass 2: find qwords that point at a target, then look for the carId nearby
    hits=[]
    for base,size in regs:
        off=0
        while off<size:
            b=read(h,base+off,min(blk,size-off))
            if not b: off+=blk; continue
            for p in range(0,len(b)-8,8):
                q=struct.unpack_from("<Q",b,p)[0]
                if q in tset:
                    cid,n=targets[q]
                    lo=max(0,p-args.window); hi=min(len(b),p+args.window)
                    win=b[lo:hi]
                    cid_le4=struct.pack("<I",cid); cid_le2=struct.pack("<H",cid)
                    rel=None
                    k=win.find(cid_le4)
                    if k<0: k=win.find(cid_le2)
                    if k>=0:
                        rel=(lo+k)-p  # carId offset relative to the name pointer
                        hits.append((base+off+p,q,cid,n,rel))
            off+=len(b)
    print(f"\npass2: name-pointer sites with the carId within +/-{args.window}B: {len(hits)}")
    from collections import Counter
    shape=Counter((cid,rel) for _,_,cid,_,rel in hits)
    for (cid,rel),c in shape.most_common(12):
        print(f"  carId {cid}: name_ptr, carId at offset {rel:+d}  x{c}")
    for addr,q,cid,n,rel in hits[:12]:
        print(f"   entry@0x{addr:016x} -> name 0x{q:016x} '{n}' carId {cid} @ {rel:+d}")
    kernel32.CloseHandle(h)
    return 0 if hits else 2

if __name__=="__main__":
    raise SystemExit(main())
