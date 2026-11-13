# Baltic Drive — handoff

Start here. Updated 2026-11-13 (first session, past midnight).

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

## Verified (13 Nov, after the elevation fill, D13–D17)

* Elevation: 6,290 GUGiK EVRF2007 sheets merged (225 GB downloaded, 2.5 GB kept); 4,607 km² of the bounding box
  still has no survey data, mostly at its edges. Full pack: 11,482 tiles, 820 MB, built in 40 min.
* Dworzec Centralny: ground flat (~115 m), no pit. The viaduct south of it now slopes down along its own deck (D15):
  the street beside the ground-level lane at 68650,640204 went from 5.1 m to 0.6 m above ground; the car passes.
* Trasa Łazienkowska runs in cuttings under Al. Niepodległości, Marszałkowska and Plac na Rozdrożu with the streets
  on short decks above (D16); 157 underpasses, 476 decks, 71 raised junctions across Mazowsze.
* Streets widened (D18): residential/unclassified 6.5 m, tertiary 7 m, 3.5 m per tagged lane.
* `--autotest=drive --start=68600,639300 --dest=70500,642500 --seconds=600`: ARRIVED, 8.54 km, 9.8 s off road,
  upright. The test driver now looks 7 m ahead at low speed and backs off when pinned; it did so twice, at tight
  corners at 69963,642042 and 70461,642454 (worth a look: buildings may stand right at the kerb there).
* Exported EXE (dist/BalticDrive) runs the new pack.

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
* Lane changes along a road (2 → 3 lanes where a turn lane opens, 3 → 2 where lanes merge): OSM splits the way at
  that node and each piece has one width, so the road steps sideways. Plan (Tom, 13 Nov): per-point width, taper
  the wider piece over ~30–50 m, keep existing lanes fixed and grow the new one on the side `turn:lanes` implies
  (left-turn lanes by the median), merges in reverse.
* Trees: OSM's mapped trees and forests are not planted yet (Thornwake's tree field and impostors are the candidate).
* Sky and atmosphere: Godot's procedural sky; Thornwake's Atmosphere is the candidate.
* Rivers use the lake water style; flow direction from OSM waterways is not fed to the shader yet.
* `MapRouter` / `MapRoute` / `Navigation` keep world positions as floats: fine within a region (6 cm at 700 km), must
  become doubles before routes cross the Baltic.
* Junctions have their own surfaces (D11); roundabouts and slip roads have not been checked closely.
* Cuttings have sloped earth sides; retaining walls would look right in town. Floating buildings (D17) fixed in code, not yet confirmed by Tom.
* Multiplayer: Thornwake's `Net/` (ENet, host-authoritative) not brought over yet.
* Other regions: the importer takes any Geofabrik extract; elevation outside Poland needs another source
  (Copernicus GLO-30, or national bare-ground models: Lantmäteriet, Kartverket, SDFI, Maa-amet, MML, LGIA, LVM GEO).
