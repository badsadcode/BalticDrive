"""Build the list of GUGiK terrain sheets needed to fill the holes in the 10 m blocks, from the sheet index's WFS:
a few thousand area queries instead of a hundred thousand point queries (D110).

GUGiK publishes its NMT sheet index (outline, year, grid, coordinate and height system, download link) as WFS, one
layer per survey year, for both height systems:
    https://mapy.geoportal.gov.pl/wss/service/PZGIK/NumerycznyModelTerenuKRON86/WFS/Skorowidze    (2000-2019)
    https://mapy.geoportal.gov.pl/wss/service/PZGIK/NumerycznyModelTerenuEVRF2007/WFS/Skorowidze  (2018-)
(see https://www.geoportal.gov.pl/pl/dane/numeryczny-model-terenu-nmt/). Every block with holes is covered by boxes,
every box asked of every layer; then, like fill_gugik_sheets.py's point queries, the holes are probed every 1.5 km and
the best sheet over each probe is chosen: a 1 m grid before 0.5 m (the game keeps 10 m), PL-1992 before other
coordinate systems, then the newest. The result is merged into the folder's sheets_wanted.json (the old list is kept
as sheets_wanted.before_wfs.json), which fill_gugik_sheets.py then downloads without --reindex.

    python tools/dem/sheet_index_wfs.py data/dem/gugik10 [--box 40000] [--workers 8]

Note: EPSG:2180 in WFS 2.0 is northing first: BBOX=ymin,xmin,ymax,xmax and the posList pairs are (y, x).
"""
import argparse
import concurrent.futures as cf
import json
import os
import re
import shutil
import sys
import time
import urllib.request

import numpy as np

SERVICES = {
    "https://mapy.geoportal.gov.pl/wss/service/PZGIK/NumerycznyModelTerenuKRON86/WFS/Skorowidze": None,
    "https://mapy.geoportal.gov.pl/wss/service/PZGIK/NumerycznyModelTerenuEVRF2007/WFS/Skorowidze": None,
}
FEATURE = re.compile(r"<gugik:(SkorowidzNMT\d+)\b[^>]*>(.*?)</gugik:\1>", re.S)


def field(body, name):
    m = re.search(rf"<gugik:{name}>([^<]*)</gugik:{name}>", body)
    return m.group(1).strip() if m else ""


def get(url, timeout=180, tries=5):
    err = None
    for a in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=timeout) as r:
                return r.read().decode("utf-8", "replace")
        except Exception as e:
            err = e
            time.sleep(4 * (a + 1))
    raise IOError(f"{url}: {err}")


def layers(service):
    caps = get(f"{service}?SERVICE=WFS&REQUEST=GetCapabilities")
    return sorted(set(re.findall(r"<(?:wfs:)?Name>(gugik:SkorowidzNMT\d+)</(?:wfs:)?Name>", caps)))


def sheets_in(service, layer, x0, y0, x1, y1):
    url = (f"{service}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAMES={layer}"
           f"&BBOX={y0},{x0},{y1},{x1},urn:ogc:def:crs:EPSG::2180&COUNT=20000")
    xml = get(url)
    out = []
    for _, body in FEATURE.findall(xml):
        u = field(body, "url_do_pobrania")
        if not u.lower().endswith(".asc"):
            continue
        pos = re.search(r"<gml:posList[^>]*>([^<]*)</gml:posList>", body)
        if not pos:
            continue
        v = np.array(pos.group(1).split(), dtype=np.float64).reshape(-1, 2)   # (y, x) pairs
        m = re.search(r"<gml:timePosition>([^<]*)</gml:timePosition>", body)
        out.append({"url": u, "date": m.group(1) if m else field(body, "akt_rok"), "crs": field(body, "uklad_xy"),
                    "cell": field(body, "char_przestrz"), "h": field(body, "uklad_h"), "godlo": field(body, "godlo"),
                    "ring": v[:, ::-1].tolist()})
    if 'numberReturned="20000"' in xml:
        print(f"warning: {layer} {x0},{y0}: 20000 sheets, the box may be cut short", flush=True)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--box", type=int, default=40000, help="query box side (m)")
    ap.add_argument("--probe", type=float, default=1500, help="spacing (m) of probes over holes")
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--cached", action="store_true", help="don't ask the WFS: use sheets_index_wfs.json from a run before")
    a = ap.parse_args()
    import rasterio
    from shapely import STRtree, points, Polygon

    # 1. the holes, probed as fill_gugik_sheets.py does
    probes, extents = [], []
    for name in sorted(os.listdir(a.folder)):
        m = re.match(r"^(-?\d+)_(-?\d+)\.tif$", name)
        if not m:
            continue
        with rasterio.open(os.path.join(a.folder, name)) as d:
            arr = d.read(1)
            block, n = int(round(d.bounds.right - d.bounds.left)), d.width
        hole = (arr == 0) | ~np.isfinite(arr) | (arr < -50)
        if hole.mean() < 0.001:
            continue
        bx, by = int(m.group(1)), int(m.group(2))
        cell = block / n
        step = int(a.probe / cell)
        for j in range(step // 2, n, step):
            for i in range(step // 2, n, step):
                win = hole[max(0, j - step // 2): j + step // 2, max(0, i - step // 2): i + step // 2]
                if win.mean() > 0.02:
                    probes.append((bx + (i + 0.5) * cell, by + block - (j + 0.5) * cell))
        extents.append((bx, by, block))
    print(f"{len(extents)} blocks with holes, {len(probes)} probes", flush=True)

    # 2. the boxes covering them, asked of every layer of both services
    boxes = sorted({(bx // a.box, by // a.box) for bx, by, _ in extents})
    jobs = []
    for s in SERVICES:
        for L in layers(s):
            for gx, gy in boxes:
                jobs.append((s, L, gx * a.box, gy * a.box, (gx + 1) * a.box, (gy + 1) * a.box))
    print(f"{len(boxes)} boxes of {a.box / 1000:.0f} km × {len(jobs) // max(1, len(boxes))} layers = {len(jobs)} queries", flush=True)
    sheets = {}
    t0 = time.time()
    failed = 0
    if a.cached:
        jobs = []
    with cf.ThreadPoolExecutor(a.workers) as ex:
        futs = {ex.submit(sheets_in, *j): j for j in jobs}
        for k, f in enumerate(cf.as_completed(futs)):
            try:
                for s in f.result():
                    sheets.setdefault(s["url"], s)
            except Exception as e:
                failed += 1
                print(f"FAILED {futs[f][1]} {futs[f][2]},{futs[f][3]}: {e}", flush=True)
            if (k + 1) % 200 == 0 or k + 1 == len(jobs):
                print(f"{k + 1}/{len(jobs)} queries, {len(sheets)} sheets, {failed} failed, {time.time() - t0:.0f} s", flush=True)
    if failed:
        print(f"{failed} queries failed: run again to retry (sheets found are merged)", flush=True)

    # everything found, kept (a later step failing shouldn't cost another 10 minutes of queries)
    raw = os.path.join(a.folder, "sheets_index_wfs.json")
    old = {s["url"]: s for s in json.load(open(raw))} if os.path.exists(raw) else {}
    old.update(sheets)
    sheets = old
    json.dump(list(sheets.values()), open(raw + ".part", "w"))
    os.replace(raw + ".part", raw)

    # 3. the best sheet over each probe
    lst = list(sheets.values())
    tree = STRtree([Polygon(s["ring"]) for s in lst])
    pts = points(np.array(probes)) if probes else []
    wanted = {}
    if len(lst) and len(probes):
        hit_p, hit_s = tree.query(pts, predicate="within")   # probe within sheet
        best = {}
        for p, si in zip(hit_p.tolist(), hit_s.tolist()):
            s = lst[si]
            key = (s["cell"] == "1.00 m", s["crs"] == "PL-1992", s["date"])
            if p not in best or key > best[p][0]:
                best[p] = (key, si)
        for _, si in best.values():
            s = lst[si]
            wanted[s["url"]] = {k: s[k] for k in ("url", "date", "crs", "cell")}
        print(f"{len(best)} of {len(probes)} probes covered; {len(wanted)} sheets chosen", flush=True)

    # rough size: an ASCII grid takes about 8 bytes a cell
    def area(r):
        r = np.array(r)
        return 0.5 * abs(np.dot(r[:, 0], np.roll(r[:, 1], 1)) - np.dot(r[:, 1], np.roll(r[:, 0], 1)))
    by_url = {s["url"]: s for s in lst}
    gb = sum(area(by_url[u]["ring"]) / float(by_url[u]["cell"].split()[0].replace(",", ".")) ** 2 * 8 for u in wanted) / 1e9
    cells = {}
    for u in wanted:
        cells[by_url[u]["cell"]] = cells.get(by_url[u]["cell"], 0) + 1
    print(f"grids: {cells}; about {gb:,.0f} GB to download", flush=True)

    # 4. merged into the list fill_gugik_sheets.py reads (the old one kept)
    cache = os.path.join(a.folder, "sheets_wanted.json")
    if os.path.exists(cache):
        keep = os.path.join(a.folder, "sheets_wanted.before_wfs.json")
        if not os.path.exists(keep):
            shutil.copyfile(cache, keep)
        for s in json.load(open(cache)):
            wanted.setdefault(s["url"], s)
    tmp = cache + ".part"
    json.dump(list(wanted.values()), open(tmp, "w"))
    os.replace(tmp, cache)
    print(f"{len(wanted)} sheets in {cache}", flush=True)


if __name__ == "__main__":
    main()
