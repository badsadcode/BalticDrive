shader_type spatial;
render_mode cull_back, diffuse_burley;

// Buildings raised from map footprints. One mesh per chunk; the vertex colour carries the tint
// (RGB) and what the face is (A): 1 wall with windows, 0.66 blank wall, 0.33 pitched roof, 0 flat roof.
// Walls: UV = (metres along the wall, metres above the floor), UV2 = (wall length, wall height),
// which lets the windows be spaced to fit each wall exactly.

#include "res://Shaders/common.gdshaderinc"

global uniform float wetness;

varying vec3 wpos;

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

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wpos.xz += world_offset; // patterns stay fixed to the ground when the origin moves
}

void fragment() {
	float kind = COLOR.a;
	vec3 tint = COLOR.rgb;
	float dist = length(VERTEX);
	float fade = clamp(1.0 - dist / 140.0, 0.0, 1.0);
	float is_wall = step(0.5, kind);
	float has_windows = step(0.85, kind);
	float is_tiles = step(0.2, kind) * (1.0 - is_wall);

	vec2 wall_p = vec2(UV.x, UV.y);
	float n_big = vnoise(wall_p * 0.35 + wpos.xz * 0.05);
	float n_fine = mix(0.5, vnoise(wall_p * 6.0) * 0.6 + vnoise(wall_p * 19.0) * 0.4, fade);

	// ---- wall ----
	vec3 wall = tint * (0.78 + 0.3 * n_big + 0.16 * (n_fine - 0.5));
	// rain streaks under the eaves, grime rising from the ground
	float streak = vnoise(vec2(UV.x * 2.2, UV.y * 0.25)) * smoothstep(UV2.y - 2.5, UV2.y, UV.y);
	wall *= 1.0 - 0.28 * streak;
	wall *= mix(0.62, 1.0, smoothstep(-0.2, 0.9, UV.y + (n_fine - 0.5) * 0.5));

	float len = abs(UV2.x);
	float front = step(UV2.x, -0.01);
	float height = UV2.y;
	float floors = max(1.0, floor(height / 2.9));
	float fh = max(height / floors, 0.01); // roofs carry no wall size: avoid 0/0 (NaN turned every roof black)
	float cols = max(1.0, floor(len / 2.7));
	float cw = max(len / cols, 0.01);
	vec2 cell = vec2(floor(UV.x / cw), floor(UV.y / fh));
	float xu = (fract(UV.x / cw) - 0.5) * cw;
	float yv = fract(UV.y / fh) * fh;
	float in_wall = step(0.0, UV.y) * step(UV.y, height) * step(1.7, len) * step(2.5, height);
	float win_w = min(0.62, cw * 0.5 - 0.3);
	float sill = 0.95, head = min(fh - 0.35, 2.35);
	float frame = smoothstep(win_w + 0.07, win_w + 0.05, abs(xu)) * smoothstep(sill - 0.07, sill - 0.05, yv) * smoothstep(head + 0.07, head + 0.05, yv);
	float glass = smoothstep(win_w, win_w - 0.02, abs(xu)) * smoothstep(sill, sill + 0.02, yv) * smoothstep(head, head - 0.02, yv);
	float window = has_windows * in_wall;
	float r = hash(cell + vec2(floor(len * 7.3), floor(height * 3.1)));
	float boarded = step(r, 0.14);
	vec3 frame_col = vec3(0.34, 0.33, 0.31) * (0.8 + 0.4 * n_fine);
	vec3 glass_col = mix(vec3(0.012, 0.016, 0.02), vec3(0.05, 0.065, 0.075), r * r);
	// mullion cross
	float mull = max(smoothstep(0.03, 0.02, abs(xu)), smoothstep(0.03, 0.02, abs(yv - (sill + head) * 0.5)));
	glass_col = mix(glass_col, frame_col * 0.8, mull * 0.8);
	vec3 board_col = vec3(0.20, 0.15, 0.10) * (0.7 + 0.5 * vnoise(vec2(xu * 9.0, yv * 1.5 + r * 30.0)));
	vec3 pane = mix(glass_col, board_col, boarded);
	// front door: replaces the ground-floor window in the middle bay of the front wall
	float door_bay = front * step(cell.y, 0.5) * step(abs(cell.x - floor(cols * 0.5)), 0.5) * in_wall * has_windows;
	window *= 1.0 - door_bay;
	float door_w = min(0.52, cw * 0.5 - 0.2);
	float door_frame = smoothstep(door_w + 0.08, door_w + 0.06, abs(xu)) * smoothstep(2.2, 2.18, yv);
	float door_leaf = smoothstep(door_w, door_w - 0.02, abs(xu)) * smoothstep(2.12, 2.1, yv);
	vec3 door_col = mix(vec3(0.13, 0.085, 0.05), vec3(0.10, 0.16, 0.14), step(0.5, r)) * (0.75 + 0.5 * vnoise(vec2(xu * 14.0, yv * 2.0)));
	door_col *= 1.0 - 0.35 * smoothstep(0.03, 0.0, abs(abs(xu) - door_w * 0.5)) ;
	wall = mix(wall, frame_col, frame * window);
	wall = mix(wall, pane, glass * window);
	wall = mix(wall, frame_col * 0.8, door_frame * door_bay);
	wall = mix(wall, door_col, door_leaf * door_bay);
	float glassy = glass * window * (1.0 - boarded);

	// ---- roofs ----
	float row = fract(wpos.y * 3.2 + n_big * 0.3);
	vec3 tiles = tint * (0.75 + 0.35 * n_big) * mix(0.72, 1.05, smoothstep(0.0, 0.25, row));
	tiles = mix(tiles, vec3(0.05, 0.075, 0.035), smoothstep(0.62, 0.9, vnoise(wpos.xz * 0.6)) * 0.45);
	vec3 flat_roof = tint * (0.7 + 0.5 * vnoise(wpos.xz * 0.9) + 0.2 * (n_fine - 0.5));
	vec3 roof = mix(flat_roof, tiles, is_tiles);

	vec3 col = mix(roof, wall, is_wall);
	float wet = wetness * (1.0 - is_wall * 0.6);
	ALBEDO = col * mix(1.0, 0.68, wet) * mix(1.0, cloud_shade(wpos.xz, TIME), 1.0 - is_wall * 0.5);
	ROUGHNESS = mix(mix(0.92, 0.55, wet), 0.12, glassy);
	SPECULAR = mix(0.3, 0.6, glassy);
	METALLIC = 0.0;
}
