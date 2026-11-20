"""Merge several OpenStreetMap extracts into one, for a map made of several regions (D90).

Neighbouring Geofabrik extracts overlap along their border (each keeps the whole of every way that crosses it), so
the same node, way or relation can be in both. Merging keeps one copy of each (the newest version), and stage 1 then
sees the border roads whole: they get one height solution and no seam, as if the regions had always been one.

    python tools/osm_import/merge.py data/source/polska-261120.osm.pbf data/source/mazowieckie-*.osm.pbf data/source/podlaskie-*.osm.pbf
"""
import glob
import os
import sys
import time

import osmium


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    out, patterns = sys.argv[1], sys.argv[2:]
    inputs = sorted({p for pat in patterns for p in glob.glob(pat)})
    if len(inputs) < 1:
        sys.exit("no input files")
    t0 = time.time()
    reader = osmium.MergeInputReader()
    for p in inputs:
        print(f"  + {p} ({os.path.getsize(p) / 1e6:.0f} MB)", flush=True)
        reader.add_file(p)
    if os.path.exists(out):
        os.remove(out)
    writer = osmium.SimpleWriter(out)
    try:
        reader.apply(writer, simplify=True)
    finally:
        writer.close()
    print(f"wrote {out} ({os.path.getsize(out) / 1e6:.0f} MB) in {time.time() - t0:.0f} s", flush=True)


if __name__ == "__main__":
    main()
