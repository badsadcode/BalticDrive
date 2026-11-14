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

## How to run and test

See README.md. The map packs are not in git: build them with tools/osm_import (README there; about 30 minutes for
Mazowieckie with the elevation already downloaded). Elevation for Mazowieckie (570 GUGiK blocks, ~2 GB) lives in
`data/dem/gugik10`; the extract in `data/source`.

Handy places (world metres, x east, z south): Palace of Culture 68891,639498.

The game needs the BadBuilding library cloned beside it (`../BadBuilding`, github.com/badsadcode/BadBuilding): it is a
project reference (D29). `--buildings=bb` (or F7 in game) switches to its buildings.

## Architecture in one breath

`tools/osm_import` projects everything with one Baltic-wide Lambert conic (D2), solves road heights on the road graph
from the DEM (D4), writes 2048 m tiles (D5). In the game, `MapData` reads tiles; `MapWorld`/`MapContext` answer height
and road queries; `TerrainStreamer` (Thornwake's nested LOD rings, worker builds, frame budgets, collision in pieces)
draws the ground through `TerrainBuilder`; `MapContentStreamer` meshes roads and buildings in 128 m cells with Dead
Miles' `MapMesher`; `WorldOrigin` moves the float origin (D3). `Car` is Dead Miles' driving model, trimmed.

## Not done / next

* **Open from Tom (14 Nov, late):** F9 dumps in %APPDATA%\BalticDrive\dumps showing missing parts of classic
  buildings, to go through first; and gaps in BadBuilding's top cornices (gzyms) at roof corners.
* **Crash while flying (Tom, 15 Nov, F7 on, central Warsaw, ~45k BadBuilding buildings made that session):** the
  window just vanished; access violation 0xc0000005 in the engine, log cut off, dump in
  %LOCALAPPDATA%/CrashDumps/BalticDrive.exe.10732.dmp. Suspects: memory or huge chunk meshes (BadBuilding bakes 3
  vertices per triangle, nothing shared: hundreds of thousands of vertices per central chunk), or a race between
  streaming workers and node teardown. Next: read the dump (module and memory), share vertices, run a long fly test
  with --buildings=bb watching memory, and make close/teardown wait for in-flight jobs anyway.
  **Repro (Tom):** fly far away from the car in free camera, press F ("drive here") → the window closes. Path:
  Main.SpawnCar → Car.Recover (NearestRoad within 600 m, Warp) on a car that was frozen far away (Car freezes where no
  ground collision). Script it as a self-test (fly N km, SpawnCar) with F7 on and off; suspects: warping a frozen
  body, physics on fresh concave collision (degenerate triangles from BadBuilding masses?), origin shift.
* **Plac Konstytucji is one big concrete slab (Tom, 15 Nov):** the square's pedestrian area is drawn over the roads
  crossing it, hiding the carriageways, tram tracks, markings and the car park. Suspect: pedestrian junction surfaces
  get the high junction bias (0.105) in MapMesher.AddJunction, so they sit above every road; they should sit under
  roads like paved areas (0.045), or be cut where roads cross. Check at x 69560, z 640428.
* **Wobbly, stitched service roads (Tom, 15 Nov, top-down screenshot round a block):** tight driveway loops look
  jagged at the edges and show seams where ribbon pieces meet or overlap. Suspects: roads meshed segment by segment
  (no shared vertices at joints, so tight bends fold or gap), coarse OSM polylines on curves, the noisy asphalt edge in
  map_road.gdshader, and overlapping service roads without a junction surface. Fixes to try: one continuous strip per
  road with mitred joints, resample curves smoothly, calmer edge noise; check with an F9 dump there.
* **From Tom's side-by-side comparisons (15 Nov):** zebra crossings (OSM highway=crossing nodes and footway=crossing
  ways, painted on the road with stop lines), and checking why some parking lots are missing. In BadBuilding: PRL
  commercial pavilions (an upper floor overhanging the shops on pillars, usually mapped with building:part or min_height).
  Street level: tram overhead wires on their poles (catenary), traffic signs and lights at junctions, more bollards
  and kerb furniture. Distinctive PRL/brutalist landmarks with upside-down stepped floors are override candidates.

* Barriers (D25): fences, walls, hedges, guard rails, bollards to ~600 m; generated looks (Synty pieces later), gates
  are openings only.
* Far roads (D24): ribbons out to ~9 km. Far buildings beyond the 3 km skyline are still missing.
* Tree colours are a fixed mix (some autumn maples and birches); Tom (13 Nov): leave it, seasons and weather will
  drive them in the final game.
* Ideas from Tom (13 Nov), for later:
  - Override layer keyed by OSM id: replace a building's box with a hand-made model (landmarks first).
  - Fences, walls, gates, bollards, hedges from OSM `barrier=*`, with Synty pieces.
  - A world authoring mode in game that saves to that override layer; the release ships map + edits.
  - BadBuilding (D29, 14 Nov): its buildings are in the game behind the switch (F7 / `--buildings=bb`), for
    flat-roofed town buildings within 600 m. Next: roofs, then making it the default once Tom has driven it.
  - Buildings with interiors (Tom, 14 Nov): mix BadShip (github.com/badsadcode/BadShip, C#: seed + recipe → modular
    exterior with sockets, LODs, collision, metadata) and BadLevel (github.com/badsadcode/BadLevel, Python: room
    graph → floor plans, stairs, doors, validation, gameplay markup) on the OSM footprint, levels and type. Clones
    beside the project (read-only). Proposal in the chat of 14 Nov: exterior from the footprint for every building,
    interiors generated on demand from the OSM id when the player comes close.
  - Interiors under a height map (Tom, 14 Nov): metro stations, basements, bunkers, caves as underground models
    reached through small openings or doors (OSM has every Warsaw metro station, entrance and line); the
    cut-and-cover tunnels (D26) are the same trick at ground level.
  - Pavements: from `sidewalk=*` / `footway=sidewalk`, else estimated for town streets (~2 m, kerb). Ask Tom
    about kerb height and paving look first.
* Sky (D23): Thornwake's volumetric sky, sun and light are in; weather, rain, mist, moon phases, forest rays and
  biome moods are not. Look switches for testing: --grade=N (0 off), --volfog=0, --haze=F, --sat=F.
* Rivers use the lake water style; flow direction from OSM waterways is not fed to the shader yet.
* `MapRouter` / `MapRoute` / `Navigation` keep world positions as floats: fine within a region (6 cm at 700 km), must
  become doubles before routes cross the Baltic.
* Junctions have their own surfaces (D11); roundabouts and slip roads have not been checked closely.
* Cuttings have sloped earth sides; retaining walls would look right in town. Floating buildings (D17) fixed in code, not yet confirmed by Tom.
* Multiplayer: Thornwake's `Net/` (ENet, host-authoritative) not brought over yet.
* Other regions: the importer takes any Geofabrik extract; elevation outside Poland needs another source
  (Copernicus GLO-30, or national bare-ground models: Lantmäteriet, Kartverket, SDFI, Maa-amet, MML, LGIA, LVM GEO).
