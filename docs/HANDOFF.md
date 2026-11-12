# Baltic Drive — handoff

Start here. Updated 2026-11-12 (first session).

## Where things stand

Milestone 1 — *one Polish voivodeship drivable on Thornwake's world machinery* — is built end to end for Mazowieckie:
importer → packs → streamed ground, roads, buildings, water → a car on Jolt → map screen and GPS. See "Verified" for
what has been checked on Tom's PC (RTX 3080) and what has not.

## Verified (12 Nov, Tom's PC, RTX 3080, by the self-tests and their screenshots)

* Full Mazowieckie pack: 11,482 tiles, 686 MB, built in 31 min (road heights 5.5 min, heights 15 min).
* Views at Warsaw, Płock, Wyszogród, Kampinos, Radom: 60 fps (vsync), 550 terrain tiles, ~20 km view.
* `--autotest=drive --dest=70500,642500`: the car followed a 4.7 km GPS route through central Warsaw, 4.32 km in 240 s,
  0.9 s off the road, upright throughout (the test's time ran out 370 m short).
* `--autotest=map --dest=<Radom>`: route Warsaw → Radom 104.7 km found in 0.5 s and drawn on the map screen.
* `--autotest=fly` (test map): 6 km at 40 m/s, median 16.7 ms, 3 frames over 33 ms (worst 70 ms), never without ground.
* Fixed on the way: every roof was black on a real GPU (0/0 in the building shader's window maths, D9).

## Not verified

* Driving by hand (keyboard feel, chase camera), long motorway runs at 140 km/h, bridges up close, night.
* Magenta specks at the far horizon in some views (probably distant water or skyline tiles) — not yet traced.

## How to run and test

See README.md. The map packs are not in git: build them with tools/osm_import (README there; about 30 minutes for
Mazowieckie with the elevation already downloaded). Elevation for Mazowieckie (570 GUGiK blocks, ~2 GB) lives in
`data/dem/gugik10`; the extract in `data/source`.

Handy places (world metres, x east, z south): Palace of Culture 68891,639498.

## Architecture in one breath

`tools/osm_import` projects everything with one Baltic-wide Lambert conic (D2), solves road heights on the road graph
from the DEM (D4), writes 2048 m tiles (D5). In the game, `MapData` reads tiles; `MapWorld`/`MapContext` answer height
and road queries; `TerrainStreamer` (Thornwake's nested LOD rings, worker builds, frame budgets, collision in pieces)
draws the ground through `TerrainBuilder`; `MapContentStreamer` meshes roads and buildings in 128 m cells with Dead
Miles' `MapMesher`; `WorldOrigin` moves the float origin (D3). `Car` is Dead Miles' driving model, trimmed.

## Not done / next

* Far roads (beyond ~1 km) are not drawn; motorways vanish into the distance.
* Trees: OSM's mapped trees and forests are not planted yet (Thornwake's tree field and impostors are the candidate).
* Sky and atmosphere: Godot's procedural sky; Thornwake's Atmosphere is the candidate.
* Rivers use the lake water style; flow direction from OSM waterways is not fed to the shader yet.
* `MapRouter` / `MapRoute` / `Navigation` keep world positions as floats: fine within a region (6 cm at 700 km), must
  become doubles before routes cross the Baltic.
* Junction corners, roundabouts, slip roads: as in Dead Miles (overlapping ribbons).
* Multiplayer: Thornwake's `Net/` (ENet, host-authoritative) not brought over yet.
* Other regions: the importer takes any Geofabrik extract; elevation outside Poland needs another source
  (Copernicus GLO-30, or national bare-ground models: Lantmäteriet, Kartverket, SDFI, Maa-amet, MML, LGIA, LVM GEO).
