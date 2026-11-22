# Baltic Drive

Drive the real Baltic — Poland, Lithuania, Latvia, Estonia, Finland, Sweden, Norway and Denmark — built from
OpenStreetMap and real elevation. Godot 4.7.2 .NET (C#), Forward+, Jolt physics.

Baltic Drive combines two earlier projects:

* **Dead Miles** — the OpenStreetMap importer, tile format, road and building meshes, routing and map screen.
* **Thornwake** — the world machinery: nested-LOD terrain streaming with worker-thread builds and frame budgets, the
  moving (rebasing) origin, and the water shaders.

**Milestone 1** (in progress): województwo mazowieckie, drivable on that machinery with real ground heights.

## Running

1. Build a map (see [tools/osm_import/README.md](tools/osm_import/README.md)) into `map/` (git-ignored).
2. Open the project in Godot 4.7.2 .NET and run, or from a terminal:

```bash
godot --path . -- --map=mazowsze --place=Warszawa
```

Keys: W/S drive and brake, A/D steer, Space handbrake, C camera, H lights, R back onto the road, F get in or out,
F8 free camera (right mouse to look, WASD/QE, Shift/Ctrl faster). On foot: WASD, Shift run, Ctrl crouch, Q/E lean,
V first/third person, 1/2/3 guns, F crates and the car, H medkit, M map.

Command-line options (after `--`): `--map=NAME`, `--mapdir=PATH`, `--place=NAME` or `--start=x,z` (world metres),
`--fly`, and the tests below.

## Tests

The game tests itself (`Game/Tests.cs`); results go to the log as `[Test]` lines, screenshots to `--shots=DIR`.

```bash
godot --path . -- --map=mazowsze --views="68891:639700:60:0:-12;68891:639498:400:30:-35" --shots=C:/temp/shots
```

* `--views=x:z:above:yaw:pitch;...` — go to each view, wait for everything to stream in, save a screenshot.
* `--autotest=fly --speed=40` — fly 6 km at 40 m/s, report frame times and frames without ground collision.
* `--autotest=drive` — the car keeps to the road for 20 s; reports speed and time off the road.
* `--autotest=craft` — the crafting list (Tab) photographed; a bandage made in real time (photographed half-way), kartacze interrupted by a hit; rags, rice, herbata, kartacze made by tags; a molotov refused, then made with vodka (D106, D107).
* `--autotest=generator` (with `--place=Trześcianka`) — generators found nearby; a can taken, an empty one refused, petrol poured, started (a bandit 80 m off comes to look), fuel burnt, carried and put down; day, night and carry shots (D109).
* `--autotest=heat` (with `--place=Białowieża`) — cooking needs heat: a tree chopped for firewood, a campfire built, rice and tea cooked on it, a bottle filled at a pond, river water drunk raw and boiled on a camping stove, the fire burns out and is relit; day, night and chopping shots (D111).
* `--autotest=books` — recipe books: kartacze refused until the cookbook is read (15 s, photographed), then kartacze and placki made; knowledge kept in a file (D112).
* `--autotest=skills` — Cooking levels up after five pots of rice (faster, the note photographed); First aid, Woodcraft, Mechanics and Shooting effects checked; XP read back. Every autotest keeps its XP in user://skills_selftest.json (D113).
* `--autotest=bigos` — the bigos test (yes): a pot started, four ingredients added, reheated three times, photographed, carried into a box and back, eaten serving by serving until the saucepan comes back (D114).
* `--autotest=fresh` — food going off on the play clock: kiełbasa fresh, stale (eaten at 60 %), rotten (recipes skip it, hunger skips it, eating it hurts); fridge loot of mixed ages; a stale bigos reheated fresh (D115).
* `--autotest=ui` — the rucksack and the notebook photographed: Tab with a varied pack, an item card, a cupboard beside the rucksack (D117). `--ui=classic` brings back the old lists.
* `--autotest=wells` (a map built with wells, e.g. `--map=podlaskie_wells --start=214000,536000`) — wells, hand pumps and springs counted and photographed, a bottle filled at a well (D121).
* `--autotest=takecar` — out of the car, take the nearest wreck if it runs (else why not), drive it, back to the old car (D99). With `--sb.wrecks.runners=1` every wreck runs.
* Sandbox settings for any run: `--sandbox=apocalypse` (a preset from `GameData/sandbox.json`) or `--sb.loot.abundance=2` (one setting).
* `--autotest=tip` — double lane changes at 50/70/90 km/h, full-lock turns, a handbrake turn: the worst roll and any flips (D97). `--grip=K` and `--antiroll=0` for tuning.
* `--autotest=leads` — a lead to a guarded stash (D54): map, arrival, guards, stash, the next lead.
* `--autotest=lean` — leaning left and right, first and third person (D55).
* `--autotest=death` — dying, waking elsewhere, going back for your things, a safehouse (D55).
* `--autotest=farbench [--at=x,z]` — far buildings shown and hidden 90 m up: GPU time per frame (D61).
* `--autotest=banditpath [--nav=0]` — a bandit behind a building finds a way round to you (D62).
* `--autotest=grassbench` — grass on and off at the grassiest spot near the start: GPU time per frame (D59).
* `--autotest=roadscan [--radius=3000]` — lists bends tighter than their road is wide, with a `--views=` line (D57).
* `--autotest=ragdoll` — a bandit and the walker falling as ragdolls, an origin shift under a body (D56).
* `--autotest=knockdown --start=68600,639300` — the car knocks a bandit down; it lies, gets up, engages; a hurt one dies (D80).
* `--autotest=interior --start=68600,639300 --buildings=bb` — into a block of flats: the door, the landing, up the stairs on foot, flats, a top-floor window, and out (D81). Options: `--interior-loot` (search furniture, take things, the pack; D89), `--interior-angle=M` (come at the door from the side), `--interior-want=loggia`, `--look=sofa,bed_double` (photograph pieces), `--cams="x,y,z,yaw,pitch;..."` (stand where an F9 dump was taken), `--interior-safehouse` (claim a flat, die, wake in it; D88).

## Layout

| Path | What |
| --- | --- |
| `Game/Core/` | Projection (world ↔ lat/lon), hashing, logging — plain C# |
| `Game/Map/` | Tile reader (`MapData`), map queries (`MapWorld`, `MapContext`), road/building mesher, skyline, router (from Dead Miles) |
| `Game/World/` | Moving origin, terrain streamer and tile builder (Thornwake's scheme), map content streamer |
| `Game/Vehicle/` | The car |
| `Shaders/` | Terrain, road, building (Dead Miles), water (Thornwake) |
| `Assets/` | Models in use (Quaternius, CC0) |
| `tools/osm_import/` | Python importer: OSM extract + elevation → tile packs |
| `tools/dem/` | Elevation download (GUGiK NMT for Poland) |
| `docs/` | Decisions log, tile format, handoff |
| `data/`, `map/` | Local only (git-ignored): sources, elevation, asset drops, built map packs |

## Licences

Map data © OpenStreetMap contributors, ODbL 1.0 — the generated packs are a derived database under the same licence
and the credit must stay visible in the game. Elevation for Poland: GUGiK (free for any use). Models: Quaternius, CC0.
