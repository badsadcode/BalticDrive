# BadBuilding — Noor's reconnaissance notes

**Companion to:** `docs/BADBUILDING_RECON.md` (14 November 2026)  
**For:** Sage / Baltic Drive  
**Status:** Design feedback and research notes, **not** an implementation order

> **Intent:** Keep Baltic Drive as the practical proving ground for a focused Warsaw/Mazowsze building generator. Learn from BadShip and BadLevel without interrupting their development or prematurely turning this into the full NoorArchitect. Later, proven techniques can inform NoorArchitect and perhaps reusable Noor Library DLLs.

## First: what I agree with

Sage's recon identifies a strong starting point:

- **BadShip:** deterministic seeds and stage rerolls; modular geometry; LOD, collision, exports; multi-deck interiors; room accessibility; and especially the interior/exterior window-placement handshake.
- **BadLevel:** constrained room layouts, stair cores, subdivision with rollback, accessibility rules, and recognizable style fingerprints. Since BadLevel is Python, borrow or port ideas selectively rather than treating it as a C# dependency.
- **OSM/Baltic Drive:** real footprints, some building types and storey counts, and occasional entrances and address numbers. Most roof, material, colour, and historical detail must be inferred, **not presented as verified real-world information**.

The following are additions to investigate, not a replacement for the recon.

## 1. Make buildings fit the *terrain*, not just the footprint

A footprint is a horizontal boundary, not a complete building site. Baltic Drive already knows terrain elevation and road geometry. Consider:

- Ground height sampled around the outline, not just once at the centre.
- A deliberate building datum: where the main floor and entrances sit relative to the street.
- Foundations, plinths, steps, ramps, retaining walls, or exposed basement walls on slopes.
- Doors that connect to plausible ground levels; avoid floating thresholds or buried entrances.
- Separate the natural terrain from the adjusted ground near a building, so the structure does not force bizarre slopes into surrounding streets.

**Recon test:** The same house on a flat lot and on a sloped lot should be structurally plausible, without changing its footprint or leaving a floating doorway.

## 2. Understand neighbouring buildings and facade exposure

Street orientation is a good start, but **not every polygon edge is a facade that should get windows**.

Investigate categories such as:

- Street-facing frontage: entrance, shop front, prominent window rhythm.
- Courtyard-facing frontage: windows and secondary access appropriate to the style.
- Exposed side wall: possibly fewer windows or a simpler finish.
- Party/shared wall: **no ordinary exterior windows where another building physically abuts it**.
- Very narrow gaps between buildings: avoid windows and balconies that face into unusable slivers.

Use adjacent OSM footprints and street geometry as **context**, while recognising that missing, inaccurate, or overlapping map data may require a safe fallback. Don't infer a party wall merely because two coordinates are almost equal without a tolerance and geometry check.

**Recon test:** A row of joined kamienice should read as a street frontage, with courtyard facades behind them, rather than a row of isolated houses with windows through their neighbours.

## 3. Give styles architectural *grammar*, not random decoration

The five starter styles in Sage's recon are a sensible scope. Each should have a recognizable hierarchy of **massing → facade rhythm → structural details → materials → small details**.

Examples to explore:

| Style | Character-defining features to test |
|---|---|
| PRL panel block | Panel grid, repeated window and balcony modules, stairwell glazing, entrance canopies, consistent floor rhythm. |
| Kamienica | Street/courtyard contrast, taller windows, cornice and plinth proportions, gate passages, period-inspired but variable facade composition. |
| Detached house | Plausible pitched roof, eaves, porch, window scale, clear public/private entrance. |
| Outbuilding / farm | Utility-driven openings, straightforward construction, sparse decoration. |
| Shop / warehouse | Large functional openings, loading or sales frontage, roof and bay rhythm appropriate to use. |

Aim for **variation within a coherent design language**, not identical clones and not noisy greeble. A PRL block should remain recognizable as a PRL block after rerolling its details.

Keep inferred attributes distinguishable from mapped data: e.g., `roof_source: "inferred"`, `levels_source: "osm"`.

**Recon test:** View several seeds of each style without labels. You should still recognize the architectural family.

## 4. Apply building-specific rules to interiors

BadShip's interior system is a substantial head start. Housing, however, needs different layout constraints from ship compartments:

- Entrances, stair cores, and lifts should align coherently across storeys.
- Every flat should connect to shared circulation **without routing through another flat**.
- Living rooms and bedrooms should generally have meaningful access to exterior-facing windows, where the footprint allows.
- Bathrooms and utility rooms can occupy internal zones; kitchens and bathrooms can plausibly cluster around shared service zones.
- Exterior windows must correspond to actual rooms, and doors must not intersect structural members or stair landings.
- The room planner should handle courtyards, L-shapes, long thin buildings, and awkward corners without unreachable leftover islands.
- Basements, shops at ground level, and upper-floor flats may have different circulation rules.

These are **believability rules**, not a requirement to simulate plumbing, electrical circuits, or building codes in full. OSM footprints do not reveal real apartments, so the interiors must be presented as **fictional procedural layouts**.

**Recon test:** A generated apartment plan can be followed from the front door to each room; private spaces are separated correctly; window-facing rooms get sensible window positions.

## 5. Keep deterministic identity *and* persistent world changes separate

Sage already plans deterministic generation from building identifiers. Extend that concept carefully:

- Stable identities for buildings, floors, flats, rooms, doors, windows, and significant modules.
- IDs should survive ordinary **visual detail** rerolls when the corresponding object still exists.
- Version the recipe and generator algorithm. The same OSM ID + seed may generate a different layout after a future algorithm update unless a version or resolved plan is preserved.
- Allow selected hand-authored overrides for landmarks and important interiors.
- Treat generated layout as the **initial state**; save gameplay changes separately: looted containers, unlocked doors, player-placed objects, damage, etc.
- Consider OSM edits (modified footprints or feature IDs) and a migration/fallback path rather than assuming the source data will never change.

This supports Baltic Drive's survival possibilities and eventually helps NoorArchitect's editable objects and asset replacement.

**Recon test:** Re-entering a building recreates its intended initial geometry consistently, but does **not** reset the player's persistent changes.

## 6. Build a deliberately difficult footprint test set

The first gallery should not contain only convenient rectangles. Include representative **real** Warsaw/Mazowsze footprints plus synthetic stress shapes clearly labelled as such:

- Long rectangular PRL block.
- L-shaped house or apartment block.
- Narrow corner lot / irregular wedge.
- Concave footprint with a deep recess.
- Building with a courtyard hole.
- Row of touching buildings sharing walls.
- Sloped-site building with different front/rear elevations.
- Very small or thin/degenerate footprint for a graceful rejection or simplified fallback.

If the library cannot produce a sensible layout for an input, **report why and fall back safely**. Do not silently invent geometry extending outside the OSM boundary.

**Gallery idea:** Show the exterior plus a per-floor plan side by side, with footprint, street direction, entrances, window positions, stair core, inaccessible areas, inferred attributes, and validation warnings.

## Two complementary definitions of success

**Technical:** Valid meshes; believable terrain intersection; no accidental facade overlaps; reachable rooms; deterministic results; reliable LOD/collision/export; bounded streaming cost.

**Architectural:** Someone who knows Warsaw can look at a generated building and say, *“Yes, that looks like it belongs on that street.”* This needs human visual review, not just green validator checks.

## Scope and ownership

1. **Baltic Drive is the experiment.** Optimize for recognizable, efficient Mazowsze buildings and selected on-demand interiors.
2. **BadShip and BadLevel continue independently.** Read them, learn from them, and reuse code only when appropriate and maintainable; don't require those sessions to stop and reorganize themselves.
3. **Do not build universal NoorArchitect here.** Medieval castles, all-world architecture, and generalized editor UX are outside this recon.
4. **Library boundaries can remain provisional.** If a standalone C# core helps test and reuse the Baltic Drive generator, great; don't commit to an entire Noor Library architecture yet.
5. **No implementation demanded by this note.** Compare these concerns against Sage's original recon, identify which ones truly matter for the first prototype, and record the rest as later investigations.

---

*Bad Ideas Works: We Create Worlds. Badly. Sometimes we even make the staircases reach the right floor.*  
*— Noor 💜*
