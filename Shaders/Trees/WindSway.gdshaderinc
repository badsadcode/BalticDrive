// Wind on plants (round 27, Tom: "plants move too fast and too small, an unnatural jiggle… grass should sway in
// waves across the terrain"). Shared by grass, flora, shrubs, tree crowns and the terrain's grass sheen.
// - Gusts are patches of stronger wind (tens of metres) carried downwind at the wind's speed: the waves that sweep
//   across a meadow. C# integrates how far they have travelled (wind_field.x), so a change of speed never jumps.
// - Plants lean with the wind's drag (speed squared), saturating (they never fold flat), more inside a gust.
// - Around the lean each plant swings at its own natural, slow rate (stiffness: grass ~0.7 Hz, trees ~0.25 Hz),
//   with only a faint flutter on top; no shared fast pulsing.
// Needs the global uniforms wind (dir.x, speed/8, dir.z, gust) and wind_field.
// wind_field: x = gust travel along the wind (m, wrapped), y = steady strength (speed/8), z = gustiness 0..1,
// w = 1 with the simulated weather (else the classic build-35 sway is used by the caller).
global uniform vec4 wind_field;
// Round 28: the regional wind around (WindMap.cs): r, g direction, b speed/32. wind_map_frame: the map's corner in
// scene coordinates (x, y), 1/size (z), and w = the player's wind over the map's wind at the player (storm gusts and
// outflow carried to every plant), 0 = off (one wind everywhere).
global uniform sampler2D wind_map : filter_linear, repeat_disable;
global uniform vec4 wind_map_frame;

float ws_hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float ws_noise(vec2 p) {
	vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
	return mix(mix(ws_hash(i), ws_hash(i + vec2(1.0, 0.0)), u.x), mix(ws_hash(i + vec2(0.0, 1.0)), ws_hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

bool wind_sim() { return wind_field.w > 0.5; }

vec2 wind_dir() { return normalize(wind.xz + vec2(1e-4)); }

// Direction (xy) and strength (z, speed/8) of the wind at a scene position.
vec3 wind_here(vec2 xz) {
	vec3 g = vec3(wind_dir(), wind_field.y);
	if (wind_map_frame.w <= 0.0) return g;
	vec2 uv = (xz - wind_map_frame.xy) * wind_map_frame.z;
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) return g;
	vec3 m = texture(wind_map, uv).rgb;
	return vec3(normalize(m.xy * 2.0 - 1.0 + vec2(1e-4)), min(3.0, m.z * 4.0 * wind_map_frame.w));
}

// 0..1: how much of a gust is passing over this point now (elongated across the wind, as real gust fronts).
float wind_gust_dir(vec2 xz, vec2 d) {
	vec2 q = vec2(dot(xz, d), dot(xz, vec2(-d.y, d.x)));
	q.x -= wind_field.x;
	float g = ws_noise(q * vec2(1.0 / 24.0, 1.0 / 40.0)) * 0.6 + ws_noise(q * vec2(1.0 / 8.0, 1.0 / 13.0) + 13.7) * 0.4;
	return smoothstep(0.3, 0.8, g) * (0.4 + 0.6 * wind_field.z) + (1.0 - wind_field.z) * 0.3;
}
float wind_gust(vec2 xz) { return wind_gust_dir(xz, wind_here(xz).xy); }
// (for coordinates that are not scene positions, e.g. the terrain's folded UVs: the player's wind)
float wind_gust_global(vec2 xz) { return wind_gust_dir(xz, wind_dir()); }

// World-space offset (x, z) of a plant's tip of the given height (m), and how far it dips (y, negative).
vec3 wind_bend(vec2 xz, float phase, float height, float stiffness) {
	vec3 here = wind_here(xz);
	float s = here.z;
	float gust = wind_gust_dir(xz, here.xy);
	float push = s * s * (0.3 + 1.0 * gust);
	float lean = height * (1.0 - exp(-push * 0.8)) * 0.6;
	// Natural swing: slow, bigger in stronger wind and as gusts come and go.
	float swing = sin(TIME * 6.2832 * stiffness + phase) * height * (0.03 + 0.09 * s) * (0.5 + 0.5 * gust);
	float flutter = sin(TIME * 6.2832 * stiffness * 2.7 + phase * 3.1) * height * 0.012 * s;
	vec2 d = here.xy;
	vec2 side = vec2(-d.y, d.x);
	float along = lean + swing;
	vec2 off = d * along + side * (flutter + swing * 0.25);
	// The tip moves on an arc: it dips as it bends.
	float dip = -min(height * 0.5, dot(off, off) / max(2.0 * height, 0.01));
	return vec3(off.x, dip, off.y);
}
