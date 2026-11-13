// The body of CloudsNear.gdshader, shared with its test twin CloudsNearNoFog.gdshader (--cloud-fog-off), which
// differs only in render_mode fog_disabled and skipping the emulated sky fog (CLOUDS_NEAR_FOG_OFF).
uniform float fog_depth = 200.0;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
// The sun light as the sky sees it (LIGHT0): colour (rgb, as set on the light: sRGB) and energy (a); direction toward it (xyz).
global uniform vec4 cloud_light;
global uniform vec4 cloud_light_dir;
// Godot's fog as it falls on the sky (Atmosphere.cs): colour (linear, × energy) and sky affect (0 = fog off); sun scatter,
// aerial perspective.
global uniform vec4 cloud_fog;
global uniform vec4 cloud_fog2;
// Cloud blend (setting): 0 = build 52; 1 = matched to the sky's clouds (round 31, Tom: "you can see the mountains' outline
// in the clouds", a band without clouds under the horizon, white clouds in front of the land under grey-blue ones above):
//   * land past ~47 km counted as sky (its reversed depth is below 1e-6 with the 90 km far plane) — neither pass drew
//     clouds over it, a cloudless band under the horizon; now only the cleared depth (0) is sky;
//   * the sun's colour is linear here as in the sky (it was the light's sRGB colour: paler, brighter, pink-white at sunset
//     where the sky's clouds glow orange);
//   * Godot's fog veils the sky, its clouds included, but not this pass: the same veil is applied here.
// (the uniform cloud_blend is declared in SkyCommon.gdshaderinc; the sky reads it too)
// Test (--cloud-split-test): this pass also draws the sky's clouds on the left half of the screen, the sky on the right,
// so the two can be compared side by side at the middle.
uniform int split_test = 0;
vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
uniform int cloud_light_linear = 1; // = cloud_blend (set together by Atmosphere.cs; SkyCommon declares cloud_blend after this)
vec3 cloud_light_rgb() { return cloud_light_linear == 1 ? srgb_to_lin(cloud_light.rgb) : cloud_light.rgb; }
#define SKY_LIGHT0_COLOR cloud_light_rgb()
#include "res://Shaders/Sky/SkyCommon.gdshaderinc"

uniform int max_steps = 128;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
	// The view-space point fog_depth metres deep along this corner's ray (fog reads the fragment's position).
	vec4 near = INV_PROJECTION_MATRIX * vec4(VERTEX.xy, 1.0, 1.0);
	vec3 v = near.xyz / near.w;
	VERTEX = v * (fog_depth / max(-v.z, 1e-4));
}

void fragment() {
	float depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	bool sky = cloud_blend == 1 ? depth <= 0.0 : depth < 1e-6;
	bool test = split_test == 4 || (split_test >= 1 && SCREEN_UV.x < 0.5); // (4: the whole screen)
	bool probe = split_test == 2 && test && SCREEN_UV.y < 0.12;
	// Direction from the near plane (a sky pixel's depth 0 is at infinity); distance to the scene.
	vec4 near = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, 1.0, 1.0);
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(normalize(near.xyz / near.w), 0.0)).xyz);
	// Cloud look reference: below the horizon this pass draws the clouds of sky pixels too (Sky.gdshader leaves them).
	// Water writes no depth, and beyond the farthest seabed the far sea's pixels read as sky: the sky's clouds were drawn
	// behind it and the sea over them — a stepped strip of clear sea along the horizon inside clouds (Tom, dump 220450).
	bool below = cloud_look == 1 && dir.y < 0.0;
	float one_k = sky && !test && !below ? clouds_one_pass_k() : 1.0; // (D179: sky pixels faded in over height)
	if (((sky && !test && !below && one_k <= 0.001) || quality != 3) && !probe) discard; // sky pixels: the sky draws them
	float scene = 1e9;
	if (!sky) {
		vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
		scene = length(view.xyz / view.w);
	}
	vec3 sun_dir = normalize(cloud_light_dir.xyz);
	vec3 sun_col = cloud_light_rgb() * cloud_light.a;
	float day = clamp(sun_dir.y * 4.0 + 0.35, 0.08, 1.0);
	vec4 v = volumetric_v2(dir, scene, cloud_jitter(FRAGCOORD.xy, TIME), sky ? 150 : (cloud_dev.z > 0.5 ? int(cloud_dev.z) : max_steps), sun_dir, sun_col, day, dot(dir, sun_dir), cloud_state.z, cloud_state.w);
	if (v.a < 0.002 && !probe && !(split_test == 3 && test)) discard;
	// The sky's biome haze over its clouds (Sky.gdshader, "Biome haze"), here too: without it these clouds stayed
	// white in front of distant land while the same clouds in the sky above were veiled in dust, and the land's
	// silhouette showed as a hard seam (Tom, dumps 115949 / 120402, Needle Rocks). Premultiplied colour.
	vec3 haze = horizon_color * mix(vec3(1.0), sky_mood.rgb, 0.8) * day;
	v.rgb = mix(v.rgb, haze * v.a, sky_mood.a * 0.7 * (1.0 - smoothstep(0.0, 0.6, dir.y)));
#ifndef CLOUDS_NEAR_FOG_OFF
	if (cloud_blend == 1 && cloud_fog.a > 0.0) {
		// Godot's sky fog (sky.glsl fog_process): the fog light colour, toward the pixel's own colour by the aerial
		// perspective, plus the sun scattered around it; mixed in by the sky affect.
		vec3 own = v.rgb / v.a;
		vec3 fog = mix(cloud_fog.rgb, own, cloud_fog2.y) + sun_col * pow(max(dot(dir, sun_dir), 0.0), 8.0) * PI * cloud_fog2.x;
		v.rgb = mix(v.rgb, fog * v.a, cloud_fog.a);
	}
#endif
	// Probe (--cloud-split-test=2): the sun as this pass sees it, beside the sky's (Sky.gdshader).
	v *= one_k; // (premultiplied: the sky pass keeps 1 − k of its own clouds underneath)
	if (probe) v = vec4(SCREEN_UV.y < 0.06 ? sun_col * 0.2 : sun_dir * 0.5 + 0.5, 1.0);
	if (split_test == 3 && test) v = vec4(vec3(v.a), 1.0);
	ALBEDO = v.rgb / max(v.a, 1e-4);
	ALPHA = v.a;
	PREMUL_ALPHA_FACTOR = v.a;
}
