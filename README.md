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
* `--autotest=leads` — a lead to a guarded stash (D54): map, arrival, guards, stash, the next lead.
* `--autotest=lean` — leaning left and right, first and third person (D55).
* `--autotest=death` — dying, waking elsewhere, going back for your things, a safehouse (D55).
* `--autotest=farbench [--at=x,z]` — far buildings shown and hidden 90 m up: GPU time per frame (D61).
* `--autotest=banditpath [--nav=0]` — a bandit behind a building finds a way round to you (D62).
* `--autotest=grassbench` — grass on and off at the grassiest spot near the start: GPU time per frame (D59).
* `--autotest=roadscan [--radius=3000]` — lists bends tighter than their road is wide, with a `--views=` line (D57).
* `--autotest=ragdoll` — a bandit and the walker falling as ragdolls, an origin shift under a body (D56).
* `--autotest=knockdown --start=68600,639300` — the car knocks a bandit down; it lies, gets up, engages; a hurt one dies (D80).
* `--autotest=interior --start=68600,639300 --buildings=bb` — into a block of flats: the door, the landing, up the stairs on foot, flats, a top-floor window, and out (D81). Options: `--interior-angle=M` (come at the door from the side), `--interior-want=loggia`, `--look=sofa,bed_double` (photograph pieces), `--cams="x,y,z,yaw,pitch;..."` (stand where an F9 dump was taken), `--interior-safehouse` (claim a flat, die, wake in it; D88).

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
