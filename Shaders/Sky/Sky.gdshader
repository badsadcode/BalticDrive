// Sky with drifting clouds for the rewrite world. The main cloud field is a tileable noise texture laid on a plane
// ~1.6 km above the ground; the same texture (offset along the sun direction) casts the cloud shadows on the land
// (Atmosphere.cs), so shadows match the clouds above. Cloud positions/coverage come from global uniforms: updating
// them every frame does not dirty this material (which would re-bake the sky's radiance map each frame).
//
// Cloud quality (setting "Clouds"):
//   0 one layer   — the main layer only (build 10 look);
//   1 layered     — + high, thin, streaky cirrus drifting slowly on another heading and low, ragged, fast clouds
//                   under the main deck: different speeds and heights give wind and depth;
//   2 volumetric  — experimental: the main deck ray-marched as a 1.3–2.4 km thick volume lit by the sun (self
//                   shadowing, silver lining, darker bases), at half resolution; high and low layers as in 1.
//   3 volumetric v2 — round 29: the same volume, also drawn in front of the scene by CloudsNear.gdshader (depth-aware),
//                   so clouds come down onto the mountains, wrap summits and can be flown into; ray offsets from
//                   interleaved gradient noise. The sky draws the part beyond the scene.
// Biome moods: a global tint (rgb) and haze (a) set by Atmosphere.cs from the biome around the player (option).
shader_type sky;
render_mode use_debanding, use_half_res_pass, use_quarter_res_pass;
// Cloud look "reference" (round 32): Godot fogs the half-resolution pass as well as the full one (sky.glsl: only the
// cubemap passes skip fog), so the clouds drawn there were veiled twice — depth fog and volumetric fog — and in front of
// the land (CloudsNear) only once: greyer, flatter clouds in the sky and a seam between the two. Both fogs are affine per
// pixel, x·A + B. The half-resolution pass writes the clouds × FOG_UNDO_K (B becomes negligible), the quarter-resolution
// pass writes FOG_UNDO_K alone (its fogged value is K·A + B ≈ K·A), and the full pass divides: the clouds unfogged, fogged
// once there with the rest of the sky. (FOG_UNDO_K keeps bright clouds × K well inside the half floats' range.)
#define FOG_UNDO_K 2048.0

#define SKY_LIGHT0_COLOR LIGHT0_COLOR
// Test (--cloud-split-test, CloudsNear.gdshader): the left half of the screen leaves its volumetric clouds to CloudsNear.
uniform int split_test = 0;
#include "res://Shaders/Sky/SkyCommon.gdshaderinc"

void sky() {
	vec3 dir = EYEDIR;
	float up = dir.y;
	vec3 sun_dir = LIGHT0_DIRECTION;
	vec3 sun_col = LIGHT0_COLOR * LIGHT0_ENERGY;
	float day = clamp(sun_dir.y * 4.0 + 0.35, 0.08, 1.0);
	float mu = dot(dir, sun_dir);
	float coverage = cloud_state.z, storm = cloud_state.w;

	if (AT_HALF_RES_PASS) {
		// Only the volumetric deck is drawn here (premultiplied colour + opacity), composited in the full pass.
		COLOR = vec3(0.0); ALPHA = 0.0;
		if (quality == 2) { vec4 v = volumetric(dir, sun_dir, sun_col, day, mu, coverage, storm); COLOR = v.rgb; ALPHA = v.a; }
		// v2: the same volume from any height; nearer than the scene it is drawn by CloudsNear.gdshader.
		if (quality == 3 && clouds_one_pass_k() < 0.999) { vec4 v = volumetric_v2(dir, 1e9, cloud_jitter(FRAGCOORD.xy, cloud_more.z), 150, sun_dir, sun_col, day, mu, coverage, storm); COLOR = v.rgb; ALPHA = v.a; }
		if (cloud_look == 1 && !AT_CUBEMAP_PASS) COLOR *= FOG_UNDO_K;
	} else if (AT_QUARTER_RES_PASS) {
		// The fog's own transmittance A, measured (cloud look reference; see FOG_UNDO_K).
		COLOR = vec3(cloud_look == 1 && !AT_CUBEMAP_PASS ? FOG_UNDO_K : 0.0); ALPHA = 1.0;
	} else {
		vec3 col = sky_colour(dir, sun_dir, sun_col, day, mu);
		float night = 1.0 - smoothstep(-0.16, 0.04, sun_dir.y);
		if (!AT_CUBEMAP_PASS) {
			col += night_sky_colour(dir, sun_dir, night);
			col += (aurora_light(dir, cloud_more.z) + meteor(dir, cloud_more.z) * sky_extra.z) * night;
		}
		vec3 disc = sun_col * smoothstep(0.99955, 0.9998, mu) * 6.0 * sun_glare;
		if (sun_style == 1) {
			// Softer at noon (less blinding), larger and orange-red as it sinks toward the horizon.
			float lowsun = 1.0 - smoothstep(0.0, 0.4, sun_dir.y);
			float r0 = mix(0.99962, 0.99915, lowsun);
			vec3 tint = mix(vec3(1.0, 0.98, 0.92), vec3(1.0, 0.45, 0.18), lowsun * lowsun);
			disc = sun_col * tint * smoothstep(r0, r0 + 0.0002, mu) * mix(3.2, 0.95, lowsun) * sun_glare;
		}
		disc *= smoothstep(-0.004, 0.004, up); // a sun under the horizon shows no disc
		// How much of the sky behind the clouds below the cirrus still shows here: the halo and sun dogs form in the
		// cirrus, so any cloud in front hides them (Tom, storm dump 204727: a sun dog through the storm deck).
		float clear = 1.0;
		bool layered = quality >= 1 && !AT_CUBEMAP_PASS;
		float cirrus_here = 0.0;
		if (up > 0.0) {
			// High cirrus (7 km): thin, bright, slow, on its own heading.
			if (layered) {
				float t = 7000.0 / max(up, 0.015);
				float d = cirrus_density(cloud_layers.xy + dir.xz * t, coverage, cloud_front.w) * smoothstep(0.02, 0.15, up) * (1.0 - storm);
				cirrus_here = d;
				vec3 c = vec3(1.0, 0.99, 0.97) * (0.3 + 0.8 * day) * mix(vec3(1.0), LIGHT0_COLOR, 0.6) + sun_col * pow(max(mu, 0.0), 8.0) * 0.5
					+ underlight(dir) * 1.1; // high cirrus stays lit longest after sunset
				c *= mix(1.0, 0.06 + 0.1 * sky_extra.w, sky_night_k()); // (D187: dark at night, faintly moonlit)
				col = mix(col, c, d);
			}
			// Middle grey sheet (altostratus, ~5 km; D187, layered draft): a front's leading cloud, spreading over the sky
			// as it comes; the sun shows through it as through frosted glass (no disc, a soft bright patch).
			if (layered && layers_mode == 1 && cloud_front.w > 0.01) {
				float t = 5000.0 / max(up, 0.015);
				vec2 xz = cloud_layers.zw * 0.6 + dir.xz * t;
				float near_front = cloud_front.w * smoothstep(70000.0, 15000.0, cloud_front.z);
				// thicker toward the front's side of the sky, patchy over tens of km
				float side = 0.6 + 0.4 * dot(normalize(dir.xz + vec2(1e-4)), cloud_front.xy);
				// (D189: toward the horizon one pixel spans kilometres of the sheet and the texture aliased into horizontal
				// stripes: an explicit mip level from the footprint, and the fine octave fades out there)
				float fp = log2(max(t * 0.0015 / max(up, 0.02) / (36000.0 / float(textureSize(cloud_tex, 0).x)), 1.0));
				float fine = 1.0 - smoothstep(0.06, 0.2, 0.25 - up);
				float n = textureLod(cloud_tex, xz / 36000.0 + vec2(0.17, 0.59), fp).r * 0.7
					+ mix(0.5, textureLod(cloud_tex, xz / 9000.0, fp + 2.0).g, fine) * 0.3;
				float d = clamp(near_front * side * 1.4 - (0.6 - n) * 0.9, 0.0, 0.92) * smoothstep(0.01, 0.12, up);
				if (d > 0.0) {
					// (a flat grey-blue, darker toward the thick parts; the horizon colour made a pale glare)
					vec3 grey = vec3(0.56, 0.58, 0.63) * mix(1.05, 0.8, d) * mix(vec3(1.0), sky_mood.rgb, 0.3) * (0.25 + 0.75 * day);
					grey *= mix(1.0, 0.06 + 0.06 * sky_extra.w, sky_night_k());
					grey += sun_col * (pow(max(mu, 0.0), 60.0) * 0.35 + pow(max(mu, 0.0), 6.0) * 0.04) * (1.0 - d * 0.6) * (1.0 - sky_night_k());
					col = mix(col, grey, d);
					disc *= 1.0 - d;
					clear *= 1.0 - d;
				}
			}
			// Mackerel sky before a front (4.5 km): rows of small cloudlets, lit like the main deck.
			if (layered && cloud_more.x > 0.001) {
				float t = 4500.0 / max(up, 0.015);
				vec2 xz = cloud_state.xy * 0.75 + dir.xz * t;
				float d = ct_mackerel_f(xz, t, up, cloud_shape == 1 ? 3.0 : 1.0) * smoothstep(0.06, 0.3, up) * (1.0 - storm);
				d *= mix(1.0, 0.55, sky_night_k()); // (D187: softer at night, no hard cut-outs)
				if (d > 0.0) {
					vec4 c = layer(dir, xz, 4500.0, d, sun_dir, sun_col, day, mu, d * 0.5, 1.04);
					col = mix(col, c.rgb, d);
					disc *= 1.0 - d;
					clear *= 1.0 - d;
				}
			}
		}
		// Far clouds on and over the horizon: afternoon towers, distant thunderstorms, a lens over the mountains.
		// Drawn after the flat layers: low in the sky they stand in front of the flat deck (whose clouds there are
		// thin slivers, which would slice the towers into bands); higher up the deck covers them.
		vec4 far = vec4(0.0);
		float deck_d = 0.0;
		if (!AT_CUBEMAP_PASS && up > -0.02) {
			vec3 ambient = (horizon_color * 0.55 + zenith_color * 0.45) * sky_mood.rgb * (0.12 + 0.88 * day) * (1.0 - storm * 0.45);
			ambient = mix(ambient, vec3(0.05, 0.06, 0.08) * (0.4 + 0.6 * sky_extra.w), night);
			far = ct_far(dir, sun_dir, sun_col * (1.0 - night), ambient, col, underlight(dir), false, quality < 2);
		}
		// An approaching front: behind the main deck and the volume (clouds nearer than it stay in front).
		if (!AT_CUBEMAP_PASS) {
			vec3 ambient = (horizon_color * 0.55 + zenith_color * 0.45) * sky_mood.rgb * (0.12 + 0.88 * day) * 0.62;
			vec4 fw = front_wall(dir, ambient, sun_dir, sun_col * (1.0 - night), col);
			col = col * (1.0 - fw.a) + fw.rgb;
			disc *= 1.0 - fw.a;
			clear *= 1.0 - fw.a;
		}
		if (up > 0.0 || (quality == 3 && !AT_CUBEMAP_PASS)) {
			// Main deck: volumetric (quarter-res pass) or the flat textured layer.
			if (quality >= 2 && !AT_CUBEMAP_PASS) {
				// Far storms and the lens stand behind the volume (it grows its own towers).
				col = col * (1.0 - far.a) + far.rgb;
				disc *= 1.0 - far.a;
				clear *= 1.0 - far.a;
				far = vec4(0.0);
				vec4 v = HALF_RES_COLOR;
				if (cloud_look == 1) v.rgb /= max(QUARTER_RES_COLOR.rgb, vec3(1.0)); // fogged once, not twice (FOG_UNDO_K)
				if (cloud_look == 1 && up < 0.0) v = vec4(0.0); // (below the horizon CloudsNear draws them, over the far sea too)
				if (split_test >= 1 && quality == 3 && (split_test == 4 || SCREEN_UV.x < 0.5)) v = vec4(0.0); // (4: CloudsNear draws all clouds)
				v *= 1.0 - clouds_one_pass_k(); // (D176/D179: in the layer CloudsNear draws them, faded in over height)
				col = col * (1.0 - v.a) + v.rgb;
				disc *= 1.0 - v.a;
				clear *= 1.0 - v.a;
			} else {
				float t = cloud_height / max(up, 0.015);
				vec2 xz = cloud_state.xy + dir.xz * t;
				float d = cloud_density(xz, coverage);
				// Banks over the mountains and the deck of an approaching front (round 19).
				d = max(d, land_and_front(dir.xz * t).x * smoothstep(0.35, 0.75, texture(cloud_tex, xz / cloud_period * 2.0).r + 0.25));
				if (t > front_distance(dir)) d *= 1.0 - cloud_front.w; // hidden behind the front
				if (d > 0.0) {
					float toward = cloud_density(xz + normalize(sun_dir.xz + vec2(1e-4)) * 260.0, coverage);
					vec4 c = layer(dir, xz, cloud_height, d, sun_dir, sun_col, day, mu, toward, 1.0);
					// Cumulus afternoons: thick clouds show flat, darker bases from below.
					c.rgb *= 1.0 - cloud_types.x * 0.32 * smoothstep(0.55, 1.0, d) * smoothstep(0.1, 0.5, up);
					d *= smoothstep(0.015, 0.12, up);
					deck_d = d;
					col = mix(col, c.rgb, d);
					disc *= 1.0 - d;
					clear *= 1.0 - d;
				}
			}
			// Low ragged clouds (900 m): greyer, faster, in front of the main deck (layered mode; the volume has its own depth).
			if (layered && quality == 1) {
				float t = 900.0 / max(up, 0.015);
				vec2 xz = cloud_layers.zw + dir.xz * t;
				float d = low_density(xz, coverage, storm) * smoothstep(0.02, 0.1, up);
				if (d > 0.0) {
					float toward = low_density(xz + normalize(sun_dir.xz + vec2(1e-4)) * 150.0, coverage, storm);
					vec4 c = layer(dir, xz, 900.0, d, sun_dir, sun_col, day, mu, toward, 0.86);
					col = mix(col, c.rgb, d);
					disc *= 1.0 - d;
					clear *= 1.0 - d;
				}
			}
		}
		if (far.a > 0.0) {
			float k = 1.0 - deck_d * smoothstep(0.08, 0.25, up);
			col = col * (1.0 - far.a * k) + far.rgb * k;
			disc *= 1.0 - far.a * k;
			clear *= 1.0 - far.a * k;
		}
		if (!AT_CUBEMAP_PASS) {
			// Halo and sun dogs where thin cirrus crosses 22° from the sun (the sun must be up and not hidden).
			col += halo(dir, sun_dir, sun_col, cirrus_here * 2.2 * smoothstep(-0.02, 0.05, sun_dir.y) * (1.0 - storm) * clear);
			// Rainbows: rain has wetted the world, the sun is breaking through, low enough (< 42°).
			float bow = smoothstep(0.15, 0.5, rain_wetness.x) * (1.0 - smoothstep(0.35, 0.8, storm)) * smoothstep(0.0, 0.05, sun_dir.y)
				* smoothstep(0.67, 0.55, sun_dir.y) * smoothstep(0.0, 0.03, up);
			if (bow > 0.0) {
				vec4 rb = rainbow(dir, sun_dir, bow * 0.28 * (0.4 + 0.6 * day));
				col = col * (1.0 - rb.a) + rb.rgb;
			}
		}
		// Biome haze: a veil of the horizon colour (mist, dust, ash).
		col = mix(col, horizon_color * mix(vec3(1.0), sky_mood.rgb, 0.8) * day, sky_mood.a * 0.7 * (1.0 - smoothstep(0.0, 0.6, up)));
		// Storm: a low grey overcast.
		vec3 overcast = mix(vec3(0.42, 0.45, 0.48), vec3(0.3, 0.32, 0.35), smoothstep(0.0, 0.5, up)) * (0.35 + 0.65 * day);
		// Steady rain: the low grey deck has slow darker swells and paler thin parts (not a flat paint).
		if (storm > 0.01 && up > 0.0 && !AT_CUBEMAP_PASS) {
			vec2 xz = cloud_layers.zw + dir.xz * (700.0 / max(up, 0.03));
			float deck = texture(cloud_tex, xz / (cloud_period * 0.7)).r * 0.65 + texture(cloud_tex, xz / (cloud_period * 0.22) + 0.4).g * 0.35;
			overcast *= mix(1.0, 0.8 + 0.4 * deck, smoothstep(0.02, 0.2, up));
		}
		// Simulated weather: the low grey deck only where it rains — overhead when it rains here, on the horizon
		// toward rain far away — darker under thunderstorms.
		float storm_here = storm;
		if (weather_frame.w > 0.5) {
			vec3 w = wmap(dir.xz * min(700.0 / max(up, 0.02), 60000.0));
			storm_here = max(w.g, w.b * 0.9) * smoothstep(-0.05, 0.02, up) + storm * smoothstep(0.02, -0.05, up);
			storm_here = max(storm_here, storm * smoothstep(0.05, 0.4, up)); // the storm here (also forced rain, H) overhead
			overcast *= 1.0 - 0.35 * w.b;
			// Round 29: a storm deck with structure and racing scud instead of the flat grey (classic model unchanged).
			if (!AT_CUBEMAP_PASS) overcast = storm_deck(dir, day, max(storm, storm_here), w.b, sun_dir);
		}
		// (D186, Tom: "when it's raining during the nighttime, it lights up the whole thing"): the deck's colours had a
		// daylight floor, a lit grey ~10× brighter than the night sky, which also lit the land as sky light. At night a
		// rain deck is darker than the clear night sky, lit only faintly from the moon behind it.
		overcast *= mix(1.0, 0.05 + 0.05 * sky_extra.w, smoothstep(2.0, -10.0, sun_true.w));
		col = mix(col, overcast, storm_here * (weather_frame.w > 0.5 ? 0.95 : 0.85));
		disc *= 1.0 - storm;
		// Lightning (round 27): the clouds light up toward the strike — a broad glow for a near one (the whole deck
		// overhead flashes), a narrow low glow on the horizon for a far one; clear sky barely.
		if (lightning.z > 0.001) {
			float near_k = 1.0 - smoothstep(1.0, 12.0, lightning.w);
			float toward = max(0.0, dot(normalize(dir.xz + vec2(1e-5)), lightning.xy));
			float lobe = mix(pow(toward, mix(6.0, 60.0, smoothstep(2.0, 30.0, lightning.w))) * (1.0 - smoothstep(0.02, 0.35, up)), 1.0, near_k * 0.45);
			float cloudy = clamp(max(storm, deck_d) + 0.25, 0.0, 1.0);
			// (daylight drowns most of it: a white-out over a sunlit sky in the first render)
			col += vec3(0.72, 0.78, 1.0) * lightning.z * lobe * cloudy * (0.3 + 0.55 * near_k) * mix(1.0, 0.12, day);
		}
		COLOR = col + disc;
		// Probe (--cloud-split-test=2): the sun as the sky sees it in a strip at the top (right half; CloudsNear the left).
		if (split_test == 2 && SCREEN_UV.y < 0.12) COLOR = SCREEN_UV.y < 0.06 ? sun_col * 0.2 : sun_dir * 0.5 + 0.5;
		// Probe (--cloud-split-test=3): the clouds' opacity as grey (right half: this pass; left: CloudsNear).
		if (split_test == 3 && SCREEN_UV.x >= 0.5 && quality == 3) COLOR = vec3(HALF_RES_COLOR.a);
	}
}
