# Furniture models

`furniture.json` says which model stands in for each kind of furniture that BadBuilding's NoorFengShui places in the
flats (D87). It's a list per kind, and the game picks between the entries by each piece's id. The game puts each model
on the piece's footprint, turned to face into the room and standing on the floor. A kind with no entry keeps
BadBuilding's generated shape. So does a kind whose model file isn't there: the Synty packs are licensed and are never
in this repository.

## Using your own model

1. Put the model in this folder (`Assets/Furniture/`), for example `bed_double_01.glb`. Any `.glb`, `.gltf`, `.tscn`
   or mesh `.res` works. The textures go next to it.
2. Add an entry under its kind in `furniture.json`:

   ```json
   "bed_double": [ { "model": "res://Assets/Furniture/bed_double_01.glb", "fit": "footprint", "yaw": 0 } ]
   ```

3. Run the game, walk into a block of flats (F7 on), and look. The self-test photographs a piece of each kind:
   `--autotest=interior --start=68567,639452 --buildings=bb --look=bed_double,sofa`.

## The fields

- **yaw**: degrees to turn the model so that its front faces +Z, the way the piece faces into the room. Synty props
  need 0.
- **fit**:
  - `stretch` scales the model to the footprint and to the piece's height.
  - `footprint` scales it to the footprint and keeps its own height.
  - `uniform` keeps its proportions, fits it inside the footprint and stands it at the wall.
- **Footprints**: the sizes are along the wall × out from the wall × height, in metres.

| Kind | Footprint |
|---|---|
| `bed_double` | 1.6 × 2.05 × 0.6 |
| `bed_single` | 0.95 × 2.05 × 0.6 |
| `nightstand` | 0.45 × 0.4 × 0.5 |
| `wardrobe` | 1.2–1.8 × 0.6 × 2.1 |
| `desk` | 1.2 × 1.1 × 0.76 (the chair is included) |
| `bookshelf` | 0.9 × 0.35 × 1.9 |
| `sofa` | 2.0 × 0.9 × 0.85 |
| `armchair` | 0.85 × 0.85 × 0.8 |
| `tv_unit` | 1.6 × 0.45 × 0.5 |
| `coffee_table` | 1.0 × 0.55 × 0.42 |
| `dining_table` | 1.4 × 1.8 × 0.76 (the chairs are included) |
| `counter` | 1.2–3.0 × 0.6 × 0.9 |
| `fridge` | 0.6 × 0.65 × 1.8 |
| `kitchen_table` | 0.8 × 1.8 × 0.76 (the chairs are included) |
| `bath` | 1.6 × 0.72 × 0.55 |
| `shower` | 0.85 × 0.85 × 2.0 |
| `toilet` | 0.45 × 0.68 × 0.8 |
| `basin` | 0.6 × 0.45 × 0.88 |
| `washer` | 0.6 × 0.6 × 0.85 |
| `shoe_cabinet` | 0.9 × 0.35 × 0.9 |
| `coat_rack` | 0.8 × 0.3 × 1.8 |
| `shelves` | 1.2 × 0.45 × 2.0 |

`tools/prefab_sizes.gd` prints a model's size, so you can choose its fit:

```
godot --headless --path . --script res://tools/prefab_sizes.gd -- res://Assets/Furniture/bed_double_01.glb
```
