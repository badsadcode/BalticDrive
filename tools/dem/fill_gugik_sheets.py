"""Fill the holes the GUGiK WCS leaves (sheets surveyed in the EVRF2007 height datum are not in it) from GUGiK's
per-sheet downloads.

For every 10 km block written by fetch_gugik.py, the no-data cells are found; the EVRF2007 sheet index (WMS
GetFeatureInfo) is asked which sheets cover them; each needed sheet (ARC/INFO ASCII grid, 0.5–1 m, 10–40 MB) is
downloaded, averaged down to the block's 10 m grid where the block has holes, and deleted. Progress is kept in
`sheets_done.txt` so the run can be stopped and restarted. Height datum: EVRF2007 is within ~0.2 m of Kronsztadt 86 in
Mazowsze, below what the game can show.

    python tools/dem/fill_gugik_sheets.py data/dem/gugik10 [--workers 4]
"""
import argparse
import concurrent.futures as cf
import io
import os
import re
import sys
import threading
import time
import urllib.request

import numpy as np

WMS = ("https://mapy.geoportal.gov.pl/wss/service/PZGIK/NMT/WMS/SkorowidzeUkladEVRF2007?SERVICE=WMS&VERSION=1.1.1"
       "&REQUEST=GetFeatureInfo&LAYERS={L}&QUERY_LAYERS={L}&SRS=EPSG:2180&BBOX={x0},{y0},{x1},{y1}&WIDTH=101&HEIGHT=101"
       "&X=50&Y=50&INFO_FORMAT=text/html&FEATURE_COUNT=30&STYLES=")
LAYERS = ["SkorowidzeNMT2023iStarsze", "SkorowidzeNMT2024", "SkorowidzeNMT2025", "SkorowidzeNMT2026"]
_rec = re.compile(r'\{[^{}]*?godlo:"(?P<godlo>[^"]*)"[^{}]*?\}', re.S)
_name = re.compile(r'_NAME_')
lock = threading.Lock()


def get(url, timeout=180, tries=5):
    for a in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=timeout) as r:
                return r.read()
        except Exception as e:
            err = e
            time.sleep(4 * (a + 1))
    raise IOError(f"{url}: {err}")


def sheets_at(x, y):
    """Sheets covering a point (EPSG:2180): list of dicts with url, year, crs, cell size."""
    out = []
    for L in LAYERS:
        html = get(WMS.format(L=L, x0=x - 50, y0=y - 50, x1=x + 50, y1=y + 50), timeout=60).decode("utf-8", "replace")
        urls = re.findall(r"https://opendata\.geoportal\.gov\.pl/NumDaneWys/NMT/[^\"' <>]+\.asc", html)
        recs = re.findall(r'godlo:"([^"]*)".*?aktualnosc:"([^"]*)".*?ukladWspolrzednychPoziomych:"([^"]*)"', html, re.S)
        cells = re.findall(r'charakterystykaPrzestrzenna:"([^"]*)"', html)
        # records and links come in the same order
        for i, u in enumerate(urls):
            date, crs = (recs[i][1], recs[i][2]) if i < len(recs) else ("", "")
            out.append({"url": u, "date": date, "crs": crs, "cell": cells[i] if i < len(cells) else ""})
    return out


def parse_asc(data):
    """ARC/INFO ASCII grid → (array rows north→south, xll centre, yll centre, cellsize, crs-agnostic)."""
    head = {}
    f = io.BytesIO(data)
    for _ in range(6):
        k, v = f.readline().split()
        head[k.decode().lower()] = float(v)
    n, m = int(head["nrows"]), int(head["ncols"])
    arr = np.fromstring(f.read().decode("ascii", "replace"), sep=" ", dtype=np.float32)
    arr = arr[: n * m].reshape(n, m)
    arr[arr == head.get("nodata_value", -9999)] = np.nan
    xll = head.get("xllcenter", head.get("xllcorner", 0) + head["cellsize"] / 2)
    yll = head.get("yllcenter", head.get("yllcorner", 0) + head["cellsize"] / 2)
    return arr, xll, yll, head["cellsize"]


_TRANSFORMERS = {}


def fetch_bin(s, block, n):
    """Worker process: download one sheet, average its heights into 10 m cells of the blocks it covers.
    Returns (bytes, [(block key, flat cell indices, sums, counts), ...])."""
    from pyproj import Transformer
    data = get(s["url"], timeout=600)
    arr, xll, yll, cs = parse_asc(data)
    nrows, ncols = arr.shape
    k = max(1, int(round(2.0 / cs)))                    # about one sample per 2 m is plenty for a 10 m cell
    sub = arr[::k, ::k]
    jj, ii = np.nonzero(np.isfinite(sub))
    if len(jj) == 0:
        return len(data), []
    xs = xll + ii * k * cs
    ys = yll + (nrows - 1 - jj * k) * cs
    crs = s["crs"]
    if crs != "PL-1992":
        if crs not in _TRANSFORMERS:
            zone = int(re.search(r"S(\d+)", crs).group(1))
            _TRANSFORMERS[crs] = Transformer.from_crs(f"EPSG:{2170 + zone}", "EPSG:2180", always_xy=True)
        xs, ys = _TRANSFORMERS[crs].transform(xs, ys)
    vals = sub[jj, ii].astype(np.float64)
    cell = block / n
    bx = np.floor(xs / block).astype(np.int64)
    by = np.floor(ys / block).astype(np.int64)
    ci = ((xs - bx * block) / cell).astype(np.int64).clip(0, n - 1)
    cj = ((by * block + block - ys) / cell).astype(np.int64).clip(0, n - 1)
    out = []
    keys = bx * 1_000_003 + by
    for key in np.unique(keys):
        sel = keys == key
        flat = cj[sel] * n + ci[sel]
        u, inv = np.unique(flat, return_inverse=True)
        out.append(((int(bx[sel][0]) * block, int(by[sel][0]) * block), u.astype(np.int32),
                    np.bincount(inv, vals[sel]).astype(np.float32), np.bincount(inv).astype(np.float32)))
    return len(data), out


class Block:
    def __init__(self, path, block, n):
        import rasterio
        self.path = path
        with rasterio.open(path) as d:
            self.a = d.read(1).astype(np.float32)
            self.profile = d.profile
            b = d.bounds
        self.x0, self.y1 = b.left, b.top
        self.cell = block / n
        self.hole = (self.a == 0) | ~np.isfinite(self.a) | (self.a < -50)
        self.sum = np.zeros_like(self.a)
        self.cnt = np.zeros_like(self.a)
        self.dirty = False

    def save(self):
        import rasterio
        got = self.hole & (self.cnt > 0)
        if not got.any():
            return 0
        self.a[got] = self.sum[got] / self.cnt[got]
        self.hole &= ~got
        self.sum[:] = 0
        self.cnt[:] = 0
        tmp = self.path + ".part"
        with rasterio.open(tmp, "w", **self.profile) as d:
            d.write(self.a, 1)
        os.replace(tmp, self.path)
        return int(got.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--probe", type=float, default=1500, help="spacing (m) of index queries over holes")
    ap.add_argument("--reindex", action="store_true", help="ask the sheet index again even if a saved list exists")
    a = ap.parse_args()
    from pyproj import Transformer
    import rasterio

    files = {}
    for name in os.listdir(a.folder):
        m = re.match(r"^(-?\d+)_(-?\d+)\.tif$", name)
        if m:
            files[(int(m.group(1)), int(m.group(2)))] = os.path.join(a.folder, name)
    with rasterio.open(next(iter(files.values()))) as d:
        block, n = int(round(d.bounds.right - d.bounds.left)), d.width
    blocks = {}
    probes = []
    for (bx, by), path in files.items():
        b = Block(path, block, n)
        if b.hole.mean() < 0.001:
            continue
        blocks[(bx, by)] = b
        step = int(a.probe / b.cell)
        for j in range(step // 2, n, step):
            for i in range(step // 2, n, step):
                win = b.hole[max(0, j - step // 2): j + step // 2, max(0, i - step // 2): i + step // 2]
                if win.mean() > 0.02:
                    probes.append((bx + (i + 0.5) * b.cell, by + block - (j + 0.5) * b.cell))
    holes = sum(int(b.hole.sum()) for b in blocks.values())
    print(f"{len(blocks)} blocks with holes, {holes * (block / n) ** 2 / 1e6:.0f} km2 missing, {len(probes)} index queries",
          flush=True)

    # 1. which sheets: newest per spot, preferring PL-1992 (no reprojection)
    done_path = os.path.join(a.folder, "sheets_done.txt")
    done = set(open(done_path).read().split()) if os.path.exists(done_path) else set()
    wanted = {}

    def ask(p):
        try:
            return sheets_at(*p)
        except Exception as e:
            print(f"index query failed at {p}: {e}", flush=True)
            return []
    if os.path.exists(os.path.join(a.folder, "sheets_wanted.json")) and not a.reindex:
        probes = []                    # use the saved sheet list (--reindex to ask the index again)
    with cf.ThreadPoolExecutor(16) as ex:
        for k, res in enumerate(ex.map(ask, probes)):
            if res:
                # the game keeps 10 m: prefer 1 m sheets (about 4x fewer bytes per km2 than 0.5 m ones), then the newest
                res.sort(key=lambda s: (s["cell"] == "1.00 m", s["crs"] == "PL-1992", s["date"]), reverse=True)
                best = res[0]
                wanted[best["url"]] = best
            if k % 200 == 0:
                print(f"index {k}/{len(probes)}: {len(wanted)} sheets so far", flush=True)
    import json
    cache = os.path.join(a.folder, "sheets_wanted.json")
    if os.path.exists(cache):          # sheets found by an earlier run's index lookups (lookups take ~30 min)
        for s in json.load(open(cache)):
            wanted.setdefault(s["url"], s)
    json.dump(list(wanted.values()), open(cache, "w"))
    todo = [s for u, s in wanted.items() if u not in done]
    print(f"{len(wanted)} sheets needed, {len(todo)} still to fetch", flush=True)

    to92 = {}

    def transformer(crs):
        if crs not in to92:
            if crs == "PL-1992":
                to92[crs] = None
            else:
                zone = int(re.search(r"S(\d+)", crs).group(1))
                to92[crs] = Transformer.from_crs(f"EPSG:{2170 + zone}", "EPSG:2180", always_xy=True)
        return to92[crs]

    got_bytes = [0]
    filled = [0]

    def fetch(s):
        data = get(s["url"], timeout=600)
        arr, xll, yll, cs = parse_asc(data)
        nrows, ncols = arr.shape
        # sample every k-th cell (≈ 1 per 2 m) and average into the 10 m blocks
        k = max(1, int(round(2.0 / cs)))
        sub = arr[::k, ::k]
        jj, ii = np.nonzero(np.isfinite(sub))
        if len(jj) == 0:
            return len(data), 0
        xs = xll + ii * k * cs
        ys = yll + (nrows - 1 - jj * k) * cs
        t = transformer(s["crs"])
        if t is not None:
            xs, ys = t.transform(xs, ys)
        vals = sub[jj, ii]
        bx = (np.floor(xs / block) * block).astype(np.int64)
        by = (np.floor(ys / block) * block).astype(np.int64)
        with lock:
            for key in set(zip(bx.tolist(), by.tolist())):
                b = blocks.get(key)
                if b is None:
                    continue
                sel = (bx == key[0]) & (by == key[1])
                ci = ((xs[sel] - key[0]) / b.cell).astype(np.int64)
                cj = ((key[1] + block - ys[sel]) / b.cell).astype(np.int64)
                ok = (ci >= 0) & (ci < n) & (cj >= 0) & (cj < n)
                ci, cj, v = ci[ok], cj[ok], vals[sel][ok]
                m = b.hole[cj, ci]
                np.add.at(b.sum, (cj[m], ci[m]), v[m])
                np.add.at(b.cnt, (cj[m], ci[m]), 1)
                b.dirty = True
        return len(data), int(len(vals))

    t0 = time.time()
    with cf.ProcessPoolExecutor(a.workers) as ex:
        futs = {ex.submit(fetch_bin, s, block, n): s for s in todo}
        for k, f in enumerate(cf.as_completed(futs)):
            s = futs[f]
            try:
                nb, parts = f.result()
                got_bytes[0] += nb
                for key, flat, sm, cnt in parts:
                    b = blocks.get(key)
                    if b is None:
                        continue
                    jj, ii = np.divmod(flat, n)
                    m = b.hole[jj, ii]
                    b.sum[jj[m], ii[m]] += sm[m]
                    b.cnt[jj[m], ii[m]] += cnt[m]
                    b.dirty = True
                done.add(s["url"])
            except Exception as e:
                print(f"FAILED {s['url']}: {e}", flush=True)
            if (k + 1) % 25 == 0 or k + 1 == len(todo):
                for b in blocks.values():
                    if b.dirty:
                        filled[0] += b.save()
                        b.dirty = False
                open(done_path, "w").write("\n".join(sorted(done)))
                el = time.time() - t0
                left = sum(int(b.hole.sum()) for b in blocks.values()) * (block / n) ** 2 / 1e6
                print(f"{k + 1}/{len(todo)} sheets, {got_bytes[0] / 1e9:.1f} GB downloaded, "
                      f"{el / 60:.0f} min, {left:.0f} km2 still missing", flush=True)
    print("done", flush=True)


if __name__ == "__main__":
    main()
