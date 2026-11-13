# Woodland runtime assets

These 51 GLBs are copies of VegetationPack/Exports, integrated in game 0.6.3. Authoring source: VegetationPack/Blender/Thornwake_Woodland.blend (Blender 5.1+). Original textures were made with built-in image generation; source maps and exact prompts are retained in VegetationPack/Textures/Source.

Godot's per-file .import settings disable automatic mesh LOD generation because the pack already includes authored LOD0/1/2 meshes. Embedded maps are extracted by Godot. Runtime material filtering is explicitly Nearest; foliage is alpha-scissor, not soft blending. Do not replace this with linear filtering.

Game/Core/Woodland.cs maps persistent resource IDs to species/variants, rotations, trunk radii and LOD thresholds. Game/World/WoodlandAssets.cs loads and shares imported meshes/materials, creates stumps, and manages crown cutaways. Planted pine models also use a stable identity mapping. No model name is written into world saves.

To update art, replace the matching GLB filenames and reimport the game. Keep metre scale, ground origin, identity transforms and material names containing Bark for trunk geometry; foliage must remain separate from bark to support cutaways. Check all three LODs in gameplay before publishing. The woodland smoke scenario validates exports, filtering, cutaways, collision and reload stability.