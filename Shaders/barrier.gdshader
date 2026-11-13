shader_type spatial;
render_mode cull_disabled, diffuse_burley;

// Fences, walls, hedges and guard rails (D25, Map/MapMesher.AddBarriers). One mesh per map chunk:
//   COLOR.rgb = base colour, COLOR.a = style: 0 solid, 0.1 wire mesh, 0.2 vertical bars (railing),
//   0.3 concrete panels, 0.4 brick, 0.5 plaster, 0.6 hedge, 0.7 steel band (guard rail), 0.8 noise barrier panels
//   UV = (metres along the line, metres above the bottom); UV2.x = height of this barrier (m).
// Wire mesh and bars are cut out (alpha scissor), so you see through a fence.

#include "res://Shaders/common.gdshaderinc"

varying vec3 wpos;
varying float style;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

float bayer(vec2 frag) {
	ivec2 p = ivec2(mod(frag, 4.0));
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	return (m[p.x + p.y * 4] + 0.5) / 16.0;
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wpos.xz += world_offset;
	style = COLOR.a;
}

void fragment() {
	vec2 uv = UV;
	float h = UV2.x;
	vec3 col = COLOR.rgb;
	float alpha = 1.0;
	float rough = 0.85;
	float metal = 0.0;
	float dist = length(VERTEX);
	float s = floor(style * 10.0 + 0.5);
	if (s == 1.0) {
		// wire mesh: diamonds 6 cm, posts every 2.5 m drawn as geometry; fades to a translucent sheet far away
		// (the wires are the cell edges, where fract() is near 0 or 1; thicker with distance so they don't vanish)
		vec2 d = vec2(uv.x + uv.y, uv.x - uv.y) / 0.06;
		vec2 f = abs(fract(d) - 0.5);
		float w = 0.44 - min(0.2, dist * 0.004);
		float wire = smoothstep(w - 0.03, w, max(f.x, f.y));
		float top = step(h - 0.05, uv.y) + step(uv.y, 0.04);
		// far away the mesh becomes a thin dithered veil instead of a solid sheet
		float far_cover = mix(1.0, 0.3, smoothstep(15.0, 60.0, dist));
		alpha = max(wire * step(bayer(FRAGCOORD.xy), far_cover), top);
		metal = 0.5; rough = 0.55;
	} else if (s == 2.0) {
		// railing: vertical bars every 12 cm, a rail at the top and bottom
		float bar = 1.0 - smoothstep(0.012, 0.02, abs(fract(uv.x / 0.12) - 0.5) * 0.12);
		float rail = step(h - 0.06, uv.y) + step(uv.y, 0.06);
		float far_cover = mix(1.0, 0.45, smoothstep(20.0, 70.0, dist));
		alpha = max(max(bar, rail) * step(bayer(FRAGCOORD.xy), far_cover), rail);
		metal = 0.6; rough = 0.45;
	} else if (s == 3.0) {
		// concrete panels 2 m wide with a grooved pattern and a seam at each post
		float seam = smoothstep(0.03, 0.0, abs(fract(uv.x / 2.0) - 0.5) * 2.0 - 0.97);
		float groove = 0.9 + 0.1 * step(0.5, fract(uv.y / 0.25));
		col *= groove * (1.0 - 0.35 * seam) * (0.9 + 0.2 * vnoise(wpos.xz * 1.5 + uv.y));
	} else if (s == 4.0) {
		// brick: 25 × 6.5 cm, mortar lines, a little colour per brick
		vec2 b = vec2(uv.x / 0.25 + 0.5 * floor(uv.y / 0.065), uv.y / 0.065);
		vec2 f = fract(b);
		float mortar = step(f.x, 0.06) + step(f.y, 0.12);
		col *= mix(0.8 + 0.35 * hash(floor(b) + floor(wpos.xz)), 1.45, clamp(mortar, 0.0, 1.0));
	} else if (s == 5.0) {
		// plaster: soft mottling, dirt rising from the ground
		col *= (0.88 + 0.18 * vnoise(vec2(uv.x, uv.y) * 1.3)) * mix(0.75, 1.0, smoothstep(0.0, 0.6, uv.y));
	} else if (s == 6.0) {
		// hedge: leafy clumps, darker inside and at the base, ragged top edge
		float n = vnoise(wpos.xz * 3.1 + uv.y * 2.7) * 0.6 + vnoise(wpos.xz * 9.0 - uv.y * 7.0) * 0.4;
		col *= (0.6 + 0.6 * n) * mix(0.65, 1.0, smoothstep(0.0, 0.8, uv.y / max(h, 0.1)));
		alpha = step(uv.y, h - 0.12 + 0.18 * n) + 0.0;
		rough = 0.95;
	} else if (s == 7.0) {
		// guard rail: corrugated steel band
		col *= 0.85 + 0.25 * sin(uv.y * 30.0);
		metal = 0.7; rough = 0.35;
	} else if (s == 8.0) {
		// noise barrier: panels 4 m wide, ribbed
		float seam = smoothstep(0.04, 0.0, abs(fract(uv.x / 4.0) - 0.5) * 2.0 - 0.98);
		col *= (0.9 + 0.1 * step(0.5, fract(uv.x / 0.15))) * (1.0 - 0.4 * seam);
	}
	if (alpha < 0.5) discard;
	// panels are one sheet drawn from both sides: light the side we see
	if (!FRONT_FACING) NORMAL = -NORMAL;
	ALBEDO = col * cloud_shade(wpos.xz, TIME);
	ROUGHNESS = rough;
	METALLIC = metal;
}
