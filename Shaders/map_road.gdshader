shader_type spatial;
render_mode cull_back, diffuse_burley;

// Roads of the real-map world. One mesh carries every road in a chunk, so the per-road
// properties travel in the vertex colour:
//   R = surface (0 asphalt, 0.25 concrete, 0.5 gravel/dirt, 0.75 bare steel, 1 railway)
//   G = markings (0 none, 0.33 two-way centre line only, 0.66 two-way with edge lines, 1 one-way lanes)
//   A = (lane count + 9 * lanes in the point direction) / 255; that second number is 0 for an even split (D19)
//   B = half width / 16
// UV.x = lateral offset in metres from the centre line, UV.y = distance along the road.

#include "res://Shaders/common.gdshaderinc"

global uniform float wetness;
global uniform vec3 sky_tint;

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
	float u = UV.x;
	float v = UV.y;
	float au = abs(u);
	float half_width = COLOR.b * 16.0;
	float surf = COLOR.r;
	float mark = COLOR.g;
	float dist = length(VERTEX);
	float fade = clamp(1.0 - dist / 120.0, 0.0, 1.0);

	float n_patch = vnoise(vec2(u * 0.35, v * 0.06));
	float n_mid = vnoise(wpos.xz * 0.7);
	float n_fine = mix(0.5, vnoise(wpos.xz * 11.0) * 0.6 + vnoise(wpos.zx * 23.0) * 0.4, fade * fade);

	// ---- asphalt ----
	vec3 asphalt = mix(vec3(0.030, 0.031, 0.034), vec3(0.060, 0.059, 0.057), n_patch);
	asphalt *= 0.8 + 0.25 * n_mid + 0.3 * (n_fine - 0.5);
	float crack = smoothstep(0.035, 0.0, abs(vnoise(vec2(u * 1.3, v * 0.45)) - 0.5)) * fade;
	asphalt *= 1.0 - 0.55 * crack * step(0.55, vnoise(vec2(v * 0.02, 3.0)));

	// ---- paint ----
	// UV2.x = metres to the edge of the nearest junction (negative inside it), UV2.y = +1 when that
	// junction is ahead in the direction of increasing v.
	float wear = smoothstep(0.25, 0.75, vnoise(vec2(u * 2.0, v * 0.35)) + 0.25 * vnoise(vec2(u * 9.0, v * 3.0)));
	float clear = UV2.x;
	float open_road = smoothstep(0.0, 1.2, clear);          // markings fade out before a junction
	float dash = step(fract(v / 9.0), 0.42);
	float code = floor(COLOR.a * 255.0 + 0.5);
	float lanes = max(1.0, mod(code, 9.0));
	float fwd = floor(code / 9.0);
	float split = step(0.5, fwd) * step(fwd, lanes - 0.5);   // a known, uneven two-way split
	float one_way = step(0.9, mark);
	float two_way = step(0.2, mark) * (1.0 - one_way);
	float has_edges = step(0.5, mark);
	float lw = half_width * 2.0 / lanes;

	// lane dividers: dashes on every boundary between lanes
	float fu = fract((u + half_width) / lw);
	float lane_d = min(fu, 1.0 - fu) * lw;
	float inner = step(au, half_width - 1.0);
	float lane = smoothstep(0.085, 0.06, lane_d) * dash * inner * step(1.5, lanes);
	// two-way roads: the boundary between the directions is the centre line (double and solid on four lanes or
	// more). Lanes in the point direction are on the right (positive u). An unknown odd lane count leaves the
	// centre line alone in the middle; a known split puts it after the backward lanes and keeps every divider.
	float cu = split * (-half_width + (lanes - fwd) * lw);
	float ac = abs(u - cu);
	float is_centre = smoothstep(0.5, 0.3, ac);
	float even = 1.0 - mod(lanes, 2.0);
	float centre_dash = smoothstep(0.085, 0.06, ac) * dash;
	float centre_double = smoothstep(0.05, 0.03, abs(ac - 0.11)) * step(3.5, lanes);
	float centre = mix(centre_dash, centre_double, step(3.5, lanes));
	float dividers = max(even * step(3.5, lanes), split * step(2.5, lanes));
	float lane_two_way = lane * (1.0 - is_centre) * dividers;
	float marks = one_way * lane + two_way * max(centre, lane_two_way);
	float edge = smoothstep(0.075, 0.05, abs(au - (half_width - 0.3))) * has_edges;
	// stop line across the approaching lanes, just before the junction
	float approach = mix(step(0.0, (u - cu) * UV2.y), step(0.0, UV2.y), one_way);
	float stop_line = smoothstep(0.22, 0.18, abs(clear - 0.75)) * approach * step(au, half_width - 0.3) * step(0.2, mark) * step(clear, 30.0);
	float paint = max(max(marks, edge) * open_road, stop_line) * mix(0.25, 0.95, wear);
	vec3 col = mix(asphalt, vec3(0.42, 0.40, 0.34), paint);

	// ---- gravel / dirt track: two worn ruts and a rougher crown ----
	float n_rut = vnoise(vec2(u * 1.5, v * 0.2));
	vec3 loose = mix(vec3(0.105, 0.085, 0.062), vec3(0.185, 0.160, 0.125), n_mid);
	loose *= 0.72 + 0.4 * n_fine;
	float rut = exp(-pow((au - min(0.85, half_width * 0.5)) / 0.38, 2.0));
	loose *= 1.0 - 0.22 * rut;
	vec3 crown = mix(vec3(0.05, 0.085, 0.03), vec3(0.085, 0.105, 0.04), n_mid);
	loose = mix(loose, crown, (1.0 - rut) * smoothstep(0.45, 0.7, n_rut) * 0.6 * step(half_width, 2.4));
	float is_loose = step(0.4, surf) * step(surf, 0.62);
	col = mix(col, loose, is_loose);

	// ---- concrete (bridge parapets and undersides) ----
	vec3 concrete = vec3(0.21, 0.205, 0.195) * (0.75 + 0.3 * n_mid + 0.2 * (n_fine - 0.5));
	float is_concrete = step(0.12, surf) * step(surf, 0.38);
	col = mix(col, concrete, is_concrete);

	// ---- bare steel (tram rails set into the street) ----
	float is_steel = step(0.65, surf) * step(surf, 0.85);
	col = mix(col, vec3(0.16, 0.13, 0.11) * (0.8 + 0.4 * n_fine), is_steel);

	// ---- railway: ballast, sleepers, two rails ----
	float is_rail = step(0.85, surf);
	vec3 ballast = mix(vec3(0.085, 0.078, 0.072), vec3(0.16, 0.15, 0.14), n_fine) * (0.8 + 0.3 * n_mid);
	float sleeper = step(fract(v / 0.62), 0.38) * step(au, 1.25);
	ballast = mix(ballast, vec3(0.045, 0.036, 0.03), sleeper * 0.85);
	float rail_line = smoothstep(0.06, 0.035, abs(au - 0.72));
	ballast = mix(ballast, vec3(0.20, 0.16, 0.13), rail_line);
	col = mix(col, ballast, is_rail);

	// gravel shoulder outside the carriageway
	float hard = 1.0 - max(max(is_loose, is_rail), is_steel);
	float shoulder = smoothstep(half_width - 0.1, half_width + 0.25, au + (n_mid - 0.5) * 0.4) * (1.0 - is_concrete) * (1.0 - is_steel);
	vec3 gravel = mix(vec3(0.10, 0.085, 0.065), vec3(0.17, 0.15, 0.12), n_mid) * (0.75 + 0.5 * n_fine);
	col = mix(col, gravel, shoulder);

	float puddle = smoothstep(0.55, 0.75, vnoise(vec2(u * 0.5 + 3.0, v * 0.13))) * smoothstep(0.35, 1.0, wetness) * (1.0 - shoulder) * (1.0 - is_rail);
	float wet = clamp(wetness + puddle, 0.0, 1.0);
	float rough_dry = mix(0.9, 0.97, max(shoulder, 1.0 - hard));
	float rough_wet = mix(0.28, 0.6, max(shoulder, 1.0 - hard));
	ALBEDO = col * mix(1.0, 0.5, wet) * cloud_shade(wpos.xz, TIME);
	ROUGHNESS = mix(rough_dry, rough_wet, wet) - puddle * 0.2;
	METALLIC = max(rail_line * is_rail, is_steel) * 0.6;
	SPECULAR = 0.4;
	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
	EMISSION = sky_tint * fres * wet * 0.35 * (1.0 - shoulder * 0.7) * hard;
}
