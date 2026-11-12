"""Download GUGiK's bare-ground terrain model (NMT, 1 m source) for a lat/lon box, resampled to a coarser grid.

Writes one GeoTIFF per square block (EPSG:2180, Poland CS92) to the output folder and skips blocks already there, so
it can be stopped and restarted. Outside Poland the service returns zeros; the importer treats 0 as "no data".

    python tools/dem/fetch_gugik.py --pbf data/source/mazowieckie-261110.osm.pbf --out data/dem/gugik10

Data: Główny Urząd Geodezji i Kartografii, free for any use (Prawo geodezyjne i kartograficzne, art. 40a ust. 2 pkt 1).
"""
import argparse
import concurrent.futures as cf
import math
import os
import sys
import time
import urllib.request

from pyproj import Transformer

WCS = ("https://mapy.geoportal.gov.pl/wss/service/PZGIK/NMT/GRID1/WCS/DigitalTerrainModelFormatTIFF"
       "?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCoverage&COVERAGEID=DTM_PL-KRON86-NH_TIFF&FORMAT=image/tiff"
       "&SUBSET=x({x0},{x1})&SUBSET=y({y0},{y1})&SCALESIZE=x({n}),y({n})")


def blocks(bbox, block):
    lon0, lat0, lon1, lat1 = bbox
    to92 = Transformer.from_crs("EPSG:4326", "EPSG:2180", always_xy=True)
    xs, ys = [], []
    for i in range(21):
        for j in range(21):
            x, y = to92.transform(lon0 + (lon1 - lon0) * i / 20, lat0 + (lat1 - lat0) * j / 20)
            xs.append(x); ys.append(y)
    bx0, bx1 = math.floor(min(xs) / block), math.ceil(max(xs) / block)
    by0, by1 = math.floor(min(ys) / block), math.ceil(max(ys) / block)
    return [(bx, by) for by in range(by0, by1) for bx in range(bx0, bx1)]


def blocks_with_data(pbf, block, margin=1, min_nodes=2000):
    """Blocks holding at least `min_nodes` nodes of the extract, plus a margin. (A region's bounding box is mostly
    neighbours, and an extract carries a few far-away nodes of relations that cross its border.)"""
    import numpy as np
    import osmium
    to92 = Transformer.from_crs("EPSG:4326", "EPSG:2180", always_xy=True)
    from collections import Counter
    seen = Counter()
    lons, lats = [], []

    def flush():
        if not lons:
            return
        x, y = to92.transform(np.array(lons), np.array(lats))
        seen.update(zip((np.floor(x / block)).astype(int).tolist(), (np.floor(y / block)).astype(int).tolist()))
        lons.clear(); lats.clear()

    for obj in osmium.FileProcessor(pbf, osmium.osm.NODE):
        lons.append(obj.location.lon); lats.append(obj.location.lat)
        if len(lons) >= 2_000_000:
            flush()
    flush()
    dense = [b for b, n in seen.items() if n >= min_nodes]
    out = {(bx + i, by + j) for bx, by in dense for i in range(-margin, margin + 1) for j in range(-margin, margin + 1)}
    return sorted(out, key=lambda b: (b[1], b[0]))


def fetch(bx, by, block, spacing, out):
    path = os.path.join(out, f"{bx * block}_{by * block}.tif")
    if os.path.exists(path):
        return "have"
    n = block // spacing
    url = WCS.format(x0=bx * block, x1=(bx + 1) * block, y0=by * block, y1=(by + 1) * block, n=n)
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                data = r.read()
                kind = r.headers.get("Content-Type", "")
            if "tiff" not in kind:
                raise IOError(data[:300].decode("utf-8", "replace"))
            tmp = path + ".part"
            with open(tmp, "wb") as f:
                f.write(data)
            os.replace(tmp, path)
            return "got"
        except Exception as e:  # network hiccups: back off and retry
            err = e
            time.sleep(3 * (attempt + 1))
    return f"FAILED {bx},{by}: {err}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bbox", nargs=4, type=float, metavar=("LON0", "LAT0", "LON1", "LAT1"))
    ap.add_argument("--pbf", help="only blocks that hold data of this OSM extract (instead of --bbox)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--block", type=int, default=10000, help="block side in metres")
    ap.add_argument("--spacing", type=int, default=10, help="grid spacing in metres")
    ap.add_argument("--workers", type=int, default=4)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    todo = blocks_with_data(a.pbf, a.block) if a.pbf else blocks(a.bbox, a.block)
    print(f"{len(todo)} blocks of {a.block} m at {a.spacing} m", flush=True)
    done = failed = 0
    with cf.ThreadPoolExecutor(a.workers) as ex:
        for r in ex.map(lambda b: fetch(b[0], b[1], a.block, a.spacing, a.out), todo):
            done += 1
            if r.startswith("FAILED"):
                failed += 1
                print(r, flush=True)
            if done % 25 == 0:
                print(f"{done}/{len(todo)}", flush=True)
    print(f"done, {failed} failed", flush=True)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
