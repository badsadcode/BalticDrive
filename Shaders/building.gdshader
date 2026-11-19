shader_type spatial;
render_mode cull_back, diffuse_burley;

// Buildings raised from map footprints. One mesh per chunk; the vertex colour carries the tint
// (RGB) and what the face is (A): 1 wall with windows, 0.66 blank wall, 0.5 real glass (BadBuilding windows, D29),
// 0.33 pitched roof, 0 flat roof; and patterns on blank walls (D63): 0.6 roller door, 0.72 ribbed metal, 0.78 brick.
// Walls: UV = (metres along the wall, metres above the floor), UV2 = (wall length, wall height),
// which lets the windows be spaced to fit each wall exactly.

#include "res://Shaders/common.gdshaderinc"

global uniform float wetness;
// the skyline's copy of a building the near cells draw themselves: hidden within this distance (D32; 0 = never)
uniform float near_cut = 0.0;

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
	if (near_cut > 0.0 && length(VERTEX) < near_cut) discard;
	float kind = COLOR.a;
	vec3 tint = COLOR.rgb;
	float dist = length(VERTEX);
	float fade = clamp(1.0 - dist / 140.0, 0.0, 1.0);
	float is_wall = step(0.5, kind);
	float has_windows = step(0.85, kind);
	float is_tiles = step(0.2, kind) * (1.0 - is_wall);
	float is_glass = 1.0 - step(0.06, abs(kind - 0.5));

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
	// patterns on blank walls (D63): ribbed metal cladding, brick courses, roller-door slats
	float is_ribbed = 1.0 - step(0.02, abs(kind - 0.72));
	float is_brick = 1.0 - step(0.02, abs(kind - 0.78));
	float is_roller = 1.0 - step(0.02, abs(kind - 0.6));
	{
		// ribs every 0.25 m: a lit crest and a shaded trough, fading to an average far away
		float rib = fract(UV.x / 0.25);
		float profile = smoothstep(0.0, 0.12, rib) * smoothstep(0.5, 0.38, rib);
		float rib_fade = clamp(1.0 - dist / 90.0, 0.0, 1.0);
		vec3 ribbed = tint * mix(0.9, mix(0.72, 1.08, profile), rib_fade) * (0.92 + 0.12 * n_big);
		ribbed *= mix(0.75, 1.0, smoothstep(-0.2, 1.2, UV.y));   // dirt splashed up from the ground
		wall = mix(wall, ribbed, is_ribbed);
		// brick: 25 x 6.5 cm courses, mortar lines, a little colour per brick
		vec2 bc = vec2(UV.x / 0.25 + 0.5 * floor(UV.y / 0.075), UV.y / 0.075);
		vec2 bf = fract(bc);
		float mortar = max(smoothstep(0.08, 0.0, bf.x), smoothstep(0.14, 0.0, bf.y));
		float brick_fade = clamp(1.0 - dist / 60.0, 0.0, 1.0);
		vec3 brick = tint * (0.82 + 0.3 * hash(floor(bc) + floor(wpos.xz))) * (0.85 + 0.25 * n_big);
		brick = mix(tint * 0.95, mix(brick, vec3(0.32, 0.30, 0.27), mortar * 0.8), brick_fade);
		// weathered, sooty: some saturation out (pure reds glowed orange in the grade, Tom: "too orange")
		brick = mix(brick, vec3(dot(brick, vec3(0.299, 0.587, 0.114))), 0.45) * 0.85;
		wall = mix(wall, brick, is_brick);
		// roller door: horizontal slats 10 cm
		float slat = smoothstep(0.0, 0.15, fract(UV.y / 0.1)) * smoothstep(1.0, 0.8, fract(UV.y / 0.1));
		wall = mix(wall, tint * mix(0.7, 1.0, slat), is_roller);
	}
	// glass curtain wall (D70): panels 1.5 m wide, a floor band and slab edge every 3.6 m, thin mullions; the glass
	// darker head-on and reflecting the sky at grazing angles, each panel a shade different
	float is_curtain = 1.0 - step(0.012, abs(kind - 0.635));
	if (is_curtain > 0.5) {
		float pu = fract(UV.x / 1.5), pv = fract(UV.y / 3.6);
		vec2 cell_id = vec2(floor(UV.x / 1.5), floor(UV.y / 3.6));
		// lines 6 cm wide but never under a pixel (a thinner one vanished at 30 m), averaged out far away
		vec2 px = max(fwidth(UV), vec2(1e-4));
		float mull_fade = clamp(1.0 - dist / 600.0, 0.0, 1.0);
		float du = min(pu, 1.0 - pu) * 1.5, dv = min(pv, 1.0 - pv) * 3.6;
		float wu = max(0.06, px.x * 1.2), wv = max(0.08, px.y * 1.2);
		float mull = max(smoothstep(wu, wu - px.x, du) * min(1.0, 0.06 / wu * 1.5),
		                 smoothstep(wv, wv - px.y, dv) * min(1.0, 0.08 / wv * 1.5)) * mull_fade;
		// the opaque spandrel at each floor: 0.9 m of lighter, frosted panel
		float band = smoothstep(0.25 + px.y / 3.6, 0.25, pv) * mull_fade;
		float fres = pow(1.0 - clamp(abs(dot(NORMAL, VIEW)), 0.0, 1.0), 3.0);
		vec3 sky = vec3(0.55, 0.65, 0.75);
		vec3 pane = tint * (0.7 + 0.45 * hash(cell_id + floor(wpos.xz * 0.01)));
		pane = mix(pane, sky * 0.7, fres * 0.75);
		pane = mix(pane, tint * 1.35 + vec3(0.06, 0.065, 0.07), band * 0.8);
		wall = mix(pane, vec3(0.30, 0.31, 0.32), mull * 0.85);
	}
	// weathered wood (D73): horizontal planks 18 cm (timber walls, 0.82) or vertical boards 20 cm with battens over the
	// joints (gables, 0.69); each plank a shade different, grain along it, dark gaps, greyer and darker low down
	float is_timber = 1.0 - step(0.012, abs(kind - 0.82));
	float is_boards = 1.0 - step(0.012, abs(kind - 0.69));
	if (is_timber + is_boards > 0.5) {
		float across = mix(UV.y, UV.x, is_boards), along = mix(UV.x, UV.y, is_boards);
		float pw = mix(0.18, 0.2, is_boards);
		float pf = fract(across / pw);
		float pid = floor(across / pw);
		float wood_fade = clamp(1.0 - dist / 80.0, 0.0, 1.0);
		float gap = mix(1.0, smoothstep(0.0, 0.07, pf) * smoothstep(1.0, 0.9, pf), wood_fade);
		float shade = 0.82 + 0.3 * hash(vec2(pid, floor(wpos.x + wpos.z)));
		float grain = 0.88 + 0.24 * vnoise(vec2(along * 1.3, across * 22.0 + pid * 3.7));
		vec3 wood = tint * shade * mix(1.0, grain, wood_fade) * mix(0.55, 1.0, gap);
		// a batten over every other joint on boards
		float batten = is_boards * (1.0 - step(0.06, abs(fract(across / (pw * 2.0)) - 0.0) * pw * 2.0)) * wood_fade;
		wood = mix(wood, tint * 0.75, batten * 0.7);
		wood *= mix(0.7, 1.0, smoothstep(-0.2, 1.4, UV.y));   // damp and dirt near the ground
		wall = wood;
	}
	float glassy = max(max(glass * window * (1.0 - boarded), is_glass), is_curtain * 0.85);
	// real glass from BadBuilding: its tint, a faint sky reflection, no grime
	wall = mix(wall, tint * 0.9 + vec3(0.012, 0.016, 0.02), is_glass);

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
