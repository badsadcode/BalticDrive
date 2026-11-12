shader_type spatial;
render_mode cull_back, diffuse_burley;

// Ground surface. Vertex colour carries the land-cover masks from the map (World/TerrainBuilder.cs):
//   R = forest floor, G = cultivated field, B = bare earth / gravel (tracks, yards, road verge), A = water
// Everything else (grass variation, crop rows, rock on slopes, lake shore, rain wetness) is procedural
// in world space, so chunks tile seamlessly at every level of detail.

#include "res://Shaders/common.gdshaderinc"

global uniform float wetness;

varying vec3 wpos;
varying vec3 wnrm;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
			   mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

float fbm(vec2 p) {
	return vnoise(p) * 0.55 + vnoise(p * 2.13 + 7.1) * 0.3 + vnoise(p * 4.37 - 3.3) * 0.15;
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wpos.xz += world_offset; // patterns stay fixed to the ground when the origin moves
	wnrm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec2 p = wpos.xz;
	float dist = length(VERTEX);
	float detail_fade = clamp(1.0 - dist / 90.0, 0.0, 1.0);

	float n_big = fbm(p * 0.012);
	float n_mid = fbm(p * 0.09);
	// three differently rotated layers so the value-noise grid never lines up into visible tiles
	vec2 pr = vec2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8);
	vec2 pq = vec2(p.x * 0.28 + p.y * 0.96, -p.x * 0.96 + p.y * 0.28);
	float n_fine = mix(0.5, (vnoise(pr * 1.7) + vnoise(pq * 2.9 + 3.0) + vnoise(p * 5.3 - 7.0)) / 3.0, detail_fade);
	float n_grain = mix(0.5, (vnoise(pr * 11.0) + vnoise(pq * 17.0)) * 0.5, detail_fade * detail_fade);

	// meadow grass
	vec3 grass = mix(vec3(0.030, 0.080, 0.020), vec3(0.060, 0.115, 0.030), n_mid);
	grass = mix(grass, vec3(0.095, 0.115, 0.040), smoothstep(0.5, 0.85, n_big) * 0.5);
	grass *= 0.8 + 0.4 * n_fine;

	// forest floor: needles, moss, shade
	vec3 floor_col = mix(vec3(0.060, 0.047, 0.028), vec3(0.045, 0.070, 0.028), n_mid);
	floor_col *= 0.75 + 0.5 * n_fine;

	// field: stubble rows following a slowly varying direction
	float ang = floor(n_big * 4.0) * 0.8;
	float rows = sin(dot(p, vec2(cos(ang), sin(ang))) * 3.2);
	vec3 crop = mix(vec3(0.085, 0.105, 0.038), vec3(0.150, 0.140, 0.055), n_mid);
	crop *= 0.82 + 0.18 * mix(0.0, rows, detail_fade) + 0.12 * n_fine;

	// bare earth / gravel
	vec3 dirt = mix(vec3(0.095, 0.075, 0.055), vec3(0.150, 0.125, 0.095), n_mid);
	dirt *= 0.7 + 0.35 * n_fine + 0.25 * n_grain;

	vec3 col = mix(grass, floor_col, COLOR.r);
	col = mix(col, crop, COLOR.g);
	float dirt_m = clamp(COLOR.b + (n_fine - 0.5) * 0.5 * step(0.02, COLOR.b), 0.0, 1.0);
	col = mix(col, dirt, smoothstep(0.25, 0.75, dirt_m));

	// steep ground turns to earth and rock
	float slope = 1.0 - wnrm.y;
	vec3 rock = mix(vec3(0.10, 0.095, 0.085), vec3(0.17, 0.16, 0.145), n_mid) * (0.75 + 0.5 * n_fine);
	col = mix(col, rock, smoothstep(0.16, 0.34, slope + (n_fine - 0.5) * 0.08));

	// shore and bed: mud wherever the land cover says water (the carved bed and a little of the bank)
	float shore = smoothstep(0.05, 0.6, COLOR.a + (n_fine - 0.5) * 0.3);
	vec3 mud = vec3(0.085, 0.075, 0.055) * (0.8 + 0.4 * n_fine);
	col = mix(col, mud, shore);

	float wet = max(wetness, shore * 0.8);
	ALBEDO = col * mix(1.0, 0.62, wet) * cloud_shade(wpos.xz, TIME);
	ROUGHNESS = mix(0.96, 0.5, wet * (0.4 + 0.6 * dirt_m));
	SPECULAR = 0.25;
}
