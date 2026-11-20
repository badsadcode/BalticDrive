# Making a car for Baltic Drive (D96)

Tom's cars: Noor's concept art → Meshy (low-poly, segmented; commercial use under Tom's subscription) → cleanup in
Blender → one GLB per car. This page is what the game expects, so a car works the first time.

## Format
* **glTF Binary (`.glb`)**, one file per car. Not `.blend`: Godot reads GLB without Blender installed, and NoorEngine
  will read the same file (Noor Interchange).
* Drop new cars in `data/inbox` (git-ignored). Once one works, it is copied to `Assets/Vehicles/` with a licence note.

## Size, direction, origin (Noor Interchange §2)
* Real size in metres (a hatchback is about 4.2 m long), scale 1.0, transforms applied.
* The nose points to Blender's **−Y** (the exporter turns it into glTF +Z, the spec's "front").
* The car's origin on the ground (z = 0) in the middle of the footprint.

## Objects
Separate objects, named like this, each with its **origin where it turns**:

| Object | Origin (pivot) | Notes |
|---|---|---|
| `Body` | the car's origin | the shell with openings for the doors, hood and trunk; the interior behind them |
| `Door_FL`, `Door_FR`, `Door_RL`, `Door_RR` | on the hinge line (the door's front edge) | modelled closed; a 3-door car has only `Door_FL`, `Door_FR` |
| `Hood` | on its hinge (by the windshield) | |
| `Trunk` | on its hinge (top of the rear window) | the hatch on a hatchback |
| `Wheel_FL`, `Wheel_FR`, `Wheel_RL`, `Wheel_RR` | exactly at the hub centre | tyre and rim only |
| `Windshield`, `RearWindow`, `Window_FL`, `Window_FR`, `Window_RL`, `Window_RR` | anywhere | they crack, break and get replaced |
| `Seat_FL`, `Seat_FR`, `Seat_Rear` | anywhere | |
| `Engine`, `Battery` | anywhere, in place under the hood | seen when the hood opens; highlighted and removed in the workshop |
| `Brake_FL` … `Brake_RR`, `Spring_FL` … `Spring_RR` | at the wheel | optional |
| `Headlight_L`, `Headlight_R`, `Taillight_L`, `Taillight_R` | anywhere | emissive material: the game switches them |
| `SteeringWheel` | on the column's axis | |

These match the car's parts in the game (`CarParts`, D67): engine, battery, tyres, brakes, suspension, windshield,
windows, doors, hood, trunk, seats.

## Rear-engined cars (a Maluch-like one)
Name the lids by where they are: `Hood` at the front, `Trunk` at the back, whatever is under them. The game looks at
where `Engine` sits: the lid over the engine opens onto the engine, the other one onto the luggage (a Maluch's small
front boot). A two-door car has only `Door_FL` and `Door_FR`.

## Materials
A handful of PBR materials, textures embedded: **`Paint`** (the body colour, light grey or white: the game repaints
every car), `Glass`, `Chrome`, `Rubber`, `Interior`, `Lights`. About 15–40k triangles per car.

## Blender export
glTF 2.0 → **glTF Binary**; Include: Custom Properties on; Transform: +Y Up; Geometry: Apply Modifiers;
**no Draco compression** (Godot can't read it); no animations (the game opens things itself).

## What the game does with it
* Doors, hood and trunk swing from their hinges (F at the part).
* The trunk (about 50 kg), the rear seat (30 kg) and a glovebox (3 kg) are containers in the inventory window (D89);
  the trunk only with its lid open.
* The workshop lights up the part you work on (the real engine, the real wheel); a removed part is gone, a missing door
  leaves a hole, a broken window shows.
* The paint is tinted per car; dirt and rust go over wrecks (D69).

## Noor Interchange
The spec has the vehicle pivot rule and a `door` marker, but no vehicle part names (and `trunk` means a tree trunk
there) and no `container` marker. Proposed for v0.6, not yet in the spec: parts `body`, `wheel`, `hood`, `boot`, `seat`,
`glass`, `light`, `engine`, `battery`, `steering`; marker `container` (size, weight limit). Until it's accepted the
object names above carry it, which the spec allows as the fallback.
