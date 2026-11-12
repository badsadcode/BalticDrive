#!/usr/bin/env python3
"""Stage 1 of the Baltic Drive map importer: OpenStreetMap .pbf -> flat numpy arrays.

Reads the extract once and writes one .npz per feature family into the work directory.
All coordinates are projected to world metres (the Baltic-wide projection in common.py),
stored as int32 centimetres: x = east, y = north. Waterways keep OSM's order (downstream).

    python3 extract.py <region.osm.pbf> <workdir>
"""
import sys
import os
import time
from array import array

import numpy as np
import osmium

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common import project, parse_float, ROAD_CLASS, RAIL, LANDUSE, building_type, \
    BUILDING_DEFAULT_HEIGHT, ROOF_SHAPES, poi_kind, point_kind, PT_TREE, PLACE_RANK


class Lines:
    """Polylines with per-feature attributes and (optionally) OSM node ids."""

    def __init__(self, with_nodes=False):
        self.xy = array('i')
        self.off = array('q', [0])
        self.nodes = array('q') if with_nodes else None
        self.attrs = []

    def add(self, lons, lats, attr, node_ids=None):
        x, y = project(lons, lats)
        pts = np.empty((len(lons), 2), dtype=np.int32)
        pts[:, 0] = np.round(x * 100)
        pts[:, 1] = np.round(y * 100)
        self.xy.frombytes(pts.tobytes())
        self.off.append(len(self.xy) // 2)
        if self.nodes is not None:
            self.nodes.extend(node_ids)
        self.attrs.append(attr)

    def save(self, path, attr_dtype):
        out = dict(xy=np.frombuffer(self.xy, dtype=np.int32).reshape(-1, 2),
                   off=np.frombuffer(self.off, dtype=np.int64),
                   attrs=np.array(self.attrs, dtype=attr_dtype))
        if self.nodes is not None:
            out['nodes'] = np.frombuffer(self.nodes, dtype=np.int64)
        np.savez(path, **out)


class Polys:
    """Polygons as rings; each feature owns a run of rings (outer first, then its holes)."""

    def __init__(self):
        self.xy = array('i')
        self.ring_off = array('q', [0])
        self.ring_inner = array('b')
        self.feat_ring = array('q', [0])
        self.attrs = []

    def add_ring(self, ring, inner):
        n = len(ring)
        lons = np.fromiter((p.lon for p in ring), dtype=np.float64, count=n)
        lats = np.fromiter((p.lat for p in ring), dtype=np.float64, count=n)
        x, y = project(lons, lats)
        pts = np.empty((n, 2), dtype=np.int32)
        pts[:, 0] = np.round(x * 100)
        pts[:, 1] = np.round(y * 100)
        self.xy.frombytes(pts.tobytes())
        self.ring_off.append(len(self.xy) // 2)
        self.ring_inner.append(1 if inner else 0)

    def add_area(self, area, attr):
        """One feature per outer ring, so multipolygons with several outers stay simple."""
        count = 0
        for outer in area.outer_rings():
            if len(outer) < 4:
                continue
            self.add_ring(outer, False)
            for inner in area.inner_rings(outer):
                if len(inner) >= 4:
                    self.add_ring(inner, True)
            self.feat_ring.append(len(self.ring_inner))
            self.attrs.append(attr)
            count += 1
        return count

    def save(self, path, attr_dtype):
        np.savez(path,
                 xy=np.frombuffer(self.xy, dtype=np.int32).reshape(-1, 2),
                 ring_off=np.frombuffer(self.ring_off, dtype=np.int64),
                 ring_inner=np.frombuffer(self.ring_inner, dtype=np.int8),
                 feat_ring=np.frombuffer(self.feat_ring, dtype=np.int64),
                 attrs=np.array(self.attrs, dtype=attr_dtype))


ROAD_DTYPE = [('cls', 'u1'), ('flags', 'u1'), ('width_dm', 'u1'), ('layer', 'i1'), ('lanes', 'u1')]
WATERLINE_DTYPE = [('width_dm', 'u1')]
LAND_DTYPE = [('cls', 'u1')]
BUILDING_DTYPE = [('type', 'u1'), ('part', 'u1'), ('roof', 'u1'), ('height_dm', 'u2'),
                  ('min_dm', 'u2'), ('roof_dm', 'u2'), ('has_height', 'u1')]

UNPAVED = {'unpaved', 'gravel', 'fine_gravel', 'compacted', 'dirt', 'ground', 'earth', 'sand',
           'grass', 'mud', 'pebblestone', 'woodchips', 'rock'}
PAVED = {'paved', 'asphalt', 'concrete', 'concrete:plates', 'concrete:lanes', 'paving_stones',
         'sett', 'cobblestone', 'unhewn_cobblestone', 'metal', 'wood', 'bricks'}

FLAG_BRIDGE, FLAG_TUNNEL, FLAG_ONEWAY, FLAG_UNPAVED, FLAG_LIT, FLAG_RAIL = 1, 2, 4, 8, 16, 32


class Handler(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.roads = Lines(with_nodes=True)
        self.waterlines = Lines()
        self.land = Polys()
        self.buildings = Polys()
        self.pois = []      # (kind, x_cm, y_cm, name)
        self.places = []    # (rank, x_cm, y_cm, population, name)
        self.pt_kind = array('B')   # trees, lamps, poles, pylons
        self.pt_lon = array('d')
        self.pt_lat = array('d')
        self.t0 = time.time()
        self.seen = 0

    def tick(self, what):
        self.seen += 1
        if self.seen % 500000 == 0:
            print(f'  {self.seen / 1e6:.1f}M {what} ... {time.time() - self.t0:.0f}s', flush=True)

    # ---- points --------------------------------------------------------------------------

    def add_point_features(self, tags, lon, lat):
        kind = poi_kind(tags)
        place = tags.get('place')
        if kind is None and place not in PLACE_RANK:
            return
        x, y = project(np.array([lon]), np.array([lat]))
        xc, yc = int(round(x[0] * 100)), int(round(y[0] * 100))
        if kind is not None:
            self.pois.append((kind, xc, yc, tags.get('name', '') or tags.get('brand', '') or ''))
        if place in PLACE_RANK and tags.get('name'):
            pop = parse_float(tags.get('population')) or 0
            self.places.append((PLACE_RANK[place], xc, yc, int(pop), tags['name']))

    def node(self, n):
        if len(n.tags) == 0:
            return
        if not n.location.valid():
            return
        pk = point_kind(n.tags)
        if pk is not None:
            self.pt_kind.append(pk)
            self.pt_lon.append(n.location.lon)
            self.pt_lat.append(n.location.lat)
            return
        self.add_point_features(n.tags, n.location.lon, n.location.lat)

    # ---- lines ---------------------------------------------------------------------------

    def way(self, w):
        self.tick('ways')
        tags = w.tags
        hw = tags.get('highway')
        rw = tags.get('railway')
        ww = tags.get('waterway')
        if hw in ROAD_CLASS and tags.get('area') != 'yes':
            cls, default_width = ROAD_CLASS[hw]
            self.add_road(w, tags, cls, default_width, hw)
        elif rw in RAIL and tags.get('service') not in ('yard', 'siding', 'spur', 'crossover'):
            self.add_road(w, tags, RAIL[rw], 3.2, None)
        elif tags.get('natural') == 'tree_row':
            # a mapped avenue: one tree roughly every 9 m along the line
            lons, lats = self.coords(w)
            if lons is not None:
                x, y = project(lons, lats)
                seg = np.hypot(np.diff(x), np.diff(y))
                total = float(seg.sum())
                if total > 4:
                    cum = np.concatenate([[0.0], np.cumsum(seg)])
                    at = np.arange(2.0, total, 9.0)
                    li = np.interp(at, cum, lons)
                    la = np.interp(at, cum, lats)
                    self.pt_kind.extend([PT_TREE] * len(at))
                    self.pt_lon.extend(li.tolist())
                    self.pt_lat.extend(la.tolist())
        elif ww in ('river', 'stream', 'canal') and tags.get('tunnel') in (None, 'no'):
            width = parse_float(tags.get('width')) or {'river': 14.0, 'canal': 7.0, 'stream': 2.6}[ww]
            lons, lats = self.coords(w)
            if lons is not None:
                self.waterlines.add(lons, lats, (int(min(255, max(10, width * 10))),))

    @staticmethod
    def coords(w):
        n = len(w.nodes)
        if n < 2:
            return None, None
        try:
            lons = np.fromiter((nd.lon for nd in w.nodes), dtype=np.float64, count=n)
            lats = np.fromiter((nd.lat for nd in w.nodes), dtype=np.float64, count=n)
        except osmium.InvalidLocationError:
            return None, None
        return lons, lats

    def add_road(self, w, tags, cls, default_width, hw):
        lons, lats = self.coords(w)
        if lons is None:
            return
        flags = 0
        if hw is None:
            flags |= FLAG_RAIL
        if tags.get('bridge') not in (None, 'no'):
            flags |= FLAG_BRIDGE
        if tags.get('tunnel') not in (None, 'no'):
            flags |= FLAG_TUNNEL
        oneway = tags.get('oneway')
        if oneway in ('yes', '1', 'true', '-1') or hw in ('motorway', 'motorway_link') or tags.get('junction') == 'roundabout':
            flags |= FLAG_ONEWAY
        surface = tags.get('surface')
        if hw is not None:
            if surface in UNPAVED or (hw == 'track' and surface not in PAVED):
                flags |= FLAG_UNPAVED
        if tags.get('lit') == 'yes':
            flags |= FLAG_LIT
        lanes = parse_float(tags.get('lanes')) or 0
        width = parse_float(tags.get('width'))
        if hw is None:
            width = default_width
        elif width is None or width < 2 or width > 25:
            width = default_width
            if lanes >= 1:
                width = max(default_width if lanes >= 2 else 3.2, lanes * 3.3 + 0.6)
                if lanes == 1 and not (flags & FLAG_ONEWAY):
                    width = min(default_width, 4.0)
        try:
            layer = int(float(tags.get('layer', '0')))
        except ValueError:
            layer = 0
        layer = max(-5, min(5, layer))
        self.roads.add(lons, lats,
                       (cls, flags, int(max(10, min(255, round(width * 10)))), layer, int(max(0, min(8, lanes)))),
                       [nd.ref for nd in w.nodes])

    # ---- areas ---------------------------------------------------------------------------

    def area(self, a):
        self.tick('areas')
        tags = a.tags
        b = tags.get('building')
        bp = tags.get('building:part')
        if (b and b != 'no') or (bp and bp != 'no'):
            # Bridges mapped in 3D as building parts (man_made=bridge, building=bridge) are drawn from the roads
            # instead; underground structures (stations, car parks below ground) are not seen at all.
            if tags.get('man_made') == 'bridge' or b == 'bridge' or bp == 'bridge' or 'bridge:structure' in tags:
                return
            if tags.get('location') in ('underground', 'subway') or (tags.get('layer', '0').lstrip('-').isdigit() and int(tags.get('layer', '0')) < 0):
                return
            self.add_building(a, tags, b if (b and b != 'no') else bp, part=not (b and b != 'no'))
            if b and b != 'no':
                self.add_area_poi(a, tags)
            return
        cls = LANDUSE(tags)
        if cls is not None:
            self.land.add_area(a, (cls,))
        if tags.get('amenity') == 'fuel' or tags.get('shop') or tags.get('place') in PLACE_RANK:
            self.add_area_poi(a, tags)

    def add_area_poi(self, a, tags):
        if poi_kind(tags) is None and tags.get('place') not in PLACE_RANK:
            return
        for outer in a.outer_rings():
            n = len(outer)
            if n < 4:
                continue
            lon = sum(p.lon for p in outer) / n
            lat = sum(p.lat for p in outer) / n
            self.add_point_features(tags, lon, lat)
            break

    def add_building(self, a, tags, kind, part):
        btype = building_type(kind, tags)
        height = parse_float(tags.get('height') or tags.get('building:height'))
        levels = parse_float(tags.get('building:levels'))
        roof_levels = parse_float(tags.get('roof:levels')) or 0
        roof_h = parse_float(tags.get('roof:height'))
        roof = ROOF_SHAPES.get(tags.get('roof:shape', ''), 0)
        has_height = 1
        if height is None:
            if levels is not None and levels > 0:
                height = levels * 3.0 + (1.0 if roof == 0 else 0.0)
                if roof_h is None and roof_levels > 0:
                    roof_h = roof_levels * 2.6
                if roof_h:
                    height += roof_h
            else:
                height = 0.0
                has_height = 0
        min_h = parse_float(tags.get('min_height'))
        if min_h is None:
            ml = parse_float(tags.get('building:min_level'))
            min_h = ml * 3.0 if ml else 0.0
        height = max(0.0, min(height, 400.0))
        min_h = max(0.0, min(min_h, height))
        roof_h = max(0.0, min(roof_h or 0.0, max(0.0, height - min_h)))
        self.buildings.add_area(a, (btype, 1 if part else 0, roof, int(round(height * 10)),
                                    int(round(min_h * 10)), int(round(roof_h * 10)), has_height))


def main():
    pbf, work = sys.argv[1], sys.argv[2]
    os.makedirs(work, exist_ok=True)
    h = Handler()
    print(f'Reading {pbf}', flush=True)
    h.apply_file(pbf, locations=True, idx='flex_mem')
    print(f'Parsed in {time.time() - h.t0:.0f}s: {len(h.roads.attrs)} road/rail ways, '
          f'{len(h.waterlines.attrs)} waterways, {len(h.land.attrs)} land polygons, '
          f'{len(h.buildings.attrs)} buildings/parts, {len(h.pois)} pois, {len(h.places)} places', flush=True)
    h.roads.save(os.path.join(work, 'roads.npz'), ROAD_DTYPE)
    h.waterlines.save(os.path.join(work, 'waterlines.npz'), WATERLINE_DTYPE)
    h.land.save(os.path.join(work, 'land.npz'), LAND_DTYPE)
    h.buildings.save(os.path.join(work, 'buildings.npz'), BUILDING_DTYPE)
    np.save(os.path.join(work, 'pois.npy'), np.array(
        [(k, x, y) for k, x, y, _ in h.pois], dtype=[('kind', 'u1'), ('x', 'i4'), ('y', 'i4')]))
    with open(os.path.join(work, 'poi_names.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(n.replace('\n', ' ') for _, _, _, n in h.pois))
    np.save(os.path.join(work, 'places.npy'), np.array(
        [(r, x, y, p) for r, x, y, p, _ in h.places],
        dtype=[('rank', 'u1'), ('x', 'i4'), ('y', 'i4'), ('pop', 'i4')]))
    with open(os.path.join(work, 'place_names.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(n.replace('\n', ' ') for *_, n in h.places))
    px, py = project(np.frombuffer(h.pt_lon, dtype=np.float64), np.frombuffer(h.pt_lat, dtype=np.float64))
    pts = np.zeros(len(h.pt_kind), dtype=[('kind', 'u1'), ('x', 'i4'), ('y', 'i4')])
    pts['kind'] = np.frombuffer(h.pt_kind, dtype=np.uint8)
    pts['x'] = np.round(np.asarray(px) * 100)
    pts['y'] = np.round(np.asarray(py) * 100)
    np.save(os.path.join(work, 'points.npy'), pts)
    print(f'{len(pts)} trees, lamps and poles', flush=True)
    print('Stage 1 done.', flush=True)


if __name__ == '__main__':
    main()
