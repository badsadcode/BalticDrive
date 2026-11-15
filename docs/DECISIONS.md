# Decisions

Numbered, newest last. Each says what was decided and why.

## D1 — New project from two (2026-11-12)
Baltic Drive takes the OpenStreetMap importer, tile format, road/building meshing, routing and map screen from Dead
Miles, and the world machinery (nested-LOD streaming, worker builds with frame budgets, moving origin) and water from
Thornwake. Thornwake's terrain sampler, biomes and hydrology are procedural and stay behind: the ground here is real.
Its `TileBuilder` calls that sampler directly, so the terrain tile builder is rewritten in the same layout (33×33 grid
per tile, skirts, level-0 collision in pieces) fed from map tiles.

## D2 — One projection for the whole Baltic loop
Lambert conformal conic on WGS84, standard parallels 53° and 67° N, centred 58° N 20° E
(`+proj=lcc +lat_1=53 +lat_2=67 +lat_0=58 +lon_0=20`). Scale error depends only on latitude and stays within about
±1 % from the Tatras (49° N) to North Cape (71° N), so distances and speeds are real to within a percent everywhere
and roads continue across borders with no seams. The earlier idea of a projection per tile or region would have meant
re-projecting at region edges; doubles plus the moving origin (D3) carry the 2,000+ km. Grid north differs from true
north by up to ~10° at the far east and west; `Projection.Convergence` gives the correction for the compass.

## D3 — Moving origin
World positions are doubles; the scene holds float offsets from an origin that jumps (snapped to 1,024 m) when the
focus is 2 km away. Streamed nodes are anchored (`WorldOrigin.Anchor`) and repositioned on a jump; the car warps
keeping its velocity. Shaders get `world_offset` = origin mod 65,536 m so procedural patterns stay on the ground.

## D4 — Real elevation, smoothed road profiles
Poland: GUGiK NMT (bare ground, 1 m source) resampled to 10 m through its WCS. Copernicus GLO-30 would be global but is a
surface model: forests and city blocks become 10–25 m plateaus, which dominate a flat region like Mazowsze.
Road heights are solved on the road graph (densified to 10 m): each class averages the ground over its own length
(motorways 150 m … tracks 25 m), bridges are interpolated between their ends and raised 6 m over what they cross.
The ground meets the roads at runtime (a level bed within 9 m of the edge) at the three finest terrain levels.

## D5 — Tile format 3: 2048 m tiles
Data tiles are 2048 m so they line up with the terrain quadtree (64 m × 2⁵). Each carries an 8 m height grid
(uint16, 4 cm steps from −100 m), a water-level grid where there is water, a 4 m land-cover raster, roads with absolute
heights, buildings, points and POIs. Details: `docs/TILE_FORMAT.md`.

## D6 — Water from the DEM
The water surface is the DEM over mapped water, averaged over 60 m and dropped 25 cm; the bed is carved below it
(0.4 m at the shore, deepening to 3 m). Rivers slope with the land; lakes come out level. Drawn with Thornwake's water
shader (lake style for now).

## D7 — Physics: Jolt
Godot 4.7's built-in Jolt. Dead Miles' car was tuned on GodotPhysics; it is retuned here.

## D8 — Map content radius
Roads, buildings and street furniture are meshed in 128 m cells within 1 km (collision within 320 m), inside the
terrain levels whose ground follows the road beds (≥ 1,024 m). Buildings over 42 m are drawn per tile out to ~5 km
(skyline). Far roads are not drawn yet.

## D9 — Building shader: guard the window maths
Roof faces carry no wall length or height, and the shader divided by both for every face; the NaN survived the
wall/roof mix and turned every roof black on real GPUs (Dead Miles was only ever seen on a software renderer). Both
divisors are clamped. Flat roofs are drawn at 1.5× the palette (Dead Miles' 0.8× read as tar-black here).

## D10 — Car model: Synty when installed, Quaternius otherwise
Tom's Synty Store packs are licensed for use in the game but not for redistribution, and this repository is public:
they live in `Assets/Synty/` (git-ignored) and `data/assets/synty/`. The car uses POLYGON City's sedan when it is there,
else Quaternius' CC0 pickup (committed). The car reads its wheel layout from the model (`*_Wheel_fl/fr/rl/rr` or
Quaternius' names), fits its collision boxes to the body, and strips colliders the model brings. Centre of mass
near the axles and low roll influence keep it on its wheels (the first sedan rolled over at 60 km/h).

## D11 — Junction surfaces (tile format 4)
Where three or more drivable roads meet, the importer builds one surface: junction nodes closer than their half widths
+ 8 m form one junction (dual carriageway crossings, roundabout entries), every arm is trimmed where it is clear of
the others' strips widened by a corner radius r (3–8 m by road width), and the union of the arms is closed with r —
inside corners become kerb arcs, outside corners and the straight cuts stay. Roads are cut back to the edge and carry
JunctionStart/End flags (markings stop with a stop line there); the router links road ends around each junction
(one-ways respected). The ground is shaped to the surface like to a road; elevated junctions are left alone.

## D12 — Road heights: urban smoothing, stacked decks, ramp limit
Smoothing length shrinks to 30–100 % of the class value with road density (towns follow the accurate street-level
DEM). Bridge decks clear what they cross by 6 m per level, solved in two passes so stacked levels add up (instead of
6 m × layer). Climbs up to bridges are limited to 4–6 % by lifting the approach (central Warsaw had 7 % of primary
pieces steeper than 10 %, up to 24 %).

## D13 — Elevation holes filled from GUGiK sheets
GUGiK's WCS serves only the older Kronsztadt-86 survey: 15–56 % of every Mazowsze block (21,500 km², central Warsaw
among it) came back empty and was filled with copied neighbour heights — the pits, steps and phantom viaduct ramps
Tom saw. `tools/dem/fill_gugik_sheets.py` asks the EVRF2007 sheet index which sheets cover each hole, downloads them
(ARC/INFO ASCII, preferring 1 m sheets), averages them into the 10 m blocks and deletes them: ~100+ GB of transfer,
under 3 GB kept. The datums differ by ~0.2 m in Mazowsze.

## D14 — Bridges and underground structures are not buildings
OSM maps some viaducts in 3D as building parts (man_made=bridge, bridge:structure): they stood as 5 m boxes on the
road. They are skipped (the game draws bridges from the roads), as are underground structures (location=underground,
negative layer).

## D15 — A bridge deck keeps its clearance only over what it crosses
Every point of a bridge that passes over a road or railway used to be pinned 6 m above it, end to end. A 220 m
viaduct crossing Al. Jerozolimskie 29 m from its north end then stayed 6 m up at its south end too, and the ramp
limiter (D12) held the street beyond it up for another ~120 m — a wall beside the ground-level lane that joins it
(Tom's car stuck at 68650,640204 near Dworzec Centralny). Now only deck points within the crossed road's half width
+ 8 m (`DECK_MARGIN`) of the crossing are pinned; the rest of the deck has no ground term and slopes down to its
ends, no steeper than the ramp grade. All crossing points count (a deck crossing the same road twice is pinned at
both). Real decks do the same: the abutments sit low and the deck climbs over the obstacle.

## D16 — Underpasses: sunk tunnels, cuttings and short decks
Road tunnels used to be drawn at ground level, crossing the streets above them at grade (Trasa Łazienkowska under
Al. Niepodległości, Marszałkowska and Plac na Rozdrożu). Now every road tunnel (not railway tunnels: the game does
not draw those) is free of the ground like a bridge deck, and where it passes under a road or railway it is pinned
`TUNNEL_DEPTH` (6.5 m) below it. `limit_dips` — the mirror of the ramp limit — lowers its approaches so it dives in
no steeper than the ramp grade. The game's ground already follows a road's bed, so a sunk road digs its own
cutting. The road above becomes a short deck (drawn as a bridge, which does not shape the ground) over the cutting's
width (tunnel half width + the game's 9 m shoulder + 1.5 m, longer at a skew), and junction surfaces over a cutting
are marked elevated. Long city tunnels under parks or buildings simply run straight between their portals.
A cutting has sloped earth sides, not retaining walls: good enough until we draw walls.

## D17 — Building walls reach the natural ground
Beyond the rings where the ground follows road beds (~500 m), the terrain is the plain elevation model, while
buildings sat on the road-shaped height: near roads on embankments or smoothed above the ground, they floated at
medium distance. Walls now run down to the lower of the road-shaped and natural ground at each corner, and 2 m
further (was 0.6 m) for the coarser distant terrain.

## D18 — Town streets a little wider than life
Most OSM streets carry no width or lane count, so the class default decides: residential and unclassified streets
(42,000 km of Mazowsze) were 5.5 m, 2.75 m a lane. True to life, but in the game there are no pavements, parked cars
or verges around them and the camera sits behind the car, and Tom found them tight. Defaults now: tertiary 7.0 m,
residential and unclassified 6.5 m, living street and pedestrian 5.5 m, service 4.0 m (motorway to secondary
unchanged). Tagged lane counts get 3.5 m a lane (was 3.3). A mapped width is kept but raised to 3 m per tagged lane,
and two-way streets never come out under 5 m unless mapped as one lane. Real streets are much wider than their
carriageway between building lines, so the extra metre does not reach into buildings.

## D19 — Uneven lane splits
A two-way road with an odd lane count (OSM `lanes=3`) drew only a centre line, so 3 lanes looked like one wide lane
each way. The importer now reads `lanes:forward` / `lanes:backward`, or counts `turn:lanes:forward` / `:backward`,
and stores the lanes in the way's direction in road flags2 bits 2–5 (0 = unknown, drawn as before). The road shader
gets the lane count and that split packed into COLOR.a (lanes + 9 × forward, over 255, safe in 8-bit colours) and
puts the centre line after the backward lanes, with dividers on both sides. Without split tags an odd count stays a
plain centre line; guessing the side would be wrong half the time.
(Bitwy Warszawskiej at Grójecka is the temporary roadworks layout: one carriageway is `highway=construction`, so it
is not drawn, and the other carries 3 lanes both ways.)

## D20 — Parking lots and squares are paved surfaces
Parking lots were land-cover paint (`L_PAVED`), which reads as packed dirt. Now `amenity=parking` / `fuel` (not
underground, multi-storey or rooftop, not on an unpaved surface), `area:highway=*`, `highway=pedestrian|footway|
service|living_street` with `area=yes`, and `place=square` are extracted to `paved.npz` and built as flat surfaces
like junctions: the same record with flag 4 (8 = pedestrian paving, drawn as concrete). They shape the ground, have
collision, sit just under the roads crossing them, and the router and stop-line logic ignore them.

## D21 — Lane tapers (tile format 5)
OSM splits a way where the lane count changes; each piece had one width, so the road stepped sideways and every
lane line jumped (Banacha, Tom's sketches). Where two pieces meet end to end at a node no other road touches, with
widths differing by 1.5 m or more, the wider piece narrows to the other's width over 30–60 m (15 m per metre of
difference, at most 45 % of the piece), with a smoothstep. One-way roads keep one edge fixed: a lane opening in
the direction of travel opens by the median unless `turn:lanes` says it is a right-turn lane; a lane ending merges
from the kerb side. Such pieces carry a width and a lane offset per point (flags2 bit 7); the shader still lays out
lanes for the full width, shifted by that offset, so existing lane lines run straight and the new lane grows
beside them. The centre line itself does not move (OSM draws each piece through its own middle), so the fixed edge
drifts half a lane over the taper: barely visible.

## D22 — Trees: Thornwake Woodland, streamed like Thornwake
Tom chose his own Thornwake Woodland pack (pine, spruce, oak, birch, poplar, willow, autumn birch and maple, three
levels of detail). Thornwake's tree field is ported (Game/Trees): 128 m cells of MultiMesh 3D trees (detailed model
near, lighter model to 700 m, dithered hand-over), 1024 m cells of impostors baked at start-up to 3 km, the canopy,
bark and impostor shaders with their globals (wind, sun, tree ranges). Placement (TreePlacement) is ours: every tree
OSM maps on its own (street and park trees), plus a world-aligned jittered scatter per land class — forest every
6.5 m (half pine, then birch, oak, spruce, a few autumn birches), parks and cemeteries sparse, scrub and wetland a
few. Each tree's position, species, size and turn come from independent hashes of its grid cell, so near and far
cells agree. Near cells keep trees off roads, junctions and buildings and give trunks collision for the car.
Measured: Kampinos from 150 m up, 11k 3D + 500k impostor trees at 60 fps; the Warsaw drive test unchanged.

## D23 — Sky and light: Thornwake's atmosphere, core only
Ported from Thornwake (Game/Sky, Shaders/Sky): the volumetric sky (Sky.gdshader with SkyCommon and CloudShapes) and
the same cloud volume drawn in front of the scene (CloudsNear, "cloud renderer v2"), sun shafts, cloud shadows (a
decal tiled with the sky's noise), light model 1 (sun colour and energy by elevation, warm sun and cool shade, gentle
eye adaptation) with the temperate and plains light profiles averaged for Mazowsze, the natural look (ACES, white 6)
and Tom's tuned Thornwake settings as fixed values: cloud quality v2, clouds 0.4, glow 0.6, sun shafts 0.6, the
"Fantasy" grade at 0.6, SSAO strong, SSIL, 8k soft shadows to 700 m. The sun is the real sun over 52.2° N for a day
of the year and local solar time ([ and ] move the clock half an hour; --hour, --day, --clouds for screenshots).
One change from Thornwake: its volumetric fog density (0.002–0.0035) veiled every view over a 20 km landscape (Godot
applies the fog at its 180 m length to everything beyond), so it is 0.0011 here with sky affect 0.35.
Not ported yet: weather and rain, mist, lightning, the moon's phases and path (a simple full moon opposite the sun
stands in), stars' rotation, auroras, forest light shafts, biome moods. The water's reflected sky and sun follow the
hour (Thornwake's UpdateWaterSky).
Measured: flying at golden hour, median 16.7 ms (vsync), p99 16.7 ms.

## D24 — Far roads, and the magenta horizon specks
Detailed roads end at ~1 km (MapContentStreamer); beyond it the landscape had no roads, so motorways and avenues
dissolved into grass. World/FarRoads.cs builds one light mesh per 2 km map tile out to 4 tiles (~9 km): a ribbon per
road (main roads and railways to the horizon, residential and unclassified streets to 3.5 km, no tracks, service
roads, trams or tunnels) at the road's own heights. Shaders/far_road.gdshader widens the ribbon with distance (at
least ~1 px), pulls it toward the camera in depth so the coarse distant terrain never swallows it, and hands over to
the detailed roads across a dithered band at 960–1020 m. Cost: none measurable (fly test 16.7 ms median).
The magenta (and some green and blue) specks on the horizon came from the terrain shader: on distant tiles a few
pixels' albedo came out invalid, and ACES plus the colour grade turned them into saturated specks (found by turning
effects off one by one: only replacing the terrain shader removed them). The albedo is now clamped and the world
normal guarded; 0 specks in the test views.

## D25 — Fences, walls, hedges and guard rails (tile format 6)
OSM maps 223k fences, 19k hedges, 11k walls (1,800 noise barriers), 3,600 retaining walls and 3,400 guard rails in
Mazowsze, with 144k gate nodes. They are extracted as lines (barriers.npz, kind and height; gate nodes on a line
give a gap per point) and written per tile (section 6). In the game Map/BarrierMesher.cs walks each line in ≤2 m
steps on the road-shaped ground, leaves a gap at gates and wherever the line would cross a drivable road or a
junction surface, and builds thin double-sided panels with posts (wire mesh, railings, concrete panels, guard rails)
or boxes with caps (walls, hedges, noise barriers, retaining walls, jersey barriers, city walls). One shader
(Shaders/barrier.gdshader) draws every style from COLOR.a: wire mesh and railings are cut out and thin to a
dithered veil with distance, brick, plaster, concrete panels, ribbed noise barrier panels, leafy hedges with a
ragged top. Near chunks (lod 0) collide; drawn to ~600 m (lod 1, no posts). Bollards (barrier=bollard/block nodes)
are a new point kind: short posts with collision. Looks are generated, not Synty yet — a later pass can swap pieces.

## D26 — Cut-and-cover underpasses; garage ramps left out; barriers on bridges
A sunk underpass (D16) made an open earth pit 9 m wide on each side. Now, where a road runs ≥ 1 m below the natural
ground, MapContext.Height blends the ground to it within ~2.7 m of the carriageway edge instead of 9 m, and
Map/CuttingMesher.cs lines the cut with concrete retaining walls 1.3 m past the edge (with a coping); where OSM
calls it a tunnel and there is ≥ 4.8 m of headroom the slot gets a roof slab at ground level (lid on top, a
concrete ceiling under it). Between two carriageways of the same cutting the median stays at road level and gets no
walls. Tunnels on service roads and tracks (underground garage ramps, building passages: Tom's pit at 65243,641750)
lead nowhere a height-map world can show; like railway tunnels they are left out and not sunk.
Barriers tagged bridge=yes or layer ≥ 1 (railings and guard rails of viaducts and footbridges) stood on the ground
across the tracks (Tom, 66062,643393 and 64503,642573). The extract keeps that flag (barriers.npz 'raised', the
kind's top bit in the tiles); the game stands such a barrier on the nearest bridge deck within 3.5 m of its edge, or
leaves it out (footbridges are not drawn yet). Ground barriers also step aside at railway tracks.

## D27 — Pavements: grey slabs, 12 cm kerbs (tile format 7)
Tom chose grey concrete slabs (płyty chodnikowe) and a real 12 cm kerb. Each road side gets a pavement from OSM's
sidewalk tags (sidewalk, sidewalk:both/left/right; yes/both/separate/left/right count, no/none do not; 'separate'
means a mapped footway we do not draw, so the pavement is drawn along the road instead). Untagged sides of town
streets get one: primary to residential, paved, not tunnels, with ≥ 4 buildings within 50 m of the way's middle.
Stored as a walk byte in each road record (format 7). The road mesh drops its shoulder on that side and adds a kerb
face, a strip of slabs (3 m along main roads, 2 m elsewhere) and a short slope to the ground; the ground under it is
flat (NearestGroundRoad counts pavements unless asked not to: fences use the carriageway). The road shader draws the
slabs (surface code 0.89): 50 cm grid, joints, a little tone per slab, the odd crack. Pavements stop at junction
surfaces (no kerb arcs round the corners yet) and are drawn to ~600 m.

## D28 — F9 debug dump; pavement corners
Tom's idea: F9 saves a screenshot and a text file (%APPDATA%\BalticDrive\dumps\dump-YYYYMMDD-HHMMSS.png/.txt) with
the place (world, lat/lon with a Google Maps link, tile, a --start / --views line to repeat it), the camera, the hour,
frame timings, the car and GPS, and the map within 40 m: roads (class, flags, lanes, width, heights, lift, pavement
sides), junctions and paved areas, barriers, counts of buildings and points; and a "Notes" line for what is wrong.
Pavements (D27) stopped at junction surfaces, leaving a dip between two kerbs where the car got stuck (Tom). Junction
outline edges that are street corners (no carriageway continues past them; a tram track only counts where it enters
through that edge) and have a pavement road within reach now get a kerb, slabs and a slope instead of the sunken
shoulder, so the pavements join round the corner. Drive test: arrives, 1.9 s off road (was 7.8 before pavements).

## D29 — Buildings from BadBuilding, behind a switch
The building library (github.com/badsadcode/BadBuilding, cloned beside this folder as `../BadBuilding`) is referenced
as a project, so it builds with the game. `Game/Map/BadBuildingAdapter.cs` turns each footprint into a request with
its context: courtyards, neighbouring footprints within 40 m (party walls, slivers), nearby street segments (which
walls face the street) and the ground under every corner (datum and plinth). The result (real window openings,
balconies, shop fronts, kamienica street fronts, gate passages) is baked into the chunk's building mesh: each material
role becomes a vertex colour, glass gets a new surface kind (0.5) in `building.gdshader`, everything else is a blank
wall or a flat roof, so no shader windows are painted over real ones. Collision is the library's bare mass, which has
the gate passages cut through it.
Scope for now: flat-roofed town buildings (generic, apartments, commercial, civic) within LOD 0 and 1 cells (600 m).
Pitched-roof houses, churches, towers, canopies, building parts and anything over 42 m (the skyline layer) keep the old
meshes, and so does LOD 2; LOD 1 cells get a skirt under the plinth for the coarser terrain. Off by default:
`--buildings=bb` or F7 in game switches it, and the HUD shows the counters.
Measured (central Warsaw, Tom's PC): 10–16 ms and ~4,100–4,600 triangles per building on the worker threads, none fell
back; a 6 km fly-over at 40 m/s holds 60 fps (median and p99 16.7 ms, 10 frames over 33 ms against 4 with the old
buildings); the drive test arrives.

## D30 — The "fly far, press F" crash: no more VehicleWheel3D.GetContactBody
Tom's crash (the window just closed, with BadBuilding on or off) reproduced with a new self-test,
`--autotest=respawn` (free camera, fly 3 km, press F, repeat): on the second round Godot's `VehicleWheel3D` handed
back a freed ground body from `GetContactBody()` after the teleport (an InvalidCastException), and the next node
removal in the terrain streamer crashed with an access violation. The car asked every wheel every tick what it stood
on, to tell paved from loose ground. It now casts a short ray down from each wheel in contact instead, which only
returns live bodies. 8 rounds in both modes, no crash; the drive test still arrives with 2.9 s off road.
Also: BadBuilding meshes now share vertices (one per library vertex and material) instead of three per triangle.

## D31 — Things on the water stand on the water
Tom: walls, fences and sometimes buildings stood in the water. The ground under rivers and lakes is a carved bed
(1–3 m below the surface), and barriers and buildings were set on that ground, so quay walls and bank fences stood
half sunk and moored barges (floating bars, mapped as buildings) stood on the river bottom. Barrier points and building
corners now take the water surface where it is above the ground. The stepped banks (the bed is carved on the 8 m
land-cover grid) are an importer fix still to do (HANDOFF).

## D32 — Tall blocks of flats to BadBuilding; quieter road edges
With BadBuilding on, blocks of flats between 42 and 62 m (apartments or generic, not parts) stay with the near cells
(BadBuilding within 600 m, the old meshes to 1000 m) instead of the skyline layer; the skyline still draws its copy
of them for the distance, hidden within 950 m of the camera by a `near_cut` uniform on a duplicate of the building
material (no shadows from those copies, as the cut would be measured from the light in the shadow pass). Tom's case:
a 16-storey block known only as `building=apartments` with levels now comes out as a PRL block. In BadBuilding, a
block with only a mapped height of four storeys or more fits the PRL style too. Pedestrian areas draw under the
roads crossing them like paved areas (the Plac Konstytucji fix itself needs the importer, HANDOFF). The asphalt edge
in map_road.gdshader wanders less (±5 cm, fine) instead of ±20 cm every ~1.5 m, which read as a wobbly road.

## D33 — Squares under their streets, smooth riverbanks, names in the tiles (format 8)
- **Paved areas don't cover their streets.** Squares, car parks and carriageway areas are flat polygons at the
  elevation model's height, while the streets crossing them run a little lower in their own beds, so a whole square
  covered its avenue, tram tracks and markings (Plac Konstytucji) and a paved area by the Wisłostrada tunnel looked like
  a wall of earth over the road (Tom's dump). `build_paved` now cuts the corridors of the streets that cross an area
  (classes up to residential, not bridges, half width + 0.3 m) out of it before triangulating; parking aisles, service
  roads, footways and pedestrian streets stay on it.
- **Smooth banks.** The bed was carved on an 8 m mask (a corner was wet when any of its four 4 m land-cover cells was
  water) and the game sampled the water as dry wherever any corner of an 8 m cell was dry: banks came out in 8 m
  stair steps. The importer now works out each height corner's signed distance to the shoreline of the 4 m water
  cells (2 m past them, so thin streams keep their water), deepens the bed with that distance and shapes the first
  12 m of land into a bank rising from the water at the shoreline; the game's `WaterAt` gives a shore cell the mean
  of its wet corners, so the surface runs on until the bank rises out of it.
- **Names (tile format 8).** Stage 1 keeps street names (`name`), and per building its OSM id (ways positive,
  relations negative) and address (`addr:street` or `addr:place`, `addr:housenumber`) in `names.txt`; each tile gets a
  last section with its string table, a name per road record and an id and address per building record. Format 7 maps
  still load. The OSM id is the key for the override layer and live editing; the names feed the HUD and, next, the map.
- **Where am I.** Places now include suburbs, hamlets, quarters and neighbourhoods. The HUD says the street and the
  nearest house number on it, then the district (within 1.5 km) or village (3 km), then the town:
  "Plac Konstytucji · Śródmieście Południowe · Warszawa". The map screen shows the small places only zoomed in.

## D34 — Street names and a sharp map when zoomed in
The map screen's picture is 8 m per pixel, blocky when zoomed in (Tom). From 5 m per screen pixel the map now draws
each tile's roads (true width, class colours, minor first) as triangles in tile-local metres, cached per tile and
drawn straight onto the canvas item with the tile's transform; from 2.5 m it also draws the building footprints and
puts a land-cover-only version of the tile picture underneath (baked alongside the full one), so the old fat roads
and blurry buildings do not show through. A closer zoom (1.25 m per pixel) was added. Street names (tile format 8,
D33) are laid along the roads: main roads from 5 m per pixel, every named street from 2.5; one label per ~500 px of
road at the middle of its stretch, turned along it and never upside down, outlined, skipped where it would collide
with another label or repeat the same name within 350 px. Small places (quarters, neighbourhoods) show at 2.5 m and
closer. The map self-test shoots the 1.25 and 5 m zooms.

## D35 — The minimap
A round radar in the bottom-left corner (Tom: GTA style), on the map's vector layer, which moved into
`Game/UI/MapVectors.cs` (per-tile road and building triangles, street-label placement) and is shared with the map
screen. Ahead is up; the scale eases from 2.2 m per pixel parked to 4.5 at motorway speed; roads are drawn 1.8 times
their width (at least 8 m) and buildings always, so it reads at a glance; the GPS route, street names (placed four
times a second, drawn turned with the map each frame), a north mark and your arrow. The disc is a Control drawing a
filled circle with ClipChildren = Only, so the map drawn by its child is clipped round; the ring and the marks sit in
a sibling above it. N hides and shows it; it hides while the big map is open.

## D36 — Buildings on the road are left out; the route from the car on
Tom's dump (a village near Zakroczym): a building stood on the road. Where the centre line of a public road (up to
residential, and links) runs through a building's outline, the outline is a mapping slip and the building is left out
(`MapMesher.BlocksRoad`, both the old meshes and BadBuilding). Living and pedestrian streets, service roads, tracks,
paths, bridges and tunnels do not count: arcades, driveways and gate passages really run under buildings. (The map
and the minimap still show such a footprint; the importer could drop it for good at the next rebuild.)
The map screen and the minimap draw the GPS route only from the car's place on it onwards (`Navigation.Progress`).

## D37 — Houses and pitched roofs to BadBuilding
With roofs in the library (BadBuilding D13), the switch now also hands it houses, farm buildings, sheds and
industrial buildings, and pitched roofs (gabled, hipped, pyramidal), with the roof shape and roof height from the
tile (the importer's own guess for houses counts as mapped); domes, churches, towers, canopies and building parts keep
the old meshes. Pitched roofs are drawn with the building shader's tiled surface. Measured over the western suburbs:
houses cost about 4 ms and 1,400 triangles each; a 6 km fly-over holds 60 fps (1 frame over 33 ms).

## D38 — On foot, first steps
Following docs/BalticDrive_OnFoot_Gameplay_Brief.md. `Game/OnFoot/Walker.cs`: a CharacterBody3D capsule (0.35 m
radius, 1.8 m, layer Characters = 32) walking the streamed world: run 4.2 m/s by default, Shift sprint 6.6, Alt walk
1.7, Ctrl crouch 1.5, Space jump; it waits where ground collision is not loaded (like the car) and follows origin
shifts. Third person on a SpringArm3D over the right shoulder (pulled in by walls and the ground), first person at the
eye (V toggles; the body is hidden in first person until arms and weapons come); the mouse is captured on foot (Esc
releases, a click takes it back). The body turns toward where it walks, or with the camera in first person.
E gets out of the car by the driver's door (only when it has stopped) and back in within 4.5 m; an empty car brakes
and holds its handbrake. F from foot goes to the free camera. The HUD, the minimap and the map follow whoever has
the controls.
The look is the Synty POLYGON City "Male Hoodie" character (from the licensed pack in Assets/Synty, never committed;
a capsule without it). The packs bring characters but no locomotion clips, so `WalkCycle` animates the humanoid
skeleton (Godot's profile names: Hips, LeftUpperLeg…) in code: arms brought down from the rest pose, legs and arms
swinging with the speed, knees bending on the forward swing, a bob, crouch and air poses, all as rotations in model
space, so it does not depend on the rig's bone axes. The prefab leaves its meshes' skeleton path empty (unbound
meshes draw the rest pose), so the walker binds them. Real clips (a CC0 humanoid library retargeted onto the same
skeleton) can replace the cycle later. Self-test: `--autotest=walk` (out, run, sprint, curve, first person, back in).

## D39 — Real locomotion clips (Synty Base Locomotion)
Tom brought the Synty ANIMATION Base Locomotion pack (FBX source files on Synty's own rig). Seventeen masculine in-place
clips (standing and crouching idle; walk, run, sprint forward; walk, run and crouch back; walk and run sideways;
jumps, a short fall, a soft landing) are copied into Assets/Synty/AnimationBaseLocomotion (git-ignored, licence
note beside them) with .import files carrying the retarget settings from the City pack's own Characters.fbx.import:
the same BoneMap onto Godot's SkeletonProfileHumanoid (UpperLeg_L → LeftUpperLeg, …) and rest fixer, so the clips
come out with tracks on %GeneralSkeleton and the profile's bone names, and play on the City character unchanged.
The walker loads them into an AnimationLibrary and drives an AnimationTree: a standing BlendSpace2D (velocity in the
body's frame: idle, walk, run, sprint forward, walk and run back and sideways), a crouching one (idle, forward,
back), a crouch blend and a fall blend after 0.12 s in the air. The skeleton gets the unique name the tracks use
(owner = the model). Without the pack the procedural WalkCycle (D38) still stands in.

## D40 — First-person gun handling, all procedural

The on-foot brief asks for three guns that feel great (a Mosin-style bolt-action, an AK-style rifle, a pistol), aiming
always in first person, and approachable ballistics. There are no arms or weapon animations yet, so everything is code
(`Game/OnFoot/Weapons.cs`):
* **Models:** the Mosin from Synty POLYGON War (its bolt is a separate part, so the bolt really cycles; its texture is
  assigned in code because the FBX points at a missing .psd), the AK (Rifle_Assault_03) and the pistol (Pistol_Heavy_01)
  from POLYGON Battle Royale (slide and magazine are separate parts). Collision bodies are stripped from held guns.
  Rifle_Assault_03's prefab has its magazine, slide and trigger turned 90° about Y (Tom spotted the sideways magazine);
  parts are squared up to the gun's frame on equip. All licensed: git-ignored, never committed.
* **Each gun is a `WeaponDef`:** fire mode, rate, muzzle velocity, magazine, reload time, recoil, ADS field of view, and
  the hip pose, rear sight, muzzle and ejection points measured from the model (a top-profile scan of each mesh).
* **Handling:** RMB aims down the sights and forces first person while held (the walker returns to its own view
  after). The rear sight sits on the eye line (a rifle at the cheek, a pistol at arm's length). The gun sways against
  the look and bobs with the walk (both damped when aiming). Recoil kicks the view up and sideways and punches the gun
  back; both spring back. Reload dips and rolls the gun and drops the magazine. The Mosin's bolt cycles (up, back,
  forward, down) after the trigger is let go.
* **Bullets** are simulated, not hitscan: muzzle velocity plus the walker's own velocity, gravity and a little drag,
  ray-stepped every frame against ground, roads, buildings, vehicles and props. They move with the world origin.
  Hip fire spreads; aimed fire barely does. Hits throw dust coloured by surface and leave a mark (the last 60 stay).
* **Sound:** generated placeholders (the brief: NoorSounds bleeps are fine), layered as crack + thump + tail for the
  shot and clicks for mechanisms, so real recordings can replace each layer.
* **Keys:** 1 Mosin, 2 AK, 3 pistol (the same key again holsters), LMB fire, RMB aim, R reload. HUD: a crosshair for
  hip fire and the ammo count.
* `--autotest=shoot` checks each gun (hip, aim, fire, reload) with screenshots and the hits in the log.

Later: arms (first-person hands on the guns), third-person aiming with IK, real sounds, Mosin stripper-clip reloads,
sight-picture tuning, damage.

## D41 — First-person arms: the walker's own, bent by IK

None of the packs Tom has has first-person arms, so the walker's own character (Synty City hoodie) supplies them:
`Game/OnFoot/FpArms.cs`.
* **The rig:** a second copy of the character rides with the eye camera, turned to face the view. Its head and legs
  are collapsed (bone scale ~0), and its head sits ~14 cm behind the camera, so the hood and the smeared head
  vertices stay out of view. (At 9 cm they showed as dark triangles at the top of the screen.) The sleeves are the
  same red hoodie as in third person.
* **Two-bone IK** per arm, analytic, in the skeleton's space. The elbow goes on the circle where both bones reach,
  toward a pole (down and out). Each bone's rest direction is turned onto its new direction. The hand gets a
  direction (wrist to knuckles) and a palm facing; the wrist sits ~6.5 cm behind the grip point and ~3 cm off the gun.
  A gun held further out than the arm reaches stretches the arm up to 30 % (the hand keeps its size), the usual
  view-model trick.
* **Fingers:** each joint bends ~0.55 rad × curl toward the palm, adding up along the finger. The chain is carried
  in code, because the skeleton's global poses update later. The bend direction was checked A/B in screenshots.
* **Per gun:** `RightGrip` and `LeftGrip` points on the model (the pistol grip behind the trigger; the handguard, or
  the right hand for the two-handed pistol hold). On a reload the left hand goes to the magazine; on the Mosin's bolt
  cycle the right hand goes to the bolt knob.
* The arms show only when the gun does (first person, or aiming).

Later: third-person hands on the gun (IK on the walker's own body), better finger shapes, the Mosin's stripper clip.

## D42 — Something to shoot at: hits that react, and a little range (T)

Bullets now do something to what they hit, through one interface the bandits will use too:
* **`IHittable.Hit(BulletHit)`:** where, the normal, velocity, mass and range; it returns a short line for the HUD.
  `Hits.Apply` looks for an `IHittable` on the collider or above it. Any other loose rigid body gets a shove: momentum
  × 0.12, because real momentum would send a can off at 500 m/s. Bullet mass is per gun: Mosin 9.6 g, AK 7.9 g,
  pistol 8 g.
* **Targets** (`Game/OnFoot/Targets.cs`):
  * glass bottles shatter into a dozen shards that fade;
  * cans jump and spin;
  * barrels rock;
  * a paper target on its stand is scored by ring (10 to 1);
  * steel plates, made in code since no pack has them: a white-painted 30 cm disc hanging on a hinge from a frame,
    which swings and rings when hit.
  Props come from Synty Gang Warfare (bottles, range target and stand, metal barrel) and City (soda can), scaled to
  real sizes: a 30 cm bottle, a 12 cm can, a 1 m barrel.
* **T on foot** sets up a range in front of you, on whatever ground a ray finds:
  * 8 m: two barrels with cans and bottles on top;
  * 15 m: the paper target;
  * 25, 50 and 100 m: plates.
  It moves with the world origin; T again sets it up afresh where you stand. It's a toy and a test rig; real caches
  and bandits come from the map later.
* **HUD:** a hit marker (an X round the crosshair) and a note: "plate, 50 m", "target: 9, 15 m".
* **Sounds**, generated like the guns' (D40): breaking glass, a can's tink, a barrel's clunk, a plate's ring
  (inharmonic partials). Metal hits throw sparks.
* `--autotest=range` shoots every target, the 100 m plate with the Mosin. First run: 12 shots, 12 hits.
* `tools/synty_copy.py` copies chosen prefabs and everything they reference out of a pack zip into the git-ignored
  `Assets/Synty/<Pack>/`, with a licence note.

## D43 — The wrist's twist goes into the forearm (Tom's catch)

Tom noticed the left wrist pinched thin in first person. The IK turned each bone the shortest way, so all of the
hand's roll about the forearm sat at the wrist joint, and the skin twisted like a sweet wrapper. Now the hand's
rotation is split into swing and twist about the forearm axis, and 80 % of the twist moves into the forearm bone.
That's what a real forearm does: the radius turns over the ulna along its whole length. The wrist keeps the swing
and a little twist. Checked side by side in screenshots: the forearm keeps its thickness up to the hand.

## D44 — Quiet in the background (Tom's humming GPU)

Tom leaves the game running behind other windows. With the window out of focus, Baltic Drive now caps itself at
10 fps (`Engine.MaxFps`), and restores its own limit when the focus comes back. Self-tests are exempt: they often
run unfocused and must run at full speed. The world keeps simulating; it is only drawn less often.

## D45 — Clearer sights and a proper bolt stroke (Tom: hands cover the screen, the elbow bends wrong)

* **Mosin sight picture:** the rear sight now sits 0.55 m from the eye (was 0.42). On a real Mosin it's well forward;
  at 0.42 the stock, and the gripping hand with it, came to 10 cm from the eye and filled half the view.
* **Pistol:** both hands hold the lower grip (round the magazine), not up by the slide, so the sights stay clear.
* **Hands** are drawn at 85 % (Synty's mittens are big seen this close). The gripping hand's knuckles point slightly
  down along the stock, not up. The thumb rolls over the top of the grip instead of standing up beside the sights.
* **Bolt stroke:** the knob target was wrong: forward of the bolt and too high. The bolt mesh lies behind its node;
  the knob is at its rear (x -0.5, z -1.3, model units). Logged IK points then showed the real fault: in this pose
  the arm points up, forward and in, the elbow hint ("down and back") lay almost along the arm, and the elbow flipped
  up so the forearm crossed the face. The bolt stroke now hints straight down, the hand comes to the knob from
  behind, and the rifle dips 6 cm off the cheek while the hand works.
* **Shoot test:** it levels the view before each gun (recoil carried over and made hits flaky). It logs the right
  arm's shoulder, elbow and wrist in eye space, and screenshots three moments of the bolt stroke.

## D46 — The small survival loop: health, hunger, thirst, and crates by real places

From the brief: "simple health, hunger and thirst, with clear effects but no tedious micromanagement", and "OSM points
of interest help decide what might be found nearby". `Game/OnFoot/Survival.cs`:
* **The body:**
  * Hunger and thirst drain only on foot (a long drive shouldn't starve you): full to empty in 45 and 30 minutes of
    walking, twice as fast when sprinting.
  * Food and water you carry are used by themselves when you drop below 35 %; no eating menu. A medkit (H) heals
    60 over five seconds.
  * Effects: very thirsty or badly hurt, no sprinting; empty stomach or bottle, health drains; fed and watered, you
    heal slowly up to 70.
  * At 0 you black out and come round at 40 (a placeholder until bandits make death matter).
  * `Damage()` is ready for the bandits.
  * You carry up to 4 food, 4 water and 3 medkits; ammo goes into the guns' reserves, up to twice the starting
    reserve.
* **Crates:** every 2 s the points of interest within 150 m are looked at (tile POIs: fuel, food, shop, medical,
  diner, garage, police, hardware, camp).
  * Each place gets a crate or not by a hash of its OSM id: 35 % for shops, food and diners, 60 % for medical,
    police and fuel, 70 % for camps. The same place always has the same luck.
  * The crate is set down on open ground near the place: not in a building, not on a carriageway, on the ground a
    ray finds.
  * What's inside follows the place:
    * shops and food: food and water;
    * diners: food;
    * fuel stations: water, a hot dog, some ammo, sometimes a medkit;
    * pharmacies and clinics: medkits;
    * police: AK and pistol ammo;
    * hardware and garages: rifle ammo.
  * The food is named by the place: kebab, pizza, bread, zapiekanka at diners, beans otherwise. Tom's first wish on
    foot was a kebab.
  * Crates further than 220 m are removed; opened ones stay opened for the session.
  * Synty Battle Royale crates: a small ammo-style box, and a medical case at medical places.
* **Keys:** E opens a crate in reach (2.6 m), else E is the car as before. H uses a medkit.
* **HUD:** health, food and water bars bottom right with what you carry; "E: open the crate (place)" when in reach;
  "Found 2× zapiekanka (Mleczarnia Jerozolimska)".
* `--autotest=loot`: first run found 3 crates within 150 m, opened the nearest (2 food), used carried food when
  hungry, and healed 45 → 100 with a medkit.

Not yet: crates are hard to spot (no map or minimap marks), and the shop kind doesn't tell a beauty salon from a
grocery (all "shop").

## D47 — Bandits

From the brief: human bandits who "patrol or guard, notice the player, shoot from cover when possible, react to hits,
and investigate loud gunfire"; "avoid bullet-sponge enemies, unrealistically perfect enemy aim, and constant
combat"; "sparse, local, generated around the player".

**Shared pieces** (the player's code, made reusable):
* `Ballistics`: every bullet in flight, whoever fired it; characters are now hit too, never the shooter.
* `CharacterRig`: the Synty City character plus the locomotion tree, for the walker and the bandits.
* `ArmIk`: the two-bone arm IK from the first-person arms. Bandits use it in a `SkeletonModifier3D` (`GunHold`), so
  the hands go on the gun after the walking animation has posed the body.

**A bandit** (`Game/OnFoot/Bandits.cs`):
* Looks: a City character in everyday clothes (jacket or coat; not the police one). It carries an AK (50 %), a
  pistol (30 %) or the Mosin (20 %): low across the body on patrol, at the shoulder pointing at you when fighting.
* Senses, four times a second:
  * sight: 70 m, a 120° view, a clear line to your head; awareness builds faster up close and half as fast if you
    crouch;
  * hearing: your shots within 150 m send them to look; a mate who spots you calls in everyone within 60 m.
* Fighting:
  * it runs to cover: a spot within 12 m where a crouching body is hidden from you, best if standing sees over;
  * it crouches, stands, fires a burst (AK 3–5, pistol 2–3, Mosin 1) and crouches again;
  * it moves to new cover every 9–15 s, and after a hit;
  * caught in the open, it pauses 1.5–2.5 s between bursts.
* Its aim is never perfect: the first shot comes 0.6–1.1 s after it sees you. The spread is 0.02 rad, plus range,
  plus your speed, plus a recent hit, and a much wider first burst; it tightens a little as the bandit settles.
* Its bullets do 30 % of what the same gun does to it (an AK hit 10, the Mosin 28). The first try at 50 % with tight
  aim took the player from 100 to 0 in seconds.
* Health 90:
  * the Mosin drops one in a hit;
  * the AK takes three body hits, or one to the head (×2.5);
  * the pistol takes four.
  A hit makes it flinch (no shot for 0.4 s, wider aim).
* Dying: it tips over away from the shot, slumped, the gun falling. The animation keeps running: switching it off,
  or freezing it, dropped the body into its T-pose. Its pockets can be searched (E): rounds for its gun, sometimes
  food, water or a medkit.

**Where they come from** (`BanditField`): only on foot, every 10 s.
* If no group is within 220 m and none was met in the last two minutes, one time in three a group of 2–3 appears
  90–150 m away, on open ground out of your sight, and patrols there.
* Far ones (320 m) and long-dead ones (2 min) are removed.
* B spawns a group 45 m ahead.
* Blacking out (D46) clears them away.

**Getting hit:** the screen edge flashes red, and the survival health bar drops.

**Self-tests leave the mouse free** (Tom works while they run): `Main.CaptureMouse` is off in test runs unless
`--capture-mouse` is given.

**Tests:**
* `--autotest=bandits`: a group 25 m ahead notices you, the test shoots back with the AK, then searches a body.
* `--autotest=banditlook`: one bandit 5 m away, relaxed and then aiming.

**Not yet:** the gun at the cheek (arms can't reach, so it's held at chest height); real cover beside walls (the cover
search is random sampling); hearing their own wounded; districts with different danger; a real death (still the
blackout placeholder).

## D48 — Zero-length road pieces (Tom's dump on Tamka: a pavement across the road)

On Tamka at the Dobra junction a pavement slab ran diagonally across the carriageway. The cause was a road piece of
zero length: the road was cut where it enters the junction surface, and the cut left a duplicate end point. Its
direction was taken as due north, which bent the corner (miter) at the end of the previous piece. The zero-length
piece itself then drew a pavement quad between two different directions: the diagonal slab. The pavement overlap
check (D27) didn't stop it either, because it treats a zero-length piece as never blocked.
* A zero-length piece now borrows the direction of its nearest real neighbour, so corners next to it stay true.
* Zero-length pieces aren't drawn, unless they carry the road's end cap.
* The F9 dump now lists the pavement pieces within 25 m: which sides were kept or blocked, and the free space beside
  the kerb along each. `--views=... --dump` writes a dump at each view from a self-test.
Fixed in the game, so existing map packs benefit. The importer could also drop duplicate points at the next rebuild.

## D49 — The gun in your hands in third person (Tom: "carrying a gun and not seeing it looks weird")

With a gun out, the walker now holds it in third person too. These are the bandits' parts (D47), shared:
* `GunModel.Make`: the gun's own model.
* `GunHold.Goals`: where the hands go on it.
* `GunHold`: the modifier that puts the hands there after the walking animation.

How it's held:
* at the ready, across the chest and pointing forward and down;
* lower while sprinting;
* brought up to the shoulder toward where the camera looks for half a second after each shot (`Weapons.Firing`);
* aiming still switches to first person, as the brief says.

With a gun out the body faces where you look and strafes, using the strafe and back clips, instead of turning to
run wherever you go. Without a gun it turns to its direction of travel as before.

Also fixed: since the change that leaves the mouse free in test runs (D47), V, Esc and H did nothing on foot. An
`else` attached to the wrong `if` because of a missing pair of braces. The walk test now presses V and H through
Godot's input, as a player would.

`--autotest=tpgun`: each gun in third person (standing, walking, sprinting, firing), seen from the side.

## D50 — Polish: crates on the radar, a real death, rifles at the cheek

* **Radar:** while on foot, the radar marks nearby crates (an amber box; white with a red cross at medical places)
  and bodies to search (a small cross), grey once opened. Bandits are not shown: spotting them is the player's job.
  `Minimap.Markers` is a list of world points with a kind; Main feeds it from the crate field.
* **Death** replaces the blackout placeholder (D46). Going down (shot, starved or dehydrated):
  * the screen darkens over 1.6 s;
  * you come round beside your car at 60 health, with hunger and thirst at least 40;
  * the pack is gone (food, water, medkits) and half the spare rounds of every gun, but the guns and what's in
    their magazines stay;
  * the bandits move on (are removed), and a note says what happened and what was lost.
  Damage is ignored while down.
* **Bandits' rifles at the cheek:** a bandit's eye is now the head bone's position as animated. It used to be a fixed
  guess (1.6 m standing, 1.05 m crouched) switched the moment it decided to crouch, which held the gun at chest
  height while the body still stood. Logged: the gun at 0.9 m with the head at 1.57 m; now 1.45 m. With the muzzle at
  the face they also clear cover better: the fight test ended at 43 hp instead of 100. To be felt out in play.
* `--autotest=death`: the crates on the radar, then down, dark, and awake by the car with the pack gone.

## D51 — Tom's spin: free-look car camera, crosshair, more bandits, and the dumps (ids, parts, a wall on a bridge)

* **Free look in the car:** the mouse is captured while driving and turns the chase camera all the way round the car
  and up and down; no button to hold. The camera stays above the ground: it's clamped and kept 0.8 m over the
  terrain. Left alone at speed for 1.5 s it drifts back behind the car. Esc frees the mouse, a click takes it back.
  The chase camera now sits on a sphere round the car, following the car's smoothed heading, and looks at the car.
  In the cockpit the mouse looks around too (±0.6 rad up and down). Camera modes as an option: later.
* **Crosshair in the car:** getting in turned the walker's processing off before its gun HUD could hide; the walker
  now hides the gun HUD itself on the way out.
* **Bandits:** a group may come every 8 s (one in two), 60 s after the last fight, while fewer than two are about:
  at most four near you at once. No hordes.
* **OSM ids and addresses on the wrong buildings:** the importer writes an id and an address only for each tile's own
  buildings, but the game handed them out in record order, ghost copies of neighbours' buildings included. So after
  the first ghost every id and address shifted onto the wrong building: the HUD's street and number, the F9 dump's
  ids, and the shop matching. Fixed in the game: ghosts are skipped.
* **Multi-part buildings** (Tom's "buildingception" and floating towers):
  * parts that touch now share one colour seed; each had its own, so one building read as two, an orange one inside
    a white one;
  * a part floating up to 4.5 m over the part under it is lowered onto it (OSM's storey numbering differs between
    parts);
  * in the importer, an outline is dropped in favour of its parts only if a part inside it reaches the ground.
    Otherwise it's the base the parts stand on: the Warsaw Financial Centre's towers start at level 6 on top of its
    outline, which is tagged `building=office`. This needs a map rebuild to show.
* **A wall across the Poniatowski bridge approach:** a concrete wall mapped on a bridge crossed its carriageway.
  Barriers on bridges skipped the rule that cuts barriers where they cross a road. They're now kept only off the
  deck's carriageway (an edge distance over -0.3 m).
* **The F9 dump** lists each building nearby (OSM id, part or not, height, min height, roof, who draws it) and, for
  barriers within 12 m, their points with the road-edge distance there.
