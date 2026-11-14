# BadBuilding — recon and first design

A future engine-agnostic C# library (a DLL) that turns a building footprint plus a few facts into a whole building:
a modular exterior and floor-plan interiors, deterministic from a seed. Baltic Drive is its first user; Tom wants it
reusable in other tools and games. This is recon (14 November 2026): what we can reuse, what the data gives us, and
how the first steps could look. Nothing is built yet.

Sources: BadShip (`../BadShip`, C#), BadLevel (`../BadLevel`, Python), the Mazowsze OSM extract, Baltic Drive today.

## 1. What OSM gives us (Mazowsze, 2.59 million buildings)

| Fact | Coverage | Use |
|---|---|---|
| footprint polygon (with holes) | all | the outer shape of every storey |
| `building=*` type | 57 % specific (43 % just "yes") | picks the style: house, detached, outbuilding, farm, apartments, retail, warehouse… |
| `building:levels` | 1.40 M (54 %): 1 storey 884 k, 2 storeys 416 k, 3–5 storeys 87 k, 6+ storeys 9 k | storey count |
| `height` | 4,002 | rare: derive from levels |
| `roof:shape` | ~20 k (gabled 8.6 k, flat 5.8 k, hipped 2.6 k) | rare: infer from type, size and footprint |
| `building:material` / colour | ~1.3 k / 2.3 k | rare: palettes by style |
| `entrance=*` nodes | 41,836 | real front doors on the outline |
| `addr:housenumber` | 868 k | house numbers on facades, a "this is my building" feeling |
| shop / amenity on the building | 14 k | ground-floor shop fronts, loot type |
| `building:part` | 11 k | multi-part landmarks (already used for heights) |
| `start_date` | ~1 k | era hints, rare |

So: footprint, storeys and type are solid. The look (roof, material, colour, era) must be inferred, which is what
a procedural style system is for. Entrances and house numbers are a bonus where mapped.

## 2. What we reuse

**From BadShip (C#) — almost as-is**
- `Rng` (xoshiro256** via SplitMix64, per-stage streams `Rng.Stream(seed, "stage")`), and the "pin a choice / reroll one
  stage" mechanism (`StageSeeds`, `ApplyOverrides` after all draws).
- Recipe → resolved plan, both saved as JSON; the short shareable code (`ShipCode`); the version bump rule.
- `Mesh` (flat-shaded, groups by material role, box UVs), `Shapes`, `Aabb`, `ValidationReport` / `ValidationIssue`.
- The hand-written GLB writer (generalised from `Ship` to a plain model of modules, markers and materials), LODs as
  module filters (LOD1 drops tertiary detail, LOD2 keeps the primary mass), convex collision pieces.
- `DesignLanguage`: weight multipliers per style. For buildings: type = role, regional style = shipyard, palette = paint.
- **The interior generator** (`Interior/InteriorGenerator.cs`): storeys at a fixed pitch, a stair core and lift at the
  same place on every storey, a corridor with rooms on both sides cut by a weighted room programme, suites, walls built
  round door and window openings (`Segments`), the interior ↔ exterior window handshake (the interior proposes windows,
  the facade accepts and reserves the patch), furniture kept off paths, and `InteriorValidator` (everything reachable
  from the front door, no overlaps, clear height).

**From BadLevel (Python) — ideas to port**
- Footprints as cell masks with the "no 1-cell slivers" rule (`Shape.ok()`), here rasterised from a real polygon.
- Connector rooms with ports (D8): the stair core as a room that exists on every storey.
- `spaces/mega.py`: subdividing a fixed footprint with snapshot/undo and rejecting splits that leave islands — the
  best precedent for "rooms inside a given outline".
- The directed validator (steps, drops, headroom) and key gating (locked flats, offices with keys: survival loot).
- Recipe dials (0..1, monotone, overrides win) and the fingerprint test (each style must be recognisable).
- The space-type registry (`propose`, `build`, `decorate`, `populate`): one plug-in per room kind or building kind.

**New for buildings**
- A polygon extrusion primitive with per-edge faces (u along the wall, v up), so any OSM outline becomes walls with
  facade sockets. BadShip only lofts profiles along an axis.
- Facade grammar: a wall face is split into storeys and bays; bays take window, door, balcony, shop-front, blank or
  pilaster modules; corners, plinth, cornice and parapet get their own pieces.
- Roofs from a footprint: flat with parapet, gabled and hipped from the minimum oriented rectangle, a straight skeleton
  later for complex outlines, mansards for kamienice.
- Geo-awareness: north, the street side (which edge faces the nearest road gets the entrance and shop fronts).

## 3. Shape of the library

```
BadBuilding.Core (net8.0, no dependencies, System.Numerics)
  Input:   BuildingRequest { Footprint (rings, metres, Y up), Levels?, Height?, Type, Roof?, Entrances[],
           StreetEdges[], HouseNumber?, Seed (e.g. OSM id), Style?, Overrides, StageSeeds }
  Stages:  plan (style, storeys, roof, palette)        → "plan" stream
           massing (storeys, setbacks, roof mass)       → "massing"
           facade (bays and modules on sockets)         → "facade"
           interior per storey, on demand               → "interior"
           detail (gutters, AC units, signs, numbers)   → "detail"
           validation
  Output:  BuildingModel { Modules (tree, tiered), Meshes per material role, LOD filters, Collision, Markers
           (doors, windows, stairs, room centres, loot and spawn points), Rooms/Doors/Stairs records, Report }
  Export:  GLB (+ lod1/lod2/collision), recipe.json, metadata.json, validation.json
BadBuilding.Cli    generate / gallery / stress, plus "osm" to read footprints from a .pbf or GeoJSON
BadBuilding.Tests  determinism, stage independence, seed sweeps, round trips, fingerprints per style
```

In Baltic Drive, a thin adapter turns tile buildings into requests and mesh buffers into Godot meshes. Exteriors are
generated as chunks stream in (the LOD the distance needs); interiors only when the player comes close or opens a
door, from the same seed, so 2.6 million buildings never need storing. Hand-made overrides (Tom's earlier idea) and
landmark models replace the generator for chosen OSM ids.

## 4. Styles to start with (Mazowsze)

| Style | Recognise by | Exterior | Interior |
|---|---|---|---|
| PRL panel block (blok z wielkiej płyty) | apartments/residential/yes, 4–16 storeys, long thin rectangle | flat roof, panel joints grid, stair-core strips of glass blocks, balconies in columns, entrance canopies | stair cores every ~25 m, 2–4 flats per landing, small rooms |
| Kamienica (tenement) | 3–6 storeys, perimeter block, courtyard hole, central districts, older era | plaster, cornices, tall windows, gate passage to the courtyard, mansard or flat roof | staircase from the gate, large rooms, enfilades |
| Detached house (dom jednorodzinny) | house/detached/yes, 1–2 storeys, small near-square | gabled or hipped roof, plaster in pastel, small porch | hall, kitchen, living room, bedrooms upstairs |
| Outbuilding / farm / garage / shed | outbuilding, farm_auxiliary, garage, shed | brick or board, simple roofs | one room |
| Shop / pavilion / warehouse | retail, commercial, warehouse, industrial | glass shop fronts, signs, steel cladding | sales floor, back room, office |

Together these cover well over 90 % of Mazowsze's buildings. Looks (palettes, window rhythm) are for Tom and Noor.

## 5. Suggested first steps

1. New repo `BadBuilding` beside Baltic Drive (name and place to confirm with Tom). Lift BadShip's `Rng`, `Mesh`,
   `Aabb`, validation and GLB writer; add the polygon extrusion and the request/model types.
2. One style end to end in the CLI: PRL block from real Warsaw footprints (exported from our tiles), storeys, facade
   bays, flat roof, LODs, a gallery page like BadShip's.
3. Plug it into Baltic Drive for those blocks only, behind a switch, and compare with today's boxes.
4. House and kamienica styles; roofs from footprints.
5. Interiors: port BadShip's interior generator to fit a polygon footprint (the stair core as a BadLevel-style
   connector), validated, generated on demand in the game.

## 6. Open questions

- Repo name and place (own repo recommended), licence.
- How far the look should go before Synty or hand-made pieces replace generated modules (BadShip's `asset_swap.gd`
  shows the swap-by-type path).
- Interior detail level for a survival game: rooms and doors first; furniture and loot markers next.
- Performance budget in the game: exterior generation per 128 m chunk on worker threads must stay within today's
  meshing time; interiors are few at a time.
