# Map packs, format 4

Written by `tools/osm_import/build.py`, read by `Game/Map/MapData.cs`. Little-endian throughout. Change both together
and bump `FORMAT`.

## Files

* `<name>.idx` — header and tile index
* `<name>.NN.pak` — zlib-compressed tile blobs, each file under 48 MiB
* `<name>.places.json` — named settlements `[{n, r, x, z, p}]` (name, rank 0 city / 1 town / 2 village, world x/z, population)

## Index

| Field | Type |
| --- | --- |
| magic | `BDIX` |
| format | u32 = 4 |
| tile size | f32 = 2048 |
| land raster | u32 = 512 (cells per side, 4 m) |
| height grid | u32 = 256 (cells per side, 8 m; 257² corner values) |
| height unit, base | f32 = 0.04, f32 = −100 (metres = value × unit + base) |
| projection | u16 length + UTF-8 proj string (must equal `Projection.Proj4`) |
| count | u32 |
| entries | count × (tx i32, tz i32, pak u16, offset u32, length u32, raw length u32) |

Tile (tx, tz) covers world x ∈ [tx·2048, (tx+1)·2048), z ∈ [tz·2048, (tz+1)·2048). World z points south.

## Tile blob

`BDT4`, then:

1. **Roads**: u32 count, each: class u8, flags u8 (1 bridge, 2 tunnel, 4 one-way, 8 unpaved, 16 lit, 32 rail,
   64 start cap, 128 end cap), flags2 u8 (1 starts at a junction surface, 2 ends at one, bits 2–5: lanes in the point direction on a
   two-way road, 0 = even split; D19), width u8 (0.1 m), layer i8,
   mapped lanes u8 (0 = unknown), distance along the whole way at the first point f32, point count u16, then per
   point x i16 (0.1 m, tile-local), z i16, height u16 (absolute, height units), lift u8 (0.1 m above natural ground).
   Points ≤ 10 m apart. Roads are cut where they enter a junction surface.
2. **Junctions** (D11) and **paved areas** (D20): u32 count, each: flags u8 (1 ghost: drawn by another tile, here
   only to shape the ground; 2 elevated; 4 paved area — parking, square, carriageway area — not a junction; 8 pedestrian
   paving), ring count u8, ring sizes (ring count × u16; the first ring is the outline, the rest holes), then per
   point x i16, z i16 (0.1 m, tile-local), height u16 (height units), then u32 index count and that many u16 triangle
   indices into the points.
3. **Buildings**: u32 count, each: type u8, flags u8 (1 ghost, 2 has triangles, 4 part), roof u8, height u16 (0.1 m),
   min height u16, roof height u16, ring count u8, rings (u16 n, n × (x i16, z i16)), then if flag 2: u16 n, n × u16.
4. **POIs**: u16 count, each: kind u8, x u16, z u16 (0.1 m), name length u8, UTF-8 name.
5. **Points** (trees, lamps, poles, pylons): u32 count, each: kind u8, x u16, z u16.
6. **Land cover**: 512 × 512 u8, rows north to south.
7. **Ground**: 257 × 257 u16 corner heights (natural ground with water beds carved in), rows north to south.
8. **Water**: u8 flag; if 1: 257 × 257 u16 water surface heights, 0 = dry.

Class, flag, land-cover and kind numbers: `tools/osm_import/common.py` and the top of `MapData.cs`.
