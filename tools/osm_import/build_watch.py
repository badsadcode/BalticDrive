"""Runs a map build with a memory watchdog (D133). The first all-Poland build ran Tom's PC (32 GB) into a hard crash
after 2.5 hours, with nothing in the logs to say why. This starts the build itself, writes free RAM and the build's own
memory (its main process; the pool workers come and go) to a log every 30 s, and stops the build cleanly if free RAM
falls under a floor: a stopped build costs time, a frozen PC costs whatever else was open.

    python tools/osm_import/build_watch.py --log data/work/build_poland_mem.log --floor-gb 2.5 -- \
        python tools/osm_import/build.py data/work/poland2 map --dem data/dem/gugik10 --name poland --workers 3

Windows only (GlobalMemoryStatusEx, GetProcessMemoryInfo through ctypes: no extra packages).
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time


class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", wt.DWORD), ("dwMemoryLoad", wt.DWORD), ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t)]


def free_gb():
    m = MEMORYSTATUSEX(); m.dwLength = ctypes.sizeof(m)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.ullAvailPhys / 2**30, m.ullAvailPageFile / 2**30


def process_gb(pid):
    h = ctypes.windll.kernel32.OpenProcess(0x0400 | 0x0010, False, pid)   # query information, vm read
    if not h:
        return 0.0, 0.0
    try:
        c = PROCESS_MEMORY_COUNTERS(); c.cb = ctypes.sizeof(c)
        ctypes.windll.psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb)
        return c.WorkingSetSize / 2**30, c.PagefileUsage / 2**30   # in RAM, committed
    finally:
        ctypes.windll.kernel32.CloseHandle(h)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--floor-gb", type=float, default=2.5, help="stop the build if free RAM falls below this")
    ap.add_argument("--every", type=float, default=30)
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    a = ap.parse_args()
    cmd = a.cmd[1:] if a.cmd and a.cmd[0] == "--" else a.cmd
    t0 = time.time()
    with open(a.log, "a", buffering=1) as log:
        def say(s):
            log.write(f"{time.strftime('%H:%M:%S')} [{time.time() - t0:6.0f}s] {s}\n")
        say("start: " + " ".join(cmd))
        p = subprocess.Popen(cmd)
        peak = 0.0
        low = 0
        while p.poll() is None:
            free, free_commit = free_gb()
            ws, committed = process_gb(p.pid)
            peak = max(peak, committed)
            say(f"free RAM {free:5.1f} GB, free commit {free_commit:5.1f} GB; build {ws:5.1f} GB in RAM, {committed:5.1f} GB committed (peak {peak:.1f})")
            low = low + 1 if free < a.floor_gb else 0
            if low >= 2:   # twice in a row: not a passing spike
                say(f"STOPPED: free RAM under {a.floor_gb} GB twice in a row")
                subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
                p.wait()
                return 3
            for _ in range(int(a.every)):
                if p.poll() is not None:
                    break
                time.sleep(1)
        say(f"build ended with code {p.returncode}; peak {peak:.1f} GB committed")
        return p.returncode


if __name__ == "__main__":
    sys.exit(main())
