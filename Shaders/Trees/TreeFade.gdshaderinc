// Tree hand-over by distance (round 30, docs/PERFORMANCE_TREES.md): 3D trees, impostors and tree cards cross-fade
// on circles around the camera with one shared screen-space dither, so each pixel of the band shows either the nearer
// or the farther kind, never both and never neither: no square rings, no popping. Distances are measured from the
// main camera (tree_camera, set every frame by C#) in every pass, so shadow passes fade exactly like the picture.
// tree_ranges: x = where 3D trees hand over to impostors (m), y = where impostors hand over to cards (m),
// z = half-width of each cross-fade band (m), w = 1 while the tree field draws the trees (0: off, e.g. the valley game).
global uniform vec4 tree_ranges;
global uniform vec4 tree_camera;
// Detailed → lighter model of the same 3D trees (build 51; was Godot's visibility-range fade, which draws a fading
// object in the transparent pass, out of the depth buffer, so full-screen effects such as the distant rain painted
// over crowns in the band — Tom's dump 104752): tree_lod.x = the hand-over distance (m), y = half-width of the band.
// tree_lod_role per MultiMesh: 1 = the detailed model (leaves the band), 2 = the lighter one (takes it), 0 = no hand-over.
global uniform vec4 tree_lod;
// The role comes from the instance's custom data (INSTANCE_CUSTOM.x, set by TreeField; 0 for anything else): each
// shader's vertex() does `tree_lod_role = INSTANCE_CUSTOM.x;`. It was an instance uniform (D86), but every instance of
// such a shader holds 16 slots of Godot's global shader buffer: thousands of tree batches overflowed it ("Too many
// instances using shader instance variables"), and those batches lost the hand-over. Shaders without the hand-over
// define TREE_NO_LOD_ROLE first.
#ifndef TREE_NO_LOD_ROLE
varying flat float tree_lod_role;
#endif

float tree_bayer(vec2 frag) {
	ivec2 p = ivec2(mod(frag, 4.0));
	int i = p.x + p.y * 4;
	// 4×4 ordered dither, 0..15 / 16
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	return (m[i] + 0.5) / 16.0;
}

// 0 → 1 across the band centred on r.
float tree_band(float dist, float r) {
	return clamp((dist - (r - tree_ranges.z)) / max(2.0 * tree_ranges.z, 1e-3), 0.0, 1.0);
}

float tree_distance(vec3 world) { return length(world.xz - tree_camera.xz); }

// The nearer kind leaves the band's pixels as the farther kind takes them.
bool tree_fade_out(vec3 world, vec2 frag, float r) {
	return tree_ranges.w > 0.5 && tree_bayer(frag) < tree_band(tree_distance(world), r);
}
bool tree_fade_in(vec3 world, vec2 frag, float r) {
	return tree_ranges.w > 0.5 && tree_bayer(frag) >= tree_band(tree_distance(world), r);
}
// The same dither between the detailed and the lighter model (each pixel shows one or the other, both stay opaque).
// Measured in 3D, as Godot measures the visibility ranges that switch each model's batches on and off (TreeField pads
// them by a cell's size): horizontally, from 120 m up, the detailed model's batches were already off below the camera
// while the lighter one still left the band to it — a hole in the forest under the player (Tom, dump 220524).
#ifndef TREE_NO_LOD_ROLE
bool tree_lod_out(vec3 world, vec2 frag) {
	if (tree_lod_role < 0.5 || tree_lod.y <= 0.0) return false;
	float t = clamp((length(world - tree_camera.xyz) - (tree_lod.x - tree_lod.y)) / (2.0 * tree_lod.y), 0.0, 1.0);
	return tree_lod_role < 1.5 ? tree_bayer(frag) < t : tree_bayer(frag) >= t;
}
#endif
