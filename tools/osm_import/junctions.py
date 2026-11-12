"""Junction surfaces for the importer (D11 in docs/DECISIONS.md).

Where three or more road arms meet, the roads stop at the junction's edge and the junction is one surface:

1. Junction nodes: points where drivable roads (not driveways or tracks) bring 3+ arms together.
2. Clusters: junction nodes closer than their roads' half widths + 8 m are one junction (a dual carriageway crossing
   is four OSM nodes; a roundabout entry two).
3. For each cluster, the pieces of road between its nodes are the core; every road leaving it is an arm. An arm is
   trimmed where its cross-section is clear of every other arm's strip widened by the corner radius r.
4. The surface is the union of the core and the trimmed arms, "closed" with radius r (grown by r, shrunk by r): that
   fills each inside corner with an arc of radius r — a kerb line — and leaves the outside corners and the straight
   cuts at the arm ends as they were.
5. Heights at its outline come from the solved road heights nearby; the roads are cut back by the trimmed lengths.
"""
import math
from collections import defaultdict

import numpy as np

# classes whose meeting makes a junction (motorway..living, link); driveways, tracks and rails do not
JUNCTION_CLASSES = {0, 1, 2, 3, 4, 5, 6, 7, 10}
MAX_EXTENT = 110.0          # a cluster wider than this is split back into single nodes


def corner_radius(hw):
    return float(np.clip(2.5 + 0.6 * hw, 3.0, 8.0))


def build_junctions(xy, off, node, nnodes, attrs, y, lift, log):
    """Returns (cuts, junctions).

    cuts: {way: [(s0, s1), ...]} distance intervals along each way to leave out (inside a junction).
    junctions: list of dicts {x, n (north), y: arrays of the outline (closed, counter-clockwise), holes: [...],
    tris: flat triangle indices over outline + holes, elevated: bool}.
    """
    import shapely
    from scipy.spatial import cKDTree
    import mapbox_earcut as earcut

    npts = len(xy)
    way = np.repeat(np.arange(len(attrs)), np.diff(off))
    cls = attrs['cls'][way]
    hw_pt = attrs['width_dm'][way].astype(np.float64) * 0.05
    counted = np.isin(cls, list(JUNCTION_CLASSES))
    first = np.zeros(npts, dtype=bool); first[off[:-1]] = True
    last = np.zeros(npts, dtype=bool); last[off[1:] - 1] = True
    seg = np.zeros(npts)
    seg[1:] = np.hypot(np.diff(xy[:, 0]), np.diff(xy[:, 1]))
    seg[first] = 0
    along = np.cumsum(seg)
    along -= np.repeat(along[off[:-1]], np.diff(off))       # distance from each way's start

    arms = np.zeros(nnodes, dtype=np.int32)
    np.add.at(arms, node[counted], np.where(first | last, 1, 2)[counted])
    jn = np.nonzero(arms >= 3)[0]
    is_j = np.zeros(nnodes, dtype=bool); is_j[jn] = True
    pos = np.zeros((nnodes, 2)); pos[node] = xy
    hgt = np.zeros(nnodes); hgt[node] = y          # road height at each node (grade separation check)
    hwn = np.zeros(nnodes); np.maximum.at(hwn, node[counted], hw_pt[counted])
    log(f'junctions: {len(jn)} junction nodes')

    # occurrences of each junction node on counted ways
    occ_pts = np.nonzero(counted & is_j[node])[0]
    occ = defaultdict(list)
    for p in occ_pts:
        occ[int(node[p])].append(int(p))

    # clusters (union-find over close junction nodes)
    parent = {int(n): int(n) for n in jn}

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    tree = cKDTree(pos[jn])
    rmax = 2 * hwn[jn].max() + 8 if len(jn) else 0
    for i, j in tree.query_pairs(rmax):
        a, b = int(jn[i]), int(jn[j])
        # close on the map AND at the same level: a junction on a viaduct never merges with the street below it
        if np.hypot(*(pos[a] - pos[b])) < hwn[a] + hwn[b] + 8 and abs(hgt[a] - hgt[b]) < 2.5:
            parent[find(a)] = find(b)
    groups = defaultdict(list)
    for n in jn:
        groups[find(int(n))].append(int(n))
    clusters = []
    for g in groups.values():
        p = pos[g]
        if len(g) > 1 and max(np.ptp(p[:, 0]), np.ptp(p[:, 1])) > MAX_EXTENT:
            clusters.extend([[n] for n in g])
        else:
            clusters.append(g)
    log(f'junctions: {len(clusters)} junctions after grouping close nodes')

    cuts = defaultdict(list)
    junctions = []
    failed = 0
    for ci, members in enumerate(clusters):
        try:
            res = _one(members, occ, xy, off, way, node, along, hw_pt, is_j, y, lift, shapely, earcut)
        except Exception:  # odd geometry: leave this junction as overlapping roads
            failed += 1
            continue
        if res is None:
            continue
        jcuts, jsurf = res
        for w, s0, s1 in jcuts:
            cuts[w].append((s0, s1))
        junctions.extend(jsurf)
        if ci % 20000 == 0:
            log(f'junctions {ci}/{len(clusters)}')
    log(f'junctions: {len(junctions)} surfaces ({failed} left as plain overlaps)')
    return cuts, junctions


def _walk(p, step, xy, off_lo, off_hi, limit, node, is_j, members):
    """Points from p along its way in direction step (+1/-1) up to `limit` metres or a junction node.
    Returns (indices, reached) where reached = 'member' | 'other' | None."""
    idx = [p]
    dist = 0.0
    q = p
    while True:
        r = q + step
        if r < off_lo or r >= off_hi:
            return idx, None
        dist += math.hypot(xy[r, 0] - xy[q, 0], xy[r, 1] - xy[q, 1])
        idx.append(r)
        nr = int(node[r])
        if nr in members:
            return idx, 'member'
        if is_j[nr]:
            return idx, 'other'
        if dist >= limit:
            return idx, None
        q = r


def _one(members, occ, xy, off, way, node, along, hw_pt, is_j, y, lift, shapely, earcut):
    mset = set(members)
    hw_all = [hw_pt[p] for n in members for p in occ[n]]
    if not hw_all:
        return None
    hw_med = float(np.median(hw_all))
    r = corner_radius(hw_med)
    core_lines, core_hw, core_keys = [], [], set()
    arms = []          # (way, p, step, indices, hw)
    for n in members:
        for p in occ[n]:
            w = int(way[p])
            lo, hi = int(off[w]), int(off[w + 1])
            for step in (-1, 1):
                if not (lo <= p + step < hi):
                    continue
                limit = max(40.0, 4 * hw_pt[p] + 25)
                idx, reached = _walk(p, step, xy, lo, hi, limit, node, is_j, mset)
                if len(idx) < 2:
                    continue
                if reached == 'member':
                    key = (w, min(idx[0], idx[-1]), max(idx[0], idx[-1]))
                    if key in core_keys:
                        continue
                    core_keys.add(key)
                    core_lines.append(idx)
                    core_hw.append(hw_pt[p])
                else:
                    if reached == 'other':          # stop halfway to the next junction
                        idx = idx[:max(2, len(idx) // 2 + 1)]
                    arms.append((w, p, step, idx, hw_pt[p]))
    if len(arms) + len(core_lines) < 3 and not core_lines:
        return None

    def strip(idx, hw, extra=0.0):
        return shapely.buffer(shapely.LineString(xy[idx]), hw + extra, cap_style='flat', join_style='round')

    core_parts = [strip(i, h) for i, h in zip(core_lines, core_hw)]
    core_parts += [shapely.Point(xy[occ[n][0]]).buffer(max(hw_pt[p] for p in occ[n]), 16) for n in members]
    core = shapely.union_all(core_parts)
    arm_strips = [strip(a[3], a[4], r) for a in arms]
    core_wide = core.buffer(r)

    cut_list, pieces, involved = [], [], []
    for i, (w, p, step, idx, hw) in enumerate(arms):
        others = shapely.union_all([s for j, s in enumerate(arm_strips) if j != i] + [core_wide])
        line = xy[idx]
        cum = np.concatenate([[0.0], np.cumsum(np.hypot(np.diff(line[:, 0]), np.diff(line[:, 1])))])
        total = cum[-1]
        t = None
        s = 0.5
        while s <= total:
            k = min(np.searchsorted(cum, s), len(cum) - 1)
            k0 = max(k - 1, 0)
            f = 0.0 if cum[k] == cum[k0] else (s - cum[k0]) / (cum[k] - cum[k0])
            c = line[k0] + (line[k] - line[k0]) * f
            d = line[k] - line[k0]
            ln = math.hypot(d[0], d[1])
            if ln < 1e-6:
                s += 1.0
                continue
            nx, ny = -d[1] / ln * hw, d[0] / ln * hw
            cross = shapely.LineString([(c[0] - nx, c[1] - ny), (c[0] + nx, c[1] + ny)])
            if not shapely.intersects(cross, others):
                t = s
                break
            s += 1.0
        if t is None:
            t = total
        t = float(min(t + 0.3, total))
        k = min(np.searchsorted(cum, t), len(cum) - 1)
        piece_idx = idx[:k + 1]
        cutpt = line[max(k - 1, 0)] + (line[k] - line[max(k - 1, 0)]) * (
            0.0 if cum[k] == cum[max(k - 1, 0)] else (t - cum[max(k - 1, 0)]) / (cum[k] - cum[max(k - 1, 0)]))
        coords = np.vstack([xy[piece_idx[:-1]], cutpt]) if len(piece_idx) > 1 else np.vstack([xy[piece_idx], cutpt])
        if len(coords) >= 2:
            pieces.append(shapely.buffer(shapely.LineString(coords), hw, cap_style='flat', join_style='round'))
        s_p = along[p]
        cut_list.append((w, s_p, s_p + t) if step > 0 else (w, s_p - t, s_p))
        involved.extend(piece_idx)
    for idx in core_lines:
        w = int(way[idx[0]])
        a0, a1 = along[idx[0]], along[idx[-1]]
        cut_list.append((w, min(a0, a1), max(a0, a1)))
        involved.extend(idx)

    surf = shapely.union_all([core] + pieces).buffer(r, 12).buffer(-r, 12).simplify(0.15)
    polys = list(surf.geoms) if surf.geom_type == 'MultiPolygon' else [surf] if surf.geom_type == 'Polygon' else []
    inv = np.unique(np.array(involved))
    from scipy.spatial import cKDTree
    kd = cKDTree(xy[inv])
    out = []
    for poly in polys:
        if poly.is_empty or poly.area < 4:
            continue
        poly = shapely.geometry.polygon.orient(poly, 1.0)
        rings = [np.asarray(poly.exterior.coords)[:-1]] + [np.asarray(h.coords)[:-1] for h in poly.interiors]
        verts = np.vstack(rings)
        ends = np.cumsum([len(rr) for rr in rings]).astype(np.uint32)
        tris = earcut.triangulate_float64(verts, ends)
        if len(tris) == 0 or len(verts) > 4000:
            continue
        dist, nn = kd.query(verts, k=min(3, len(inv)))
        dist = np.atleast_2d(dist); nn = np.atleast_2d(nn)
        wgt = 1.0 / np.maximum(dist, 0.5) ** 2
        hv = (y[inv][nn] * wgt).sum(1) / wgt.sum(1)
        out.append({'xy': verts, 'ends': ends, 'y': hv, 'tris': tris,
                    'elevated': bool(lift[inv].mean() > 1.0)})
    return cut_list, out
