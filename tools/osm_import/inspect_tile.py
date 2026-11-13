"""Print what a built tile holds near a world point: roads (class, flags, lanes, width, heights), ground and water.

    python tools/osm_import/inspect_tile.py map mazowsze 71900 640579 [radius]
"""
import struct
import sys
import zlib

import numpy as np

CLS = ['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'unclass', 'resid', 'living', 'service', 'track',
       'link', 'rail', 'tram']


def read_index(d, name):
    b = open(f'{d}/{name}.idx', 'rb').read()
    magic, fmt, tile, land, hres, hunit, hbase, plen = struct.unpack_from('<4sIfIIffH', b, 0)
    o = struct.calcsize('<4sIfIIffH') + plen
    n, = struct.unpack_from('<I', b, o)
    o += 4
    idx = {}
    for i in range(n):
        tx, tz, pak, off, ln, raw = struct.unpack_from('<iiHIII', b, o + i * 22)
        idx[(tx, tz)] = (pak, off, ln, raw)
    return tile, land, hres, hunit, hbase, idx


def main():
    d, name, wx, wz = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
    radius = float(sys.argv[5]) if len(sys.argv) > 5 else 80
    tile, land, hres, hunit, hbase, idx = read_index(d, name)
    tx, tz = int(wx // tile), int(wz // tile)
    pak, off, ln, raw = idx[(tx, tz)]
    with open(f'{d}/{name}.{pak:02d}.pak', 'rb') as f:
        f.seek(off)
        blob = zlib.decompress(f.read(ln))
    lx, lz = wx - tx * tile, wz - tz * tile
    o = 4
    nroads, = struct.unpack_from('<I', blob, o)
    o += 4
    print(f'tile {tx},{tz}: {nroads} road pieces; roads within {radius:.0f} m of ({lx:.0f},{lz:.0f}):')
    for _ in range(nroads):
        cls, flags, _f2, wdm, layer, lanes, along, n = struct.unpack_from('<BBBBbBfH', blob, o)
        o += struct.calcsize('<BBBBbBfH')
        pts = np.frombuffer(blob, dtype=[('x', '<i2'), ('z', '<i2'), ('y', '<u2'), ('l', 'u1')], count=n, offset=o)
        o += n * 7
        x, z = pts['x'] / 10, pts['z'] / 10
        dist = np.hypot(x - lx, z - lz)
        if dist.min() > radius:
            continue
        y = pts['y'] * hunit + hbase
        fl = ''.join(c for c, bit in zip('BTOULR', (1, 2, 4, 8, 16, 32)) if flags & bit)
        print(f'  {CLS[cls]:9s} {fl:4s} layer {layer:2d} lanes {lanes} width {wdm / 10:4.1f}  y {y.min():6.1f}..{y.max():6.1f}  '
              f'lift {pts["l"].max() / 10:4.1f}  n {n}  nearest {dist.min():5.1f} m')
    # junction surfaces (format 4)
    nj, = struct.unpack_from('<I', blob, o); o += 4
    for _ in range(nj):
        _jf, rings = struct.unpack_from('<BB', blob, o); o += 2
        total = sum(struct.unpack_from(f'<{rings}H', blob, o)); o += 2 * rings + total * 6
        nt, = struct.unpack_from('<I', blob, o); o += 4 + nt * 2
    print(f'  {nj} junction surfaces')
    # skip buildings / pois / points to reach the rasters: walk the remaining sections
    nb, = struct.unpack_from('<I', blob, o); o += 4
    for _ in range(nb):
        _, bflags, _, _, _, _, rings = struct.unpack_from('<BBBHHHB', blob, o); o += struct.calcsize('<BBBHHHB')
        for _r in range(rings):
            k, = struct.unpack_from('<H', blob, o); o += 2 + k * 4
        if bflags & 2:
            k, = struct.unpack_from('<H', blob, o); o += 2 + k * 2
    npoi, = struct.unpack_from('<H', blob, o); o += 2
    for _ in range(npoi):
        o += 5
        k = blob[o]; o += 1 + k
    npt, = struct.unpack_from('<I', blob, o); o += 4 + npt * 5
    o += land * land
    g = np.frombuffer(blob, dtype='<u2', count=(hres + 1) ** 2, offset=o).reshape(hres + 1, hres + 1) * hunit + hbase
    o += (hres + 1) ** 2 * 2
    cell = tile / hres
    i, j = int(round(lx / cell)), int(round(lz / cell))
    print(f'ground at point {g[j, i]:.1f} m; 5x5 around:\n{np.round(g[max(0,j-2):j+3, max(0,i-2):i+3], 1)}')
    if blob[o] == 1:
        w = np.frombuffer(blob, dtype='<u2', count=(hres + 1) ** 2, offset=o + 1).reshape(hres + 1, hres + 1)
        wl = np.where(w > 0, w * hunit + hbase, np.nan)
        print(f'water at point {wl[j, i]:.1f}; water in tile {np.nanmin(wl):.1f}..{np.nanmax(wl):.1f}')
    else:
        print('no water in tile')


if __name__ == '__main__':
    main()
