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
    outline, which is tagged `building=office`. Rebuilt the same night (53 minutes): the towers stand on their base.
* **A wall across the Poniatowski bridge approach:** a concrete wall mapped on a bridge crossed its carriageway.
  Barriers on bridges skipped the rule that cuts barriers where they cross a road. They're now kept only off the
  deck's carriageway (an edge distance over -0.3 m).
* **The F9 dump** lists each building nearby (OSM id, part or not, height, min height, roof, who draws it) and, for
  barriers within 12 m, their points with the road-edge distance there.

## D52 — Bandits and the car, a real crouch, pavements on squares (Tom's next spin)

* **Bandits and the car:** they used to know only the walker, so driving made you invisible.
  * They now chase a target: the walker on foot, or the car while you drive (`BanditField.Current`). A car is
    noticed 1.6× sooner, and a fast one is harder to hit.
  * While you drive, groups appear 140–200 m ahead along the way you're going, off the road (on foot: 90–150 m away,
    out of sight). The same cap applies: at most four near you.
  * The car is now an `IHittable`: a bullet in it while you drive hurts the driver for a fifth of its damage. At a
    half, three bandits took a parked driver from 100 to 8 in ten seconds; now it's about 35, enough to drive off.
  * The health, food and water bars show in the car too.
  * Going down at the wheel puts you beside the car (D50).
  * The walker exists from the start, since it carries your health even if you never get out.
* **Crouch** (Tom: "no animation, it feels like I'm not crouching"): the crouch blend was a 2D blend space whose
  three points (still, forward, back) lie on one line. A 2D blend space needs triangles, so it played nothing and the
  body fell into its T-pose; a crouch test from the side showed it. It's now a 1D blend on forward speed. Synty's
  crouch clips bend the legs but leave the hips up, so the body hovered: `CharacterRig.GroundFeet` lowers the model
  until the lower toe sits where it does standing, weighted by the crouch. Bandits use it too.
* **Pavements:**
  * One is left out where the ground just behind it is a paved area, a square, or paved land cover (2 of 3 samples):
    the paving already does the job there.
  * Street corners don't grow kerbed slabs from roads laid on a square, which draw no pavements of their own.
  * A junction whose streets all lie on a square is drawn under the square, as they are, not as a dark asphalt patch
    on top (Plac Grzybowski).
* **Still open:** one strip on Świętokrzyska that isn't a road's pavement (all of those there are now left out); most
  likely a separately mapped piece of pavement area. To look at.
* The F9 dump says what lies behind each pavement (land cover, paved area or square, the nearest road).
* Tests: `--autotest=crouch` and `--autotest=carbandits`.

## D53 — Sound: the space you're in, shots over distance, the car, the world

The brief: "sound matters enormously: powerful shots, mechanical clacks, distant reports, and different acoustic
character between apartment courtyards, streets and forests"; generated placeholders are fine, but the layers must
be replaceable. `Game/Audio/`:
* **Sound bank:** every sound by name, generated in code unless a recording with that name is in
  `Assets/Sounds/` (.ogg or .wav). The names are in that folder's README.
* **Buses:** SFX (through a reverb), Ambience, Engine.
* **Acoustics:** five times a second, 16 rays out at ear height (to 90 m) and one up. From how many hit, how far,
  and whether there's sky, the place is a courtyard, a street, the woods (forest land cover), open ground, or covered.
  That sets the reverb (room size, wetness, damping, pre-delay from the nearest wall), eased so a corner doesn't
  jump. The walls the rays found are kept for echoes.
* **Gunshots** (`GunSound`):
  * Sound arrives d / 343 s after the shot.
  * Under 70 m: the full crack and thump, placed at the gun. Beyond 40 m: a far boom (no crack) audible for
    kilometres, with Godot's attenuation filter muffling the highs.
  * Echoes: up to five walls round the listener each send the shot back from the wall itself, late by the
    shot→wall→ear path.
  * In the open (few walls, sky): a long roll instead.
  * Your own shots and bandits' shots use the same path.
* **The car** (`EngineSound`): an engine made live (`AudioStreamGenerator`):
  * revs from a pretend five-speed gearbox (up at 5,200, down at 1,900, idling at 800), since the physics has no
    engine;
  * firing frequency (four cylinders, two firings a revolution) plus harmonics and a little per-firing roughness,
    louder and brighter with the throttle;
  * a tyre hiss loop that rises with speed, and wind with the square of speed.
  Measured from a recording: 3,288 rpm gave 118 Hz (110 Hz expected), 4,788 rpm gave 164 Hz (160).
* **Ambience:** city rumble by buildings and roads (quieter at night), wind that grows in the open and up high,
  birds by day (more at dawn) in parks, woods, fields and gardens, each phrase from a spot 10–40 m away in the trees.
* **Testing sound without ears:** `--autotest=sound` records the master bus to `sound.wav` (a shot by buildings, then
  a drive) and logs when the shot and each echo should land, the revs and gears, and the places found. In the first
  recording the shot peaks at 3.51 s and echoes rise at 3.69, 3.82 and 3.90 s (due 3.68, 3.83, 3.97). Echoes were
  then raised 4 dB and the idle made quieter.
* **Test runs are muted** (master at -80 dB; the recording still hears it), so they don't fire guns through Tom's
  speakers while he works; `--sound` turns it back on.
* **After Tom listened** ("my shots sound like a gunshot from afar; near buildings it's like a small room with an
  echo"):
  * your own shot now plays dry, without the reverb, at full strength;
  * near shots get a snap: 6 ms of bright, unfiltered N-wave before the body. The generated body low-passes its
    crack, which is what made it sound distant. The first 8 ms are now 6 dB sharper;
  * outdoor reverb is down to almost nothing (street 0.04, courtyard 0.07, open 0.015 wet): outdoors the space is
    heard in the separate echoes, and dense reverb is what rooms sound like. Only under a roof does it ring (0.2);
  * echoes are brighter and shorter, a slap rather than a thud;
  * the roll in the open is 8 dB quieter.
* **Recordings:** Tom's Mosin shot (made with Suno, published with his OK) replaces the generated one, trimmed to start
  2 ms before the bang. Every far version, and the AK and pistol shots (no recordings of those yet), are derived from
  it by `tools/derive_shots.py`:
  * AK: pitched up 12 %, the low end thinned, cut at 0.6 s;
  * pistol: pitched up 35 %, much less low end, cut at 0.45 s;
  * far: mono, low-passed at ~500 Hz, a longer fade.
  A real gunshot's crack and body beat the generated ones even when borrowed. Measured: Mosin brightness 3,240 Hz,
  AK 3,380 Hz, pistol 3,890 Hz.

## D54 — A reason to roam: leads to guarded stashes (Tom's pick)

Asked what should pull you across the map, Tom picked **leads** (over dangerous districts, a get-out-of-town run and
scouting by place type), and for the stash, **guarded and rich**.

* **Where a lead comes from.** The first crate you open always holds one, so the hook appears early. After that, 20 %
  of crates have one. A dead bandit's radio has one 15 % of the time, and a taken stash points on 70 % of the time.
  At most three open leads at once.
* **What it names.** A real OSM point of interest 1.2–4 km away, of a kind with stock and room round the back:
  petrol station, police, garage, hardware shop, pharmacy, shop or camp. Named places come first, and the nearest
  named street within 80 m is added: "a scrap of paper in the crate: Stash at Polmed on Twarda. The Praga boys are
  sitting on it." The gang name comes from a short invented list. The search runs on a worker thread and loads
  only the tile at a random point at that distance (up to 10 tries), never a ring of 25 tiles.
* **On the map.** The map screen shows a red diamond with "stash: …"; a taken stash turns grey. The radar shows the
  diamond too, pinned to the rim while it's out of range, so you can drive or walk towards it without the map.
* **Arriving (180 m, on foot or driving).** The stash is set down outside the place: a big crate with a small one
  on top and a medical crate beside it. It goes on a patch clear of walls, fences and props within 1.6 m (the first
  try landed in a fenced tree pit). A gang of 3–5 camps round it within ~10 m: one Mosin watching, the rest mostly
  AKs. They're ordinary bandits with `Guarding` set, so they patrol round the stash. Their count is kept on the
  lead: the ones you shoot stay dead. If you walk off (300 m, under the bandits' own 320 m cull), the stash and the
  surviving guards go back to being a promise and come back when you return. If they're cleared away without being
  shot (you went down), they're back.
* **What's in it.** 45–75 AK rounds, 10–20 Mosin, 16–32 pistol, 1–2 medkits, 1–3 food and water. Worth the fight.
* **Messages.** A lead found in a crate is added under the "Found …" note instead of replacing it (ShowNote
  `keep`), and lead notes stay up for 12 s.
* Per session, like opened crates; nothing far away is simulated.

Test: `--autotest=leads`. It finds a lead 300–600 m away and photographs the map, puts you 150 m from it (stash
and guards appear), shoots the guards, opens the stash and photographs the chained lead on the map.
Not done: saving leads between sessions.

## D55 — Leaning, dying where you fall, waking elsewhere, a safehouse (Tom's next spin)

Tom played the leads, liked them, and walked round a corner into gunfire. He asked for leaning, then a proper death
in the spirit of Project Zomboid. His four picks: Q/E lean with interact moved to F; no safehouse means a random home
1.5–3 km away; a safehouse is any home whose wall you hold a key at; the package holds your pack and spare ammo,
and your guns stay with you.

* **Keys.** Q/E lean. F is now interact (crates, bodies, stash, your things, getting in and out of the car). The free
  camera moved from F to F8.
* **Leaning.** The eye moves 0.34 m sideways and a little down, and the view rolls about 10°. A ray at head height
  stops the lean before the eye enters a wall. There's no leaning at a sprint. The body bends Spine, Chest and Neck
  (`LeanBendModifier`, run before `GunHold` so the hands stay on the gun). In third person, leaning left swaps the
  camera to the left shoulder. A head sphere follows the leaned eye, so a bandit who sees your head can hit it:
  peeking round a corner shows only a little of you, not nothing.
* **Dying.** The body tips from the feet away from the last bullet (forward if you starved) in 0.42 s, and the
  first-person eye goes down with the head, looking wherever it looks. No controls; bandits stop counting you as a
  target. The screen dims red and says what happened. A click or F wakes you.
* **Waking.** In your safehouse if you have one; otherwise outside a real house or block of flats (OSM
  `building=house/apartments`) picked at random 1.5–3 km away, on open ground 1.4 m off the middle of a wall,
  facing away from it (`Homes.Pick` / `Homes.Step`). Full health, hunger and thirst at least 60. The note gives
  the address, and how far away and in which direction your things are. Not by the car any more.
* **Your things.** Food, water, medkits and every spare round go into a package where you fell
  (`CacheField.PlacePackage`), with a copy of your body lying in the pose it fell in. It's kept however far you go,
  shown on the map as a white box ("your things") and held at the radar's rim. Loaded magazines and the guns stay
  with you. Once taken, the body goes when you're far away, like a bandit's. An empty pack leaves no marker. Your
  own package never holds a lead note.
* **Safehouse.** Stand by the wall of a home (within 2.5 m, not inside) and hold K for 2 s
  ("Hold K: make Sienna 41 your safehouse"). It's marked with a green house on the map and the radar. One per map;
  claiming another moves it. It's kept in `user://safehouses.json`
  (`{"safehouses":[{"x","z","yaw","osmId","address","map"}]}`), as data rather than code, so the world editor Tom
  wants can later place safehouse areas in the same shape. Unlike the session's crates and leads, it lasts between
  sessions.
* **A fix found on the way:** every body fell toward the shooter. The fall axis was away × up; up × away tips the head
  away. Bandits are fixed too.

Tests: `--autotest=lean` (first person upright, left and right; third person from front and back) and
`--autotest=death` (the fall, waking 2–3 km away, the map, taking your things back, claiming Sienna 41, dying again,
waking 0.0 m from its door). Next, Tom's idea: ragdolls instead of the tipping fall.

## D56 — Ragdolls (Tom: "on death, ragdolls")

* **The body.** Every character (the walker and each bandit) gets a ragdoll built with it, asleep (`Ragdoll.Build`).
  It has 14 capsules on the main bones (hips, spine, chest, a sphere for the head, upper and lower arms, upper and
  lower legs, feet) under a `PhysicalBoneSimulator3D`. That simulator is the skeleton's last modifier, so once it
  runs it overrides the animation, the lean bend and the hands on the gun. The joints are cones with limits (spine 20°,
  hips 45°, shoulders 70°, knees and elbows 60°). Neighbours and the trunk pieces don't collide with each other.
  About 70 kg in all, a little damping so it doesn't wobble like jelly. Layer `Bodies.Ragdoll` (64), colliding with
  ground, roads, buildings, props and vehicles.
* **Dying.** The bodies take the pose the animation was in and fall. The bullet pushes harder than its real momentum
  (45 N·s on the bone nearest the hit, half as much on the chest): 8 g at 800 m/s barely moves 70 kg, and a hit
  should read. Without the licensed pack (no skeleton), the old tip-over stays.
* **You.** Your own ragdoll falls and the first-person eye rides the head body: the view goes wherever the head
  goes, ending on the sky or the pavement. The body left behind copies the ragdoll's pose bone by bone, taken from the
  bodies (the skeleton's own pose is still the animation's), and is frozen with its animation off. The package sits
  at the hips, not the feet.
* **Floating origin.** Simulated bodies live in world space, and moving their parent doesn't move them. On an origin
  shift, each body's transform is moved on the physics server. Tested: the origin jumped 3.5 km away and back,
  and the body stayed within 0.00 m.
* **Cost.** 54–60 fps with four ragdolls built and one lying there, the same as before. (The test's own screenshots
  dragged the fps counter down at first.)
* Not yet: knees and elbows are cones, not hinges, so they can bend a little sideways. The bandit's gun still drops
  by the old animation, not as a physics object.

Test: `--autotest=ragdoll`. A bandit is shot and photographed from the side at 0.15, 0.4, 1 and 4 s, and checked for
lying on the ground (hips 0.16 m above it). The origin jumps away and back. Then the walker is shot, the fall seen
through its eyes, and the body left behind is checked lying (hips 0.15 m, head 0.18 m).

## D57 — Road joins, car parks, water stripes, pavements over driveways (Tom's 16 dumps, 17 Nov)

Tom found the wobbly service roads "not that bad" (the kerbs helped) and dumped what does bother him: roads joining
badly (flicker, stray patches, wedges), stripes on the water, gaps in pavements and pavements ending abruptly.

* **Minor roads give way.** Driveways and car-park lanes are deliberately left out of junction surfaces (importer),
  and roads crossing without a shared node aren't joined either. Their ribbons just overlapped, each at its own
  solved height: the higher one showed through, a service road's sloping dirt shoulder stood up through a street as
  a wedge, and near-equal heights flickered. Now, in the mesher (no map rebuild), a road point that lies on a greater
  road's carriageway is tucked 2.5 cm under that surface and drops its shoulders and cap skirt there. "Greater" means
  class rank first, then width, then a fixed order by start point (both sides must agree). A piece crossing with both
  ends outside sinks by at most 4 cm, decided at its middle. Pieces of the same road meeting end to end and lined up
  (tile edges, tapers) are its continuation and are skipped. The context's segments are bucketed in a 16 m grid once
  per chunk, so it stays cheap (fly test: median 16.7 ms as before, no frames without ground).
* **Tight bends.** A bend tighter than the road is wide (290 within 3 km of the centre, mostly driveways; found by the
  new `--autotest=roadscan`) folded its inner shoulder edge back over the road as a thin fin. The corner stretch is
  capped so the edge of the shoulder moves along each piece by at most 90 % of the shorter one.
* **Paved ground** (land cover "paved": car parks, yards, industrial ground) was painted with the bare-earth mask,
  so every car park was brown dirt with asphalt islands. It now has its own mask (terrain UV2.x) and is old, patched
  asphalt a shade lighter than the roads. Road verges there aren't gravel: unmarked roads' and paved areas'
  shoulders render as asphalt, and the terrain paints no gravel verge.
* **Water stripes.** These were not the float precision suspected first. A flat-coloured water sheet has no lines,
  so the mesh is fine; it's the shading. The ripple normals were built from value noise, whose slope is exactly zero
  along every lattice line, so the normals went flat on a ~1.1 m world-aligned grid and the mirror-sharp sun
  highlight drew each line as a thin bright stripe. Rotating the layers only turned the stripes (which proved the
  cause). The ripples and chop now use gradient noise (a real slope everywhere), each layer on its own turned grid.
  The precision change stays as well: the ripple coordinates are x/z wrapped at 4096 m per pixel, from the precise
  local position plus the origin modulo 4096 (exact), so nothing smears across a triangle. That leaves a seam every
  4 km, lost in the ripples, and the long Gerstner waves keep the old coordinates.
* **Pavements over driveways.** A driveway or track (≤ 6 m wide) crossing a pavement used to remove that pavement
  piece, leaving a gap with two blunt ends. Now the pavement carries on across it with a lowered 3 cm kerb, as in
  town. One running along the street (a car-park aisle) still takes the pavement's place.
* **Pavement ends.** Where a pavement really stops (the next piece is blocked, or the road ends without a junction
  and not at a tile edge), its kerb slopes down to 3 cm over the last piece instead of ending like a block. A piece
  with nothing on either side lies flat. Piece states are worked out once per chunk and cached.

Checked against all 16 of Tom's dump views, re-shot with the new build (the before and after pairs), a fly test and a
drive test (3.5 km in 200 s, 3.5 s off the road).

Addendum (17 Nov, the leftovers):
* **Holes between split junctions** (Bogatki): a crossing whose nodes are too far apart to cluster becomes two
  junction surfaces, and the small gap between them, framed by the short connector roads, showed grass with a gravel
  ring in the middle of the carriageway. Where two junction surfaces come within 7 m and a connector cut back at both
  ends runs between them, the convex hull of their facing outline parts (each within 14 m of the other) is paved at
  their own heights, a hair under the junctions. Only a hull under 400 m² with no building in it
  (`AddGapFills`).
* **Service roads ending inside junction surfaces** go under them like under a greater road (paved areas and squares
  already lie under every road).
* **A road ending on another road** gives way to the one that carries on, whatever their classes and widths, and its
  end gets no rounded cap and no shoulders there.
* **Wyczółki** (the "raised patch") turned out to be the mapped shape: a 7.6 m two-lane entrance narrowing into a 4 m
  loop. The jagged overlap at its end is gone, but the narrowing still reads as a dark dome from above. Left as it is.
Fly test after: median 16.7 ms, no frames without ground.

## D58 — Street furniture: zebra crossings, traffic lights, tram wires (Tom's side-by-side comparisons)

* **Import (map rebuild).** Two new point kinds: 5 is a marked pedestrian crossing (`highway=crossing` with
  `crossing=zebra/marked/uncontrolled/traffic_signals`, `crossing_ref=zebra` or any `crossing:markings` but `no`), and
  6 is traffic signals (`highway=traffic_signals`). Mazowsze has 55,139 crossings and 5,514 signals. No format change:
  they ride in the existing point records, and older builds skip unknown kinds.
* **Zebras.** Painted the Polish way: white bars 0.5 m wide and 4 m long, along the road, 0.5 m apart, across the
  whole carriageway, at the crossing node on the nearest drivable road (on a junction surface they follow its height).
  With signals within 25 m, a stop line 2 m before the zebra on each approach (the right-hand half of a two-way road).
* **Traffic lights.** For every drivable road within 20 m of a signal node, a light on the right of each approach, 6 m
  short of the node: a 3.4 m grey pole and a black head with three lamps facing the traffic. A light isn't placed
  within 4 m of another, in a building or on another carriageway. The lamps cycle every 30 s (green 12, amber 3,
  red 15), with the two crossing directions out of step (barrier shader style 0.9, emissive).
* **Tram wires.** A contact wire 5.6 m above every tram track, and a steel pole every 35 m of track (by distance
  along it, so chunks agree) 3.2 m to the left side, or the right where the left is a track, road or building, with
  a span wire. These need no rebuild.
* All near chunks only (LOD ≤ 1). Fly test: median 16.7 ms as before.
Not yet: signs (OSM `traffic_sign` is sparse here), traffic lights that drivers or bandits obey, lights at night
looking right from far away.

## D59 — Grass blades (Tom: "a game changer"; his worry: performance)

* **What.** Blades of grass near the camera (`Trees/GrassField.cs`, `Shaders/Grass.gdshader`, after Thornwake's
  Grass.gdshader). One clump mesh (14 bent blades, 42 triangles) drawn as a MultiMesh per 24 m cell, built on worker
  threads. Clumps are 0.42 m apart at full density, within 60 m of the camera, no shadows. There's no grass when the
  camera is more than 25 m above the ground.
* **Where.** By land cover: meadow and wetland full, park 0.85, open, scrub and orchard 0.8, cemetery 0.6,
  residential (gardens) 0.5, forest 0.3, farmyard 0.25, sand a trace; none on fields (their crops and stubble are
  the terrain's), paving or water, in buildings, or wherever the road shoulder mask is over 0.3 (roads, verges,
  tracks). Heights from the same map context as the terrain mesh.
* **Look.** The terrain's grass colours at the root, lighter and yellower at the tip, and drier clumps paler in thin
  grass. Normals lean up so blades take the ground's light. Light through the blades against the sun, and the
  wind's lean with travelling gust waves. The far third of the range thins out clump by clump (each clump's own
  cut-off distance), so there's no edge.
* **Cost** (`--autotest=grassbench`: the grassiest spot within 2 km, eye height, a full turn, measured GPU time):
  80,708 clumps at a park in Młynów, 13.85 ms per frame with grass and 12.75 without, so about 1.1 ms on Tom's
  RTX 3080. Fly test after limiting uploads to two cells a frame and skipping grass above 25 m: 7 frames over 33 ms,
  the same as without grass. Drive test unchanged.
* G toggles it, and `--grass=0` starts without it.
Not yet: grass bending around the walker or car, flowers, seasons and rain.

## D60 — Colour variety of the ground (Tom: "hue variety")

`ground_hue()` in common.gdshaderinc multiplies the living ground's colour by patches tens to hundreds of metres
across: drier olive and straw (~110 m and ~45 m), lusher blue-green (~75 m and ~25 m), now and then browner and barer
(~170 m), on a mottle a few metres across. The terrain applies it fully to grass, at 0.7 to crops and at 0.6 to forest
floor. The grass blades use the same function at their root, so blades and ground agree. The first, timid version
was hardly visible next to "off". Tom saw the stronger one side by side and kept it ("leave it as it is").
Strength: the `terrain_hue` global (1 by default), `--hue=0` for off.

## D61 — Far buildings: the city to the horizon

Ordinary buildings ended at 1 km (the content streamer's radius) and only towers over 42 m went further (the
skyline, ~4 km), so past 1 km the city was fields with towers in them. `Map/MapFarBuildings.cs` draws every building
for whole map tiles out to 4 tiles (~8–9 km):
* beyond 905 m only (building shader `near_cut`; the near cells cover at least 909 m everywhere), drawn 1 % smaller
  about each centre and 15 cm lower, so where both layers overlap (to ~1090 m) the near copy hides the far one;
* outlines over 12 corners without courtyards simplified (Douglas–Peucker, 1.5 m); standing on the building's stored
  base height (no ground queries), so a whole tile builds quickly on a worker; no collision, no shadows;
* under 30 m² left out nearby and under 120 m² from the third tile out; towers included for tiles beyond the
  skyline's radius;
* each tile uploaded as 4 × 4 pieces, one per frame (a whole tile at once was a 50–60 ms hitch).
About 96,000–114,000 far buildings around Warsaw. Cost (`--autotest=farbench`, 90 m up, a full turn): 11.9 vs
11.6 ms GPU over the centre. Fly test: 19 frames over 33 ms (clean baseline 34). `--far=0` starts without it.
(An earlier fly run at 66.7 ms and a 31 ms benchmark were taken while Tom was playing the game at the same time,
sharing the GPU.)
The dump also lists courtyard rings and building parts now. Tom's hole in the Senatorska 29/31 church and monastery
complex is its mapped courtyards; the complex is one flat 10 m block in the data, with no nave.

## D62 — Bandits find a way round (Sage's pick while Tom was out)

Bandits walked straight at their goal and piled up against walls; a stuck one picked another spot after 1.5 s.
Now (`OnFoot/BanditNav.cs`), when the straight walk is blocked (a knee-height ray hits a wall, fence or car) or the
bandit is stuck, a route is searched on a worker thread. It runs on a 1 m grid around the bandit and its goal (both
with 30 m around, at most 220 m across; a farther goal is aimed at through the window's edge), built from the map,
not the physics world: buildings with 0.5 m to spare, water, and fences, walls and hedges, with their gates left
open. A* with diagonal steps (no squeezing past corners), at most 40,000 cells, then pulled taut into straight legs
(a fat line-of-sight check, so it doesn't shave corners). The bandit walks leg by leg, and replans if the goal moves
more than 3 m or it's stuck for 1.2 s; at most one request a second. Waypoints are world metres, so the floating
origin can move. `--nav=0` turns it off.

Test (`--autotest=banditpath`): a bandit on the far side of a 27 × 13 m building near the start, told where you are.
Routes off: stuck at the wall, 21.7 m away for 30 s. Routes on: a five-leg way round, engaging you after 7.6 s. The
bandit and leads tests pass as before.
Not yet: bandits climbing over low walls, using doors (no interiors), or going round each other.

## D63 — Factories and warehouses (BadBuilding D17)

BadBuilding's new industrial style (D17 there: brick, concrete and metal eras, loading docks, saw-tooth roofs,
chimneys; Tom: "mixed by age") reaches the game through the existing adapter. The building shader gets three surface
patterns on blank walls, read from the vertex colour's kind:
* 0.72 ribbed metal: ribs every 25 cm, a lit crest and a shaded trough, fading to an even tone beyond 90 m, with dirt
  splashed up from the ground;
* 0.78 brick: 25 × 7.5 cm courses with mortar lines and a little colour per brick, even beyond 60 m;
* 0.6 roller door: horizontal slats every 10 cm.
The adapter maps the roles `cladding`, `brick`/`chimney` and `roller` to them. Brick was a bright tomato red in the
game's light, then still orange (Tom: "too orange"): the palette is now dark brown-reds, and the shader takes 45 % of
the saturation out and darkens it 15 %, so it reads as weathered factory brick even in full sun. The map doesn't carry start years yet, so in the game the era
comes from size and seed (BadBuilding's fallback). Passing `start_date` through the importer would need a format
change and a rebuild: a later step.
`--autotest=roadscan --find=industrial` lists industrial buildings of three sizes to look at.
Fly test with BadBuilding on: 3 frames over 33 ms, 3,685 BadBuilding buildings at 6.7 ms and 2,587 triangles each.

## D64 — Weather drives the mood (Tom: grey November and golden evening, "both")

Tom saw no real difference between colour grades side by side (60 % strength, small nudges); four full moods (time,
clouds, haze, saturation and grade together) were clearly different, and he wanted both grey November and golden
evening. So the look is no longer one fixed grade (Fantasy at 0.6, saturation 1.08):
* **Weather drifts:** cloud cover follows two slow waves (31 and 47 minutes of play, carried by clock jumps and a
  running clock), 0.08–0.98, so a session passes through clear spells and grey fronts. `--clouds=` still fixes it.
* **Overcast** (cover 0.62 → 0.92) dims the sun to 28 %, lifts the shade by 35 % and greys it, and **golden** (sun
  under 22°, above the horizon, not overcast) marks the low warm light.
* **Grade:** a lookup table blended from three presets by those weights: the new Baltic (cool, honest, olive greens)
  under cloud, Rustic in a golden light, Cinematic in clear daylight; at 0.7 strength, rebuilt only when the blend
  moves by 0.08. **Saturation** 1.05 clear, 1.08 golden, 0.82 overcast. **Haze** up to 2.3× under cloud and up to
  3.2× in the early morning (mist, 4:30–10:30).
* The cloud-shadow texture is rebuilt in steps of 0.03 cover now that cover moves all the time.
* `--mood=0` restores the old fixed look; `--grade`, `--sat` and `--haze` still override their part.
Fly test: 4 frames over 33 ms, as before.

## D65 — Bullet holes stay on the car; holding the breath (Tom's dump and idea)

* **Bullet holes** were added to the scene root at the hit point: holes in the car stayed hanging in the air along the
  road where it had been (Tom's dump: black squares floating over the street), and holes anywhere drifted when the
  floating origin moved. Now each hole is a child of what it hit (the car, a building chunk), so it travels with it
  and is freed with it. Test: a hole stays 0.00 m from its spot on the car when the car moves 10 m.
* **Holding the breath** (Tom: "half breath… a little extra zoom for a short period, then the weapon sways for a
  while, like catching my breath"). Aiming now has a gentle drift that really moves the aim: the eye turns with it.
  It's a slow figure of eight, 1.6 mrad standing still (16 cm at 100 m), more on the move, about half crouched.
  Shift while aiming holds the breath (Shift no longer sprints while aiming): the drift falls to a quarter, the view
  zooms 18 % more, and the breath lasts 4 s. Letting go, or running out, starts catching breath for 0.8–3.4 s (longer
  the more was used): the drift grows up to 6–7× and eases off, and no holding meanwhile. Breath refills over 4 s.
  The HUD shows "Shift: hold breath", a breath bar while holding, and "catching breath...".
* Test (`--autotest=breath`): sway 1.64 mrad aiming, 0.41 holding (field of view 55° → 45°), 10.8 catching breath;
  the hold ends by itself after 4 s. The range test now holds its breath for the plates, as a player would: 11 of 11
  hits (without holding, the 50 and 100 m plates were missed).

## D66 — Muzzle light; undersides of raised building parts (Tom)

* **Muzzle light** (Tom: "when shooting, there should be some light"). There was one, but it reached 6 m for 45 ms
  (under three frames), and it hung on the first-person gun, which is hidden in third person, so there it never
  showed. Now it's its own light in the world: 16 m reach, a burst of 14 fading over 80 ms (the Mosin 30 % brighter,
  the pistol 30 % dimmer), at the muzzle in first person or just in front of the body in third. Bandits' flashes
  reach 14 m (were 5) and fade the same way, so at night you see where they shoot from. The shoot test now photographs
  the first frame after each shot (`shoot_<gun>_flash`): at 2 a.m. the arms, road and car are lit warm orange.
* **Parts that start in the air** (`min_height`: overhangs, bridges between towers, Złota 44's upper wing from 44 m)
  had walls and a roof but no underside, so you looked up into them (Tom's dump). They now get a closed soffit,
  a little darker than the walls.
* Player bug reports for the friends release are noted in the handoff (F9 with a description box, and an easy way to
  send dumps).

## D67 — Car parts and damage, step 1 of the workshop (Tom: Zomboid depth, workshop-game feel)

Tom's picks: a workshop view, parts from wrecks, clear and readable effects. The full plan is in
`docs/CAR_WORKSHOP.md`; this is step 1, parts and damage.
* **Parts** (`Vehicle/CarParts.cs`): engine, battery, 4 tyres, front and rear brakes and suspension, windshield, 2
  windows, 2 doors, hood, trunk, driver's seat; condition 0–100, or missing (for the workshop).
* **Bullets** wear the part at the hit point in body space: glass above the waistline (windshield in front, windows
  at the sides, the trunk at the back), tyres near the wheels, hood at the nose, trunk at the tail, doors at the
  sides. Glass takes 1.4× the bullet's damage, tyres 1.6×, panels 0.5×; through a broken hood into the engine or
  battery. Parts take hits when parked too. What reaches the driver now depends on the part: 0.2 of the bullet
  through intact glass and panels (D52), up to 0.45 through a broken window and door, 0.05 through a tyre, hood or
  trunk.
* **Crashes:** a change of velocity over 5 m/s in one physics step wears the end that hit; landings over 7 m/s wear
  the suspension. Teleports (Recover, Warp, origin shifts) are recognised by a jump further than the car could have
  moved, and skipped.
* **Effects:** power 25–100 % (coughs below 35 %, dead at 0; a dead engine is silent); battery under 8 %: won't start,
  dim lights; tyre grip 80–100 %, a flat: 45 % grip, on its rim (radius 82 %), pulling the car to its side; brakes
  25–100 %; suspension stiffness, damping and ride height down to 45 %, 35 % and 70 %; smoke from under the hood below
  35 % (black at 0).
* **HUD:** parts under 70 % while driving; a note when one breaks or the engine won't start.
Test (`--autotest=carparts`, always from the same start): 0–60 km/h in 3.4 s with a good engine and 7.2 s at 30 %;
braking from 60 in 11.9 m and 32.5 m with brakes at 10 %; 6 s straight with no steering: dead straight, or 7.8 m to
the left with a flat front-left tyre (which then runs into the roadside, a real crash); bullets land on the hood,
windshield and tyre. The drive test (3.75 km of city) wears nothing.

## D68 — The workshop view and ghost highlights (step 2 of the workshop; Tom: "hold F at the car")

* **Hold F** by the car (on foot, within 4.5 m) for 0.45 s opens the workshop; a tap still gets in; crates in reach
  still come first. While it's open the game's HUD, radar and bars are hidden, the walker stands still, and the
  workshop takes all input. A/D or dragging: round the car; W/S: up and down; the wheel: closer; Esc or F: back (Esc
  first stops a job). The view starts at the front left, the driver's side.
* **Parts** show as dots at their places (`Car.PartAnchor`), green → red by condition, grey when missing; hover for
  name and condition, click to pick, then **Remove** (into the parts you carry) or **Fit** one you carry of the same
  kind. Jobs take time: battery 4 s, tyre 5, window, hood, trunk, seat 6, brakes and door 8, windshield 10, suspension
  12, engine 30. A removed tyre hides its wheel and the car runs on the rim. You start with a spare tyre.
* **Ghost highlight** (Tom: "a ghost highlight of the shape of the car part, green in good condition, the more worn
  the more red"): the model has only body, glass, wheels and a steering wheel as meshes, so body and glass parts are a
  ghost copy of the body or glass cut to the part's region in the car's own space (hood, trunk, doors, windshield,
  side windows: the real surface), tyres the wheel's own mesh, and parts inside (engine, battery, brakes, suspension,
  seat) a box seen through the body (an x-ray shader variant). Soft fill, brighter rim, a gentle pulse; only in the
  workshop. `Shaders/part_ghost.gdshader`, `part_ghost_xray.gdshader`.
Test (`--autotest=workshop`): opened, front-left tyre removed in 5 s (wheel gone, flat), the spare fitted, closed;
ghosts photographed for hood 92 %, left door 55 %, windshield 20 %, tyre 70 %, engine 8 %.
Next (step 3): wrecks to strip for parts.

## D69 — Wrecks to strip for parts (step 3 of the workshop)

`Vehicle/WreckField.cs`: abandoned cars generated around you, on foot or driving, like the crates. The ground is cut
into 48 m squares; about one in eight has a wreck, always the same one (by the square), up to eight near you (put
away beyond 260 m). A wreck stands in a car park (an OSM paved area), else 1.6 m off the edge of a residential,
service, unclassified, living-street or track road, facing along it; never on a carriageway or junction, in a
building (2.5 m clear) or water. Eight Synty models (sedan, small, medium, muscle, van, taxi, police, ambulance).
* **Parts:** most worn to 5–85 %, glass smashed on three in ten, and some gone, what scavengers take first missing
  most often (battery 45 %, tyre 25 %, engine 15 %, seat 10 %, others 8 %). Whatever you take stays taken for the
  session (the part states are kept per square and restored when the wreck comes back).
* **Looks:** sagging onto the corners with no wheel (nose or tail down, leaning, lower), and a grime overlay of dust
  and dark brown rust, low down and in blotches, different on every wreck (`Shaders/wreck_grime.gdshader`).
* **Parked:** a Car with `Parked` set: frozen, no physics step, no camera of its own; bullets still wear its parts.
* **Hold F** works on the car you stand by, yours or a wreck (the nearest within 4.5 m); a tap at a wreck says "A
  wreck: hold F to strip it for parts".
Test (`--autotest=wrecks`): three wrecks round the start (two taxis, a sedan); the nearest found, a tyre (63 %)
stripped in the workshop, then fitted on your own car's empty front-left corner.
Not yet: driving a wreck once it's fixed; wrecks on the radar; tools.

## D70 — Offices and towers from BadBuilding (Tom: "Yes, offices", 18 Nov 2026)

BadBuilding has an office style now (its D18–D20): curtain glass, ribbon windows or concrete grids by era, glazed
lobbies, plant on the roof, crowns and masts on towers, and parts that start in the air. In the game, with
BadBuilding on (`--buildings=bb`, F7):
* **Towers go to BadBuilding** up to 330 m (`MapMesher.TallTower`) when they are commercial or untyped; apartment
  towers up to 62 m as before. Building parts are eligible too, when they have some size (over 60 m² and 2.4 m of
  wall): Warsaw's towers are mapped as stacks of parts. A part's `min_height` goes into the request.
* **Parts of one building tell each other their heights** (same group), so a low podium no longer turns the tower over
  it into a blank party wall (it did: one 71 m office near Rondo ONZ was windowless on every side).
* **Curtain wall in the shader** (kind 0.635, role `curtain`): 1.5 m panels, a 0.9 m opaque spandrel band every
  3.6 m storey, mullions at least a pixel wide (thinner ones vanished at 30 m) and averaged away by 600 m; each panel a
  shade different, the sky reflected at grazing angles, glossy like the windows.
* **Wall UVs from the wall normal** (`BadBuildingAdapter`): UV.x is metres along the wall for walls at any angle, so
  the patterns (panels, brick, ribs) line up on every side.
* BadBuilding's `cladding` and `plant` surfaces draw as ribbed metal.
Looked at: Rondo ONZ, Varso and the towers round it, from the street and from the air, against the classic look.
Fly test (6 km at 40 m/s): median 16.7 ms both ways; worst 59 ms with BadBuilding (62 ms classic), 4 frames over
33 ms (6 classic).
Not yet: concrete towers and setbacks; the Palace of Culture and such landmarks stay classic.

## D71 — Squares and wide pavements paved with slabs; no corner islands (Tom's dumps, 19 Nov 2026)

* **Corner pieces follow their pavements.** A street's pavement is left out where a square or paved area lies just
  behind it (D52), but the rounded corner pieces at its junctions were still drawn, and stood alone as islands with
  the zebras between them (Plac Bankowy, Senatorska, Plac Małachowskiego: "Picasso-like crossings"). A corner now
  needs the street's pavement on that side to be there, and isn't drawn where the corner itself lies on a square or
  paved area.
* **Pedestrian areas are slabs.** Squares and pavements mapped as areas (`area:highway=footway`, pedestrian squares)
  were plain light concrete, so the wide pavement of Świętokrzyska read as a missing one. They now have the
  pavement's 50 cm slabs, square to the map.
* BadBuilding D24 in the game: the tower on Fangora no longer floats over a hipped roof, no brick shards on the
  Błękitny Wieżowiec, no eaves through the wall on Al. Jerozolimskie.
Looked at: all six of Tom's dump spots, before and after.
* Addendum: the slabs on squares are laid on the world grid by the shader (UV2.y = 2 marks them): wrapping the UV
  inside the big junction triangles stretched them.

## D72 — The real building type and facts reach BadBuilding (map format 9, 19 Nov 2026)

The cookbook review (BadBuilding D21) found the game folded OSM's building types into eleven kinds, so BadBuilding
couldn't tell a school from a fire station or a garage row from a greenhouse, and never saw a build year or a listing.
Now each building carries a **use string** through the pipeline: the raw OSM building value, then the known facts
as key=value (`school;amenity=school;year=1965`): use (amenity, shop, office, healthcare, leisure, tourism,
man_made), religion and denomination, architecture, materials, heritage, levels, and a year from start_date.
* Stage 1 (`extract.py`, `building_use`) stores it in names.txt; stage 2 writes it in each tile's names section
  (format 9, tile magic `BDT9`); `MapData` reads it into `MapBuilding.Use`. Format 7 and 8 maps still load.
* `BadBuildingAdapter.Use` is the OSM adapter's translation into BadBuilding's request (its D23): the building
  value as the type; an untyped building takes its type from its use where that says what it is (amenity=school →
  school, fire_station, hospital, place_of_worship → religious, an office= → office, a shop → retail, supermarket,
  kiosk); the year, heritage, architecture and levels as request facts. The game's own kinds stay for everything
  else (classic buildings, defaults, POIs).
* The F9 dump shows each building's use string in brackets.
Rebuilt Mazowsze (extract and build, ~60 min). Looked at: Kępa Gocławska (houses with their mapped two storeys),
the Varso towers, Daniłowiczowska; the dump lists house, outbuilding, service, office with levels and materials.

## D73 — Weathered wood in the building shader (BadBuilding's houses, 19 Nov 2026)

BadBuilding's house family (its D26) brings timber cottages and boarded gables. Two new surface kinds:
`timber` (0.82): horizontal planks 18 cm, and `boards` (0.69): vertical boards 20 cm with a batten over every other
joint. Each plank a shade different, grain along it, dark gaps, darker near the ground; the detail fades by 80 m.
Looked at on Grądy and Serock, with every house forced to a cottage for the test.

## D74 — Garage doors and sheet patches as pressed metal (BadBuilding D28, 19 Nov 2026)

BadBuilding's garage rows give each door its own colour role (`door0` … `door7`) and shacks carry sheet-metal patches
(`patch_sheet`); the adapter draws both with the ribbed-metal pattern (D63). Looked at: Ksawerów's garage rows,
the Górnicza allotments.

## D75 — The buildings of one school site share a look (BadBuilding D29, 19 Nov 2026)

A school is often several OSM buildings (wings, a gym, a connector), and each drew its own accent colour: purple next
to blue on one school in Osiedle Jantar. For schools, kindergartens and universities the adapter seeds BadBuilding's
"look" stage from a 120 m grid cell (`StageSeeds`), so neighbouring wings share walls and accent. Looked at: the same
school, now one red scheme.

## D76 — Details only in the near ring; balcony glass stays plain (BadBuilding D30, 19 Nov 2026)

* **Details near only.** With houses, farm buildings and new blocks in BadBuilding the buildings got heavier (3,200
  triangles each on average, from 2,700) and the flight test showed more hitches. BadBuilding's secondary modules
  (window frames and sills, trims, balconies, porches) are now left out of the far ring of map cells (lod 1), where
  they're a few pixels: 2,340 triangles each. Flight test after it (alternating, the PC was noisy that evening):
  BadBuilding 7 and 8 frames over 33 ms, classic 4 and 26; median 16.7 ms throughout.
* **Balcony glass** (`rail_glass`) isn't drawn with the glass shading: in front of a dark room it read as solid
  blue-teal panels, and a block with a balcony on every bay looked like a glass box. It's a light frosted colour.
* Shop fronts (`shopfront`) draw as glass.

## D77 — More facts for BadBuilding's socialist realism; hidden inner parts (20 Nov 2026)

* The adapter passes the mapped wall material (`building:material`) and every neighbour's height, and a "district"
  seed (300 m cells) next to the school seed (D75), so a quarter decides its avenue blocks together.
* `MapData.TidyParts`: a part wholly inside a bigger part of its building and within its height range is hidden
  (drawn like a ghost copy: it still keeps trees out). On Plac Konstytucji a 6–25 m piece stood inside the block's
  0–25 m piece.
Looked at: Marszałkowska by Plac Konstytucji (stone blocks with paired windows, balconies and balustrades).
Not yet: a blank wall on one overlapping-piece block east of the square (found and fixed in D78).

## D78 — The blank wall on Plac Konstytucji, found (20 Nov 2026)

Isolated by skipping one OSM piece at a time (a temporary `--bbskip=` switch): the long blank cream wall east of
Plac Konstytucji was way 238951890, a 6–25 m piece of the MDM block drawn inside its 0–25 m outline piece. D77's
"hidden when wholly inside a bigger part" missed it: of its 77 traced corners a few poked a hair outside. Now a part
counts as inside when its centre and 90 % of its corners are. And the block's stone evidence is shared: the
`building:material=sandstone` tag sits on one piece only, so the other pieces of the same building now get it too,
and the whole block comes out as one stone MDM front instead of tenement pieces.
Looked at: the square from the street, from the air and along Marszałkowska.

## D79 — Board-marked concrete, and Brutalist Warsaw on Shift+F7 (BadBuilding D32, 20 Nov 2026)

* **Board-marked concrete** in the building shader (kind 0.75, roles `concrete_board` and `fin`): the imprint of
  15 cm formwork boards in staggered 2.4 m lengths, each a shade different with a faint grain, darker joints,
  form-tie holes every 0.6 × 0.9 m, rain streaks from the top, darker near the ground; detail fades by 90 m.
* **Shift+F7** switches BadBuilding's "brutalist" preset on and off (and turns BadBuilding on): the Brutalist Warsaw
  that never was. `--preset=brutalist` on the command line. Plain F7 still swaps classic and BadBuilding.
Looked at: Ksawerów and Plac Konstytucji with the preset.

## D80 — Knocked down by the car (20 Nov)

Tom (18 Nov): "a knockdown, ragdoll for a moment, then they get up if alive; little damage so running people over
isn't the easy way to win".
* The car never collides with people (a kinematic capsule is a wall to it). Each bandit checks every physics step
  whether the moving car's body (its box, grown 0.45 m, looked at two steps ahead) is about to reach it; above
  2.5 m/s that's a hit and the capsule is switched off at once.
* Damage is small: (speed − 2.5 m/s) × 1.1, 2 to 30. A 45 km/h hit takes 11 of 90 health, 90 km/h about 25. A
  bandit already hurt can die from it.
* The ragdoll starts with the whole body given 55 % of the car's speed plus a lift, and the legs kicked at bumper
  height. Lying 2.5–4.5 s (1.5 s more after a fast hit, longer while still sliding), then they stand where the hips
  lie: the ragdoll's influence fades over 0.8 s into a crouch, they pick the gun up, are dazed 0.8 s, and engage.
* Shots now hit bodies lying down (ragdoll bones are flesh, and lead to their owner), including the dead.
* The car: the hood wears a little per hit, the windshield too above 43 km/h, and it loses up to 8 % of its speed.
Self-test `--autotest=knockdown --start=68600,639300`: hit at 45 km/h, lay 4.5 s, got up and engaged; a second
bandit on 8 health died from the hit.

## D81 — Walking into blocks of flats (BadBuilding D33, 20 Nov)
Tom: "Interiors". BadBuilding plans interiors for staircase blocks (D33); the game uses them:
* The map's workers keep a shell (`BB.Interior.Shell`) for every block of flats they build in the near ring
  (`World.Interiors.Register`, forgotten beyond 800 m).
* On foot, any shell within 22 m of its outline is planned on a worker thread (7–10 ms), its mesh and collision
  arrays prepared there too, and added as one mesh and one concave shape on a new layer, `Bodies.Interior` (128),
  anchored where the building stands (4–21 ms on the main thread; the first one 37 ms, warm-up). Dropped beyond 45 m.
* While it is loaded its street doors are open: the adapter gives door quads their own kind (vertex alpha 0.84) and
  `building.gdshader` discards them within 1.5 m of up to four open doors (global uniforms `open_door0..3`).
* Inside the outline or in a doorway, `Walker.Indoors` switches the building's solid mass off for the body and the
  camera; the interior's walls, floors and stairs hold you. Bullets, bandits, ragdolls and the lean check see the
  interior layer too.
* Light: the interior casts double-sided shadows, so the sun comes in only through the windows; screen-space AO blacks
  out the sky's light in a 2.6 m room, so every surface glows faintly with its own colour as much as the daylight
  outside allows (ceilings a little more).
* F9 dumps say which building, storey and room you're in; `--interior-dump=DIR` writes each plan as JSON;
  `--interior-reach=M` for tests.
* Only with BadBuilding's buildings on (F7 / `--buildings=bb`).
Self-test `--autotest=interior --start=68600,639300 --buildings=bb`: out of the car, to the door of a 9-storey PRL
block, walked in (indoors after 1.7 s), up a flight (1.43 m) and the next (2.97 m of a 3.07 m storey) on foot, living
room and top-floor windows looked out of, and back out (indoors off). Around the start all 11 blocks within 300 m got
interiors (1–3 staircases, 4–17 storeys). Not yet: shops, balconies, furniture, loot or bandits indoors, locked doors.

## D82 — Roomier interiors (BadBuilding D34, 20 Nov)
Tom, from inside a 15-storey block near Sosnowa: "very cramped". Staircases, landings, halls, corridors, bathrooms,
bedrooms and doors all grew (sizes in BadBuilding D34); the windows show a faint glass sheen instead of looking like
holes. Same self-test at Tom's spot (`--start=68567,639452`): in, up a storey on foot (2.91 m of 3.01), out.

## D83 — Interiors seen from the right side, balconies, no cracks (BadBuilding D35, 20 Nov)
Tom's dumps: missing walls and floors, a gap by the stairs, balconies that aren't, gaps round the windows.
* Every interior face was wound the wrong way for Godot (clockwise is the front, like `MeshData.Tri` makes them; the
  library winds counter-clockwise about the normal): you saw each face from behind, so floors and outer walls vanished,
  ceilings showed the floor above's underside, and walls showed the next room's paint. The winding is flipped when
  the arrays are prepared. The stair "gap" and dark step stripes were the same thing.
* Balconies: the adapter gives balcony door glass its own kind (vertex alpha 0.52, still glass to the shader);
  `building.gdshader` discards it within 2.2 m of the player while they're inside (`player_near`), so the door opens as
  you come up to it, for every balcony at once. A balcony and its doorway count as indoors.
* Self-test: `--cams="x,y,z,yaw,pitch;..."` (from F9 dumps' Camera line) photographs exactly where a dump was taken;
  the walk-through now steps out onto a balcony: 0.7–1.0 m out, at floor height, held by the rail, in both a PRL block
  (Sosnowa) and a contemporary one (Skaryszew).

## D84 — Loggias (BadBuilding D36, 20 Nov)
Loggias in contemporary blocks open like balcony doors (their glass is the same kind, hidden near you from inside);
you walk out into the recess and stop at the rail. Self-test `--interior-want=loggia` picks the nearest block with
loggias: at Skaryszew (`--start=70356,637584`) the walker went through the glass and stood at the rail, 0.45 m in
from the facade. The window jambs that faced into the wall now face into the opening.

## D85 — Stepping up, and doors you can reach from the side (BadBuilding D37, 20 Nov)
Tom: "sometimes it's hard to get into some buildings because the character cannot jump high enough to get on the first
step". The walker now steps up ledges up to 0.42 m without jumping (a kerb, a doorstep): when the way ahead is blocked
by something upright but clear that much higher, the body is lifted onto it (`Walker.StepUp`). With BadBuilding D37's
wider landing, side ramps and thin jambs, the self-test walked in from 3.5 m to the side (`--interior-angle=M`) in
about 2 s in a PRL block and a contemporary one; the loggia in front of a stairwell is gone.

## D86 — Furnished flats (BadBuilding D38, NoorFengShui, 20 Nov)
Every flat is furnished when its block is planned (beds, wardrobes, sofas facing TVs, kitchens with sinks under the
windows...), with physics and loot markers (not used by the game yet). A 15-storey block is ~77k triangles and ~3,000
boxes: planned in ~0.3 s on a worker; its surfaces are uploaded a few per frame (4 ms each), and the collision shape
still costs ~30 ms once, about 20 m before you reach the door. The self-test starts the walker on free floor. Known: the
self-test's third-person shot in a Skaryszew bedroom comes out black (not investigated; first-person views are fine).

## D87 — Furniture models by kind: Synty now, our own later (BadBuilding D39, 20 Nov)
Tom: "I want to be able to swap the assets with any model... For now, use Synty mixed with our own if something is
missing."
* `Assets/Furniture/furniture.json` (committed): per kind, a list of models (any Godot-loadable file), each with a
  `yaw` and a `fit` (stretch, footprint, uniform); picked by the piece's id. `World.FurnitureCatalog` loads it at start;
  a missing file (Synty isn't in the public repo) leaves that kind generated. Placed per building as one multimesh per
  model part (materials baked into a copy of each mesh); the models' own colliders are ignored, the library's boxes do.
* Synty (copied with `tools/synty_copy.py`, git-ignored): City couch for sofas; Heist glass and square coffee tables,
  chest of drawers for bedside tables and the hall's shoe cabinet; Gang Warfare office shelf as the bookcase and metal
  shelving in storerooms. None of Tom's packs has beds, wardrobes, fridges, bathroom fittings, TVs or armchairs: those
  stay generated, waiting for our own models. (The Heist couches are white leather from the bank: left out.)
* The models' materials glow faintly with their own colours indoors like the rest (emission = the albedo texture,
  multiplied; "add" made everything white).
* `Assets/Furniture/README.md` explains adding a model; `tools/prefab_sizes.gd` prints a model's size; the self-test's
  `--look=kind,kind` photographs a piece of each kind from in front.

## D88 — Flat safehouses (Tom, 20 Nov)
Tom: "currently we have claiming the whole building. Can we select individual flats?" Inside a block of flats you
hold K in the flat you want ("Hold K: make the flat on the 3rd floor, left your safehouse"; the ground floor is
parter, as in Poland; a staircase number when the block has several). The save keeps the building's interior id, the
flat's id, the exact spot and height you claimed it from, the building's origin and its shell as JSON. After dying you
wake on that spot: the interior is planned and built there and then from the saved shell (`Interiors.LoadNow`), so
it works however far away you died and whether or not that part of the map is loaded. Houses and buildings without an
interior keep the whole-building claim from outside (D55).
Self-test `--autotest=interior --interior-safehouse --start=68567,639452 --buildings=bb`: claimed the 3rd-floor left
flat, every interior dropped, shot dead 300 m away, woke in the same living room at the same height; the player's own
`safehouses.json` is backed up and restored by the test.
Risk: a later change to the interior generator could move walls; the wake-up spot is a position, so it might then be
inside a wall or a piece of furniture (to handle when the generator gets versioned plans, BadBuilding roadmap).
Next, by Tom's direction (20 Nov): a Project Zomboid-like survival layer of our own (data-driven items, inventory with
weight, containers in the furniture, crafting recipes, loot tables). This changes the on-foot brief's "not the plan:
elaborate crafting"; the brief is to be updated when that work starts.

## D89 — Items, a pack with weight, furniture you can search (Tom: "Yes, items next", 20 Nov)
The first piece of the Project Zomboid-like survival layer (D88's note; our own design and data, not Zomboid's):
* **Items are data:** `GameData/items.json` (51 items: tinned food, drinks, medicine, tools, materials, junk; names
  and weights in post-PRL Mazowsze), `GameData/loot.json` (what each kind of container holds, weighted, and the rooms
  that make some items likelier). `Items.ItemDb` reads them at start; code refers to items by id only. Not `data/`:
  that folder is git-ignored (map data), and on Windows `Data/` would be too. The export now includes `*.json`
  (it hadn't: `furniture.json` was missing from the dist build).
* **The pack:** `Survival.Pack`, an `Items.Inventory` (stacks by id, a weight limit: 15 kg). The old counters (food,
  water, medkits) are views onto it; eating and drinking still happen by themselves when you get hungry or thirsty,
  now with whatever fits best (and a bottle leaves an empty bottle); H uses what fits the wound. Caches, bandits'
  bodies, leads and the package where you fell give items; ammo stays with the guns for now.
* **Furniture you can search:** every piece with a loot marker (NoorFengShui, D86) within reach in the interior you're
  in: "F: search the wardrobe". What's inside is rolled from the loot tables the first time, from the piece's id, and
  what's left is remembered (`user://containers.json`, per map; the world's changes apart from the generated world).
* **The window:** the container on the left, you on the right, with weights; drag, double-click (the pile) or
  right-click (one) to move, "Take all" (E); Eat / Drink / Use on your own food, drink and medicine; Tab opens your
  pack alone; Esc, Tab or F closes. The walker stands still while it's open.
Self-test `--autotest=interior --interior-loot --start=68567,639452 --buildings=bb`: kitchen cupboards (pâté, an empty
bottle, rice), a wardrobe (rags, sheets), an empty mirror cabinet and fridge; took everything; ate the rice when
hungry; the player's own containers file is backed up and restored. The old loot and death tests still pass.
Not yet: dropping items, ammo as items, clothing and bags (more weight), crafting recipes, items shown on shelves,
"full" furniture models (Tom's Zomboid idea), outdoor caches as containers in the same window.

## D90 — Regions joined by merging their extracts: one map, "polska" (Tom: "Next region: Podlaskie", 20 Nov)
The map grows by whole voivodeships with no seams (region-order: Podlaskie next). Building each region on its own
would leave the border tiles half-filled in two pack sets, with road heights solved twice and not meeting. Instead:
* **Merge the extracts, build once.** `tools/osm_import/merge.py` merges Geofabrik extracts (pyosmium
  `MergeInputReader`, one copy of every object, the newest), so ways crossing the border are whole; stage 1 and 2 run
  on the merged file as on one region. The packs are named `polska` (they'll hold more of Poland as it's added).
  Rebuilding everything each time costs time (Mazowsze alone: ~8 min stage 1, ~52 min stage 2), not seams; per-tile
  rebuilds can come later if it gets slow.
* **One projection, one world.** Every pack is cut from the Baltic-wide projection (D2), so a place has the same
  coordinates in `mazowsze` and `polska`. The player's saves (safehouse, searched furniture) are now kept under the
  world, `Main.SaveWorld = "baltic"`, not the pack name; saves from before, under "mazowsze", are read as the world's
  (and the containers file moves them on its next write).
* **The game opens `polska` when it's built**, else `mazowsze`; `--map=` still chooses. `--yaw=` sets the start heading
  (for tests crossing the border).
* Elevation: GUGiK's WCS (as for Mazowsze); on 20 Nov it was failing (connections cut, HTTP 400, timeouts), so the
  download retries every 20 minutes until every block is in (Tom: retry, keep the 10 m quality). Then the EVRF2007
  sheet fill as for Mazowsze.
* The death self-test now keeps the player's own safehouse file and puts it back (it used to clear it).
Not yet: the build and the drive across the border (waiting for the elevation); Podlaskie's own look (wooden
villages, Orthodox churches with OSM evidence, the Tatar mosques, forests): proposed, waiting for Tom's choices.

## D91 — Regions in data: Podlaskie gets Podlasie's wooden houses (BadBuilding D40)
`GameData/regions.json` lists regions with an outline (longitude/latitude; Geofabrik's boundaries, from OSM) and a
BadBuilding preset; `World.Regions.PresetAt(x, z, seed)` answers per building: inside, the preset; past the border
it fades over `blend_km` (12 km: 70 % at the line, none at 12 km), decided by the building's seed, so the villages
don't change at an administrative line (coherent over exact). The Shift+F7 preset still wins over it.
Checked in the game with `--preset=podlasie` in Rabędy (on the Mazowsze side, 4 km from the border): the houses
render with the planks shader, crowns, shutters, the orange sheet roof. The real look waits for the joined map
(D90: the elevation is still downloading). Next for Podlaskie: Orthodox churches (OSM evidence only), the Tatar
mosques as landmarks, forests (spruce, Białowieża), storks.

## D92 — Orthodox churches through BadBuilding (its D41)
`BadBuildingAdapter.OrthodoxChurch`: a church (the importer's church kind), whole (not a building:part, not a ghost
outline), whose use string (D72) says `denomination=…orthodox` and whose religion is christian or unmapped, goes to
BadBuilding, which builds it as a cerkiew; other churches keep the classic meshes. The request carries religion and
denomination. A church with the importer's guessed height (exactly 16 m, no height or levels mapped) goes without one,
so the style sets its own (a village cerkiew's walls are about 6 m). Checked in Warsaw: the church at Wolska 138/140
and the cathedral at Aleja Solidarności now have onions and crosses (the cathedral's ragged outline gets a flat roof;
its real five domes are not mapped as parts). In the merged data there are 231 orthodox buildings, nearly all in
Podlaskie, waiting for the joined map (D90). Fixed while doing it: every building has a Group (its own index when it
has no parts), so "Group < 0" had excluded every church.

## D93 — Mosques through BadBuilding (its D42)
`BadBuildingAdapter.PlaceOfWorship` (was `OrthodoxChurch`, D92) also takes a church-kind building whose use string says
`religion=muslim`, or `building=mosque` with no religion. In the merged data: Kruszyniany (historic, 12 × 17 m, the
long kind) and Bohoniki (historic, 13 × 14 m, the square kind), Białystok's mosque, and in Warsaw the Ochota centre
and the Wilanów mosque; the last two checked in the game (domes on the halls; Ochota's importer-guessed gabled roof
stays). A Muslim tomb (ruins) and the Muslim Religious Union's office are left alone. Kruszyniany and Bohoniki will
be seen when the joined map is built (D90).

## D94 — Podlaskie's forests and storks (Tom: "Forests and storks", 20 Nov)
**Forests in data.** `GameData/regions.json` now gives tree mixes per land class (forest, wetland, scrub, park):
* a region's (Podlaskie: more spruce than Mazowsze, 38 pine / 28 spruce / 18 birch / 13 oak; wetlands mostly birch
  and willow), fading over the region's 12 km like its houses;
* named **zones** that win inside their rough outlines, fading over 1.5–2 km: the Białowieża Forest (old mixed
  forest: oak, spruce, maple standing in for hornbeam and lime, birch, pine, poplar for aspen; bigger trees, 0.95–1.65),
  the Augustów Forest (pine and spruce), the Knyszyn Forest (pine, spruce, some oak), the Biebrza marshes (birch and
  willow scrub on the wetland, birchy woods). Outside every area the old Mazowsze mix stays. The species stand-ins are
  named in the data until the tree assets have hornbeam, lime, alder and aspen.
* `TreePlacement` asks `Regions.Near` once per cell and `Regions.TreesAt` per tree (from the tree's own hash, so near
  and far cells agree); `Regions` now reads both kinds of area (`Area`, `TreeMix`).
**Storks.** White stork nests (`MapMesher.StorkNest`): on mapped electricity poles in villages (one in 40, one in 10
in Podlasie), and, since villages seldom have their poles mapped (none within 3 km of Rabędy, none round Soce), on
nest poles of their own a few metres from a house or farm building (one site in 60 buildings, one in 14 in Podlasie).
Villages only: 2 to 30 buildings within 80 m. A twig drum on the pole's top, empty (15 %), with one stork standing or
with two (one lying): low-poly, white with black wing ends, red beak and legs. Three instanced meshes per chunk, with
the poles. Checked by Żyłowo near Rabędy (two nests on the Mazowsze side of the fade) and a 6 km fly over the fade:
median 16.7 ms, 2 frames over 33 ms, no ground gaps. Not yet: storks that fly or walk the meadows, nests on roofs,
the season (storks leave in late August), Podlaskie's forests seen in the game (the joined map, D90).

## D95 — Catholic churches through BadBuilding (its D43)
`PlaceOfWorship` now also takes a church-kind building that is a church, chapel, cathedral, basilica, monastery or
convent (or untyped and Christian), Christian or with no religion said; a footprint's box under 14 m² (a wayside
shrine) keeps the classic mesh, and synagogues and other religions too. A church's height is passed only when mapped
and at most 20 m: the importer's 16 m guess, and mapped heights that count the tower, would stretch the walls. Checked
at Ostrów Mazowiecka: a modern church with its tower and cross on Małkińska, a white one on Pasterska (its outline too
ragged for a tower), a little red-brick neo-Gothic church with a dark spire near Pałapus.

## D96 — How a car is made for the game (Tom, 21 Nov)
Tom will make the cars himself: Noor's concept art (own designs that resemble the classics, e.g. a Polonez-like
hatchback, not copies; no real badges), Meshy's low-poly segmented models (commercial use under his subscription,
~16k triangles), cleanup in Blender, one GLB per car. `docs/VEHICLE_ASSETS.md` is the contract: Noor Interchange space
(metres, nose to glTF +Z, origin on the ground at the centre), separate named objects pivoted where they turn (doors
at the hinge, hood, trunk, wheels at the hub), glass, seats, engine and battery as real pieces matching `CarParts`
(D67), a `Paint` material the game tints. The game will open doors, hood and trunk, make the trunk, rear seat and
glovebox containers (D89), and highlight or remove the real parts in the workshop. The spec lacks vehicle part names
and a `container` marker: proposed for Noor Interchange v0.6, object names carry it meanwhile (the spec's fallback).
Waiting for: Tom's first car in `data/inbox`.

## D97 — The car stops tipping over (Tom: "It's so easy to tip the car", 21 Nov)
Reproduced with a new self-test, `--autotest=tip`: on a field off the S8, the first double lane change at 50 km/h
rolled the car onto its side. The cause was the tyres' grip: Godot's wheel friction slip is a friction coefficient
(sideways force up to slip × the wheel's load), and 3.3 meant about 3 g of cornering, far past a car's roll-over
threshold (about 1.3 g for a track of 1.5 m and a centre of mass 0.55 m up), so the car pivoted over its tyres
instead of sliding. Now:
* **Grip 1.45 front, 1.35 rear** (about 1.4 g): the tyres slide first, as real ones do, rear a little looser.
* **Arcade anti-roll** (`Car.Stabilise`): while wheels touch the ground, a torque about the car's length pulls it back
  toward its wheels (22 N·m per kg per radian, like stiff anti-roll bars) and damps the roll (5 per rad/s); the roll
  rate is capped at 2.2 rad/s so a kerb strike can't throw it over in one step. Roll only (it still pitches and jumps);
  past 75° or with no wheel down it lets go, so a real crash still rolls.
* `--grip=K` scales the grip and `--antiroll=0` turns the helper off, for tuning; the autopilot can pull the handbrake
  (`Car.AutoHandbrake`).
Results, before → after: a field by the S8, lane change at 50 km/h: flipped → 5° of roll; all six manoeuvres (lane
changes at 50/70/90 km/h, full lock at 40/60, a handbrake turn at 50) in central Warsaw with kerbs: worst roll 6°,
no flips. The route drive (`--autotest=drive --dest`) covers the same distance with the old grip and the new
(1.45 / 1.44 km in 120 s, 2.8 s off road both: the test's autopilot clips something on that route either way).
Not yet: Tom's feel in his own hands; per-car settings (weight, power, grip, sound) for his own cars (D96).

## D98 — Every car drives its own way, from data (Tom: "a little bit too slidey… settings for different cars", 21 Nov)
`GameData/cars.json` holds how each car drives, `Vehicle.CarSpec` reads it: mass, the engine's pull, top speed, which
wheels drive (front, rear, all: the pull is shared by the driven wheels only), tyre grip front and rear, steering
lock, brakes, springs and dampers, the centre of mass moved up or down, the anti-roll helper's strength (D97) and
where the engine sits (for the lids, D96). A car takes the first entry whose `models` match its model's file name,
else the default; `--carspec=id` picks one; `--grip` and `--antiroll` still tune. Entries now:
* **sedan** (the default, and Synty's taxi, police car, medium car): 1,300 kg, front drive, 180 km/h, grip 1.75/1.6
  (a little more than D97's 1.45/1.35: Tom found that slidey);
* **pickup**: 1,850 kg, rear drive, 160 km/h, grip 1.65/1.5, centre of mass 10 cm higher;
* **muscle** (muscle and sports cars): 1,450 kg, rear drive, 12.5 kN, 210 km/h, a looser rear (1.75/1.55);
* **van** (ambulances, vans, buses): 2,400 kg, rear drive, 145 km/h, high centre of mass, stronger anti-roll;
* **small_classic**, waiting for Tom's Maluch-like car: 600 kg, rear engine and rear drive, 2.6 kN, 105 km/h.
Tests: `--autotest=tip` in central Warsaw: the sedan's worst roll 6°, the pickup's 14° (it leans more, as it should),
no flips; the route drive the same distance as before, with no damage this time.

## D99 — Sandbox settings, and wrecks you can drive away (Tom: "drivable wrecks next… I'm a fan of Project Zomboid…
## players are able to set so many settings in sandbox settings", 21 Nov)
**Sandbox settings** (`Core.Sandbox`, `GameData/sandbox.json`), Project Zomboid style: every generated thing asks it for
its number. Each setting has a default, a range and a line saying what it does; presets fill them (`baltic_drive`,
the game as designed; `scavenger`, kinder; `apocalypse`, scarce and dangerous; `road_trip`, no bandits and cars to
borrow); the player's choice is `user://sandbox.json` (`{"preset": …, "overrides": {…}}`, written by `Sandbox.Save` for
a future menu); `--sandbox=preset` and `--sb.key=value` win (tests). Wired now: `loot.abundance` (× the items rolled in
every container, D89), `caches.frequency` (outdoor crates), `bandits.frequency` and `bandits.max_near` (0 turns them
off), `wrecks.frequency`, `wrecks.runners` (share that still run, default 1 in 5), `wrecks.condition`,
`wrecks.picked_over`. Next: parked cars, and a menu.
**Wrecks you can drive away.** A runner has its engine, battery and four wheels (35–80 %), the rest as worn as any
wreck. A tap of F at a wreck: if it can go (`Main.WontGo`: engine there and not wrecked, battery there and not flat,
no wheel missing) you get in and it's your car, with its own handling (D98: `Car.Spec` from its model); otherwise "It
won't start: there's no battery. Hold F to work on it", and the workshop can put it right. The car you had stays parked
where you left it for the session (`WreckField.Leave`), and you can take it back the same way; a square whose wreck you
took gets no new one (`WreckField.Take`).
Self-test `--autotest=takecar`: with every wreck a runner, out of the sedan, the nearest wreck (a rusty small car,
rear suspension missing) taken and driven 14 m (straight into the bollards across Emilii Plater: the test steers
straight ahead), the sedan still there 67 m back, taken back; with no runners, "a wheel is missing" and nothing taken.

## D100 — Parked cars in car parks and at kerbs (Tom: "more cars on the streets, on parking lots", 21 Nov)
`Vehicle.ParkedCars`, generated around the player in 64 m squares (the same cars in the same bays every time):
* **Car parks** (OSM paved areas that aren't pedestrian or raised): rows of bays along the lot's longest side, 2.6 m
  wide, 5 m deep, 6 m aisles; a bay has a car about a third of the time; nose in either way.
* **Kerbs** of residential, living and service streets with buildings within 12 m: a space every 6.5 m, one in nine
  filled, off the carriageway.
* How full: the sandbox's new `parked.frequency` (D99; 0 none, 3 packed; `apocalypse` 1.3, `road_trip` 1.6).
* Light: the Synty model and a box (props layer) to bump into, no physics; the prefabs' own concave colliders removed.
  At most 140 near you, made three a frame (a big car park in one frame stuttered: 35 frames over 33 ms on the 6 km
  fly over Warsaw; now 11, against 16 with parked cars off: noise). Built from the walker, the car or the free camera.
* **Colours**: Synty's cars are all one blue (its colour atlases change only buildings), so `car_paint.gdshader`
  re-colours just the body paint (texels near the paint's hue, saturated): white and silver most, black, red, maroon,
  green, beige, yellow, navy, the original blue; dust and rust (`wreck_grime`) over it. A Synty prefab's root is the
  car body, so meshes are gathered with the root included (`ParkedCars.Meshes`).
* Models: the City cars (sedan, small, medium, van, muscle, taxi, a police car now and then), Battle Royale's car, and
  burnt-out shells (one in twelve) that stay scenery. The Gang Warfare van, low cars and truck are copied but left out:
  their models reference a material their pack doesn't ship.
* **Waking**: walk within 7 m and the nearest becomes a real car (`WreckField.Adopt`): parts rolled from its id like a
  wreck's (D99: worn, picked over, sometimes a runner), the same paint; F takes it if it runs. Far ones go back to
  being models, their parts remembered; driven-away bays stay empty.
Checked: a Warsaw courtyard car park (Emilii Plater) in rows of dusty white, beige, red, maroon and blue cars; a
parked medium car woken, taken and driven, still white; the old sedan left parked and taken back.

## D101 — Trucks (Tom: "Trucks next", 21 Nov)
Until Tom's own rigs come (D96), two Synty trucks: Gang Warfare's box delivery truck (twin rear axles) and Battle
Royale's truck with its trailer, plus a burnt-out one.
* **Drivable:** `Car` now finds a truck's rear wheels (`*_Wheel_rl_01/02`, `rr_01/02`, or one mesh per axle across both
  sides): the physics wheels stand between the rear axles on the front wheels' track, and every rear wheel mesh hangs on
  a holder that turns and bounces with its physics wheel at its own hub (`Car._followers`).
* **Handling** (`cars.json` "truck", D98): 7,500 kg, rear drive, 32 kN, 100 km/h, grip 1.3/1.25, steering lock 0.45,
  centre of mass 45 cm higher, anti-roll 1.6. Tip test (D97) in Warsaw: worst lean 7–8°, no flips; off road it tops out
  at 55 km/h (half its road speed), so the faster rounds can't be run in a field.
* **Where** (`ParkedCars`, D100): in yards (service and unclassified roads with an industrial building within 35 m, about
  one per 250 m of yard road) and, rarely, abandoned on the shoulder of a trunk, primary or secondary road out of town
  (no building within 40 m, about one per 2.5 km). They wake into real trucks like the cars.
* Fixed on the way: road pieces are about 10 m long (the importer densifies roads), and the kerb and truck loops skipped
  pieces under 14–16 m, so streets got almost no parked cars and there were no trucks; now each piece gives one
  candidate in its middle, its chance scaled by its length.
* Gang Warfare's models referenced a material (`Models/extracted/default.material`) the copy tool didn't follow (it is
  referenced from binary model files); copied by hand, so its van and low cars join the parked mix.

## D102 — Tom's UI set, first pieces in (Tom: "For now, we can try these", 21 Nov)
Tom had Codex make the interface art from `docs/UI_ASSETS_BRIEF.md`: `baltic_drive_ui_v1.zip`, 61 pictures and a
manifest (sizes and nine-slice margins), one family of dark painted panels with thin amber accents and white
pictograms. Tom's own art, so it lives in the repo (`Assets/UI`, with a README saying so). `UI.Skin` shrinks each 2×
picture to screen size once and builds `StyleBoxTexture`s from the manifest's margins; every use falls back to the old
look if a file is missing. In now:
* the inventory window (D89): the furniture's side on the crate-like `container_frame`, yours on `window_frame`, items
  on `row_normal` / `row_hover`, "Take all" and Eat / Drink / Use as the set's buttons, titles in its off-white;
* the health, food and water bars: an icon each, the gauge housing, the fill tinted per stat, glowing below 25 %;
* the minimap: the instrument bezel round the disc;
* the message strip under notes ("It won't start…").
Not yet: the settings (sandbox) menu with the sliders, checkboxes and tabs; the workshop and map screen windows;
cursors, the keycap prompt, crosshairs, the damage vignette and death overlay; the speedometer.

## D103 — The sandbox settings menu (F10) (Tom: "Yes, the menu", 21 Nov)
`UI.SandboxMenu`, opened with F10 (in the HUD's key hints), closed with F10 or Esc; the walker stands still meanwhile.
Project Zomboid style, dressed in Tom's UI set (D102): the window frame and title bar; the preset in a dropdown with its
one-line description; the settings in groups ("Finding things", "Bandits", "Cars"), each with a slider (Tom's groove,
fill and knob), its value in words ("off", "×1.5", "45 %", "2") and its description; Reset to preset, Cancel, Save.
Save writes the preset and only the settings changed from it (`Sandbox.Save`, `user://sandbox.json`); the game reads
them at once, and what the world generates from then on follows them (places already met keep theirs, as the menu
says). Settings now carry a display name and a step in `GameData/sandbox.json`; `Sandbox.PresetValue` and
`PresetAbout` serve the menu. Fixed on the way: a slider's float step (0.05f is 0.0500000007) snapped 1.0 down to
0.95; the step is now an exact decimal. `--sandbox-menu` opens it over a views run (to photograph it).

## D104 — The joined map: Mazowsze and Podlaskie as one (D90 done, 21 Nov)
Podlaskie's elevation came through GUGiK's sheet server after all (D90: the WCS was down): 3,385 sheets, 124 GB
downloaded and shrunk to the 10 m blocks, 291 minutes. The build: 16,861 tiles (Mazowsze alone 11,482), 1.2 GB in 25
packs, 22,109 named places, 80 minutes. The game opens `polska` when it's there (the dist build's `map` folder has it
too; the Mazowsze packs stay). 12,022 km² of the blocks have no elevation: the land across the Belarus and Lithuania
borders, and Mazowsze's old gaps (D90); no hole seen inside Podlaskie.
Toured in the game: a 6 km fly over the border zone (median 16.7 ms, 3 frames over 33 ms, no ground gaps); Kruszyniany
(the green wooden mosque among spruces), Bohoniki (the square one with its lantern), a green cerkiew with dark onions
in Białowieża, Białowieża's houses by the ponds, the Narew marshes at Pentowo, Tykocin; 94 stork nests round
Trześcianka. Fixed after the tour:
* Trześcianka and Soce came out mostly modern houses: their houses are `building=yes` with no height, the importer
  guesses 6.5 m, and BadBuilding read that as two storeys. `BadBuildingAdapter.GuessedHouseHeight`: a house-sized
  building (untyped or a house, footprint box under ~420 m²) whose height is exactly one of build.py's guesses (2.8,
  6.5, 8 m untyped; 4.5, 7.5 m a house) and has no mapped storeys gets no height, and the house style decides. Bigger
  buildings keep the guess (Warsaw unchanged). Together with BadBuilding D44 the villages are now mostly wooden.
Not yet: driving across at Zambrów by car (only flown), Białystok's own look.

## D105 — The whole of Poland, building in the background (Tom: "Yes, please", 21 Nov)
`data/work/poland_chain.sh` (restartable, each step skips what's done): Geofabrik's Poland extract (2.1 GB), stage 1,
elevation from GUGiK's WCS (back up, slowly) for every block with data, the blocks it can't give written empty
(`--empty`) and filled from the sheets, then the build as `poland`. The game prefers `poland`, then `polska` (Mazowsze
and Podlaskie, D104), then `mazowsze`; the older packs stay (Tom: keep everything while there's room).

## D106 — Crafting (Tom: "crafting recipes", the Zomboid layer, 21 Nov)
`GameData/recipes.json` (data, our own recipes), `Items.Recipes`: inputs used up, tools kept (each a list of
alternatives, "lighter or matches" is the fire), outputs; a recipe is made from your pack when it has everything and
what it makes fits the weight. Ten to start: rags from a bed sheet or a towel, a makeshift bandage, a clean bandage
(rags and disinfectant), boiled rice, hot soup, herbata, kabanos on rusks, **kartacze** (potatoes, an onion, a tin of
luncheon meat; a saucepan, a knife, a fire: "Wigraszek's are better, but Wigraszek is closed"), and a molotov (for
when throwing exists). New items: potatoes, onion, tea, makeshift bandage, cooked rice, hot soup, mug of tea, kabanos
on rusks, kartacze, molotov (61 now); potatoes, onions and tea in kitchen cupboards, fridges and shelves.
The window: Tab (your pack alone) shows the crafting list on the left, in Tom's UI set: makeable first, what each
uses and needs (green what you have, red what you lack), what it makes, a line of flavour, a Make button. Self-test
`--autotest=craft`: kartacze, rags, a bandage and rice made from a filled pack; a molotov refused ("You need bottle of
vodka"). Not yet: crafting time, cooking only by a cooker or a fire (now any lighter will do), skills, recipes found in
books, using the outputs beyond eating (bandages heal like any medicine; the molotov waits for throwing).

## D107 — Crafting by tags and in real time

Tom (21 Nov 2026, a Project Zomboid fan): a recipe should ask for "something that can contain water", not one
particular pot; and crafting should take time ("craft a makeshift bandage out of a rag while bandits are coming your
way. Adds tension."). Our own data and design, not Zomboid's lists.
**Tags:** items carry `tags` in `GameData/items.json` (clean_water, water_container, cookware, cup, sharp, fire,
fabric, alcohol, disinfectant, ...; 20 to start, each with a readable name in the file's `tags` table: "something to
hold water"). A recipe's input or tool is an item id or `tag:x`; any item with the tag will do. A coffee mug is both a
water holder and a cup; vodka and disinfectant are both strong alcohol. Inputs are taken in pack order.
**Leaves:** an item says what's left when it's used up (`"leaves": "empty_bottle"` on water, juice, compote, vodka),
for drinking and crafting alike; the hard-coded bottle list in Survival is gone.
**Time:** each recipe has `seconds` (bandage 5, rags 4-6, soup 15, tea 20, rice 30, kartacze 60). Make starts a gauge
in the crafting list (Tom's bar art, in the accent colour); the world goes on meanwhile; nothing is used up until it's
done; one thing at a time. Esc stops it (the window stays); a hit (health drops by more than 0.5 in a frame, so not
hunger's slow ache) drops it and closes the window ("You're hit! ... will have to wait"); closing the window leaves it
unfinished. Why only then: losing half-cut rags to a bandit is too cruel for now; partial progress can come later.
Self-test `--autotest=craft`: a bandage made in 5 s (photographed half-way, rags untouched until the end), kartacze
interrupted by a -12 hit (window closed, potatoes kept), then rice (the bottle comes back), herbata, kartacze, and a
molotov refused for want of strong alcohol and made once there's vodka.
Next, agreed with Tom: heat sources (a cooker or a campfire instead of any lighter), then recipe books, skills, bigos
as an evolved recipe, freshness, building.

## D108 — Power and water: the grid is dead, local sources aren't

Tom asked (21 Nov 2026): power, electricity and water, or a total blackout? Agreed: **the grid and the mains are
gone** (it fits every lore idea so far, it keeps water and light scarce, and dark cities give the "Warsaw lights"
something to stand out against); what's left is local and has to be found or made. Water: dry taps; wells and hand
pumps from OSM (many in Podlasie villages); rivers and lakes give dirty water, safe once boiled (not tagged
clean_water, D107), risky to drink raw. Power: portable generators burning scavenged fuel (Tom is modelling one), car
batteries for a radio or a lamp, real solar installations from OSM as rare places that still have light. Cooking: gas
stoves with a canister, campfires; electric cookers are dead. A sandbox option, "utilities last 0–30 days" (default 0),
for a Zomboid-style start. Not built yet; folded into the heat-sources step. The cause of the blackout stays Tom's
and Noor's call.


## D109 — Generators: Tom's model, petrol, noise

Tom modelled a portable generator in Blender (Assets/Props/Generator) and a petrol can with Meshy
(Assets/Props/PetrolCan); "Yes, build it". `Power.Generators`: generated beside farms (about 1 in 7), works (1 in 10),
sheds (1 in 16) and houses (1 in 30) × the new sandbox setting `generators.frequency`, from the building's OSM id so
always the same; 1.2 m out from a wall, facing away, not on a road or in water. 45 % are dry, the rest hold up to
3.5 L; 35 % have a can of petrol beside them. Tank 6 L, a litre every 4 real minutes (the clock may be frozen, so not
game hours). **F** starts or stops one (a dry one says so and points to Tab); **hold F** lifts it: you carry it at
2.1 m/s at most, no sprint, no jump, no gun (`Walker.Burden`), and F puts it down; falling or getting in a car drops
it. Petrol: the new items `petrol_can` (5 L, 4.6 kg, tag fuel_petrol, leaves `empty_petrol_can`) on shelves (more
in storage rooms) and beside generators (F takes it); in the pack its button is "Pour", into the generator in reach.
A can isn't measured: less than half poured, it stays a can; more, it's used up (simple, not exact). Running: a lamp
on the frame (warm, 14 m), a soft exhaust puff, a shiver, and a synthesized four-stroke thrum (placeholder: a
recording at Assets/Sounds/generator.wav wins). It is heard: every 3 s bandits within 110 m investigate
(`BanditField.NoiseAt`), and new ones turn up twice as often while a running one is within 200 m. Within 40 m of your
safehouse it gives it power (a note now; fridges, radio and lit windows later). Moved, filled, started or emptied ones
are kept in user://generators.json per world. Tom's can is modelled 0.95 m tall; the game scales it to 35 cm.
Self-test `--autotest=generator` (best with `--place=Trześcianka`; user://generators.json backed up and restored, no
saving after): the found ones logged; a can taken; an empty one won't start; poured and started; a bandit 80 m off
investigates; fuel burns 0.035 L in 8.4 s; carried 6 m in 3 s; photographed by day, at night and carried.


## D111 — Heat for cooking: campfires, a camping stove, firewood from trees, river water

Agreed order with Tom (tags, time, then heat sources), and D108 (the grid is dead). Until now "something to light a
fire" was enough to cook kartacze anywhere. Now recipes can need **heat** (`"heat": true` in recipes.json: boiling
water, rice, soup, tea, kartacze, clean bandages): a **burning campfire within 2.2 m**, or a **camping gas stove with
a gas canister** in the pack (each use empties the canister one time in four). The crafting list shows which ("heat
(the campfire)") or asks for "a fire or a camping stove".
**Campfire** (`Power.Campfires`): the recipe "Build a campfire" (two armfuls of firewood, something for tinder, matches
or a lighter; 20 s) puts it 1.3 m in front of you, outdoors only (not indoors, in water, by a wall, or on another).
Ten minutes of fire; F adds an armful (+5 min, at most 30); burnt out, F with firewood and a light relights it. Looks:
a ring of stones, a teepee of logs, flames, sparks, smoke, a flickering warm light with shadows, a crackle (synthesized
placeholder; Assets/Sounds/campfire.wav wins). At night its light carries: bandits within 60 m come to look (a
generator is heard at 110 m, D109). Not kept between sessions. A recipe can now put something on the ground instead
of into the pack (`places`), and can keep what its inputs leave behind out of the pack (`leftovers: false`).
**Firewood** (Tom: "chopping down trees, breaking furniture"): stand at a tree with an axe, F chops for 6 s (a knock
every 0.7 s; a hit or a step away stops it) for two or three armfuls (2 kg each); the tree stays (instanced from the
map) and gives wood once, kept in user://trees_used.json. Also on shelves and in storage rooms. Breaking furniture
waits: a flat's furniture is one built model, so a broken wardrobe would stay standing.
**River water**: facing a river or lake with an empty bottle, F fills it ("bottle of river water"). Drunk raw it
quenches and costs 8 health ("It tastes of mud"), and you never drink it by yourself when thirsty; "Boil river water"
(saucepan, heat, 25 s) turns it into a bottle of water. Wells and pumps from OSM come later (the importer doesn't
keep them yet). Electric cookers on generator power wait too: the kitchens have no cooker to stand at.
New items: firewood, axe, camping stove, gas canister, river water (68 now). Self-tests: `--autotest=heat` (with
`--place=Białowieża`; user://trees_used.json backed up and restored): cooking refused without heat; a tree chopped
(3 armfuls, then nothing more); a fire built; rice and tea cooked over it; a bottle filled at a pond; river water
drunk raw (90 -> 82); boiling refused without heat, then done on the stove; the fire burns out and is relit;
photographed by day, at night and while chopping. `--autotest=craft` now cooks on a camping stove.


## D112 — Recipe books

Tom's agreed order, after heat: recipe books. Everyday recipes stay known (rags, bandages, the campfire, boiling
water, rice, soup, tea, kabanos on rusks); the special ones are taught by books found as loot (`"book"` on the recipe,
`"teaches"` on the book in items.json, which must agree: the loader says so if not). Three books, our own titles,
Tom may rename them: **Kuchnia podlaska** (a cookbook: kartacze, and new **placki ziemniaczane**, six potato pancakes
from two bags of potatoes and an onion, no flour, no oil), **Pierwsza pomoc** (a first-aid booklet: clean bandages,
and new **vodka for the wounds**, vodka into disinfectant), **Poradnik przetrwania** (a survival handbook: the
molotov). Loot: the cookbook in kitchen cupboards and on shelves, the booklet in medicine cabinets, drawers and
shelves, the handbook on shelves. Reading: the book's button is **Read**; 15 s with the same bar as crafting (a hit
or Esc stops it), from your pack alone (Tab). What you learn (`Items.Knowledge`) is yours for good: not in the pack,
so dying keeps it, and the book can be passed on or burnt (books are tinder). Kept per world in user://knowledge.json.
Reading a book you know says so. The crafting list ends with **Not learnt yet**: each recipe with the book it's in,
in green with "(in your pack: Read)" when you have it. Why show them: you know what to look for; Zomboid hides them,
we'd rather tease. Self-test `--autotest=books` (user://knowledge.json backed up and restored): kartacze refused
("learn it from Kuchnia podlaska"); the list photographed; the cookbook read in 15 s (photographed); kartacze and
placki made; the knowledge read back from the file; a second reading starts nothing. `--autotest=craft` learns every
book first.


## D113 — Skills, learnt by doing

Tom's agreed order, after recipe books. Five skills, levels 0-5 at 50/120/220/360/550 XP (GameData/skills.json, our
own numbers), each level worth its `per_level` fraction:
**Cooking** (cooking recipes: 10 XP, 15 from a book recipe): cooking 10% faster a level.
**First aid** (medical recipes; a bandage or medkit used: 8 XP): healing 10% more a level, medical crafts faster.
**Woodcraft** (chopping 10 XP, the campfire recipe): chopping 12% faster a level; from level 3 a tree gives an armful
more. **Mechanics** (a part removed 8 XP, fitted 12): workshop jobs 10% faster a level. **Shooting** (a hit on a
target or a bandit: 6 XP): aiming sway and hip-fire spread 8% less a level.
Recipes name the skill they train (`"skill"` in recipes.json) and the Make buttons show the time it takes you. A
level-up is said on the HUD ("Cooking: level 1. Cooking 10% faster."); the inventory shows the levels under your pack.
Kept per world in user://skills.json; like recipe books (D112) dying doesn't take them (Zomboid resets skills with a
new character; our survivor wakes up as the same person). Self-tests practise too, so whenever an autotest runs the
XP goes to user://skills_selftest.json and the player's file is never touched. Self-test `--autotest=skills`:
Cooking 0 -> 1 after five pots of rice (30 s -> 27 s, the note and the skills line photographed); a bandage at First
aid 3 heals 19.9 (15 x 1.3, plus the body's own mending); woodcraft 2 chops in 4.6 s, mechanics 1 fits an engine in
27 s, shooting 4 sways x 0.68; the XP read back from the file. Not yet: skill-locked recipes (Cooking 2 for bigos?),
reading skill books faster, a skills page of its own.


## D114 — Bigos, the evolved recipe

Tom's agreed order, after skills: bigos as an evolved recipe (Zomboid's idea, our own dish and numbers). A pot of
bigos carries its state in its item id, `bigos:<what's in it>:<servings>:<reheats>` (bigos:KMGPO:6:3), and
`Items.Bigos` makes its name, food and weight from that and the "bigos" template in items.json. Why the id and not
per-item data: moving, stacking, containers, saving and loot all work unchanged; real per-item data can come later if
freshness needs it. **Start a bigos**: sauerkraut, clean water and the saucepan (it becomes the pot), heat, 40 s: six
servings, "thin". **Add** (one recipe each, `"evolve": "bigos"` with `"adds"`): meat (any: luncheon meat, kabanos,
the new kiełbasa), dried mushrooms, prunes, fresh cabbage (and a knife), onion (and a knife); each once. **Reheat**
over heat, up to three times ("everyone knows bigos is best on the third day"). Quality 0-8 (ingredients beyond
sauerkraut, plus reheats): thin, decent, proper, very good, like babcia's; a serving's food is 14 + 4 × quality (42 for
very good). Eating takes a serving; the last gives the saucepan back. The name in lists stays short ("pot of bigos
(6 servings, very good)"); what's in it and the reheats are its tooltip (a new optional `Note` on items), and long item
names now wrap instead of widening the window. New items: sauerkraut, fresh cabbage, dried mushrooms, prunes, kiełbasa
(cupboards, fridges, kitchens, storage). Not yet: bigos going off (freshness, next), bigos for the whole safehouse.
Self-test `--autotest=bigos`: adding before there's a pot refused; started (the saucepan used); meat, mushrooms,
prunes, onion added, meat twice refused; reheated three times, a fourth refused; "very good", 42 food a serving;
photographed; into a box and back intact; six servings eaten (hunger 20 -> 62 each), the saucepan back.
Tom asked whether I'd like Project Zomboid's files for scope: no. Their item and recipe scripts are their lists and
numbers; keeping our design our own is easiest if they're never opened. Scope comes from playing it, the public wiki,
and Tom.


## D115 — Freshness: fresh, stale, rotten

Tom's agreed order, after bigos. The sky's clock stands still unless turned, so food ages by a **play clock**
(`Core.WorldClock`): it runs while the game does and waits when you quit (Zomboid-style); an hour played is a "day" for
food; kept per world in user://clock.json; a new world starts on **day 312** (the lore brainstorm's "Day 312"), which
also lets found food be months old. Items that go off have `fresh` and `stale` days in items.json (kiełbasa 4+4,
cooked food 2+2, hot soup 1+1.5, tea half a day, potatoes 20+20, onion 25+25, cabbage 12+10, jam 60+60, bigos 6+6);
tins, jars, dried things and drinks in bottles don't. The sandbox's new **food.spoil** (0 never, up to 3) scales it.
Each stack remembers when it was made (`ItemStack.Made`; stacks of different ages stay apart, the oldest is used
first, moving keeps the age, containers save it). **Eating**: stale fills 60 %, rotten 25 % and costs 10 health ("It's
gone off"); hunger and thirst never take rotten food by themselves and eat what's turning first; the inventory shows
"kiełbasa (stale)" and "kiełbasa (rotten)" as their own rows (their own colours, their own Eat button). **Recipes**
don't take rotten food. **Found food** has an age: anything from just made to well past rotten (40 test fridges: 14
fresh, 16 stale, 4 rotten). **Bigos**: adding keeps the pot's age, reheating makes it fresh again; eating a serving
keeps the rest's age. Also fixed: hunger eating a pot of bigos by itself used to take the whole pot. Not yet: powered
fridges slowing it down (a generator nearby: D109), salting and drying, rotten food for compost. Self-test
`--autotest=fresh` (clock and skills in scratch files, as for every autotest).

## D116 — Evolved dishes are data

Tom: "making this game data-driven? So we can add recipes and other items without touching the code." It mostly was
(items, recipes, loot, skills, sandbox, cars and regions are JSON since D89); bigos's ingredient list was the one
shortcut in code. Now an evolved dish is a template item with an `evolved` block (ingredients by letter, base,
servings, max reheats, food per quality step, quality names, what the empty pot becomes) plus recipes that `evolve`
it; `Items.Evolved` (was Bigos.cs) reads them all. A żurek or a krupnik pot is JSON only. Code is still needed for a
new *kind* of behaviour (heat, placing a campfire, carrying a generator), never for more of one.


## D117 — The rucksack and the notebook (Tom's pick "A + B")

Tom: "a cool interface for crafting and inventory management", without breaking what works. Three directions were
mocked up (A the rucksack, B "zeszyt w kratkę", C a workbench); Tom chose A + B. The window's workings (moving,
using, timed crafting, reading, interrupts, the tests' hooks) stay as they were; only its faces changed, behind two
small interfaces (`IItemColumn`, `ICraftColumn`), so the old lists remain with `--ui=classic`.
**The rucksack** (`UI.RucksackColumn`): your side and a container's side alike: a weight bar (green, amber, red),
category tabs with counts (All, Food, Drink, Medical, Tools, Materials, Fuel, Books, Junk: only those you have), item
tiles with a freshness strip (D115), and a paper-label card for the picked item: what it is, weight, food/drink/heal,
how fresh ("fresh, 6 days left", "stale: eat it soon", "rotten"), its note (a bigos's contents), what it counts as in
recipes (its tags), and its actions (Eat/Drink/Read/Pour/Use, Take/Put one or all). Click: the card; double-click:
use it, or move the pile when a container's open; right-click: move one; drag across as before. Icons come from
`Assets/UI/icons/items/<item id>.png` when Codex's set arrives (docs/ICON_ASSETS_BRIEF.md: 78 items, categories,
freshness badges, notebook art); until then a tile shows a badge in its category's colour with the item's initials.
**The notebook** (`UI.NotebookColumn`): squared paper with a red margin and the cover's edge, handwriting in Caveat
(OFL, Polish letters; downloaded with Tom's OK, Assets/Fonts), Polish headings (Tom: "Polish headings only"):
Przepisy, sticker tabs Wszystko / Gotowanie / Bigos / Apteczka / Ogień / Materiały / Kłopoty, and "Jeszcze nie umiem"
for recipes still to learn. A recipe you can make is ringed in pencil; what you have is ticked in green ink, what you
lack in red; what it makes in amber; its flavour line in pencil; "Make · 27 s" in a hand-drawn ring. The timed craft
and reading show as a line of ink over a hatched pencil bar. Self-test `--autotest=ui` photographs the Tab window,
an item's card and a cupboard beside the rucksack; craft, books and bigos tests pass on it, and craft on
`--ui=classic`. Next: Codex's icons and notebook art in place of the drawn ones.


## D118 — Codex's icons and notebook art in place

Tom brought `baltic_drive_icons_v1.zip` (Codex, from docs/ICON_ASSETS_BRIEF.md): all 78 item icons (none missing,
none extra), 9 category icons, 3 freshness badges and 20 notebook pieces, transparent PNGs at the asked sizes, with a
manifest; one painted family (a glass of tea in its koszyczek, kartacze, the kiełbasa horseshoe, a folk-pattern
Kuchnia podlaska). In `Assets/UI/icons/` (README: Tom's own art) are the pieces the game uses: every icon and badge,
and of the notebook the left page, the six sticker tabs, the pencil tick, cross, ring and underline, and the coffee
ring; their nine-slice margins are merged into Assets/UI/manifest.json, so `Skin.Box` dresses them like the rest. The
rest (the other page, spine, cover edge, strike, question mark, sticky note, stamp, tileable paper) stays in
data/inbox until something needs it. Icons are shrunk once to the size they're drawn at (smooth, no shimmer). The
rucksack: item icons on the tiles (stale and rotten wear the wilted leaf or the fly), category icons on the tabs.
The notebook: the painted page (its own squares and red margin), sticker tabs, "Przepisy" underlined in pencil,
pencil ticks for what you have and crosses for what you lack, the pencil ring round what you can make and as the Make
button, a faint coffee ring by the title. The drawn versions remain as the fallback when an image is missing.
