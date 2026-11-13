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
