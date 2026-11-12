"""Elevation lookup for the importer: a folder of square GeoTIFF blocks in one projected CRS.

Blocks are named `<minx>_<miny>.tif` (metres, as written by tools/dem/fetch_gugik.py), all the same size and spacing.
Heights are sampled bilinearly between pixel centres. Missing blocks and no-data pixels (the GUGiK service returns 0
outside Poland) give NaN; callers fill those.
"""
import os
import re
from collections import OrderedDict

import numpy as np
from pyproj import Transformer

import common as C

_NAME = re.compile(r'^(-?\d+)_(-?\d+)\.tif$')


class DemMosaic:
    def __init__(self, folder, crs='EPSG:2180', cache_blocks=48):
        import rasterio
        self.folder = folder
        self.files = {}
        for name in os.listdir(folder):
            m = _NAME.match(name)
            if m:
                self.files[(int(m.group(1)), int(m.group(2)))] = os.path.join(folder, name)
        if not self.files:
            raise SystemExit(f'no DEM blocks in {folder}')
        with rasterio.open(next(iter(self.files.values()))) as d:
            b = d.bounds
            self.block = int(round(b.right - b.left))
            self.n = d.width
        self.spacing = self.block / self.n
        self.to_dem = Transformer.from_crs(C.WORLD_PROJ, crs, always_xy=True)
        self.cache = OrderedDict()
        self.cache_blocks = cache_blocks

    def _block(self, bx, by):
        key = (bx, by)
        a = self.cache.get(key)
        if a is not None:
            self.cache.move_to_end(key)
            return a
        path = self.files.get((bx * self.block, by * self.block))
        if path is None:
            a = None
        else:
            import rasterio
            with rasterio.open(path) as d:
                a = d.read(1).astype(np.float32)
            a[(a == 0) | (a < -50) | ~np.isfinite(a)] = np.nan
            a = a[::-1]  # row 0 = southernmost, so row index grows with northing
        self.cache[key] = a
        if len(self.cache) > self.cache_blocks:
            self.cache.popitem(last=False)
        return a

    def _gather(self, c, r):
        """Pixel values at global integer column / row (row grows northwards)."""
        out = np.full(c.shape, np.nan, dtype=np.float32)
        n = self.n
        bx, by = np.floor_divide(c, n), np.floor_divide(r, n)
        keys = bx.astype(np.int64) * 1_000_003 + by
        for k in np.unique(keys):
            sel = keys == k
            a = self._block(int(bx[sel][0]), int(by[sel][0]))
            if a is not None:
                out[sel] = a[r[sel] - by[sel][0] * n, c[sel] - bx[sel][0] * n]
        return out

    def sample_world(self, wx, wy):
        """Heights at world points (x east, y NORTH, metres in the world projection)."""
        x, y = self.to_dem.transform(np.asarray(wx, dtype=np.float64), np.asarray(wy, dtype=np.float64))
        fc = np.asarray(x) / self.spacing - 0.5
        fr = np.asarray(y) / self.spacing - 0.5
        c0, r0 = np.floor(fc).astype(np.int64), np.floor(fr).astype(np.int64)
        tx, ty = (fc - c0).astype(np.float32), (fr - r0).astype(np.float32)
        v00 = self._gather(c0, r0)
        v10 = self._gather(c0 + 1, r0)
        v01 = self._gather(c0, r0 + 1)
        v11 = self._gather(c0 + 1, r0 + 1)
        return (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty


def fill_nan(grid, fallback=100.0):
    """Replace NaN cells with the nearest valid value (or the fallback when nothing is valid)."""
    bad = ~np.isfinite(grid)
    if not bad.any():
        return grid
    if bad.all():
        return np.full_like(grid, fallback)
    from scipy import ndimage
    idx = ndimage.distance_transform_edt(bad, return_distances=False, return_indices=True)
    return grid[tuple(idx)]
