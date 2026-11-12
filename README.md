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

Keys: W/S drive and brake, A/D steer, Space handbrake, C camera, H lights, R back onto the road, F free camera
(right mouse to look, WASD/QE, Shift/Ctrl faster).

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
