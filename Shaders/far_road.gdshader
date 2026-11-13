shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_disabled;

// Distant roads (D24, World/FarRoads.cs): a plain ribbon per road beyond the detailed road meshes (~1 km) out to the
// horizon. Each vertex is the road's centre line: UV.x = which side (-1 / +1), UV.y = distance along the road,
// UV2.x = half width (m), UV2.y = the farthest this road is drawn (m), NORMAL.xz = the sideways direction,
// COLOR = surface colour. The shader
// * widens the ribbon with distance so a road stays at least a pixel or so wide instead of shimmering away;
// * pulls the ribbon toward the camera in depth (not on screen), so the coarse distant terrain, which can sit a few
//   metres off the road's own height between its grid points, never swallows it;
// * hands over to the detailed roads across a dithered band near 1 km.

#include "res://Shaders/common.gdshaderinc"

uniform float near_start = 960.0;   // the detailed roads reach ~1000 m (MapContentStreamer.Radius)
uniform float near_band = 60.0;

varying float dist;
varying float max_dist;
varying vec3 wpos;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
	vec3 centre = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	dist = length(centre.xz - CAMERA_POSITION_WORLD.xz);
	max_dist = UV2.y;
	vec3 side = normalize(vec3(NORMAL.x, 0.0, NORMAL.z) + 1e-5);
	float hw = max(UV2.x, dist * 0.0011);
	vec3 world = centre + side * hw * UV.x;
	wpos = world;
	wpos.xz += world_offset;
	vec4 view = VIEW_MATRIX * vec4(world, 1.0);
	float len = length(view.xyz);
	float pull = min(2.0 + len * 0.006, len * 0.5);
	view.xyz *= (len - pull) / len;
	POSITION = PROJECTION_MATRIX * view;
	NORMAL = (VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz;
}

float bayer(vec2 frag) {
	ivec2 p = ivec2(mod(frag, 4.0));
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	return (m[p.x + p.y * 4] + 0.5) / 16.0;
}

void fragment() {
	float t = clamp((dist - near_start) / near_band, 0.0, 1.0);
	if (bayer(FRAGCOORD.xy) >= t || dist > max_dist) discard;
	float n = hash(floor(wpos.xz * 0.5));
	ALBEDO = COLOR.rgb * (0.92 + 0.12 * n);
	ROUGHNESS = 0.9;
}
