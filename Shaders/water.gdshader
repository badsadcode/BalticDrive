// From Thornwake_Rewrite Game/Rewrite/Water.gdshader (build 142); Baltic Drive change: pattern_xz for the moving origin.
// Rewrite world water: ported from GangTheGAME Assets/Shaders/water.gdshader (refraction through the screen
// texture, depth-based absorption from the depth buffer, animated caustics on the submerged ground, screen-space
// reflections with sky fallback, shoreline/contact foam), adapted for clear natural water at world scale:
//  - colour/absorption per body (COLOR.g: 0 sea, ⅓ river, ⅔ lake, 1 pond) instead of silty flood water;
//  - Gerstner waves on sea and lakes from the sea state (OceanWaves.cs evaluates the same waves on the CPU for
//    boats and swimming; same parameters, same clock `ocean_time`), shrinking in shallow water;
//  - swash: the sea runs up and down the beach; breakers: foam bands rolling in over the shallows;
//  - river water colouring the sea or lake near a mouth (plume).
// COLOR.r = water depth / 20 m at the vertex (from the terrain sampler), COLOR.g = kind (0 sea, .375 pond, .5 lake, 1 river),
// COLOR.b = river plume 0..1. UV = river current (m/s).
shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_never;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

uniform vec3 sea_scatter : source_color = vec3(0.05, 0.22, 0.30);
uniform vec3 lake_scatter : source_color = vec3(0.06, 0.20, 0.17);
uniform vec3 river_scatter : source_color = vec3(0.07, 0.15, 0.12);
uniform vec3 absorption = vec3(0.55, 0.16, 0.12);
uniform vec3 foam_color : source_color = vec3(0.93, 0.95, 0.95);
uniform float refraction : hint_range(0.0, 0.1) = 0.03;
uniform float caustic_strength : hint_range(0.0, 2.0) = 0.8;
uniform float reflection_strength : hint_range(0.0, 1.0) = 0.9;
uniform int ssr_steps : hint_range(0, 64) = 32; // 0 = sky reflection only (settings: water reflections)
uniform vec4 waves[12];         // dir.x, dir.z, k, amplitude (OceanWaves.Waves)
uniform float wave_q[12];       // Gerstner choppiness per wave (OceanWaves.Q)
uniform float wave_height = 0.3; // sum of amplitudes
uniform float sea_state = 0.25;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0); // world, toward the sun
uniform vec3 sun_color : source_color = vec3(1.0);
uniform vec3 crest_glow : source_color = vec3(0.10, 0.55, 0.50); // light through thin wave crests
const float wave_phase[12] = { 0.0, 1.7, 4.1, 2.6, 5.3, 0.9, 3.3, 5.9, 1.1, 4.6, 2.2, 0.4 }; // OceanWaves.Phase
global uniform float ocean_time;
global uniform float caustic_light; // the direct sunlight the caustics focus (Atmosphere: 0 at night; 1 = build 74)
uniform vec3 sky_horizon : source_color = vec3(0.70, 0.76, 0.78);
uniform vec3 sky_zenith : source_color = vec3(0.32, 0.47, 0.62);

varying float vertex_depth;
varying float body;
varying vec3 world_position;
varying vec2 pattern_xz; // world x/z + the moving origin modulo 64 km (Baltic Drive): waves stay put when the origin moves
global uniform vec2 world_offset;
varying vec2 current; // river current (world xz, m/s)
varying float plume;
varying vec4 biome_tint; // biome water colour (rgb) and strength (a), from UV2
varying vec3 wave_normal;
varying float crest;
varying float fold;     // surface compression at sharp crests (Gerstner Jacobian): where foam forms
varying float wave_lift; // displacement height (m)

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 hash2(vec2 p) { return vec2(hash(p), hash(p + 17.31)); }
float noise(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float ripples(vec2 p, float t, float speed) {
	vec2 flow = vec2(0.12, 0.05) * speed;
	return noise(p * 0.9 + t * flow * 6.0) * 0.55 + noise(p * 2.1 - t * flow.yx * 9.0) * 0.3 + noise(p * 5.0 + t * vec2(0.3, -0.5)) * 0.15;
}
float caustic_layer(vec2 p, float t) {
	vec2 i = floor(p); vec2 f = fract(p);
	float d1 = 8.0; float d2 = 8.0;
	for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
		vec2 g = vec2(float(x), float(y));
		vec2 o = 0.5 + 0.42 * sin(t + 6.2831 * hash2(i + g));
		float d = length(g + o - f);
		if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) { d2 = d; }
	}
	return 1.0 - smoothstep(0.0, 0.32, d2 - d1);
}
float caustics(vec2 p, float t) {
	vec2 warp = vec2(noise(p * 0.8 + t * 0.15), noise(p * 0.8 + 5.2 - t * 0.15)) - 0.5;
	p += warp * 0.9;
	return pow(caustic_layer(p * 1.3, t * 0.8) * caustic_layer(p * 1.7 + 3.7, -t * 0.95), 1.5) * 1.6;
}
vec3 view_from_depth(vec2 uv, float raw, mat4 inv_projection) {
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return v.xyz / v.w;
}
// Ripples carried by the current: advected downstream in two phases that cross-fade (moves forever without
// smearing) and smeared along the flow with a second tap, so fast water reads as streaks. No rotated coordinates:
// at world positions of hundreds of km, rotating them by a slightly varying flow direction scrambles the pattern.
float flow_ripples(vec2 q, vec2 f, float t) {
	float s = length(f);
	vec2 dir = s > 0.001 ? f / s : vec2(1.0, 0.0);
	const float cycle = 1.6;
	float p0 = fract(t / cycle), p1 = fract(t / cycle + 0.5);
	float w0 = 1.0 - abs(2.0 * p0 - 1.0);
	vec2 a = q - f * p0 * cycle, b = q - f * p1 * cycle + vec2(13.1, 7.7);
	float tap = 0.4 + smoothstep(0.2, 1.8, s) * 1.4;
	float ra = ripples(a, t, 0.0) + ripples(a - dir * tap, t, 0.0);
	float rb = ripples(b, t, 0.0) + ripples(b - dir * tap, t, 0.0);
	return (ra * w0 + rb * (1.0 - w0)) * 0.5;
}
void vertex() {
	vertex_depth = COLOR.r * 20.0;
	body = COLOR.g;
	plume = COLOR.b;
	{
		float packed = UV2.x;
		biome_tint = vec4(floor(packed / 65536.0), mod(floor(packed / 256.0), 256.0), mod(packed, 256.0), 0.0) / 255.0;
		biome_tint.a = UV2.y;
	}
	current = UV;
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	pattern_xz = world_position.xz + world_offset;
	// Ponds (wetland pools, 0.375) are lake water without waves.
	float sea = 1.0 - step(0.1667, body), lake = step(0.5, body) * (1.0 - step(0.8333, body));
	vec3 offset = vec3(0.0);
	vec3 n = vec3(0.0, 1.0, 0.0);
	crest = 0.0; fold = 0.0; wave_lift = 0.0;
	if (sea + lake > 0.0) {
		float jxx = 0.0, jzz = 0.0, jxz = 0.0;
		// Coarse distant tiles cannot carry short waves: fade waves shorter than a few vertex spacings.
		float spacing = max(1.0, length(world_position - CAMERA_POSITION_WORLD) / 40.0);
		for (int i = 0; i < 12; i++) {
			float k = waves[i].z;
			float lambda = 6.2831853 / k;
			float depth_factor = smoothstep(0.0, 1.0, vertex_depth / (lambda * 0.12 + 0.3));
			float amp = waves[i].w * depth_factor * (sea > 0.5 ? 1.0 : (i >= 10 ? 0.0 : i >= 4 ? 0.45 : 0.05)) * smoothstep(4.0 * spacing, 8.0 * spacing, lambda);
			if (amp <= 0.0) continue;
			vec2 d = waves[i].xy;
			float theta = k * dot(d, pattern_xz) - sqrt(9.81 * k) * ocean_time + wave_phase[i];
			float c = cos(theta), sn = sin(theta);
			offset += vec3(wave_q[i] * amp * d.x * c, amp * sn, wave_q[i] * amp * d.y * c);
			n.x -= d.x * k * amp * c; n.z -= d.y * k * amp * c; n.y -= wave_q[i] * k * amp * sn;
			crest += amp * sn;
			float qa = wave_q[i] * k * amp * sn;
			jxx += qa * d.x * d.x; jzz += qa * d.y * d.y; jxz += qa * d.x * d.y;
		}
		crest /= max(wave_height, 0.01);
		fold = 1.0 - ((1.0 - jxx) * (1.0 - jzz) - jxz * jxz); // 0 flat … 1 where the surface folds
		wave_lift = offset.y;
		// Swash: near the shore the sea runs up and down the beach every ~9 s (the water mesh reaches under the beach).
		float swash = sea * (1.0 - smoothstep(0.0, 2.5, vertex_depth));
		float run = 0.5 + 0.5 * sin(ocean_time * 0.7 + sin(pattern_xz.x * 0.013) * 2.0 + cos(pattern_xz.y * 0.011) * 2.0);
		offset.y += swash * (0.18 + 0.6 * sea_state) * (run * run - 0.3);
	}
	VERTEX += offset;
	wave_normal = normalize(n);
}

void fragment() {
	vec2 p = pattern_xz;
	float sea = 1.0 - step(0.1667, body), river = step(0.1667, body) * (1.0 - step(0.5, body)), lake = 1.0 - sea - river;
	float speed = mix(1.0, 3.0, river);
	float e = 0.05;
	bool flowing = length(current) > 0.05;
	float h = flowing ? flow_ripples(p, current, TIME) : ripples(p, TIME, speed);
	// Ripples fade with distance so far water does not sparkle (they are far smaller than a pixel there).
	float amp = (mix(0.05, 0.09, river) + sea * 0.03) / (1.0 + length(VERTEX) / 120.0);
	vec2 slope = flowing
		? vec2(flow_ripples(p + vec2(e, 0.0), current, TIME) - h, flow_ripples(p + vec2(0.0, e), current, TIME) - h) / e * amp * (1.0 + 0.4 * min(length(current), 2.5))
		: vec2(ripples(p + vec2(e, 0.0), TIME, speed) - h, ripples(p + vec2(0.0, e), TIME, speed) - h) / e * amp;
	// Waves: the Gerstner normal from the vertex stage, ripples on top.
	// Chop: mid-scale wind waves on the swell (grows with the sea state; breaks up the regular Gerstner pattern).
	float pond = step(0.8333, body);
	if (sea + lake - pond > 0.5) {
		float chop_amp = (0.04 + 0.25 * sea_state) * (sea > 0.5 ? 1.0 : 0.4) / (1.0 + length(VERTEX) / 300.0);
		vec2 wind = vec2(0.8, 0.6);
		vec2 q1 = p * 0.11 + wind * TIME * 0.35, q2 = p * 0.23 - wind.yx * TIME * 0.5;
		float c0 = noise(q1) + 0.5 * noise(q2);
		float cx = noise(q1 + vec2(0.05, 0.0)) + 0.5 * noise(q2 + vec2(0.1, 0.0));
		float cz = noise(q1 + vec2(0.0, 0.05)) + 0.5 * noise(q2 + vec2(0.0, 0.1));
		slope += vec2(cx - c0, cz - c0) / 0.45 * chop_amp;
	}
	vec3 n_world = normalize(vec3(wave_normal.x / max(wave_normal.y, 0.2) - slope.x, 1.0, wave_normal.z / max(wave_normal.y, 0.2) - slope.y));
	vec3 n_view = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
	NORMAL = n_view;
	vec3 to_eye = normalize(-VERTEX);
	float fresnel = 0.02 + 0.98 * pow(1.0 - clamp(dot(n_view, to_eye), 0.0, 1.0), 5.0);

	// What lies behind the surface (depth buffer), refracted by the ripples; never pull in things above water.
	vec3 behind = view_from_depth(SCREEN_UV, texture(depth_tex, SCREEN_UV).r, INV_PROJECTION_MATRIX);
	float straight_path = max(0.0, length(behind) - length(VERTEX));
	vec2 ruv = clamp(SCREEN_UV + n_world.xz * refraction * clamp(straight_path * 0.5, 0.0, 1.0), vec2(0.001), vec2(0.999));
	vec3 rbehind = view_from_depth(ruv, texture(depth_tex, ruv).r, INV_PROJECTION_MATRIX);
	if (length(rbehind) < length(VERTEX)) { ruv = SCREEN_UV; rbehind = behind; }
	float path = max(0.0, length(rbehind) - length(VERTEX));
	vec3 surface_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 bottom_world = (INV_VIEW_MATRIX * vec4(rbehind, 1.0)).xyz;
	// Depth from height differences, but never less than the sight line's drop through the water: looking up a
	// rising river, the bed behind the surface can lie higher than the surface point itself, which made the water
	// fully transparent there (Tom: "upstream water disappears a little further away").
	float drop = abs(normalize(VERTEX).y);
	float depth_below = max(max(0.0, surface_world.y - bottom_world.y), path * drop * 0.5);
	float contact_depth = max(max(0.0, surface_world.y - (INV_VIEW_MATRIX * vec4(behind, 1.0)).y), straight_path * drop * 0.5);

	// Transmitted light: the ground through the water, with caustics in the shallows.
	vec3 ground = textureLod(screen_tex, ruv, 0.0).rgb;
	float caustic = caustics(bottom_world.xz * 0.6, TIME * 1.3) * smoothstep(0.0, 0.1, depth_below) * exp(-depth_below * 0.35);
	ground += caustic * caustic_strength * 0.3 * vec3(1.0, 0.97, 0.85) * (0.4 + ground) * caustic_light; // (sunlight focused: dims with the sun)
	vec3 transmittance = exp(-absorption * path * mix(1.0, 2.2, river));
	// Near a mouth the river's greener, siltier water spreads into the sea or lake.
	vec3 scatter = mix(sea * sea_scatter + lake * lake_scatter + river * river_scatter, river_scatter * vec3(1.15, 1.1, 0.9), plume * 0.85);
	transmittance = mix(transmittance, exp(-absorption * path * 2.2), plume * 0.6);
	// Biome water (swamps: brown-green and murky).
	scatter = mix(scatter, biome_tint.rgb * 0.35, biome_tint.a);
	transmittance = mix(transmittance, transmittance * transmittance * biome_tint.rgb, biome_tint.a * 0.8);
	float murk = 1.0 - dot(transmittance, vec3(0.333));

	// Screen-space reflection, sky as fallback.
	vec3 ray = normalize(reflect(VERTEX, n_view));
	vec4 hit = vec4(0.0);
	float step_length = ssr_steps <= 16 ? 0.4 : 0.25;
	float growth = ssr_steps <= 16 ? 1.38 : 1.18; // fewer steps reach as far, more coarsely
	vec3 march = VERTEX + ray * 0.1;
	for (int i = 0; i < ssr_steps; i++) {
		march += ray * step_length;
		step_length *= growth;
		vec4 clip = PROJECTION_MATRIX * vec4(march, 1.0);
		if (clip.w <= 0.0) break;
		vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
		if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) break;
		float scene_z = view_from_depth(uv, texture(depth_tex, uv).r, INV_PROJECTION_MATRIX).z;
		float gap = scene_z - march.z;
		if (gap > 0.0 && gap < step_length * 2.5 + 0.3) {
			vec2 edge = smoothstep(vec2(0.0), vec2(0.08), uv) * smoothstep(vec2(0.0), vec2(0.08), 1.0 - uv);
			hit = vec4(textureLod(screen_tex, uv, 0.0).rgb, edge.x * edge.y * (1.0 - float(i) / float(ssr_steps)));
			break;
		}
	}

	// Foam: a broken line where the water meets the shore or anything standing in it; more on the sea and rivers.
	float bubbles = 0.55 + 0.45 * noise(p * 1.5 + TIME * 0.4);
	float contact = 1.0 - smoothstep(0.0, mix(0.03, 0.12, sea), contact_depth);
	float foam = clamp(contact * mix(0.3, 0.6, sea) * smoothstep(0.5, 0.8, bubbles), 0.0, 1.0);
	// Breakers: foam bands rolling in over the shallows (sea), stronger in rough weather; whitecaps on crests.
	if (sea > 0.5) {
		float band = sin(depth_below * 3.0 + TIME * 1.3 + noise(p * 0.05) * 4.0);
		float shallows = smoothstep(0.1, 0.3, depth_below) * (1.0 - smoothstep(0.6, 1.0 + 1.8 * sea_state, depth_below));
		float broken = smoothstep(0.35, 0.7, noise(p * 0.35 + vec2(TIME * 0.15, 0.0)));
		foam = max(foam, smoothstep(0.92, 0.99, band) * shallows * (0.35 + 0.4 * sea_state) * broken);
		// Whitecaps where the surface is compressed at sharp crests (rough seas), in a foamy cell pattern that
		// thins out behind the crest.
		float cells = caustic_layer(p * 0.35 + TIME * 0.05, TIME * 0.3) * 0.5 + noise(p * 0.8 - TIME * 0.3) * 0.5;
		float whitecap = smoothstep(0.35 - 0.15 * sea_state, 0.75, fold) * smoothstep(0.15, 0.6, sea_state);
		foam = max(foam, whitecap * smoothstep(0.25, 0.65, cells + whitecap * 0.3) * 0.9);
		foam = max(foam, smoothstep(0.6, 0.95, crest) * smoothstep(0.45, 0.9, sea_state) * smoothstep(0.55, 0.85, noise(p * 0.4 + TIME * 0.2)) * 0.5);
	}
	// Fast water: foam streaks drifting downstream show which way it flows (same two-phase advection).
	float fs = length(current);
	if (fs > 0.6) {
		vec2 dir = current / fs;
		const float c = 2.4;
		float f0 = fract(TIME / c), f1 = fract(TIME / c + 0.5), w0 = 1.0 - abs(2.0 * f0 - 1.0);
		vec2 qa = p - current * f0 * c, qb = p - current * f1 * c + vec2(5.3, 2.9);
		float sa = (noise(qa * 0.9) + noise((qa - dir * 1.6) * 0.9) + noise((qa - dir * 3.2) * 0.9)) / 3.0;
		float sb = (noise(qb * 0.9) + noise((qb - dir * 1.6) * 0.9) + noise((qb - dir * 3.2) * 0.9)) / 3.0;
		float streak = sa * w0 + sb * (1.0 - w0);
		foam = max(foam, smoothstep(0.62, 0.8, streak) * smoothstep(0.6, 2.4, fs) * 0.5 / (1.0 + length(VERTEX) / 60.0));
	}

	vec3 transmitted = ground * transmittance;
	vec3 ray_world = normalize((INV_VIEW_MATRIX * vec4(ray, 0.0)).xyz);
	vec3 sky = mix(sky_horizon, sky_zenith, smoothstep(0.0, 0.6, ray_world.y));
	vec3 reflected = mix(sky, hit.rgb, hit.a) * reflection_strength;
	// Light through thin crests: looking toward the sun, the upper part of waves glows turquoise.
	vec3 eye_world = normalize(CAMERA_POSITION_WORLD - world_position);
	vec3 sun_flat = normalize(vec3(sun_direction.x, 0.0, sun_direction.z) + 1e-4);
	float back_lit = pow(clamp(dot(-eye_world, sun_flat), 0.0, 1.0), 2.0) * clamp(sun_direction.y * 3.0, 0.0, 1.0);
	float thin = smoothstep(0.1, 0.9, crest) * (sea + lake * 0.3) * min(1.0, wave_height * 0.8);
	vec3 glow = crest_glow * sun_color * (back_lit * 1.2 + 0.25) * thin;
	ALBEDO = mix(scatter * murk * (1.0 - fresnel), foam_color, foam);
	EMISSION = (transmitted * (1.0 - fresnel) + reflected * fresnel + glow * (1.0 - fresnel)) * (1.0 - foam);
	SPECULAR = 0.3 * (1.0 - foam);
	ROUGHNESS = mix(0.03, 0.6, foam);
	METALLIC = 0.0;
	ALPHA = smoothstep(0.0, 0.04, contact_depth);
}
