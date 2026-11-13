// Tree impostor (round 30, docs/PERFORMANCE_TREES.md): a distant tree as one camera-facing quad showing the tree's
// own model, baked at start-up from 8 sides and from above (TreeImpostors.cs). The two side views nearest to the
// camera's direction (relative to the tree's own rotation) are blended; from high up the top view takes over. Lit like
// the 3D crowns with the baked normals, leaf translucency and the sun's forward glow; sways with the wind as a whole.
// Each instance is the same tree as the 3D one (position, rotation, scale; species in INSTANCE_CUSTOM.r), so the
// hand-over from the 3D ring to the impostor ring keeps every tree where it was.
shader_type spatial;
render_mode skip_vertex_transform, cull_disabled, diffuse_burley, specular_disabled;

uniform sampler2DArray albedo_frames : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2DArray normal_frames : filter_linear_mipmap, repeat_disable;
// Per species: x = view size (m, square), y = bottom of the view above the tree's origin (m).
uniform vec4 species_size[32];
uniform int views = 8;
// Matched by eye to the 3D trees (impostors have no self-shadowing inside the crown).
uniform float shade = 0.86;
global uniform float leaf_translucency;
global uniform vec4 sun_glow;
global uniform vec3 sun_dir_world;
global uniform vec4 wind;
#define TREE_NO_LOD_ROLE // (no detailed ↔ lighter hand-over here: no instance uniform)
#include "res://Shaders/Trees/TreeFade.gdshaderinc"

varying vec3 card_world;
varying flat int layer0;
varying flat int layer1;
varying float blend;
varying float steep;
varying float view_angle0;
varying float view_angle1;
varying float top_rotation;
varying float yaw;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 axis_x = MODEL_MATRIX[0].xyz;
	float scale = length(axis_x);
	yaw = atan(-axis_x.z, axis_x.x);
	int species = int(INSTANCE_CUSTOM.r + 0.5);
	vec4 sz = species_size[species];
	float size = sz.x * scale;
	vec3 to_camera = CAMERA_POSITION_WORLD - origin;
	// In the sun's shadow pass (an orthographic view) the card faces the sun itself, every one the same way: facing the
	// shadow camera's position — which moves from split to split — they turned all ways, some edge-on (no shadow), so the
	// shadows pointed different ways (D279, Tom).
	if (PROJECTION_MATRIX[3][3] > 0.5) to_camera = sun_dir_world * 1000.0;
	vec3 view_dir = normalize(to_camera);
	// The camera's direction seen from the tree (its own rotation removed) picks the two nearest side views.
	float phi = atan(to_camera.x, to_camera.z);
	float local = (phi - yaw) / (6.2831853 / float(views));
	local = mod(local, float(views));
	float f0 = floor(local);
	blend = local - f0;
	int v0 = int(f0) % views, v1 = (v0 + 1) % views;
	layer0 = species * (views + 1) + v0;
	layer1 = species * (views + 1) + v1;
	view_angle0 = float(v0) * 6.2831853 / float(views) + yaw;
	view_angle1 = float(v1) * 6.2831853 / float(views) + yaw;
	top_rotation = phi - yaw;
	steep = smoothstep(0.62, 0.9, view_dir.y);
	// Upright around the vertical axis; facing the camera fully when seen from above.
	vec3 right = normalize(vec3(to_camera.z, 0.0, -to_camera.x) + vec3(1e-4, 0.0, 0.0));
	vec3 up = normalize(mix(vec3(0.0, 1.0, 0.0), cross(view_dir, right), steep));
	vec3 centre = origin + vec3(0.0, sz.y * scale + size * 0.5, 0.0);
	vec3 world = centre + right * VERTEX.x * size + up * (VERTEX.y - 0.5) * size;
	// Sway: the whole crown leans downwind, more toward the top.
	float h = clamp(VERTEX.y, 0.0, 1.0);
	float phase = origin.x * 0.13 + origin.z * 0.17;
	world += vec3(wind.x, 0.0, wind.z) * h * h * size * 0.012 * (0.4 + wind.y) * (0.75 + 0.25 * sin(TIME * 0.9 + phase));
	card_world = world;
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = (VIEW_MATRIX * vec4(view_dir, 0.0)).xyz;
}

// The baked view-space normal of a side view, back in world space (that view's camera looked at the tree from angle a).
vec3 side_normal(vec4 encoded, float a) {
	vec3 n = encoded.rgb * 2.0 - 1.0;
	vec3 cz = vec3(sin(a), 0.0, cos(a));
	vec3 cx = vec3(cos(a), 0.0, -sin(a));
	return normalize(cx * n.x + vec3(0.0, 1.0, 0.0) * n.y + cz * n.z);
}

void fragment() {
	if (tree_fade_in(card_world, FRAGCOORD.xy, tree_ranges.x) || tree_fade_out(card_world, FRAGCOORD.xy, tree_ranges.y)) discard;
	vec2 uv = UV;
	vec4 a0 = texture(albedo_frames, vec3(uv, float(layer0)));
	vec4 a1 = texture(albedo_frames, vec3(uv, float(layer1)));
	vec4 n0 = texture(normal_frames, vec3(uv, float(layer0)));
	vec4 n1 = texture(normal_frames, vec3(uv, float(layer1)));
	vec4 albedo = mix(a0, a1, blend);
	vec3 n_world = normalize(mix(side_normal(n0, view_angle0), side_normal(n1, view_angle1), blend));
	if (steep > 0.001) {
		// Top view, turned with the tree: image x = the tree's +x, image up = its -z.
		vec2 c = uv - 0.5;
		float r = top_rotation;
		vec2 t = vec2(c.x * cos(r) - c.y * sin(r), c.x * sin(r) + c.y * cos(r)) + 0.5;
		int top = (layer0 / (views + 1)) * (views + 1) + views;
		vec4 at = texture(albedo_frames, vec3(t, float(top)));
		vec3 nt = texture(normal_frames, vec3(t, float(top))).rgb * 2.0 - 1.0;
		// Top camera basis in tree space: x = +x, y = -z, z = +y; then the tree's rotation.
		vec3 tn = vec3(nt.x, nt.z, -nt.y);
		tn = vec3(tn.x * cos(yaw) + tn.z * sin(yaw), tn.y, -tn.x * sin(yaw) + tn.z * cos(yaw));
		albedo = mix(albedo, at, steep);
		n_world = normalize(mix(n_world, tn, steep));
	}
	// Leaves thin out in the small mip levels (averaged alpha falls below the cut): keep their coverage.
	float lod = textureQueryLod(albedo_frames, uv).y;
	ALPHA = clamp(albedo.a * (1.0 + lod * 0.45), 0.0, 1.0);
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	vec3 colour = albedo.rgb * shade;
	ALBEDO = colour;
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
	ROUGHNESS = 0.93;
	BACKLIGHT = colour * vec3(0.9, 1.08, 0.55) * leaf_translucency;
	vec3 view_ray = normalize(card_world - CAMERA_POSITION_WORLD);
	float forward = pow(clamp(dot(view_ray, sun_dir_world), 0.0, 1.0), 5.0);
	EMISSION += colour * vec3(0.95, 1.1, 0.5) * sun_glow.rgb * forward * leaf_translucency * 0.32;
}
