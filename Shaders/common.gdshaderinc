// Shared by every ground-level surface shader: moving cloud shadows driven by the weather.

global uniform float cloud_shadow;
// The moving origin modulo 64 km (World/WorldOrigin.cs): add to world-space positions used for patterns.
global uniform vec2 world_offset;

float cs_hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float cs_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(cs_hash(i), cs_hash(i + vec2(1.0, 0.0)), f.x),
			   mix(cs_hash(i + vec2(0.0, 1.0)), cs_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

// 1.0 in full sun, darker where a cloud is passing overhead.
float cloud_shade(vec2 xz, float time) {
	vec2 q = xz * 0.0032 + vec2(time * 0.011, time * 0.0045);
	float n = cs_noise(q) * 0.6 + cs_noise(q * 2.3 + 4.0) * 0.4;
	return 1.0 - cloud_shadow * smoothstep(0.42, 0.68, n);
}

// Colour variety of the living ground (D60, Tom: "hue variety"): a multiplier for grass, crops and forest floor, the
// same for the terrain and the grass blades so they agree. Patches tens to hundreds of metres across: drier
// olive-yellow, lusher blue-green, and a few browner; on a gentle mottle a few metres across. xz: fixed world x/z
// (with world_offset); strength 0..1 (the terrain_hue global, 0 = off).
global uniform float terrain_hue;
vec3 ground_hue(vec2 xz) {
	vec2 r = vec2(xz.x * 0.8 - xz.y * 0.6, xz.x * 0.6 + xz.y * 0.8);
	float dry = cs_noise(r * 0.009) * 0.65 + cs_noise(r * 0.023 + 11.0) * 0.35;      // ~110 m and ~45 m
	float lush = cs_noise(xz * 0.013 + 37.0) * 0.6 + cs_noise(xz * 0.041 - 5.0) * 0.4; // ~75 m and ~25 m
	float brown = cs_noise(r * 0.006 - 23.0);
	float mottle = cs_noise(xz * 0.35 + 3.0);
	vec3 m = vec3(1.0);
	m = mix(m, vec3(1.7, 1.25, 0.45), smoothstep(0.42, 0.72, dry));               // straw, olive
	m = mix(m, vec3(0.62, 1.05, 1.15), smoothstep(0.45, 0.75, lush) * 0.85);      // lush, blue-green, darker
	m = mix(m, vec3(1.45, 0.95, 0.6), smoothstep(0.6, 0.82, brown) * 0.75);       // bare, browner
	m *= 0.85 + 0.3 * mottle;
	return mix(vec3(1.0), m, terrain_hue);
}
