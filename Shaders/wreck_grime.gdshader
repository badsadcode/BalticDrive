// Dirt and rust over an abandoned car (D69): drawn as an overlay on its body, blotches of brown rust low down and at
// the edges, a grey film of dust over the rest. Varies with position so no two wrecks look alike.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back;

uniform float amount = 0.6;
varying vec3 wpos;
varying vec3 lpos;

float h(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float n(vec3 x) {
	vec3 i = floor(x); vec3 f = fract(x); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(h(i), h(i + vec3(1, 0, 0)), f.x), mix(h(i + vec3(0, 1, 0)), h(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(h(i + vec3(0, 0, 1)), h(i + vec3(1, 0, 1)), f.x), mix(h(i + vec3(0, 1, 1)), h(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	lpos = VERTEX;
}

void fragment() {
	float big = n(lpos * 1.7 + vec3(wpos.x * 0.07, 0.0, wpos.z * 0.07));
	float fine = n(lpos * 9.0) * 0.5 + n(lpos * 23.0) * 0.5;
	float low = 1.0 - smoothstep(0.2, 1.1, lpos.y);
	float rust = smoothstep(0.6, 0.8, big * 0.75 + fine * 0.35 + low * 0.35) * amount;
	float dust = (0.25 + 0.35 * fine) * amount;
	// dark, brown rust (a brighter one read orange in the game's light, like the factory brick)
	vec3 rust_col = mix(vec3(0.13, 0.07, 0.04), vec3(0.22, 0.12, 0.06), fine);
	vec3 dust_col = vec3(0.36, 0.34, 0.30);
	ALBEDO = mix(dust_col, rust_col, step(0.01, rust));
	ALPHA = clamp(max(dust * 0.55, rust * 0.95), 0.0, 0.95);
	ROUGHNESS = 1.0;
}
