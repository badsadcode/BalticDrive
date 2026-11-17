// Grass blades near the camera (D59, Trees/GrassField.cs; after Thornwake's Grass.gdshader): both faces lit, light
// through the blades against the sun, a breeze leaning the tips with gust waves sweeping across the field, and the
// far third of the range thinned out clump by clump so the edge never shows.
// COLOR.r = height along the blade (0 root … 1 tip). INSTANCE_CUSTOM: x dryness, y random, z density.
shader_type spatial;
render_mode cull_disabled, specular_disabled, diffuse_burley;

#include "res://Shaders/common.gdshaderinc"

uniform float range = 60.0;
global uniform float leaf_translucency;
global uniform vec4 sun_glow;
global uniform vec3 sun_dir_world;
global uniform vec4 wind; // x, z: direction; y: strength; w: gust

varying float along;
varying vec3 tint;
varying vec3 wpos;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec2 fixed_xz = origin.xz + world_offset;   // stays with the ground when the origin moves
	along = COLOR.r;
	// thinning out toward the edge of the range: each clump shrinks away at its own distance
	float d = distance(origin, CAMERA_POSITION_WORLD);
	float cut = mix(0.62, 1.0, INSTANCE_CUSTOM.y) * range;
	float keep = 1.0 - smoothstep(cut - 6.0, cut, d);
	VERTEX *= keep;
	// wind: lean downwind with travelling gust waves, and a slow sway of each clump
	float phase = fract(fixed_xz.x * 0.21 + fixed_xz.y * 0.17) * 6.2831;
	float wave = sin(dot(fixed_xz, wind.xz) * 0.15 - TIME * (1.5 + wind.y)) * 0.5 + 0.5;
	float lean = (0.04 + 0.12 * wind.y) * (0.6 + 0.4 * wind.w) * (0.4 + 0.6 * wave);
	vec3 bend = vec3(wind.x * lean + sin(TIME * 1.3 + phase) * 0.03 * (0.4 + wind.y), 0.0,
	                 wind.z * lean + sin(TIME * 1.1 + phase * 1.3) * 0.025 * (0.4 + wind.y));
	float w = along * along;
	VERTEX += inverse(mat3(MODEL_MATRIX)) * bend * w * 0.45;
	// colour: the terrain's grass at the root, lighter and a little yellower at the tip; drier clumps paler
	vec3 base = mix(vec3(0.030, 0.075, 0.020), vec3(0.055, 0.110, 0.028), INSTANCE_CUSTOM.y);
	vec3 tip = base * vec3(1.55, 1.45, 1.1) + vec3(0.012, 0.010, 0.0);
	vec3 dry = vec3(0.13, 0.12, 0.05);
	tint = mix(mix(base * 0.7, tip, along), dry * mix(0.6, 1.0, along), INSTANCE_CUSTOM.x * 0.55);
	tint *= ground_hue(fixed_xz);   // the ground's colour patches (D60), so blades and ground agree
	// blades take the ground's light (mostly up), so they don't turn dark when seen edge-on
	NORMAL = normalize(mix(NORMAL, vec3(0.0, 1.0, 0.0), 0.65));
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 c = tint * mix(0.92, 1.08, fract(sin(dot(floor(wpos.xz * 4.0), vec2(12.9898, 78.233))) * 43758.5453));
	ALBEDO = c * cloud_shade(wpos.xz + world_offset, TIME);
	ROUGHNESS = 1.0;
	if (!FRONT_FACING) NORMAL = -NORMAL;
	BACKLIGHT = ALBEDO * vec3(0.9, 1.08, 0.55) * leaf_translucency * along;
	vec3 view_ray = normalize(wpos - CAMERA_POSITION_WORLD);
	float forward = pow(clamp(dot(view_ray, sun_dir_world), 0.0, 1.0), 5.0);
	EMISSION = ALBEDO * vec3(0.95, 1.1, 0.5) * sun_glow.rgb * forward * leaf_translucency * 0.3 * along;
}
