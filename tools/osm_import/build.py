#!/usr/bin/env python3
"""Stage 2 of the Baltic Drive map importer: stage-1 arrays + elevation -> streamable tile packs (format 3).

    python build.py <workdir> <outdir> --dem <dem folder> [--name mazowsze] [--workers 6]

Writes <outdir>/<name>.idx (header + tile index), <name>.NN.pak (zlib tile blobs, under 48 MiB each) and
<name>.places.json. The binary layout is read by Game/Map/MapData.cs; keep the two in step (docs/TILE_FORMAT.md).

Game space: X = east, Z = south (north is -Z), metres in the world projection (common.WORLD_PROJ). Tiles are
2048 m squares aligned to the world origin, so they line up with the streamer's LOD quadtree (64 m * 2^5).

What changed from Dead Miles' format 2: real ground heights (an 8 m grid per tile from the DEM), smoothed absolute
road heights per road point (bridges interpolated between their ends and raised over what they cross), water levels
per grid point (lakes and rivers sit in carved channels), the real lane count, and no invented-relief water distance.
"""
import argparse
import heapq
import json
import math
import os
import struct
import sys
import time
import zlib
from collections import defaultdict
from multiprocessing import Pool

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

FORMAT = 7
TILE = 2048.0
LAND_RES = 512           # land-cover cells per tile side (4 m)
LAND_CELL = TILE / LAND_RES
H_RES = 256              # height cells per tile side (8 m); stored as (H_RES + 1)^2 corner values
H_CELL = TILE / H_RES
H_UNIT = 0.04            # metres per stored height step
H_BASE = -100.0          # height of stored value 0 ... 65535 -> 2521 m; 0 also means "dry" in the water grid
PAK_LIMIT = 48 * 1024 * 1024

F_BRIDGE, F_TUNNEL, F_ONEWAY, F_UNPAVED, F_LIT, F_RAIL, F_STARTCAP, F_ENDCAP = 1, 2, 4, 8, 16, 32, 64, 128
BF_GHOST, BF_TRIS, BF_PART = 1, 2, 4
F2_JSTART, F2_JEND, F2_ONPAVED, F2_TAPER = 1, 2, 64, 128   # second road flag byte: the piece starts / ends at a junction surface

ROAD_STEP = 10.0         # road points are at most this far apart (heights follow the ground between them)
DECK_CLEAR = 6.0         # a bridge's deck above each road, railway or lower bridge it passes over
DECK_MARGIN = 8.0        # the deck keeps its clearance this far beyond the edges of what it passes over (D15)
TUNNEL_DEPTH = 6.5       # an underpass road runs this far below the road or railway it passes under (D16)
GAME_SHOULDER = 9.0      # MapContext.Shoulder in the game: how far beyond a road's edge the ground blends back
# How far (m) a road's height profile averages the ground, by class: long and smooth for motorways, close to the
# ground for tracks. See D4.
SMOOTH = {0: 150.0, 1: 150.0, 2: 90.0, 3: 90.0, 4: 60.0, 5: 60.0, 6: 40.0, 7: 30.0, 8: 35.0, 9: 25.0, 10: 80.0,
          11: 200.0, 12: 40.0}

t0 = time.time()
KEEP = None             # (tx0, tz0, tx1, tz1) with --tiles: build only these tiles (quick test maps)


def in_scope(t):
    return KEEP is None or (KEEP[0] <= t[0] <= KEEP[2] and KEEP[1] <= t[1] <= KEEP[3])


def log(msg):
    print(f'[{time.time() - t0:6.0f}s] {msg}', flush=True)


def tile_of(v):
    return int(math.floor(v / TILE))


def encode_h(h):
    return np.clip(np.round((np.asarray(h, dtype=np.float64) - H_BASE) / H_UNIT), 1, 65535).astype('<u2')


# =================================================================================================
# roads: densify, heights, bridges
# =================================================================================================

def densify(xy, off, nodes):
    """Insert points so no segment is longer than ROAD_STEP. Returns xy, off, node index per point, node count."""
    n = len(xy)
    uniq, inv = np.unique(nodes, return_inverse=True)
    inv = inv.astype(np.int64)
    last = np.zeros(n, dtype=bool)
    last[off[1:] - 1] = True
    seg = np.zeros(n)
    seg[:-1] = np.hypot(xy[1:, 0] - xy[:-1, 0], xy[1:, 1] - xy[:-1, 1])
    seg[last] = 0
    k = np.maximum(0, np.ceil(seg / ROAD_STEP).astype(np.int64) - 1)
    k[last] = 0
    count = 1 + k
    start = np.concatenate([[0], np.cumsum(count)])
    total = int(start[-1])
    src = np.repeat(np.arange(n), count)                   # original point each new point follows
    j = np.arange(total) - start[src]                       # 0 = the original point, 1..k inserted after it
    t = j / (k[src] + 1.0)
    nxt = np.minimum(src + 1, n - 1)
    out = xy[src] + (xy[nxt] - xy[src]) * t[:, None]
    node = np.where(j == 0, inv[src], 0)
    inserted = j > 0
    node[inserted] = len(uniq) + np.arange(int(inserted.sum()))
    new_off = start[off]
    return out, new_off, node, len(uniq) + int(inserted.sum())


def find_crossings(xy, off, attrs, nodes):
    """For each bridge way, the other road/rail ways it passes over (not joined to it at a node)."""
    import shapely
    flags, layer = attrs['flags'], attrs['layer']
    is_bridge = ((flags & F_BRIDGE) != 0) & ((flags & F_TUNNEL) == 0)
    bridge_idx = np.nonzero(is_bridge)[0]
    counts = np.diff(off)
    lines = shapely.linestrings(xy, indices=np.repeat(np.arange(len(attrs)), counts))
    tree = shapely.STRtree(lines)
    pairs = tree.query(lines[bridge_idx], predicate='intersects')
    out = defaultdict(list)
    for bi, other in zip(pairs[0], pairs[1]):
        b = int(bridge_idx[bi])
        other = int(other)
        if other == b or (flags[other] & F_TUNNEL):
            continue
        if is_bridge[other] and layer[other] >= layer[b]:
            continue
        if np.intersect1d(nodes[off[b]:off[b + 1]], nodes[off[other]:off[other + 1]]).size:
            continue  # joined at a junction, not passing over
        p = shapely.intersection(lines[b], lines[other])
        for c in shapely.get_coordinates(p):
            out[b].append((other, c[0], c[1], max(1, int(layer[b]) - min(0, int(layer[other])))))
    log(f'{len(bridge_idx)} bridge ways, {len(out)} pass over a road or railway')
    return out, is_bridge


def find_underpasses(xy, off, attrs, nodes):
    """Road tunnels and what they pass under: {tunnel way: [(other way, x, y), ...]} for every road or railway (not a
    bridge, not a tunnel, not joined to it) crossing it. Railway tunnels are left alone: the game does not draw them."""
    import shapely
    flags = attrs['flags']
    is_tunnel = ((flags & F_TUNNEL) != 0) & ((flags & F_RAIL) == 0) & (attrs['cls'] < 8)   # (service-road tunnels: left out, D26)
    tun_idx = np.nonzero(is_tunnel)[0]
    out = defaultdict(list)
    if len(tun_idx) == 0:
        return out
    counts = np.diff(off)
    lines = shapely.linestrings(xy, indices=np.repeat(np.arange(len(attrs)), counts))
    tree = shapely.STRtree(lines)
    pairs = tree.query(lines[tun_idx], predicate='intersects')
    for ti, other in zip(pairs[0], pairs[1]):
        t = int(tun_idx[ti])
        other = int(other)
        if other == t or flags[other] & (F_TUNNEL | F_BRIDGE):
            continue
        if np.intersect1d(nodes[off[t]:off[t + 1]], nodes[off[other]:off[other + 1]]).size:
            continue
        for c in shapely.get_coordinates(shapely.intersection(lines[t], lines[other])):
            out[t].append((other, c[0], c[1]))
    log(f'{len(tun_idx)} road tunnel ways, {len(out)} pass under a road or railway')
    return out


def solve_heights(node, nnodes, xy, off, attrs, ground, anchor_extra=None, lscale=None):
    """Road heights on the node graph: as close to the ground as the class's smoothing length allows.

    Minimises sum(a_i (h_i - g_i)^2) + sum((h_i - h_j)^2 / d_ij), a_i = (node's length share) / L^2, so a profile
    averages the ground over about L metres. Bridge points have no ground term (they are interpolated between their
    ends). `anchor_extra` = (node indices, targets, weight) pins chosen nodes (bridge decks over roads).
    """
    import scipy.sparse as sp
    import scipy.sparse.linalg as spla
    n = len(xy)
    last = np.zeros(n, dtype=bool)
    last[off[1:] - 1] = True
    a = np.nonzero(~last)[0]
    a = a[a < n - 1]
    b = a + 1
    d = np.maximum(0.5, np.hypot(xy[b, 0] - xy[a, 0], xy[b, 1] - xy[a, 1]))
    way = np.repeat(np.arange(len(attrs)), np.diff(off))
    cls = attrs['cls'][way]
    flags = attrs['flags'][way]
    L = np.vectorize(SMOOTH.get)(cls).astype(np.float64)
    if lscale is not None:
        L = L * lscale
    # bridge decks and road tunnels have no ground term: they run straight between their ends and pins
    free = (((flags & F_BRIDGE) != 0) & ((flags & F_TUNNEL) == 0)) | (((flags & F_TUNNEL) != 0) & ((flags & F_RAIL) == 0) & (cls < 8))
    # each point owns half of each segment next to it; bridge interiors are free (no ground term)
    share = np.zeros(n)
    np.add.at(share, a, d * 0.5)
    np.add.at(share, b, d * 0.5)
    alpha_pt = np.where(free, 0.0, share / (L * L))
    # a bridge's end points belong to its ramps too: keep the ground term where a non-bridge way meets them
    u, v = node[a], node[b]
    w = 1.0 / d
    alpha = np.zeros(nnodes)
    np.add.at(alpha, node, alpha_pt)
    rhs = np.zeros(nnodes)
    np.add.at(rhs, node, alpha_pt * ground)
    if anchor_extra is not None:
        idx, target, weight = anchor_extra
        np.add.at(alpha, idx, weight)
        np.add.at(rhs, idx, weight * target)
    # isolated free nodes (a bridge way on its own) get a weak ground term so the system stays solvable
    deg = np.zeros(nnodes)
    np.add.at(deg, u, w)
    np.add.at(deg, v, w)
    gnode = np.zeros(nnodes)
    gcount = np.zeros(nnodes)
    np.add.at(gnode, node, ground)
    np.add.at(gcount, node, 1)
    gnode /= np.maximum(1, gcount)
    weak = 1e-4
    alpha += weak
    rhs += weak * gnode
    W = sp.coo_matrix((np.concatenate([w, w]), (np.concatenate([u, v]), np.concatenate([v, u]))),
                      shape=(nnodes, nnodes)).tocsr()
    A = sp.diags(alpha + deg) - W
    M = sp.diags(1.0 / (alpha + deg))
    h, info = spla.cg(A, rhs, x0=gnode, rtol=1e-7, maxiter=3000, M=M)
    if info != 0:
        log(f'warning: road height solver stopped early ({info})')
    return h


# Steepest a road may climb up to a bridge, by class (real roads: motorways ~4 %, main roads ~5 %).
RAMP_GRADE = {0: 0.04, 1: 0.04, 2: 0.05, 3: 0.05, 10: 0.05, 4: 0.06, 11: 0.025, 12: 0.06}


def urban_scale(xy):
    """Per road point: how much of its class's smoothing length to use. In towns (many roads close together) the
    elevation model is accurate to the street and roads follow it closely; out in the country they average it."""
    cell = 50.0
    gx = np.floor(xy[:, 0] / cell).astype(np.int64)
    gy = np.floor(xy[:, 1] / cell).astype(np.int64)
    x0, y0 = gx.min(), gy.min()
    W, H = int(gx.max() - x0 + 3), int(gy.max() - y0 + 3)
    grid = np.zeros((H, W), dtype=np.float32)
    np.add.at(grid, (gy - y0 + 1, gx - x0 + 1), 1)
    from scipy import ndimage
    dens = ndimage.uniform_filter(grid, size=3, mode='constant') * 9      # road points in the 150 m square around
    d = dens[gy - y0 + 1, gx - x0 + 1]
    scale = np.clip(1.25 - d / 160.0, 0.3, 1.0)
    log(f'urban smoothing: {100 * (scale < 0.6).mean():.0f} % of road points use under 60 % of the class length')
    return scale


def limit_ramps(h, node, nnodes, xy, off, attrs, ground):
    """Raised roads (bridge decks and what leads up to them) come down no steeper than RAMP_GRADE: from every node
    that rides above the ground, neighbours are lifted to at least h - grade * distance (an embankment)."""
    import heapq
    n = len(xy)
    last = np.zeros(n, dtype=bool)
    last[off[1:] - 1] = True
    a = np.nonzero(~last)[0]
    a = a[a < n - 1]
    b = a + 1
    d = np.hypot(xy[b, 0] - xy[a, 0], xy[b, 1] - xy[a, 1])
    way = np.repeat(np.arange(len(attrs)), np.diff(off))
    g = np.vectorize(lambda c: RAMP_GRADE.get(int(c), 0.08))(attrs['cls'][way[a]])
    u, v = node[a], node[b]
    uu = np.concatenate([u, v]); vv = np.concatenate([v, u])
    dd = np.concatenate([d * g, d * g])                       # height allowed to drop along each edge
    order = np.argsort(uu, kind='stable')
    uu, vv, dd = uu[order], vv[order], dd[order]
    start = np.searchsorted(uu, np.arange(nnodes + 1))
    gnode = np.full(nnodes, np.inf)
    np.minimum.at(gnode, node, ground)
    h = h.copy()
    # sources: bridge decks only (an ordinary road in a dip may sit above the ground and must not spread)
    fl = attrs['flags'][way]
    on_bridge = np.zeros(nnodes, dtype=bool)
    on_bridge[node[((fl & F_BRIDGE) != 0) & ((fl & F_TUNNEL) == 0)]] = True
    lifted = np.nonzero(on_bridge & (h > gnode + 1.0))[0]
    heap = [(-float(h[i]), int(i)) for i in lifted]
    heapq.heapify(heap)
    raised = 0
    while heap:
        neg, i = heapq.heappop(heap)
        cur = -neg
        if cur < h[i] - 1e-6:
            continue
        for k in range(start[i], start[i + 1]):
            j = int(vv[k])
            cand = cur - float(dd[k])
            if cand > h[j] + 0.01:
                h[j] = cand
                raised += 1
                heapq.heappush(heap, (-cand, j))
    log(f'ramps: {raised} road points raised to keep climbs to bridges gentle')
    return h


def limit_dips(h, node, nnodes, xy, off, attrs, ground):
    """The mirror of limit_ramps for underpasses (D16): from every road-tunnel node sunk below the ground, neighbours
    are lowered to at most h + grade * distance, so the road dives into its cutting no steeper than RAMP_GRADE."""
    import heapq
    n = len(xy)
    last = np.zeros(n, dtype=bool)
    last[off[1:] - 1] = True
    a = np.nonzero(~last)[0]
    a = a[a < n - 1]
    b = a + 1
    d = np.hypot(xy[b, 0] - xy[a, 0], xy[b, 1] - xy[a, 1])
    way = np.repeat(np.arange(len(attrs)), np.diff(off))
    g = np.vectorize(lambda c: RAMP_GRADE.get(int(c), 0.08))(attrs['cls'][way[a]])
    u, v = node[a], node[b]
    uu = np.concatenate([u, v]); vv = np.concatenate([v, u])
    dd = np.concatenate([d * g, d * g])
    order = np.argsort(uu, kind='stable')
    uu, vv, dd = uu[order], vv[order], dd[order]
    start = np.searchsorted(uu, np.arange(nnodes + 1))
    gnode = np.full(nnodes, -np.inf)
    np.maximum.at(gnode, node, ground)
    h = h.copy()
    fl = attrs['flags'][way]
    in_tunnel = np.zeros(nnodes, dtype=bool)
    in_tunnel[node[((fl & F_TUNNEL) != 0) & ((fl & F_RAIL) == 0) & (attrs['cls'][way] < 8)]] = True
    sunk = np.nonzero(in_tunnel & (h < gnode - 1.0))[0]
    heap = [(float(h[i]), int(i)) for i in sunk]
    heapq.heapify(heap)
    lowered = 0
    while heap:
        cur, i = heapq.heappop(heap)
        if cur > h[i] + 1e-6:
            continue
        for k in range(start[i], start[i + 1]):
            j = int(vv[k])
            cand = cur + float(dd[k])
            if cand < h[j] - 0.01:
                h[j] = cand
                lowered += 1
                heapq.heappush(heap, (cand, j))
    log(f'underpasses: {len(sunk)} tunnel points sunk, {lowered} road points lowered into their cuttings')
    return h


def build_roads(work, dem):
    roads = np.load(os.path.join(work, 'roads.npz'))
    xy0 = roads['xy'].astype(np.float64) / 100.0     # x east, y north
    off0, attrs, nodes0 = roads['off'], roads['attrs'], roads['nodes']
    if KEEP is not None:
        # only ways near the kept tiles (with 3 km of margin so road heights near the edge are smoothed the same)
        m = 3000.0
        x0, x1 = KEEP[0] * TILE - m, (KEEP[2] + 1) * TILE + m
        n0, n1 = -((KEEP[3] + 1) * TILE) - m, -(KEEP[1] * TILE) + m      # world north = -z
        way = np.repeat(np.arange(len(attrs)), np.diff(off0))
        inside = (xy0[:, 0] >= x0) & (xy0[:, 0] <= x1) & (xy0[:, 1] >= n0) & (xy0[:, 1] <= n1)
        sel = np.zeros(len(attrs), dtype=bool)
        sel[way[inside]] = True
        pts = sel[way]
        counts = np.diff(off0)[sel]
        xy0, nodes0, attrs = xy0[pts], nodes0[pts], attrs[sel]
        off0 = np.concatenate([[0], np.cumsum(counts)])
    crossings, _ = find_crossings(xy0, off0, attrs, nodes0)
    underpasses = find_underpasses(xy0, off0, attrs, nodes0)
    xy, off, node, nnodes = densify(xy0, off0, nodes0)
    log(f'{len(attrs)} road/rail ways, {len(xy0)} -> {len(xy)} points after densifying to {ROAD_STEP:.0f} m')
    ground = np.empty(len(xy), dtype=np.float32)
    step = 2_000_000
    for i in range(0, len(xy), step):
        ground[i:i + step] = dem.sample_world(xy[i:i + step, 0], xy[i:i + step, 1])
    bad = ~np.isfinite(ground)
    if bad.any():
        # outside the DEM: the height of the nearest road point that has one
        from scipy.spatial import cKDTree
        log(f'{int(bad.sum())} road points without elevation (outside the DEM): nearest measured point')
        if bad.all():
            ground[:] = 100.0
        else:
            _, idx = cKDTree(xy[~bad]).query(xy[bad])
            ground[bad] = ground[~bad][idx]
    ground = ground.astype(np.float64)
    lscale = urban_scale(xy)
    h = solve_heights(node, nnodes, xy, off, attrs, ground, lscale=lscale)
    log('road heights solved (pass 1)')

    # Bridges that pass over a road or railway keep a deck DECK_CLEAR above it, but only over it (plus the crossed
    # road's half width and DECK_MARGIN): the rest of the deck is free to slope down to its ends, so the ramps
    # beyond a long bridge are not raised the full clearance (D15). Repeated so stacked levels add up (a level-2
    # bridge clears the level-1 bridge under it once that one has been raised).
    for p in range(2 if crossings or underpasses else 0):
        pin_idx, pin_t = [], []
        # underpasses (D16): the tunnel runs TUNNEL_DEPTH under each road it passes below, over that road's width
        for tw, lst in underpasses.items():
            pts_t = slice(off[tw], off[tw + 1])
            tx_, ty_ = xy[pts_t, 0], xy[pts_t, 1]
            need = np.full(len(tx_), 1e9)
            for other, cx, cy in lst:
                pts = slice(off[other], off[other + 1])
                k = np.argmin(np.hypot(xy[pts, 0] - cx, xy[pts, 1] - cy))
                reach = attrs['width_dm'][other] / 20.0 + 4.0
                dist = np.hypot(tx_ - cx, ty_ - cy)
                near = dist <= reach
                if not near.any():
                    near[np.argmin(dist)] = True
                need[near] = np.minimum(need[near], h[node[off[other] + k]] - TUNNEL_DEPTH)
            for i in np.nonzero(need < 1e8)[0]:
                nd = node[off[tw] + i]
                pin_idx.append(nd)
                pin_t.append(min(need[i], h[nd]))
        for bw, lst in crossings.items():
            pts_b = slice(off[bw], off[bw + 1])
            bx, by = xy[pts_b, 0], xy[pts_b, 1]
            need = np.full(len(bx), -1e9)
            for other, cx, cy, _lay in lst:
                pts = slice(off[other], off[other + 1])
                k = np.argmin(np.hypot(xy[pts, 0] - cx, xy[pts, 1] - cy))
                reach = attrs['width_dm'][other] / 20.0 + DECK_MARGIN
                near = np.hypot(bx - cx, by - cy) <= reach
                if not near.any():
                    near[np.argmin(np.hypot(bx - cx, by - cy))] = True
                need[near] = np.maximum(need[near], h[node[off[other] + k]] + DECK_CLEAR)
            for i in np.nonzero(need > -1e8)[0]:
                nd = node[off[bw] + i]
                pin_idx.append(nd)
                pin_t.append(max(need[i], h[nd]))
        h = solve_heights(node, nnodes, xy, off, attrs, ground,
                          (np.array(pin_idx, dtype=np.int64), np.array(pin_t), 5.0), lscale=lscale)
        log(f'road heights solved (deck pass {p + 1}, {len(pin_idx)} deck points pinned)')
    h = limit_ramps(h, node, nnodes, xy, off, attrs, ground)
    if underpasses:
        h = limit_dips(h, node, nnodes, xy, off, attrs, ground)
    y = h[node]
    lift = np.clip(y - ground, 0, 25.5)

    from junctions import build_junctions
    cuts, junctions = build_junctions(xy, off, node, nnodes, attrs, y, lift, log)
    decks = underpass_decks(underpasses, xy, off, attrs, y, junctions)
    paved = build_paved(work, dem)
    on_paved = roads_on_paved(paved, xy, off, attrs)
    junctions = junctions + paved
    jtiles = encode_junctions(junctions)

    tapers = find_tapers(xy, off, node, nnodes, attrs)
    walks = estimate_pavements(work, xy, off, attrs)

    gx, gz = xy[:, 0], -xy[:, 1]
    per_tile = defaultdict(list)
    nways = len(attrs)
    for w in range(nways):
        a, b = off[w], off[w + 1]
        px, pz, py, pl = gx[a:b], gz[a:b], y[a:b], lift[a:b]
        cls, flags, width_dm, layer, lanes = (int(attrs[w][k]) for k in ('cls', 'flags', 'width_dm', 'layer', 'lanes'))
        fwd = int(attrs[w]['fwd']) if 'fwd' in attrs.dtype.names else 0
        tp = tapers.get(w)
        walk = int(walks[w])
        way_len = float(np.hypot(np.diff(px), np.diff(pz)).sum())
        seg = np.hypot(np.diff(px), np.diff(pz))
        al = np.concatenate([[0.0], np.cumsum(seg)])
        parts = cut_way(px, pz, py, pl, al, cuts.get(w))
        if w in decks:
            parts = split_decks(parts, decks[w])
        for sx, sz, sy, sl, s_start, jstart, jend, orig_start, orig_end, *deck in parts:
            keep = np.ones(len(sx), dtype=bool)
            keep[1:] = (np.abs(np.diff(sx)) + np.abs(np.diff(sz))) > 0.02
            if keep.sum() < 2:
                continue
            sx, sz, sy, sl = sx[keep], sz[keep], sy[keep], sl[keep]
            xs, zs, ys, ls, pieces = split_way(sx, sz, sy, sl)
            cum = s_start + np.concatenate([[0.0], np.cumsum(np.hypot(np.diff(xs), np.diff(zs)))])
            for (tx, tz), i0, i1 in pieces:
                if not in_scope((tx, tz)):
                    continue
                f, f2 = flags | (F_BRIDGE if deck and deck[0] else 0), (min(fwd, 15) << 2) | (F2_ONPAVED if w in on_paved else 0)
                if i0 == 0:
                    if orig_start:
                        f |= F_STARTCAP
                    if jstart:
                        f2 |= F2_JSTART
                if i1 == len(xs) - 1:
                    if orig_end:
                        f |= F_ENDCAP
                    if jend:
                        f2 |= F2_JEND
                n = i1 - i0 + 1
                wpt = uopt = None
                if tp:
                    wpt, uopt = taper_profile(tp, cum[i0:i1 + 1], way_len, width_dm / 10.0)
                    if np.abs(wpt - width_dm / 10.0).max() < 0.05:
                        wpt = None
                if wpt is not None:
                    f2 |= F2_TAPER
                rec = bytearray(struct.pack('<BBBBbBBfH', cls, f, f2, width_dm, layer, lanes, walk, float(cum[i0] % 4096.0), n))
                fields = [('x', '<i2'), ('z', '<i2'), ('y', '<u2'), ('l', 'u1')]
                if wpt is not None:
                    fields += [('w', 'u1'), ('uo', 'i1')]
                q = np.empty(n, dtype=fields)
                q['x'] = np.clip(np.round((xs[i0:i1 + 1] - tx * TILE) * 10), -32768, 32767)
                q['z'] = np.clip(np.round((zs[i0:i1 + 1] - tz * TILE) * 10), -32768, 32767)
                q['y'] = encode_h(ys[i0:i1 + 1])
                q['l'] = np.clip(np.round(ls[i0:i1 + 1] * 10), 0, 255)
                if wpt is not None:
                    q['w'] = np.clip(np.round(wpt * 10), 10, 255)
                    q['uo'] = np.clip(np.round(uopt * 10), -127, 127)
                per_tile[(tx, tz)].append(bytes(rec) + q.tobytes())
        if w % 300000 == 0:
            log(f'roads {w}/{nways}')
    log(f'roads cut into {sum(len(v) for v in per_tile.values())} pieces over {len(per_tile)} tiles')
    return per_tile, jtiles



def estimate_pavements(work, xy, off, attrs):
    """Pavements per road side (D27): OSM's sidewalk tags where given (extract.py 'walk', bits 0/1 = left/right has one,
    bits 2/3 = tagged), otherwise town streets get them on both sides: primary to residential (not motorways, trunks,
    links, living streets, service roads or tracks, not bridges' and tunnels' own pavements), not unpaved, with at least
    4 buildings within 50 m of the way's middle. Returns bits 0/1 per way."""
    n = len(attrs)
    walk = attrs['walk'].astype(np.int64) if 'walk' in attrs.dtype.names else np.zeros(n, dtype=np.int64)
    out = walk & 3
    cls, flags = attrs['cls'], attrs['flags']
    cand = (cls >= 2) & (cls <= 6) & ((flags & (F_RAIL | F_UNPAVED | F_TUNNEL)) == 0)
    need = cand & ((walk & 12) != 12)
    bpath = os.path.join(work, 'buildings.npz')
    if need.any() and os.path.exists(bpath):
        from scipy.spatial import cKDTree
        b = np.load(bpath)
        bxy = b['xy'][b['ring_off'][b['feat_ring'][:-1]]].astype(np.float64) / 100.0
        bxy[:, 1] = -bxy[:, 1]
        kd = cKDTree(bxy)
        idx = np.nonzero(need)[0]
        mid = (off[idx] + off[idx + 1] - 1) // 2
        pts = np.stack([xy[mid, 0], -xy[mid, 1]], axis=1)
        counts = kd.query_ball_point(pts, 50.0, return_length=True)
        town = counts >= 4
        for k, w in enumerate(idx):
            if not town[k]:
                continue
            for bit in (0, 1):
                if not walk[w] & (4 << bit):
                    out[w] |= 1 << bit
    tagged = int(((walk & 12) != 0).sum())
    log(f'pavements: {int((out & 1).sum() + (out >> 1 & 1).sum())} road sides ({tagged} ways tagged in OSM)')
    return out


def find_tapers(xy, off, node, nnodes, attrs):
    """Where two pieces of one road meet end to end (no junction) with different widths, the wider piece tapers from
    the narrower width over a few tens of metres (D21). {way: [(at_start, length, narrow width m, side)]}; side +1 =
    the lane opens on the right (left edge fixed), -1 = on the left (right edge fixed), 0 = both edges (two-way)."""
    n = len(xy)
    first, last = off[:-1], off[1:] - 1
    inc = np.zeros(nnodes, dtype=np.int64)
    np.add.at(inc, node, 2)
    np.add.at(inc, node[first], -1)
    np.add.at(inc, node[last], -1)
    ends = defaultdict(list)
    for w in range(len(attrs)):
        if attrs['flags'][w] & F_RAIL or attrs['cls'][w] > 10:
            continue
        ends[int(node[first[w]])].append((w, True))
        ends[int(node[last[w]])].append((w, False))
    seglen = np.hypot(np.diff(xy[:, 0]), np.diff(xy[:, 1]))
    out = defaultdict(list)
    turn = attrs['turn'] if 'turn' in attrs.dtype.names else np.zeros(len(attrs), dtype=np.uint8)
    for nd, lst in ends.items():
        if len(lst) != 2 or inc[nd] != 2:
            continue
        (a, a_start), (b, b_start) = lst
        ow_a, ow_b = bool(attrs['flags'][a] & F_ONEWAY), bool(attrs['flags'][b] & F_ONEWAY)
        if ow_a != ow_b or a_start == b_start and ow_a:
            continue        # one-way pieces must run on from each other
        wa, wb = attrs['width_dm'][a] / 10.0, attrs['width_dm'][b] / 10.0
        if abs(wa - wb) < 1.5:
            continue
        wide, wide_start, narrow_w = (a, a_start, wb) if wa > wb else (b, b_start, wa)
        W = max(wa, wb)
        L = float(seglen[off[wide]:off[wide + 1] - 1].sum())
        T = min(float(np.clip(15.0 * (W - narrow_w), 30.0, 60.0)), 0.45 * L)
        if T < 8.0:
            continue
        if not ow_a:
            side = 0
        elif wide_start:
            # a lane opens in the direction of travel: by the median unless the tags say a right-turn lane
            side = 1 if (turn[wide] & 2) and not (turn[wide] & 1) else -1
        else:
            side = 1        # a lane ending: the kerb-side lane merges in (left edge fixed)
        out[wide].append((wide_start, T, narrow_w, side))
    log(f'lane tapers: {len(out)} road pieces widen or narrow gradually where the lane count changes')
    return out


def taper_profile(tp, along, way_len, W):
    """Width and UV offset (metres) at each along-the-way distance for a tapered way. The UV offset keeps lane
    markings in place relative to the fixed edge (the shader draws lanes for the full width)."""
    w = np.full(len(along), W)
    uo = np.zeros(len(along))
    for at_start, T, wn, side in tp:
        d = along if at_start else way_len - along
        t = np.clip(d / T, 0.0, 1.0)
        t = t * t * (3.0 - 2.0 * t)
        wt = wn + (W - wn) * t
        sel = wt < w
        w = np.where(sel, wt, w)
        # side -1 (opens on the left, right edge fixed): right edge at +w/2 must read as +W/2
        uo = np.where(sel, (W - wt) * 0.5 * (-side if side else 0.0), uo)
    return w, uo


def roads_on_paved(paved, xy, off, attrs):
    """Minor roads that run mostly across a paved area (parking aisles, lanes over squares): drawn without verges."""
    if not paved:
        return set()
    import shapely
    polys = [shapely.Polygon(p['xy'][:int(p['ends'][0])]) for p in paved]
    tree = shapely.STRtree(polys)
    cand = np.nonzero((attrs['cls'] >= 5) & (attrs['cls'] <= 9) & ((attrs['flags'] & F_RAIL) == 0))[0]
    way = np.repeat(np.arange(len(attrs)), np.diff(off))
    sel = np.isin(way, cand)
    idx = np.nonzero(sel)[0]
    hit = np.zeros(len(xy), dtype=bool)
    pi, _ = tree.query(shapely.points(xy[idx]), predicate='intersects')
    hit[idx[np.unique(pi)]] = True
    inside = np.bincount(way[hit], minlength=len(attrs))
    total = np.diff(off)
    # service roads (car-park aisles and the lanes around them) lose their verges when they touch a paved area at
    # all; other minor roads only when they run mostly across one
    service = attrs['cls'] == 8
    out = set(np.nonzero((inside * 2 > total) | (service & (inside > 0)))[0].tolist())
    log(f'paved areas: {len(out)} minor roads run across them (drawn without verges)')
    return out


def build_paved(work, dem):
    """Parking lots, fuel forecourts, squares and carriageway areas (D20) as flat paved surfaces, encoded like
    junction surfaces (flag 4; 8 = pedestrian paving). Heights from the elevation model at each vertex."""
    path = os.path.join(work, 'paved.npz')
    if not os.path.exists(path):
        return []
    import shapely
    import mapbox_earcut as earcut
    d = np.load(path)
    xy = d['xy'].astype(np.float64) / 100.0
    ring_off, ring_inner, feat_ring, kinds = d['ring_off'], d['ring_inner'], d['feat_ring'], d['attrs']['kind']
    if KEEP is not None:
        m = 500.0
        x0, x1 = KEEP[0] * TILE - m, (KEEP[2] + 1) * TILE + m
        n0, n1 = -((KEEP[3] + 1) * TILE) - m, -(KEEP[1] * TILE) + m
    out, skipped = [], 0
    for f in range(len(kinds)):
        r0, r1 = feat_ring[f], feat_ring[f + 1]
        outer = xy[ring_off[r0]:ring_off[r0 + 1]]
        if KEEP is not None and not (x0 <= outer[0, 0] <= x1 and n0 <= outer[0, 1] <= n1):
            continue
        holes = [xy[ring_off[r]:ring_off[r + 1]] for r in range(r0 + 1, r1) if ring_inner[r]]
        try:
            poly = shapely.make_valid(shapely.Polygon(outer, holes)).buffer(0)
        except Exception:
            skipped += 1
            continue
        polys = list(poly.geoms) if poly.geom_type == 'MultiPolygon' else [poly] if poly.geom_type == 'Polygon' else []
        for pg in polys:
            if pg.area < 30.0 or pg.area > 400_000.0:
                continue
            pg = shapely.segmentize(pg.simplify(0.3), 20.0)
            pg = shapely.geometry.polygon.orient(pg, 1.0)
            rings = [np.asarray(pg.exterior.coords)[:-1]] + [np.asarray(h.coords)[:-1] for h in pg.interiors]
            rings = [r for r in rings if len(r) >= 3]
            verts = np.vstack(rings)
            if len(verts) > 4000:
                skipped += 1
                continue
            ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)
            tris = earcut.triangulate_float64(verts, ends)
            if len(tris) == 0:
                skipped += 1
                continue
            hv = np.asarray(dem.sample_world(verts[:, 0], verts[:, 1]), dtype=np.float64)
            if not np.isfinite(hv).any():
                continue
            hv = np.where(np.isfinite(hv), hv, np.nanmedian(hv))
            out.append({'xy': verts, 'ends': ends, 'y': hv, 'tris': tris, 'elevated': False, 'paved': int(kinds[f])})
    log(f'paved areas: {len(out)} surfaces (parking, squares, carriageway areas), {skipped} skipped')
    return out


def underpass_decks(underpasses, xy, off, attrs, y, junctions):
    """Where a road passes over an underpass's cutting it becomes a short deck (drawn as a bridge, so it does not
    shape the ground and the cutting runs on underneath): {way: [(from, to) along the way]}. Junction surfaces over
    a cutting are marked elevated for the same reason (D16)."""
    import shapely
    decks = defaultdict(list)
    sunk = []           # (x, y, height, half width) of tunnel points
    for tw, lst in underpasses.items():
        a, b = off[tw], off[tw + 1]
        line_t = xy[a:b]
        hw_t = attrs['width_dm'][tw] / 20.0
        for other, cx, cy in lst:
            oa, ob = off[other], off[other + 1]
            pts = xy[oa:ob]
            ko = int(np.argmin(np.hypot(pts[:, 0] - cx, pts[:, 1] - cy)))
            kt = int(np.argmin(np.hypot(line_t[:, 0] - cx, line_t[:, 1] - cy)))
            if y[oa + ko] - y[a + kt] < 3.0:
                continue        # the tunnel could not sink here (a steep street, a short ramp): leave both alone
            along = float(shapely.LineString(pts).project(shapely.Point(cx, cy)))
            # crossing angle from the two directions at the crossing
            do = pts[min(ko + 1, len(pts) - 1)] - pts[max(ko - 1, 0)]
            dt = line_t[min(kt + 1, len(line_t) - 1)] - line_t[max(kt - 1, 0)]
            sin = abs(do[0] * dt[1] - do[1] * dt[0]) / max(1e-6, np.hypot(*do) * np.hypot(*dt))
            half = min(60.0, (hw_t + GAME_SHOULDER + 1.5) / max(0.3, sin))
            decks[other].append((along - half, along + half))
        for k in range(len(line_t)):
            sunk.append((line_t[k, 0], line_t[k, 1], y[a + k], hw_t))
    raised = 0
    if junctions and sunk:
        from scipy.spatial import cKDTree
        sk = np.array(sunk)
        kd = cKDTree(sk[:, :2])
        for j in junctions:
            if j['elevated']:
                continue
            ring = j['xy'][:int(j['ends'][0])]
            c = ring.mean(0)
            rad = float(np.hypot(*(ring - c).T).max()) + GAME_SHOULDER + 12.0
            near = kd.query_ball_point(c, rad)
            if not near:
                continue
            poly = shapely.Polygon(ring)
            jy = float(np.mean(j['y']))
            for i in near:
                px_, py_, ph, phw = sk[i]
                if jy - ph > 3.0 and poly.distance(shapely.Point(px_, py_)) <= phw + GAME_SHOULDER:
                    j['elevated'] = True
                    raised += 1
                    break
    log(f'underpasses: {sum(len(v) for v in decks.values())} decks over cuttings, {raised} junctions raised over them')
    return decks


def split_decks(parts, intervals):
    """Splits cut_way's parts further at deck intervals; each piece carries a trailing is-deck flag."""
    for px, pz, py, pl, s0, jstart, jend, ostart, oend in parts:
        al = s0 + np.concatenate([[0.0], np.cumsum(np.hypot(np.diff(px), np.diff(pz)))])
        a0, a1 = al[0], al[-1]
        cuts = sorted({float(v) for iv in intervals for v in iv if a0 + 0.5 < v < a1 - 0.5})
        bounds = [a0] + cuts + [a1]
        last = len(bounds) - 2
        for i in range(len(bounds) - 1):
            lo, hi = bounds[i], bounds[i + 1]
            mid = 0.5 * (lo + hi)
            is_deck = any(a <= mid <= b for a, b in intervals)
            inside = (al > lo) & (al < hi)
            ss = np.concatenate([[lo], al[inside], [hi]])
            yield (np.interp(ss, al, px), np.interp(ss, al, pz), np.interp(ss, al, py), np.interp(ss, al, pl), lo,
                   jstart if i == 0 else False, jend if i == last else False,
                   ostart if i == 0 else False, oend if i == last else False, is_deck)


def cut_way(px, pz, py, pl, al, intervals):
    """Splits one way's polyline into the parts outside the junction intervals. Yields
    (x, z, y, lift, distance at start, starts at a junction, ends at a junction, is the way's start, is its end)."""
    total = al[-1]
    if not intervals:
        yield px, pz, py, pl, 0.0, False, False, True, True
        return
    iv = sorted((max(0.0, a), min(total, b)) for a, b in intervals if b > 0 and a < total)
    merged = []
    for a, b in iv:
        if merged and a <= merged[-1][1] + 0.05:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    keep, cur = [], 0.0
    for a, b in merged:
        if a - cur > 0.5:
            keep.append((cur, a))
        cur = max(cur, b)
    if total - cur > 0.5:
        keep.append((cur, total))
    for a, b in keep:
        inside = (al > a) & (al < b)
        ss = np.concatenate([[a], al[inside], [b]])
        yield (np.interp(ss, al, px), np.interp(ss, al, pz), np.interp(ss, al, py), np.interp(ss, al, pl), a,
               a > 0.01, b < total - 0.01, a <= 0.01, b >= total - 0.01)


def encode_junctions(junctions):
    """Junction surfaces into tile records: the tile holding the centroid draws it; tiles its box overlaps get a
    ghost copy (for shaping the ground)."""
    per_tile = defaultdict(list)
    for j in junctions:
        x = j['xy'][:, 0]
        z = -j['xy'][:, 1]
        cx, cz = x.mean(), z.mean()
        home = (tile_of(cx), tile_of(cz))
        tiles = {(tx, tz) for tx in range(tile_of(x.min()), tile_of(x.max()) + 1)
                 for tz in range(tile_of(z.min()), tile_of(z.max()) + 1)}
        ends = j['ends']
        counts = np.diff(np.concatenate([[0], ends])).astype(np.int64)
        for t in tiles:
            if not in_scope(t):
                continue
            fl = (0 if t == home else 1) | (2 if j['elevated'] else 0)
            if j.get('paved') is not None:
                fl |= 4 | (8 if j['paved'] == 1 else 0)
            rec = bytearray(struct.pack('<BB', fl, len(counts)))
            for c in counts:
                rec += struct.pack('<H', int(c))
            q = np.empty(len(x), dtype=[('x', '<i2'), ('z', '<i2'), ('y', '<u2')])
            q['x'] = np.clip(np.round((x - t[0] * TILE) * 10), -32768, 32767)
            q['z'] = np.clip(np.round((z - t[1] * TILE) * 10), -32768, 32767)
            q['y'] = encode_h(j['y'])
            rec += q.tobytes()
            tris = j['tris'].astype('<u2')
            rec += struct.pack('<I', len(tris)) + tris.tobytes()
            per_tile[t].append(bytes(rec))
    return per_tile


def split_way(px, pz, py, pl):
    """Cut a polyline at tile borders. Returns arrays x, z, y, lift and pieces [(tile, i0, i1)]."""
    tx0, tx1 = tile_of(px.min()), tile_of(px.max())
    tz0, tz1 = tile_of(pz.min()), tile_of(pz.max())
    if tx0 == tx1 and tz0 == tz1:
        return px, pz, py, pl, [((tx0, tz0), 0, len(px) - 1)]
    xs, zs, ys, ls = [px[0]], [pz[0]], [py[0]], [pl[0]]
    for i in range(len(px) - 1):
        x0, z0, x1, z1 = px[i], pz[i], px[i + 1], pz[i + 1]
        ts = []
        for c0, c1 in ((x0, x1), (z0, z1)):
            lo, hi = sorted((c0, c1))
            k = math.floor(lo / TILE) + 1
            while k * TILE < hi:
                ts.append((k * TILE - c0) / (c1 - c0))
                k += 1
        for t in sorted(ts):
            if 1e-6 < t < 1 - 1e-6:
                xs.append(x0 + (x1 - x0) * t)
                zs.append(z0 + (z1 - z0) * t)
                ys.append(py[i] + (py[i + 1] - py[i]) * t)
                ls.append(pl[i] + (pl[i + 1] - pl[i]) * t)
        xs.append(x1); zs.append(z1); ys.append(py[i + 1]); ls.append(pl[i + 1])
    xs, zs, ys, ls = np.array(xs), np.array(zs), np.array(ys), np.array(ls)
    mx = np.floor((xs[:-1] + xs[1:]) * 0.5 / TILE).astype(np.int64)
    mz = np.floor((zs[:-1] + zs[1:]) * 0.5 / TILE).astype(np.int64)
    pieces = []
    start = 0
    for i in range(1, len(mx) + 1):
        if i == len(mx) or mx[i] != mx[start] or mz[i] != mz[start]:
            pieces.append(((int(mx[start]), int(mz[start])), start, i))
            start = i
    return xs, zs, ys, ls, pieces


# =================================================================================================
# buildings (unchanged from format 2 apart from the tile size)
# =================================================================================================

def ring_area(x, z):
    return 0.5 * float(np.dot(x, np.roll(z, -1)) - np.dot(z, np.roll(x, -1)))


def point_in_ring(px, pz, x, z):
    inside = False
    n = len(x)
    j = n - 1
    for i in range(n):
        if (z[i] > pz) != (z[j] > pz) and px < (x[j] - x[i]) * (pz - z[i]) / (z[j] - z[i]) + x[i]:
            inside = not inside
        j = i
    return inside


def build_buildings(work):
    import mapbox_earcut as earcut
    d = np.load(os.path.join(work, 'buildings.npz'))
    xy, ring_off, ring_inner, feat_ring, attrs = d['xy'], d['ring_off'], d['ring_inner'], d['feat_ring'], d['attrs']
    nfeat = len(attrs)
    gx = xy[:, 0].astype(np.float64) / 100.0
    gz = -xy[:, 1].astype(np.float64) / 100.0
    first_ring = feat_ring[:-1]
    a = ring_off[first_ring]
    b = ring_off[first_ring + 1]
    csx = np.concatenate([[0.0], np.cumsum(gx)])
    csz = np.concatenate([[0.0], np.cumsum(gz)])
    cnt = np.maximum(1, b - a)
    cx = (csx[b] - csx[a]) / cnt
    cz = (csz[b] - csz[a]) / cnt

    # Outlines fully described by building:part pieces are dropped in favour of the parts.
    is_part = attrs['part'] == 1
    part_cells = defaultdict(list)
    for i in np.nonzero(is_part)[0]:
        part_cells[(int(cx[i] // 64), int(cz[i] // 64))].append(i)
    has_parts = np.zeros(nfeat, dtype=bool)
    if part_cells:
        for i in np.nonzero(~is_part)[0]:
            x = gx[a[i]:b[i]]
            z = gz[a[i]:b[i]]
            x0, x1, z0, z1 = x.min(), x.max(), z.min(), z.max()
            found = False
            for kx in range(int(x0 // 64), int(x1 // 64) + 1):
                for kz in range(int(z0 // 64), int(z1 // 64) + 1):
                    for p in part_cells.get((kx, kz), ()):
                        if x0 <= cx[p] <= x1 and z0 <= cz[p] <= z1 and point_in_ring(cx[p], cz[p], x, z):
                            found = True
                            break
                    if found:
                        break
                if found:
                    break
            has_parts[i] = found
    log(f'{int(is_part.sum())} building parts; {int(has_parts.sum())} outlines replaced by their parts')

    per_tile = defaultdict(list)
    kept = 0
    for i in range(nfeat):
        if has_parts[i] or not in_scope((tile_of(cx[i]), tile_of(cz[i]))):
            continue
        btype, part, roof, height_dm, min_dm, roof_dm, has_height = attrs[i]
        btype = int(btype)
        rings = []
        area = 0.0
        ok = True
        for r in range(feat_ring[i], feat_ring[i + 1]):
            x = gx[ring_off[r]:ring_off[r + 1] - 1]
            z = gz[ring_off[r]:ring_off[r + 1] - 1]
            if len(x) < 3:
                ok = ring_inner[r] == 1
                if not ok:
                    break
                continue
            ar = ring_area(x, z)
            inner = ring_inner[r] == 1
            if not inner:
                area = abs(ar)
            if (ar < 0) != inner:
                x, z = x[::-1], z[::-1]
            if len(x) > 600:
                ok = False
                break
            rings.append((x, z))
        if not ok or not rings or area < 4.0:
            continue
        height = height_dm / 10.0
        if not has_height:
            if btype == C.B_GENERIC:
                height = 2.8 if area < 30 else 6.5 if area < 260 else 8.0
            elif btype == C.B_HOUSE and area < 45:
                height = 4.5
            elif btype == C.B_APARTMENTS and area > 900:
                height = 18.0
            else:
                height = C.BUILDING_DEFAULT_HEIGHT[btype]
        height = max(2.2, height)
        roof = int(roof)
        if roof == 0:
            if btype in (C.B_HOUSE, C.B_FARM, C.B_CHURCH) or (btype == C.B_GENERIC and 30 <= area < 260):
                roof = 2 if len(rings) == 1 and len(rings[0][0]) <= 8 else 3
            else:
                roof = 1
        roof_h = roof_dm / 10.0
        if roof >= 2 and roof_h <= 0:
            roof_h = min(height * 0.42, 4.5) if roof in (2, 3) else height * 0.5
        min_h = min(min_dm / 10.0, max(0.0, height - 0.5))
        roof_h = min(roof_h, height - min_h)

        tx, tz = tile_of(cx[i]), tile_of(cz[i])
        flags = BF_PART if part else 0
        tri = None
        if len(rings) > 1:
            verts = np.concatenate([np.stack([x, z], axis=1) for x, z in rings])
            ends = np.cumsum([len(x) for x, _ in rings]).astype(np.uint32)
            tri = earcut.triangulate_float64(verts, ends)
            if len(tri) == 0 or len(tri) > 60000:
                rings = rings[:1]
                tri = None
            else:
                flags |= BF_TRIS

        def encode(ox, oz, fl):
            rec = bytearray(struct.pack('<BBBHHHB', btype, fl, roof, int(round(height * 10)),
                                        int(round(min_h * 10)), int(round(roof_h * 10)), len(rings)))
            for x, z in rings:
                rec += struct.pack('<H', len(x))
                q = np.empty((len(x), 2), dtype='<i2')
                q[:, 0] = np.clip(np.round((x - ox) * 10), -32768, 32767)
                q[:, 1] = np.clip(np.round((z - oz) * 10), -32768, 32767)
                rec += q.tobytes()
            if fl & BF_TRIS:
                rec += struct.pack('<H', len(tri)) + tri.astype('<u2').tobytes()
            return bytes(rec)

        per_tile[(tx, tz)].append(encode(tx * TILE, tz * TILE, flags))
        kept += 1
        # Ghost copies let neighbouring tiles keep trees and grass out of an overhanging building.
        x, z = rings[0]
        for gtx in range(tile_of(x.min()), tile_of(x.max()) + 1):
            for gtz in range(tile_of(z.min()), tile_of(z.max()) + 1):
                if (gtx, gtz) != (tx, tz) and in_scope((gtx, gtz)):
                    per_tile[(gtx, gtz)].append(encode(gtx * TILE, gtz * TILE, (flags | BF_GHOST) & ~BF_TRIS))
        if i % 500000 == 0:
            log(f'buildings {i}/{nfeat}')
    log(f'{kept} buildings over {len(per_tile)} tiles')
    return per_tile


# =================================================================================================
# land cover
# =================================================================================================

_land = {}


def _init_land(work):
    d = np.load(os.path.join(work, 'land.npz'))
    _land['gx'] = d['xy'][:, 0].astype(np.float64) / 100.0
    _land['gz'] = -d['xy'][:, 1].astype(np.float64) / 100.0
    _land['ring_off'] = d['ring_off']
    _land['feat_ring'] = d['feat_ring']
    w = np.load(os.path.join(work, 'waterlines.npz'))
    _land['wx'] = w['xy'][:, 0].astype(np.float64) / 100.0
    _land['wz'] = -w['xy'][:, 1].astype(np.float64) / 100.0
    _land['woff'] = w['off']
    _land['wwidth'] = w['attrs']['width_dm'].astype(np.float32) / 10.0


def _raster_tile(job):
    from PIL import Image, ImageDraw
    (tx, tz), feats, lines = job
    ox, oz = tx * TILE, tz * TILE
    gx, gz = _land['gx'], _land['gz']
    ring_off, feat_ring = _land['ring_off'], _land['feat_ring']
    img = Image.new('L', (LAND_RES, LAND_RES), C.L_OPEN)
    draw = ImageDraw.Draw(img)
    s = 1.0 / LAND_CELL

    def pix(a, b):
        p = np.empty((b - a, 2), dtype=np.float64)
        p[:, 0] = (gx[a:b] - ox) * s
        p[:, 1] = (gz[a:b] - oz) * s
        return p.ravel().tolist()

    for cls, f in feats:
        r0, r1 = feat_ring[f], feat_ring[f + 1]
        if r1 - r0 == 1:
            draw.polygon(pix(ring_off[r0], ring_off[r0 + 1]), fill=int(cls))
        else:
            mask = Image.new('1', (LAND_RES, LAND_RES), 0)
            md = ImageDraw.Draw(mask)
            md.polygon(pix(ring_off[r0], ring_off[r0 + 1]), fill=1)
            for r in range(r0 + 1, r1):
                md.polygon(pix(ring_off[r], ring_off[r + 1]), fill=0)
            img.paste(int(cls), (0, 0), mask)
    wx, wz, woff, wwidth = _land['wx'], _land['wz'], _land['woff'], _land['wwidth']
    for li in lines:
        a, b = woff[li], woff[li + 1]
        p = np.empty((b - a, 2), dtype=np.float64)
        p[:, 0] = (wx[a:b] - ox) * s
        p[:, 1] = (wz[a:b] - oz) * s
        draw.line(p.ravel().tolist(), fill=C.L_WATER, width=max(1, int(round(wwidth[li] * s))))
    return (tx, tz), img.tobytes()


def build_land(work, tiles_needed, workers):
    d = np.load(os.path.join(work, 'land.npz'))
    gx = d['xy'][:, 0].astype(np.float64) / 100.0
    gz = -d['xy'][:, 1].astype(np.float64) / 100.0
    ring_off, feat_ring = d['ring_off'], d['feat_ring']
    cls = d['attrs']['cls']
    prio = {c: i for i, c in enumerate(C.LAND_ORDER)}
    first = feat_ring[:-1]
    a, b = ring_off[first], ring_off[first + 1]
    per_tile = defaultdict(list)
    for f in range(len(cls)):
        x, z = gx[a[f]:b[f]], gz[a[f]:b[f]]
        c = int(cls[f])
        for tx in range(tile_of(x.min()), tile_of(x.max()) + 1):
            for tz in range(tile_of(z.min()), tile_of(z.max()) + 1):
                per_tile[(tx, tz)].append((prio[c], c, f))
    w = np.load(os.path.join(work, 'waterlines.npz'))
    wx = w['xy'][:, 0].astype(np.float64) / 100.0
    wz = -w['xy'][:, 1].astype(np.float64) / 100.0
    woff = w['off']
    lines_per_tile = defaultdict(list)
    for li in range(len(woff) - 1):
        x, z = wx[woff[li]:woff[li + 1]], wz[woff[li]:woff[li + 1]]
        for tx in range(tile_of(x.min() - 10), tile_of(x.max() + 10) + 1):
            for tz in range(tile_of(z.min() - 10), tile_of(z.max() + 10) + 1):
                lines_per_tile[(tx, tz)].append(li)
    jobs = []
    for t in set(per_tile) | set(lines_per_tile) | set(tiles_needed):
        if not in_scope(t):
            continue
        feats = sorted(per_tile.get(t, ()))
        jobs.append((t, [(c, f) for _, c, f in feats], lines_per_tile.get(t, [])))
    log(f'rasterising land cover for {len(jobs)} tiles')
    out = {}
    with Pool(workers, initializer=_init_land, initargs=(work,)) as pool:
        for n, (t, data) in enumerate(pool.imap_unordered(_raster_tile, jobs, chunksize=32)):
            out[t] = data
            if n % 2000 == 0:
                log(f'land {n}/{len(jobs)}')
    return out


# =================================================================================================
# ground heights and water levels
# =================================================================================================

WATER_SMOOTH = 60.0      # metres: the water surface is the DEM over water, averaged this far (lidar noise, banks)
WATER_DROP = 0.25        # the surface sits this far below that average
DEPTH_MAX = 3.0          # carved depth far from the shore
DEPTH_SLOPE = 0.25       # depth gained per metre from the shore

_hw = {}


def _init_heights(dem_folder):
    from dem import DemMosaic
    _hw['dem'] = DemMosaic(dem_folder)


def _water_mask(land_bytes):
    """Land-cover water at 8 m (any water in the 2x2 land cells around a height corner)."""
    arr = np.frombuffer(land_bytes, dtype=np.uint8).reshape(LAND_RES, LAND_RES) == C.L_WATER
    k = LAND_RES // H_RES
    cell = arr.reshape(H_RES, k, H_RES, k).any(axis=(1, 3))      # per 8 m cell
    corner = np.zeros((H_RES + 1, H_RES + 1), dtype=bool)
    corner[:-1, :-1] |= cell
    corner[1:, :-1] |= cell
    corner[:-1, 1:] |= cell
    corner[1:, 1:] |= cell
    return corner


def _heights_tile(job):
    """Height grid (and water grid when there is water nearby) for one tile, from a 3x3-tile window."""
    from scipy import ndimage
    from dem import fill_nan
    (tx, tz), masks = job
    dem = _hw['dem']
    has_water = any(m is not None and m.any() for m in masks)
    if has_water:
        halo, n = H_RES, 3 * H_RES + 1           # sample the 3x3 window
        x0, z0 = (tx - 1) * TILE, (tz - 1) * TILE
    else:
        halo, n = 0, H_RES + 1
        x0, z0 = tx * TILE, tz * TILE
    xs = x0 + np.arange(n) * H_CELL
    zs = z0 + np.arange(n) * H_CELL
    gx, gz = np.meshgrid(xs, zs)
    h = dem.sample_world(gx.ravel(), -gz.ravel()).reshape(n, n)
    missing = float((~np.isfinite(h)).mean())
    h = fill_nan(h)
    water = None
    if has_water:
        wm = np.zeros((n, n), dtype=bool)
        for j in range(3):
            for i in range(3):
                m = masks[j * 3 + i]
                if m is not None:
                    wm[j * H_RES:(j + 1) * H_RES + 1, i * H_RES:(i + 1) * H_RES + 1] |= m
        sig = WATER_SMOOTH / H_CELL
        num = ndimage.gaussian_filter(np.where(wm, h, 0.0), sig)
        den = ndimage.gaussian_filter(wm.astype(np.float64), sig)
        level = np.where(wm, num / np.maximum(den, 1e-6) - WATER_DROP, np.nan)
        dist = ndimage.distance_transform_edt(wm) * H_CELL       # metres to the nearest dry corner
        depth = np.minimum(DEPTH_MAX, 0.4 + dist * DEPTH_SLOPE)
        h = np.where(wm, np.minimum(h, level - depth), h)
        c = slice(halo, halo + H_RES + 1)
        lv = level[c, c]
        water = np.where(np.isfinite(lv), encode_h(np.nan_to_num(lv, nan=H_BASE)), 0).astype('<u2')
        if not water.any():
            water = None
        h = h[c, c]
    return (tx, tz), encode_h(h).tobytes(), None if water is None else water.tobytes(), missing


def build_heights(land, dem_folder, workers):
    masks = {t: _water_mask(b) for t, b in land.items()}
    masks = {t: (m if m.any() else None) for t, m in masks.items()}
    jobs = []
    for (tx, tz) in land:
        jobs.append(((tx, tz), [masks.get((tx + i, tz + j)) for j in (-1, 0, 1) for i in (-1, 0, 1)]))
    log(f'sampling heights for {len(jobs)} tiles ({sum(1 for m in masks.values() if m is not None)} with water)')
    out, wout = {}, {}
    worst = 0.0
    with Pool(workers, initializer=_init_heights, initargs=(dem_folder,)) as pool:
        for n, (t, hb, wb, missing) in enumerate(pool.imap_unordered(_heights_tile, jobs, chunksize=8)):
            out[t] = hb
            if wb is not None:
                wout[t] = wb
            worst = max(worst, missing)
            if n % 1000 == 0:
                log(f'heights {n}/{len(jobs)}')
    log(f'heights done; worst tile had {worst * 100:.1f} % of its grid outside the DEM')
    return out, wout


# =================================================================================================
# points
# =================================================================================================

def build_pois(work):
    p = np.load(os.path.join(work, 'pois.npy'))
    with open(os.path.join(work, 'poi_names.txt'), encoding='utf-8') as f:
        names = f.read().split('\n')
    per_tile = defaultdict(list)
    seen = set()
    for i in range(len(p)):
        x, z = p['x'][i] / 100.0, -p['y'][i] / 100.0
        key = (int(p['kind'][i]), int(x // 25), int(z // 25))
        if key in seen:
            continue
        seen.add(key)
        tx, tz = tile_of(x), tile_of(z)
        if not in_scope((tx, tz)):
            continue
        name = (names[i] if i < len(names) else '').encode('utf-8')[:60]
        while True:
            try:
                name.decode('utf-8')
                break
            except UnicodeDecodeError:
                name = name[:-1]
        per_tile[(tx, tz)].append(struct.pack('<BHHB', int(p['kind'][i]),
                                              int(round((x - tx * TILE) * 10)), int(round((z - tz * TILE) * 10)),
                                              len(name)) + name)
    log(f'{len(seen)} points of interest')
    return per_tile


def build_points(work):
    p = np.load(os.path.join(work, 'points.npy'))
    x = p['x'] / 100.0
    z = -p['y'] / 100.0
    tx = np.floor(x / TILE).astype(np.int64)
    tz = np.floor(z / TILE).astype(np.int64)
    rec = np.zeros(len(p), dtype=[('kind', 'u1'), ('x', '<u2'), ('z', '<u2')])
    rec['kind'] = p['kind']
    rec['x'] = np.clip(np.round((x - tx * TILE) * 10), 0, TILE * 10 - 1)
    rec['z'] = np.clip(np.round((z - tz * TILE) * 10), 0, TILE * 10 - 1)
    order = np.lexsort((tz, tx))
    tx, tz, rec = tx[order], tz[order], rec[order]
    per_tile = {}
    if len(rec):
        change = np.nonzero((np.diff(tx) != 0) | (np.diff(tz) != 0))[0] + 1
        starts = np.concatenate([[0], change])
        ends = np.concatenate([change, [len(rec)]])
        for a, b in zip(starts, ends):
            if in_scope((int(tx[a]), int(tz[a]))):
                per_tile[(int(tx[a]), int(tz[a]))] = rec[a:b]
    log(f'{len(rec)} trees, lamps and poles over {len(per_tile)} tiles')
    return per_tile


def build_barriers(work):
    """Fences, walls, hedges, guard rails (D25) cut at tile borders: kind u8, height u8 (0.1 m), point count u16, then
    per point x i16, z i16 (0.1 m, tile-local) and the gap a gate there leaves (u8, 0.1 m). Heights come from the
    ground in the game."""
    path = os.path.join(work, 'barriers.npz')
    if not os.path.exists(path):
        return {}
    d = np.load(path)
    xy = d['xy'].astype(np.float64) / 100.0
    off, attrs = d['off'], d['attrs']
    gaps = np.load(os.path.join(work, 'barrier_gaps.npy'))
    per_tile = defaultdict(list)
    n = 0
    for w in range(len(attrs)):
        a, b = off[w], off[w + 1]
        if b - a < 2:
            continue
        px, pz = xy[a:b, 0], -xy[a:b, 1]
        if KEEP is not None and not any(in_scope((tile_of(x), tile_of(z))) for x, z in ((px[0], pz[0]), (px[-1], pz[-1]))):
            continue
        g = gaps[a:b].astype(np.float64)
        xs, zs, gs, _, pieces = split_way(px, pz, g, np.zeros_like(px))
        # (split_way interpolates the third array at tile crossings: a gap belongs to its own node only)
        gs = np.where(np.isin(np.round(gs), g), gs, 0)
        kind, h = int(attrs['kind'][w]), int(attrs['height_dm'][w])
        if 'raised' in attrs.dtype.names and attrs['raised'][w]:
            kind |= 0x80   # (top bit: on a bridge or viaduct)
        for (tx, tz), i0, i1 in pieces:
            if not in_scope((tx, tz)) or i1 - i0 < 1 or i1 - i0 + 1 > 65535:
                continue
            q = np.empty(i1 - i0 + 1, dtype=[('x', '<i2'), ('z', '<i2'), ('g', 'u1')])
            q['x'] = np.clip(np.round((xs[i0:i1 + 1] - tx * TILE) * 10), -32768, 32767)
            q['z'] = np.clip(np.round((zs[i0:i1 + 1] - tz * TILE) * 10), -32768, 32767)
            q['g'] = np.clip(np.round(gs[i0:i1 + 1]), 0, 255)
            per_tile[(tx, tz)].append(struct.pack('<BBH', kind, h, len(q)) + q.tobytes())
            n += 1
    log(f'barriers: {len(attrs)} ways, {n} pieces over {len(per_tile)} tiles')
    return per_tile


def build_places(work):
    p = np.load(os.path.join(work, 'places.npy'))
    with open(os.path.join(work, 'place_names.txt'), encoding='utf-8') as f:
        names = f.read().split('\n')
    out = []
    seen = set()
    for i in range(len(p)):
        rank = int(p['rank'][i])
        if rank > 2:
            continue
        key = (names[i], int(p['x'][i] // 200000), int(p['y'][i] // 200000))
        if key in seen:
            continue
        seen.add(key)
        out.append({'n': names[i], 'r': rank, 'x': round(p['x'][i] / 100.0, 1), 'z': round(-p['y'][i] / 100.0, 1),
                    'p': int(p['pop'][i])})
    out.sort(key=lambda e: (e['r'], -e['p'], e['n']))
    return out


# =================================================================================================
# pack
# =================================================================================================

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('work')
    ap.add_argument('out')
    ap.add_argument('--dem', required=True)
    ap.add_argument('--name', default='mazowsze')
    ap.add_argument('--workers', type=int, default=6)
    ap.add_argument('--tiles', help='tx0,tz0,tx1,tz1: build only these tiles (a quick test map)')
    args = ap.parse_args()
    global KEEP
    if args.tiles:
        KEEP = tuple(int(v) for v in args.tiles.split(','))
        log(f'test build: tiles x {KEEP[0]}..{KEEP[2]}, z {KEEP[1]}..{KEEP[3]}')
    os.makedirs(args.out, exist_ok=True)
    from dem import DemMosaic
    dem = DemMosaic(args.dem, cache_blocks=160)

    roads, jtiles = build_roads(args.work, dem)
    del dem
    buildings = build_buildings(args.work)
    pois = build_pois(args.work)
    points = build_points(args.work)
    barriers = build_barriers(args.work)
    land = build_land(args.work, set(roads) | set(jtiles) | set(buildings) | set(points), args.workers)
    heights, water = build_heights(land, args.dem, args.workers)
    txs = [t[0] for t in land]
    tzs = [t[1] for t in land]
    log(f'tile bounds x {min(txs)}..{max(txs)}, z {min(tzs)}..{max(tzs)}')

    index = []
    pak_no = 0
    pak = open(os.path.join(args.out, f'{args.name}.{pak_no:02d}.pak'), 'wb')
    pak_size = 0
    total = 0
    for t in sorted(land):
        r = roads.get(t, [])
        b = buildings.get(t, [])
        p = pois.get(t, [])
        blob = bytearray(b'BDT7')
        blob += struct.pack('<I', len(r)) + b''.join(r)
        jr = jtiles.get(t, [])
        blob += struct.pack('<I', len(jr)) + b''.join(jr)
        blob += struct.pack('<I', len(b)) + b''.join(b)
        blob += struct.pack('<H', len(p)) + b''.join(p)
        pt = points.get(t)
        blob += struct.pack('<I', 0 if pt is None else len(pt)) + (b'' if pt is None else pt.tobytes())
        br = barriers.get(t, [])
        blob += struct.pack('<I', len(br)) + b''.join(br)
        blob += land[t]
        blob += heights[t]
        wb = water.get(t)
        blob += struct.pack('<B', 0 if wb is None else 1) + (b'' if wb is None else wb)
        comp = zlib.compress(bytes(blob), 9)
        if pak_size + len(comp) > PAK_LIMIT:
            pak.close()
            pak_no += 1
            pak = open(os.path.join(args.out, f'{args.name}.{pak_no:02d}.pak'), 'wb')
            pak_size = 0
        index.append((t[0], t[1], pak_no, pak_size, len(comp), len(blob)))
        pak.write(comp)
        pak_size += len(comp)
        total += len(comp)
    pak.close()

    proj = C.WORLD_PROJ.encode('utf-8')
    with open(os.path.join(args.out, f'{args.name}.idx'), 'wb') as f:
        f.write(b'BDIX')
        f.write(struct.pack('<IfIIffH', FORMAT, TILE, LAND_RES, H_RES, H_UNIT, H_BASE, len(proj)))
        f.write(proj)
        f.write(struct.pack('<I', len(index)))
        for tx, tz, pn, o, n, raw in index:
            f.write(struct.pack('<iiHIII', tx, tz, pn, o, n, raw))
    places = build_places(args.work)
    with open(os.path.join(args.out, f'{args.name}.places.json'), 'w', encoding='utf-8') as f:
        json.dump(places, f, ensure_ascii=False, separators=(',', ':'))
    log(f'wrote {len(index)} tiles, {total / 1e6:.1f} MB in {pak_no + 1} pak file(s), {len(places)} places')


if __name__ == '__main__':
    main()
