// Screen-space sun shafts ("god rays"): from each pixel, march toward the sun's screen position and gather the
// bright, unobstructed sky (depth = far plane) on the way. Mountains, trees and clouds in front of the sun cut the
// light into rays. Drawn on a full-screen quad after the scene (additive).
shader_type spatial;
render_mode unshaded, blend_add, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0); // world space, toward the sun
uniform vec3 sun_color : source_color = vec3(1.0, 0.95, 0.85);
uniform float intensity = 0.6;
// Build 33 (Tom: "make god rays stand out a bit more without losing what we have"): 0 = the build 32 rays exactly.
// Contrast (setting "God ray contrast") adds no light: each pixel's path to the sun is compared with the paths of its neighbours rotated a
// little around the sun. Clear gaps get brighter and the shadow lanes behind clouds, ridges and trees get darker, while
// an open sky (no occluders: every path alike) stays as it was. (A first try gathered farther with a slower falloff:
// the bright sky around the sun added up into the blob of round 24 again.)
uniform float contrast = 0.0;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

// Bright, unobstructed sky gathered from `start` toward the sun, `n` samples over 85 % of the way.
// Round 29 (Tom: the sun "blobbing 80 % of the screen", build 33+ with God ray contrast 1 and shafts 3): only sky within
// ~8° of the sun counts (sun_view: toward the sun in view space). Strong forward-scattering haze made the whole sky
// around a low sun brighter than the threshold, and the shafts gathered it into a white disc half the screen wide.
float gather(vec2 start, vec2 sun_uv, int n, float decay, float jitter, mat4 inv_proj, vec3 sun_view) {
	vec2 delta = (sun_uv - start) / float(n) * 0.85;
	vec2 uv = start + delta * jitter; // dither: no banding
	float light = 0.0, weight = 1.0;
	for (int i = 0; i < n; i++) {
		uv += delta;
		if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) break;
		float depth = textureLod(depth_tex, uv, 0.0).r;
		if (depth < 1e-6) { // sky (reverse-Z: the far plane is 0)
			vec3 c = textureLod(screen_tex, uv, 0.0).rgb;
			vec4 vv = inv_proj * vec4(uv * 2.0 - 1.0, 0.5, 1.0);
			float near_disc = smoothstep(0.9903, 0.99863, dot(normalize(vv.xyz / vv.w), sun_view)); // 8° … 3°
			// Only very bright sky casts rays: the sun disc, its halo and sunlit cloud edges. Clouds (dimmer) block it,
			// so the sun breaks through gaps in rays; a clear sky gives rays past trees and mountains.
			// (round 24: relative to the threshold 0.9, a hazy low-sun sky was "bright" for a quarter of the screen and
			// gathered into a huge white disc — Tom: "the sun is like a huge blob"; now only the disc and the inner halo,
			// each sample capped)
			light += min(max(0.0, dot(c, vec3(0.3, 0.59, 0.11)) - 1.8), 3.0) * weight * near_disc;
		}
		weight *= decay;
	}
	return light / float(n);
}

void fragment() {
	vec3 sun_view = (VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz;
	if (sun_view.z >= -0.05 || intensity <= 0.0) discard; // sun behind the camera
	vec4 clip = PROJECTION_MATRIX * vec4(sun_view, 0.0);
	vec2 sun_uv = clip.xy / clip.w * 0.5 + 0.5;
	// Fade out as the sun leaves the screen.
	vec2 off = max(vec2(0.0), max(-sun_uv, sun_uv - 1.0));
	float on_screen = 1.0 - smoothstep(0.0, 0.45, length(off));
	if (on_screen <= 0.0) discard;

	float jitter = hash(FRAGCOORD.xy);
	vec3 sv = normalize(sun_view);
	float light = gather(SCREEN_UV, sun_uv, 32, 0.955, jitter, INV_PROJECTION_MATRIX, sv);
	if (contrast > 0.0) {
		// Neighbours rotated about 1.5 degrees either way around the sun (in pixels, so the rotation is round).
		vec2 px = vec2(VIEWPORT_SIZE.x / VIEWPORT_SIZE.y, 1.0);
		vec2 v = (SCREEN_UV - sun_uv) * px;
		vec2 side = vec2(-v.y, v.x) * 0.026 / px;
		float here = gather(SCREEN_UV, sun_uv, 16, 0.92, jitter, INV_PROJECTION_MATRIX, sv);
		float around = 0.5 * (gather(SCREEN_UV + side, sun_uv, 16, 0.92, jitter, INV_PROJECTION_MATRIX, sv) + gather(SCREEN_UV - side, sun_uv, 16, 0.92, jitter, INV_PROJECTION_MATRIX, sv));
		float lane = (here - around) / (here + around + 0.02); // -1 (in a shadow lane) .. 1 (in a gap)
		light *= clamp(1.0 + contrast * 3.0 * lane, 0.0, 1.0 + 1.5 * contrast);
	}
	// Stronger toward the sun, and only while the sun is above the horizon.
	float near_sun = 1.0 - smoothstep(0.0, 0.9, distance(SCREEN_UV, sun_uv));
	float elevation = smoothstep(-0.02, 0.12, sun_direction.y);
	ALBEDO = sun_color * light * intensity * on_screen * elevation * (0.6 + 1.4 * near_sun) * 2.6;
}
