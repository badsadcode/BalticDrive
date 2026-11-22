# Models for things you build (D128)

The game builds these from box stand-ins until a model arrives. Drop a GLB at the path below and it's used at once,
with no code change: the ghost while placing, and the built thing.

All of them:

- **Units and axes:** metres, +Y up, origin at the **bottom centre** of the footprint.
- **Front:** faces **+Z**. That's the side that faces you when you place it, and the side a crate opens on.
- **Size:** keep to the size given. The fit check and the collider use these numbers, not the mesh.
- **Detail:** low poly, flat colours or a small texture, like the generator and the pump. One mesh is fine.
- **Ghost:** the game repaints every mesh in see-through green or red while placing, so no special material is needed.

| File (`Assets/Props/Built/…`) | What | Size (width × height × depth) | Notes |
|---|---|---|---|
| `workbench.glb` | A home-made workbench: planks on four legs, a shelf underneath | 1.6 × 0.95 × 0.7 m | Rough and nailed together, not shop-bought. A vice or a clamp on one corner would be lovely. |
| `crate.glb` | A wooden crate with a lid | 0.9 × 0.6 × 0.6 m | Slatted planks; the lid on top. Holds 50 kg. |
| `rain_collector.glb` | A plastic barrel under a sheet on four posts, the sheet sloping into the barrel | 1.2 × 1.8 × 1.2 m | A blue 200-litre barrel is the classic. The sheet can sag. |
| `barricade.glb` | Boards nailed across a doorway or window: two upright battens, four or five boards across, one on the diagonal | 1.3 × 1.6 × 0.12 m | Thin: it's nailed flat against a wall or across an opening. Odd board lengths look right. |

Item icons the inventory would like, in the same style as the Codex set (128 px, transparent): **plank** (*deska*),
**plastic barrel** (*plastikowa beczka*), **set of barricade boards** (*deski na barykadę*), and **siphon hose**
(*wężyk do benzyny*, from D123).
