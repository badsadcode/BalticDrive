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
