// The sky's colours and clouds (Sky.gdshader), shared with the clouds drawn in front of the scene (CloudsNear.gdshader,
// cloud renderer v2). The including shader defines SKY_LIGHT0_COLOR (the sun light's colour).
uniform vec3 zenith_color : source_color = vec3(0.29, 0.45, 0.62);
uniform vec3 horizon_color : source_color = vec3(0.68, 0.75, 0.78);
uniform vec3 ground_color : source_color = vec3(0.24, 0.28, 0.25);
uniform sampler2D cloud_tex : filter_linear_mipmap, repeat_enable;
uniform float cloud_period = 6000.0;
uniform float cloud_height = 1600.0;
uniform int quality = 1;
uniform float sun_glare = 1.0; // disc and halo strength (setting)
uniform int sun_style = 0;     // 0 classic (build 11), 1 natural (colour, size and glow change with the sun's height)
// Cloud types from the weather and the land (round 17): afternoon towers, distant thunderstorms, lenticulars, mackerel
// sky — shared with the water's reflection. Its cloud_more.z is also the sky's clock (twinkling, auroras, meteors):
// the built-in TIME would make Godot re-bake the sky's radiance map every frame.
#include "res://Shaders/Sky/CloudShapes.gdshaderinc"
// xy: world position of the camera plus the wind drift, wrapped to the cloud period (m); z: coverage 0..1;
// w: storm 0..1 (rain: grey, darker sky and clouds).
global uniform vec4 cloud_state;
// Round 27, simulated weather: the weather around (WeatherMap.cs) — r cloud, g rain, b thunderstorm — and the
// camera's place in it (uv, 1/size in m, on).
global uniform sampler2D weather_map : filter_linear, repeat_disable;
global uniform vec4 weather_frame;
vec3 wmap(vec2 rel) { return texture(weather_map, weather_frame.xy + rel * weather_frame.z).rgb; }
// xy: the same for the high cirrus layer, zw: for the low layer (each drifts at its own speed and heading).
global uniform vec4 cloud_layers;
// Biome mood: rgb multiplies the sky colours, a = extra haze toward the horizon colour (0 = realistic sky).
global uniform vec4 sky_mood;
// Night: moon direction (xyz) and phase (w: 0 new … 0.5 full); star field rotation; x = days, y = planets on,
// z = star brightness, w = moon illuminated fraction.
global uniform vec4 night_sky;
global uniform mat3 star_rotation;
global uniform vec4 sky_extra;
// The real sun (xyz) and its elevation in degrees (w), also below the horizon (the light itself stops at −4°).
global uniform vec4 sun_true;

// Aurora strength (Atmosphere.cs: high latitudes, dark clear nights); the sign is the hemisphere (curtains toward
// the north pole for > 0, the south for < 0).
global uniform float aurora;

float shash(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

// Auroras (round 16): curtains low over the poleward horizon — a bright green lower edge rising into faint violet,
// rippling slowly along their length, with fine vertical rays.
vec3 aurora_light(vec3 dir, float t) {
	float strength = abs(aurora);
	if (strength <= 0.001 || dir.y <= 0.0) return vec3(0.0);
	float pole = aurora > 0.0 ? -dir.z : dir.z; // north is −Z
	float facing = smoothstep(-0.25, 0.55, pole);
	if (facing <= 0.0) return vec3(0.0);
	float az = atan(dir.x, max(pole, 0.05));
	float el = asin(clamp(dir.y, 0.0, 1.0));
	vec3 total = vec3(0.0);
	// Two curtains at different heights and drifts; each folds along its length, waxes and wanes in patches, and
	// carries irregular vertical rays.
	for (int i = 0; i < 2; i++) {
		float fi = float(i), sp = 1.0 + fi * 0.6;
		float foot = 0.18 + fi * 0.1 + 0.09 * sin(az * (2.3 + fi) + t * 0.03 * sp) + 0.05 * sin(az * (6.1 - fi * 1.7) - t * 0.07 * sp)
			+ 0.025 * sin(az * 13.0 + t * 0.15);
		float h = el - foot;
		float curtain = smoothstep(-0.015, 0.01, h) * exp(-max(h, 0.0) * mix(4.5, 9.0, 0.5 + 0.5 * sin(az * 3.7 + t * 0.05 + fi * 2.0)));
		float patch = smoothstep(0.15, 0.85, 0.5 + 0.3 * sin(az * 1.6 + t * 0.021 + fi * 4.0) + 0.25 * sin(az * 4.3 - t * 0.037 + fi));
		float rays = 0.5 + 0.5 * sin(az * (120.0 + 40.0 * fi) + 7.0 * sin(az * 11.0 + t * 0.2) + 3.0 * sin(az * 37.0 - t * 0.31));
		rays = mix(0.35, 1.0, rays * rays);
		vec3 c = mix(vec3(0.12, 1.0, 0.45), vec3(0.55, 0.22, 0.75), smoothstep(0.04, 0.3, h));
		total += c * curtain * rays * patch * (1.0 - fi * 0.45);
	}
	return total * facing * strength * 0.36;
}

// Shooting stars: now and then a short bright streak (every few seconds a chance of one, lasting under a second).
vec3 meteor(vec3 dir, float t) {
	float slot = floor(t / 6.0);
	if (shash(slot) < 0.55) return vec3(0.0);
	float tt = t - slot * 6.0 - shash(slot + 7.1) * 4.0;
	if (tt < 0.0 || tt > 0.7) return vec3(0.0);
	vec3 start = normalize(vec3(shash(slot + 1.3) * 2.0 - 1.0, 0.35 + 0.5 * shash(slot + 2.9), shash(slot + 4.7) * 2.0 - 1.0));
	vec3 axis = normalize(cross(start, normalize(vec3(shash(slot + 5.3) - 0.5, 1.0, shash(slot + 6.1) - 0.5))));
	vec3 head = normalize(start + axis * tt * 0.45), tail = normalize(start + axis * max(tt - 0.22, 0.0) * 0.45);
	vec3 seg = head - tail;
	float u = clamp(dot(dir - tail, seg) / max(dot(seg, seg), 1e-6), 0.0, 1.0);
	float d = length(dir - (tail + seg * u));
	float bright = smoothstep(0.0, 0.1, tt) * smoothstep(0.7, 0.45, tt) * u;
	return vec3(0.9, 0.95, 1.0) * smoothstep(0.0025, 0.0, d) * bright * 1.6;
}

// x: how wet the world is from rain (Atmosphere.cs; rainbows appear as the rain clears), y: puddles.
global uniform vec2 rain_wetness;

// A rainbow-like spectrum: t = 0 violet … 1 red.
vec3 spectrum(float t) {
	return clamp(vec3(1.5 - abs(4.0 * t - 3.6), 1.5 - abs(4.0 * t - 2.2), 1.5 - abs(4.0 * t - 0.8)), 0.0, 1.0);
}
// Rainbows (round 16): opposite the sun — the primary bow at 40.5–42.5° (red outside), the fainter secondary at
// 50.5–53.5° (colours reversed), Alexander's darker band between them and a brighter sky inside the primary.
// Returns (added colour, darkening).
vec4 rainbow(vec3 dir, vec3 sun_dir, float amount) {
	float a = degrees(acos(clamp(dot(dir, -sun_dir), -1.0, 1.0)));
	vec3 c = vec3(0.0);
	float t1 = (a - 40.5) / 2.0;
	c += spectrum(t1) * smoothstep(0.0, 0.15, t1) * smoothstep(1.0, 0.85, t1);
	float t2 = 1.0 - (a - 50.5) / 3.0;
	c += spectrum(t2) * smoothstep(0.0, 0.15, t2) * smoothstep(1.0, 0.85, t2) * 0.4;
	c += vec3(0.07) * smoothstep(20.0, 40.5, a) * (1.0 - step(40.5, a));
	float dark = smoothstep(42.0, 43.5, a) * smoothstep(51.0, 49.5, a) * 0.12;
	return vec4(c * amount, dark * amount);
}
// The 22° halo and sun dogs, seen through thin high cloud (cirrus density d).
vec3 halo(vec3 dir, vec3 sun_dir, vec3 sun_col, float d) {
	float a = degrees(acos(clamp(dot(dir, sun_dir), -1.0, 1.0)));
	float ra = (a - 22.0) / 0.7, ring = exp(-ra * ra);
	vec3 col = mix(vec3(1.0, 0.75, 0.6), vec3(0.9, 0.95, 1.0), smoothstep(21.3, 23.0, a)) * ring * 0.35;
	// Sun dogs: bright spots at the sun's height, 22° (a little more when the sun is higher) to each side.
	float da = 22.0 + 8.0 * max(sun_dir.y, 0.0), dy = (dir.y - sun_dir.y) / 0.025;
	float dx = (a - da) / 1.2, dog = exp(-dx * dx) * exp(-dy * dy);
	col += spectrum(clamp(1.0 - (a - da + 1.0) / 2.5, 0.0, 1.0)) * dog * 0.9; // red on the side toward the sun
	return col * sun_col * d;
}

// Twilight (round 16): how much of each twilight effect shows for the sun's true elevation e (degrees).
float golden_w(float e) { return smoothstep(9.0, 1.0, e) * smoothstep(-7.0, -1.0, e); }  // sunset glow at the horizon
float belt_w(float e) { return smoothstep(4.0, 0.0, e) * smoothstep(-7.0, -2.0, e); }     // Belt of Venus
float blue_w(float e) { return smoothstep(-11.0, -4.0, e) * smoothstep(2.0, -2.0, e); }  // blue hour
// Clouds lit from below by a sun under (or at) the horizon: orange, then pink.
vec3 underlight(vec3 dir) {
	float e = sun_true.w;
	float w = smoothstep(-7.0, -1.0, e) * smoothstep(10.0, 2.0, e);
	if (w <= 0.0) return vec3(0.0);
	vec2 sd = normalize(sun_true.xz + vec2(1e-4)), dd = normalize(dir.xz + vec2(1e-4));
	float toward = dot(sd, dd) * 0.5 + 0.5;
	vec3 c = mix(vec3(1.0, 0.55, 0.25), vec3(0.95, 0.42, 0.52), smoothstep(1.0, -4.0, e));
	return c * w * (0.25 + 0.75 * toward * toward);
}

float cloud_density(vec2 xz, float coverage) {
	if (weather_frame.w > 0.5) coverage = mix(coverage, wmap(xz - cloud_state.xy).r, 0.85); // where the weather really is
	vec2 uv = xz / cloud_period;
	float base = texture(cloud_tex, uv).r;
	float detail = texture(cloud_tex, uv * 4.0 + vec2(0.31, 0.17)).g;
	float n = base * 0.8 + detail * 0.2;
	float edge = 1.0 - coverage;
	return smoothstep(edge - 0.05, edge + 0.2, n) * step(0.001, coverage);
}

// High cirrus: thin streaks stretched along a slanted axis. All texture scales are integers after the 3-4-5 rotation,
// so the pattern repeats exactly every 25 cloud periods (150 km) and Atmosphere.cs can wrap its drift there.
// Cloud layers (setting, D187): 0 = build 97; 1 = cirrus in fair weather too and thicker before a front, and a middle grey
// sheet (altostratus, ~5 km) spreading over the sky as a front comes.
uniform int layers_mode = 0;
float cirrus_density(vec2 xz, float coverage, float front) {
	vec2 uv = xz / (cloud_period * 2.5);
	uv = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * uv;
	float n = texture(cloud_tex, uv * vec2(1.0, 4.5)).g * 0.7 + texture(cloud_tex, uv * vec2(3.0, 9.0) + 0.37).r * 0.3;
	float edge = 1.0 - (0.1 + coverage * 0.45);
	if (layers_mode == 1) {
		// (layered: fair-weather cirrus at any cover, more and denser as a front comes; front = its strength 0..1)
		edge = 1.0 - (0.3 + coverage * 0.25 + front * 0.3);
		// (D189: more cirrus lined up as regular parallel bands across the sky: patches tens of km across, with wisps
		// of varied width, instead of one even field of streaks)
		float patches = smoothstep(0.35, 0.75, texture(cloud_tex, uv * 0.23 + vec2(0.61, 0.27)).r + front * 0.25);
		float wisp = texture(cloud_tex, uv * vec2(2.0, 0.7) + vec2(0.13, 0.81)).g;
		return smoothstep(edge, edge + 0.3, n * (0.75 + 0.5 * wisp)) * (0.45 + 0.2 * front) * patches;
	}
	return smoothstep(edge, edge + 0.3, n) * 0.35;
}

// Low ragged clouds: smaller, broken pieces.
float low_density(vec2 xz, float coverage, float storm) {
	if (weather_frame.w > 0.5) { vec3 w = wmap(xz - cloud_state.xy); coverage = mix(coverage, w.r, 0.85); storm = max(w.g, w.b); }
	vec2 uv = xz / (cloud_period * 0.45) + vec2(0.53, 0.29);
	float n = texture(cloud_tex, uv).r * 0.65 + texture(cloud_tex, uv * 3.0).g * 0.35;
	float edge = 1.0 - (coverage * 0.55 + storm * 0.35);
	return smoothstep(edge, edge + 0.16, n) * 0.85 * step(0.05, coverage);
}

float nhash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

// Stars (twinkling), a faint Milky Way, planets and the moon with its phase; faded in as the sun sets.
vec3 night_sky_colour(vec3 dir, vec3 sun_dir, float night) {
	if (night <= 0.001 || dir.y < -0.05) return vec3(0.0);
	vec3 sd = star_rotation * dir;
	vec3 col = vec3(0.0);
	float strength = sky_extra.z;
	// Stars on a cube-face grid of the celestial sphere (even density everywhere): one candidate per cell, few lit.
	vec3 a = abs(sd);
	vec2 fuv; float face;
	if (a.x >= a.y && a.x >= a.z) { fuv = sd.yz / a.x; face = sign(sd.x); }
	else if (a.y >= a.z) { fuv = sd.xz / a.y; face = 2.0 + sign(sd.y); }
	else { fuv = sd.xy / a.z; face = 4.0 + sign(sd.z); }
	float theta = atan(sd.z, sd.x), phi = asin(clamp(sd.y, -1.0, 1.0));
	for (int layer = 0; layer < 2; layer++) {
		float scale = layer == 0 ? 90.0 : 230.0;
		vec2 g = fuv * scale;
		vec2 cell = floor(g);
		vec2 key = cell + face * 1013.0 + float(layer) * 91.7;
		float h = nhash(key);
		if (h > (layer == 0 ? 0.955 : 0.92)) {
			vec2 c = cell + 0.25 + 0.5 * vec2(nhash(key + 3.1), nhash(key + 7.3));
			float d = length(g - c);
			float size = layer == 0 ? 0.3 : 0.24;
			float b = smoothstep(size, 0.0, d) * pow(fract(h * 37.0), layer == 0 ? 2.0 : 4.0) * (layer == 0 ? 2.2 : 0.9);
			b *= 0.75 + 0.25 * sin(cloud_more.z * (2.0 + 5.0 * nhash(key)) + h * 40.0); // twinkle
			vec3 tint = mix(vec3(0.75, 0.85, 1.0), vec3(1.0, 0.85, 0.7), nhash(key + 11.0));
			col += tint * b;
		}
	}
	// Milky Way: a soft band around a great circle, broken by dust.
	float bd = dot(sd, normalize(vec3(0.2, 0.55, 0.81))), band = exp(-bd * bd * 22.0); // (pow of a negative base is undefined)
	float dust = nhash(floor(vec2(theta, phi) * 60.0)) * 0.4 + 0.6;
	col += vec3(0.55, 0.6, 0.75) * band * dust * 0.06;
	// Planets on the ecliptic (tilted 23.4°), each wandering at its own pace.
	if (sky_extra.y > 0.5) {
		for (int i = 0; i < 4; i++) {
			float fi = float(i);
			float lon = fi * 1.7 + sky_extra.x * (0.035 / (1.0 + fi * 1.3)) + 0.6;
			vec3 p = normalize(vec3(cos(lon), sin(lon) * 0.397, sin(lon) * 0.918));
			vec3 pd = transpose(star_rotation) * p;
			float d = acos(clamp(dot(dir, pd), -1.0, 1.0));
			vec3 tint = i == 0 ? vec3(1.0, 0.98, 0.9) : i == 1 ? vec3(1.0, 0.6, 0.45) : i == 2 ? vec3(1.0, 0.92, 0.75) : vec3(0.85, 0.9, 1.0);
			col += tint * smoothstep(0.0022, 0.0006, d) * (i == 0 ? 3.0 : 1.6);
		}
	}
	col *= strength * smoothstep(-0.02, 0.12, dir.y); // thick air at the horizon hides the faint stars
	// Moon: a lit sphere (phase from the sun's direction), a soft glow around it.
	vec3 md = night_sky.xyz;
	float mc = dot(dir, md);
	float rad = 0.02; // a bit larger than life, as in most games
	float r = sqrt(max(0.0, 2.0 * (1.0 - mc))) / rad;
	if (r < 1.0) {
		vec3 right = normalize(cross(md, vec3(0.0, 1.0, 0.0) + vec3(1e-4)));
		vec3 up = cross(right, md);
		vec2 q = vec2(dot(dir - md, right), dot(dir - md, up)) / rad;
		// The visible hemisphere faces us; it is lit where it faces the (far away) sun: full when opposite the sun.
		vec3 n = normalize(right * q.x + up * q.y - md * sqrt(max(0.0, 1.0 - dot(q, q))));
		float light = clamp(dot(n, sun_dir) * 1.5, 0.0, 1.0);
		float maria = 0.8 + 0.2 * nhash(floor(q * 4.0 + 9.0));
		col = mix(col, vec3(0.92, 0.93, 0.98) * (0.035 + 1.6 * light * maria), smoothstep(1.0, 0.92, r));
	}
	col += vec3(0.5, 0.58, 0.75) * pow(max(mc, 0.0), 400.0) * 0.45 * sky_extra.w * strength;
	return col * night;
}

vec3 sky_colour(vec3 dir, vec3 sun_dir, vec3 sun_col, float day, float mu) {
	float up = dir.y;
	vec3 zenith = zenith_color * sky_mood.rgb, horizon = horizon_color * mix(vec3(1.0), sky_mood.rgb, 0.7);
	vec3 col = mix(horizon, zenith, smoothstep(0.0, 0.45 + sky_mood.a * 0.4, up));
	float low = 1.0 - smoothstep(0.05, 0.45, sun_dir.y);
	col = mix(col, col * vec3(1.25, 0.95, 0.75), low * smoothstep(0.35, 0.0, up) * (0.5 + 0.5 * max(mu, 0.0)));
	col *= day;
	// Twilight colours from the sun's true elevation (round 16, docs/SKY_IDEAS.md): a red-orange band along the horizon
	// toward the sun; opposite it the pink Belt of Venus with Earth's blue-grey shadow rising underneath after sunset;
	// then the deep blue hour.
	{
		float e = sun_true.w;
		vec2 sd = normalize(sun_true.xz + vec2(1e-4)), dd = normalize(dir.xz + vec2(1e-4));
		float az = dot(sd, dd); // 1 toward the sun's azimuth, −1 away
		float h = max(up, 0.0);
		float gw = golden_w(e);
		if (gw > 0.0) {
			float band = exp(-h * h / mix(0.004, 0.02, smoothstep(-4.0, 6.0, e)));
			vec3 glow = mix(vec3(1.0, 0.62, 0.25), vec3(0.95, 0.3, 0.12), smoothstep(3.0, -3.0, e));
			col += glow * band * gw * (0.08 + 0.92 * pow(max(az, 0.0), 2.0)) * 0.65 * sky_mood.rgb;
		}
		float bw = belt_w(e);
		if (bw > 0.0) {
			float shadow_top = clamp(-e * 0.012, 0.0, 0.09);           // Earth's shadow rises as the sun sinks
			float bx = (h - shadow_top - 0.06) / 0.05, belt = exp(-bx * bx);
			float away = pow(max(-az, 0.0), 1.5);
			col += vec3(0.62, 0.36, 0.45) * belt * bw * away * 0.45;
			col = mix(col, vec3(0.16, 0.2, 0.32) * 0.6, (1.0 - smoothstep(shadow_top * 0.6, shadow_top + 0.01, h)) * bw * away * step(0.001, shadow_top) * 0.6);
		}
		float blw = blue_w(e);
		if (blw > 0.0) col += vec3(0.035, 0.07, 0.17) * blw * (0.55 + 0.45 * smoothstep(0.0, 0.5, h)) * sky_mood.rgb;
	}
	if (sun_style == 0) col += (sun_col * pow(max(mu, 0.0), 10.0) * 0.18 + sun_col * pow(max(mu, 0.0), 300.0) * 0.6) * sun_glare;
	else {
		// Natural: a tight white halo high up; near the horizon a wide, warm glow (sunrise, sunset).
		float lowsun = 1.0 - smoothstep(0.0, 0.35, sun_dir.y);
		vec3 warm = mix(vec3(1.0, 0.95, 0.88), vec3(1.0, 0.55, 0.25), lowsun);
		col += sun_col * warm * (pow(max(mu, 0.0), mix(400.0, 60.0, lowsun)) * 0.5 + pow(max(mu, 0.0), mix(18.0, 5.0, lowsun)) * mix(0.1, 0.32, lowsun)) * sun_glare;
	}
	col = mix(col, ground_color * day, smoothstep(0.0, -0.06, up));
	return col;
}

// A flat cloud layer at height h: the colour over the sky and the opacity.
// How far into the night the real sun is (D187): the sky's light is held at −4°, so its own "night" stopped at ~0.6 and
// the high layers stayed ~40 % daylit at night (bright mackerel cut-outs on a black sky).
float sky_night_k() { return smoothstep(2.0, -10.0, sun_true.w); }
vec4 layer(vec3 dir, vec2 origin, float h, float d, vec3 sun_dir, vec3 sun_col, float day, float mu, float toward, float bright) {
	float lit = clamp(1.0 - toward * 0.65, 0.0, 1.0);
	vec3 shade = mix(vec3(0.46, 0.5, 0.56), vec3(1.0, 0.99, 0.97), lit);
	vec3 cloud = shade * (0.25 + 0.85 * day) * mix(vec3(1.0), SKY_LIGHT0_COLOR, 0.5) * bright;
	// Night: clouds go dark, faintly lit by the moon (brighter toward it).
	float night = max(1.0 - smoothstep(-0.16, 0.04, sun_dir.y), sky_night_k());
	vec3 moonlit = vec3(0.07, 0.08, 0.11) * (0.4 + 0.6 * sky_extra.w) * (1.0 + 2.0 * pow(max(dot(dir, night_sky.xyz), 0.0), 8.0));
	cloud = mix(cloud, moonlit * shade, night);
	// Sunset and twilight: clouds glow from below, toward the sun most (their white daylight shading gives way, so
	// they turn golden and pink rather than cream).
	vec3 ul = underlight(dir);
	cloud *= 1.0 - 0.45 * clamp(dot(ul, vec3(0.5)), 0.0, 0.8);
	cloud += ul * (0.45 + 0.55 * d) * 0.9;
	cloud += sun_col * pow(max(mu, 0.0), 6.0) * (1.0 - d) * 0.8;
	// Silver lining (Tom, round 8): in front of the sun, thick cloud is backlit and darker while its thin edges blaze
	// (light scattered forward through the edge).
	float toward_sun = max(mu, 0.0);
	float rim = smoothstep(0.02, 0.25, d) * (1.0 - smoothstep(0.35, 0.85, d));
	cloud *= 1.0 - 0.35 * pow(toward_sun, 10.0) * smoothstep(0.5, 1.0, d) * (1.0 - night);
	cloud += sun_col * rim * (pow(toward_sun, 60.0) * 2.2 + pow(toward_sun, 14.0) * 0.6) * (1.0 - night);
	return vec4(cloud, d);
}

// ---- volumetric clouds (quality 2), round 18 (Tom: "they look flat… lack detail, contrast and colours")
// A real volume, not a lit slab: the 2D cloud map (the same one the flat layer and the ground shadows use) says where
// cloud is and how high it grows — thick parts higher, on cumulus afternoons the cores tower to ~7 km; a tiling 3D
// noise (CloudNoise3D.cs: Perlin-Worley billows, Worley lumps on lumps) carves the shape. Calm air: crisp cauliflower
// edges; wind: the fine detail is drawn out along the wind into smudged, streaky edges and towers lean downwind.
// Light: the sun traced through the cloud (dark cores, glowing thin edges, silver lining toward the sun, a few orders of
// multiple scattering so thick cloud is not black), blue sky light from above and the sunlit ground from below, sunset
// colours on the bases, aerial perspective with distance.
uniform sampler3D cloud_noise : filter_linear, repeat_enable;
// Cloud detail (setting, D199): 0 = build 101; 1 = fine: the billows carved by a 128³ noise four times finer (lumps on
// lumps on lumps), so a lobe has smaller lobes and creases to shade instead of a smooth, flat-lit face.
uniform sampler3D cloud_fine_noise : filter_linear, repeat_enable;
uniform int cloud_fine = 0;
global uniform vec4 wind; // dir.x, speed/8, dir.z, gust
// The volume's base: ~1.5 km above the sea (cloud_more.w = base above the camera, Atmosphere), lower over mountains, but
// never within ~0.8 km of the camera — the sky is drawn behind the terrain and cannot wrap clouds around it.
// Cloud renderer v2 (cloud quality 3, round 29): the clouds are also drawn in front of the scene (CloudsNear.gdshader),
// so the base may come down to and below the camera — onto the mountains, a sea of cloud under a summit.
global uniform float cloud_v2;
#define VBOTTOM (cloud_v2 > 0.5 ? min(cloud_more.w - 200.0, 2000.0) : clamp(cloud_more.w - 200.0, 800.0, 2000.0))
#define VLOW_MIN (cloud_v2 > 0.5 ? -1e5 : 600.0)

// Round 19 (Noor: "cloud banks building around mountains", "weather visible from far away … sailing toward a giant
// wall of rain"). The land around the camera (Atmosphere.SetRelief: r = height above the sea / 4000 m, g = how much the
// wind is pushed up the slope there) and the next rain front (cloud_front: direction toward it xz, distance to its edge
// in m, strength; Sky draws it with front_wall). relief_frame: offset of the camera from the texture's centre (xz, m), its size (m), the camera's
// height above the sea (m).
uniform sampler2D relief_tex : filter_linear, hint_default_black, repeat_disable;
global uniform vec4 relief_frame;
global uniform vec4 cloud_front;
global uniform vec4 lightning; // round 27: horizontal direction to the strike (x, z), brightness, distance (km)
// The region's mean ground above the sea (m) when the cloud base rides on the land (D165), else 0: mountains catch cloud
// by how far they rise above their region, not above the sea (a whole high country was one cloud bank, painting an even
// blue over the view from Tom's 3,060 m ridge).
global uniform float cloud_region;
// Cloud sea (D169, draft): 0 = off; toward 1 in high country the layer is thin (a few hundred metres, no towers) and sits
// low (Atmosphere.CloudBaseAboveSea), under the ridges. Storms keep their thick deck.
global uniform float cloud_sea;
#define SEA_K (cloud_sea * (1.0 - smoothstep(0.3, 0.7, cloud_state.w)))
#define SEA_TALL (1.0 - 0.45 * SEA_K)
// Mountains shade the clouds (D167): the air map's sun-shadow heights (WorldLab.AirRays, 20 km around the player). xy: the
// camera's offset from the map's centre (m), z: the map's size (0 = off), w: the camera's scene height (m).
global uniform sampler2D air_map : filter_linear, repeat_disable;
global uniform vec4 cloud_air;
// The setting Mountains shade the clouds (cloud_air also carries the map for the broken cloud sea, D172).
uniform int terrain_shade = 0;
// Cloud sea broken from below (D172, Tom: "light through it"; the first draft filled high valleys with fog): the sea's
// clouds thin out over the last ~300 m above the ground, so in a valley you stand under the sea and see up through it.
uniform int sea_broken = 0;
uniform vec4 sea_tune = vec4(0.5, 0.0, 0.0, 0.0); // x: hole size (Atmosphere, --ray-dev=seahole)
// 1 = sunlit over the terrain at a cloud point (camera-relative xz, y above the camera), 0 = in a mountain's shadow.
float cloud_terrain_sun(vec2 rel, float y) {
	if (cloud_air.z <= 0.0 || terrain_shade == 0) return 1.0;
	vec2 m = (rel + cloud_air.xy) / cloud_air.z + 0.5;
	if (m.x <= 0.0 || m.y <= 0.0 || m.x >= 1.0 || m.y >= 1.0) return 1.0;
	float shadow = textureLod(air_map, m, 0.0).g;
	return smoothstep(shadow - 40.0, shadow + 40.0, y + cloud_air.w);
}

// Extra cloud at a point (relative to the camera, xz in m): x = mass the land and the front add, y = extra height (m),
// z = how low the base comes down toward the ground (m above the camera; 1e9 = unchanged).
vec3 land_and_front(vec2 rel) {
	vec3 r = vec3(0.0, 0.0, 1e9);
	if (relief_frame.z > 0.0) {
		vec2 uv = (rel + relief_frame.xy) / relief_frame.z + 0.5;
		if (all(greaterThan(uv, vec2(0.0))) && all(lessThan(uv, vec2(1.0)))) {
			vec2 t = texture(relief_tex, uv).rg;
			float hgt = t.r * 4000.0;
			// Mountains catch cloud: caps on the high peaks, banks along the windward slopes (moist air pushed up).
			float oro = smoothstep(600.0, 2200.0, hgt - cloud_region) * (0.35 + 0.65 * t.g) * (0.35 + 0.65 * cloud_state.z);
			r.x = oro * 0.85;
			r.y = oro * 900.0;
			r.z = hgt - relief_frame.w - 250.0; // the bank sits on the ridge, just below its top
		}
	}
	return r;
}

float vremap(float v, float a, float b, float c, float d) { return c + (v - a) / max(b - a, 1e-4) * (d - c); }

// The cloud map at xz: x = mass (0..1), y = how high the cloud grows above VBOTTOM (m), z = base (m above the camera).
// Cloud blend (setting, v2): 0 = build 52; 1 = the sky's and CloudsNear's clouds matched (CloudsNear.gdshader). Here:
// the cloud map's detail level inside the march follows the distance (one pixel's footprint there), not the screen
// derivatives — those are meaningless in a ray march and twice as large in the sky's half-resolution pass, which
// blurred its clouds into a thin even veil over the whole sky while the same clouds in front of the land stayed crisp.
uniform int cloud_blend = 1;
// Cloud look (setting): 0 = build 53; 1 = reference (round 32, aimed at the M3 references, docs/ATMOSPHERE_2_0_REFERENCES.md:
// bright tops, flat blue-grey bases, crisp cauliflower edges, wind streaks only in strong wind). Here the shape and the
// light; the sky's double fog is undone in Sky.gdshader (FOG_UNDO_K).
uniform int cloud_look = 0;
// How streaked by the wind (0 calm … 1 stretched): build 53 from ~4 m/s (wind.y = speed / 8), so a Wind strength above 1
// streaked every cloud; reference only from ~12 m/s (a strong breeze) to full at ~20 m/s.
// Cloud caps (setting): 0 = build 54; 1 = dense — the cloud mountains catch is up to 3.5× denser, so a cap hides the
// summit it sits on instead of showing the rock through a thin layer (Tom, dumps 222017 / 222024).
uniform int cloud_caps = 0;
// Cloud turbulence (setting): 0 = off; 1 = within ~600 m of the camera the cloud churns — eddies ~60 m across drifting
// and turning at a few m/s, so walking or flying through cloud shows wisps going by (Tom: "some kind of turbulence").
uniform int cloud_turbulence = 0;
// Cloud shape (setting, M3 draft D160; Tom: "really flat and sharply cut off on the bottom and on top, like the clouds
// are locked in a box"): 0 = build 84; 1 = rounded — each cloud its own height (big tops and small ones), domes that
// close before the layer's ceiling and a top roughened by the billows; bases still near level (the condensation
// level) but a few hundred metres apart from cloud to cloud, lumpy, with a softer onset.
uniform int cloud_shape = 0;
// (the larger volume only for heaped clouds: with the same steps a taller volume reached less far, and the far storm
// deck came out in blue streaks)
#define SHAPE_K0 (cloud_shape == 1 ? 1.0 - smoothstep(0.6, 0.85, cloud_state.z) : 0.0)
#define SHAPE_TALL (1.0 + 0.4 * SHAPE_K0)
#define SHAPE_TOP_EXTRA (200.0 * SHAPE_K0)
#define SHAPE_LOW_EXTRA (250.0 * SHAPE_K0 + (cloud_bases == 1 ? 50.0 : 0.0))
// Cloud bases (setting, draft D203; Tom: "the bottoms of the clouds look flat, without details"): 0 = build 103 (each
// cloud's underside one flat sheet, one flat blue-grey); 1 = shaped: the base in lumps (rolls along the wind) and sagging
// under thick cores, lobes hanging up to ~150 m below it, and the sky light from above taken by the cloud overhead
// (darker centres, thin edges keep the light) with less of the flat blue-grey.
uniform int cloud_bases = 0;
float cloud_windy() { return cloud_look == 1 ? smoothstep(1.5, 2.5, wind.y) : smoothstep(0.5, 1.6, wind.y); }
float cloud_map(vec2 uv, float lod, int ch) {
	vec4 c = lod >= 0.0 ? textureLod(cloud_tex, uv, lod) : texture(cloud_tex, uv);
	return ch == 0 ? c.r : c.g;
}

vec3 vweather(vec2 xz, float coverage, float dist, out float land) {
	// Simulated weather: cover from the map, rain thickens the deck, thunderstorm cells grow towers (up to ~5 km).
	vec3 wm = weather_frame.w > 0.5 ? wmap(xz - cloud_state.xy) : vec3(coverage, cloud_state.w, 0.0);
	coverage = mix(coverage, wm.r, weather_frame.w * 0.85);
	vec2 uv = xz / cloud_period;
	// (texel: the map's size over its period; a pixel covers ~1/800 of the distance)
	float lod = cloud_blend == 1 ? log2(max(dist * 0.00125 / (cloud_period / float(textureSize(cloud_tex, 0).x)), 1.0)) : -1.0;
	float n = cloud_map(uv, lod, 0) * 0.8 + cloud_map(uv * 4.0 + vec2(0.31, 0.17), lod < 0.0 ? lod : lod + 2.0, 1) * 0.2;
	float edge = 1.0 - coverage;
	// (graded, not saturated: the denser the map, the more and higher the cloud)
	// (cloud sea: the map's edge itself, not widened: clear gaps onto the valleys)
	float mass = clamp((n - edge + 0.1 * (1.0 - SEA_K)) / 0.35, 0.0, 1.0) * step(0.001, coverage);
	float core = smoothstep(edge + 0.05, edge + 0.4, n);
	float tower = cloud_types.x > 0.001 ? cloud_types.x * core * (0.35 + 0.65 * cloud_map(uv * 0.5 + vec2(0.7, 0.2), lod < 0.0 ? lod : max(lod - 1.0, 0.0), 1)) : 0.0;
	vec3 extra = land_and_front(xz - cloud_state.xy);
	land = extra.x;
	// (only where the land holds cloud does the base come down toward the ridges; elsewhere it stays put)
	float base = mix(VBOTTOM, clamp(extra.z, max(VBOTTOM - 500.0, VLOW_MIN), VBOTTOM), clamp(extra.x / 0.85, 0.0, 1.0));
	mass = max(mass, extra.x * (0.75 + 0.5 * n));
	float cb = smoothstep(0.1, 0.7, wm.b) * weather_frame.w;
	mass = max(mass, cb * smoothstep(0.25, 0.6, n + cb * 0.3));
	float tall = 350.0 + 950.0 * sqrt(mass) + max(tower, cb * (0.6 + 0.4 * n)) * 5200.0 + wm.g * 700.0 + extra.y;
	// (cloud sea: a flat-topped layer of stratocumulus; the caps the land holds still rise a little above it)
	tall = mix(tall, 200.0 + 500.0 * sqrt(mass) + max(tower, cb) * 1200.0 + wm.g * 300.0 + extra.y * 0.4, SEA_K);
	return vec3(mass, tall + (VBOTTOM - base), base);
}

// Cloud body (setting, D176; Tom: "sometimes the clouds are uniformly transparent … especially in the mountains"):
// 0 = build 91; 1 = denser, crisper: thin cloud (the even veil around mountains) becomes clear air, what is left rises
// steeply to full density — distinct bodies with clear gaps — and the march makes it ~1.6× more opaque per metre.
uniform int cloud_body = 0;
float body_k(float d) { return cloud_body == 1 ? smoothstep(0.1, 0.45, d) : d; }
// Density at p (camera-relative x/z offset by the cloud drift, y above the camera). detail: the fine erosion (off for
// light rays and far away).
float vdens(vec3 p, bool detail) {
	vec2 lean = vec2(wind.x, wind.z) * min(wind.y, 2.0) * 0.35; // towers lean downwind with height
	float above = max(p.y - VBOTTOM, 0.0);
	vec4 n = texture(cloud_noise, p / 2600.0 + vec3(0.0, cloud_more.z * 0.00005, 0.0));
	// The map is read through a 3D warp: a cloud's sides bulge in and out with height instead of standing as walls.
	vec2 warp = (vec2(n.g, n.b) - 0.5) * (500.0 + above * 0.18);
	float land;
	vec3 wx = vweather(p.xz - lean * above + warp, cloud_state.z, length(vec3(p.x - cloud_state.x, p.y, p.z - cloud_state.y)), land);
	if (wx.x <= 0.0) return -1.0; // clear sky here at every height: the march may stride
	// A full deck is not flat paint: thick and thin patches over kilometres, and a lumpy base (rolls of stratocumulus).
	float full = smoothstep(0.6, 0.9, cloud_state.z) * (1.0 - 0.75 * smoothstep(7000.0, 22000.0, length(p.xz - cloud_state.xy))); // (far away the 3D noise would repeat visibly)
	// (broken cloud sea: the thin layer keeps its mass at high cover, so it closes into a grey day; the patches opened
	// holes in it, D172)
	wx.x *= mix(1.0, 0.2 + 1.0 * smoothstep(0.3, 0.75, n.r), full * (1.0 - 0.85 * SEA_K * float(sea_broken)));
	wx.z += full * (n.g - 0.5) * 500.0;
	// (rounded: only for heaped clouds; a full deck and a storm keep build 84's shape, which opened blue gaps)
	// (by the cover itself, not `full`, which fades with distance: far storm decks still opened blue streaks)
	float shape_k = cloud_shape == 1 ? (1.0 - smoothstep(0.6, 0.85, cloud_state.z)) * (1.0 - smoothstep(0.3, 0.7, cloud_state.w)) : 0.0;
	if (shape_k > 0.0) {
		// (the broad noise moves with the clouds: each cloud keeps its own height and base as it drifts)
		vec4 big = texture(cloud_noise, vec3(p.xz / 5200.0, 0.37));
		wx.y *= mix(1.0, mix(0.9, 1.4, big.r), shape_k); // (Tom: right direction, more mass: fewer low clouds)
		wx.x = min(wx.x * mix(1.0, 1.5, shape_k), 1.0);
		wx.z += ((big.g - 0.5) * 300.0 + (n.b - 0.5) * 140.0) * shape_k;
	}
	if (cloud_bases == 1) {
		// (D203) The base in lumps ~0.5–1.5 km across, stretched along the wind into rolls in a breeze, and rising toward
		// each cloud's thin edges (a rounded underside, lowest under the core); only the base moves, the top stays.
		// (lifted, not lowered: a first try hung the cores ~250 m lower, into the seam between the two passes at the
		// horizon, and the wider one-pass margin it needed doubled the cost flying under the layer)
		vec2 wd2 = normalize(vec2(wind.x, wind.z) + vec2(1e-4));
		vec2 q = vec2(dot(p.xz, wd2) / mix(1.0, 2.2, smoothstep(0.3, 1.2, wind.y)), dot(p.xz, vec2(-wd2.y, wd2.x)));
		float l1 = texture(cloud_noise, vec3(q / 1500.0, 0.43)).g, l2 = texture(cloud_noise, vec3(q / 560.0, 0.19)).b;
		float bo = l1 * 160.0 + (l2 - 0.5) * 90.0 + 170.0 * (1.0 - wx.x) * (1.0 - wx.x);
		wx.z += bo;
		wx.y = max(wx.y - bo, 150.0);
	}
	float h = (p.y - wx.z) / wx.y;
	if (h <= 0.0 || h >= 1.0) return 0.0;
	float clear_k = 1.0;
	if (sea_broken == 1 && SEA_K > 0.0 && cloud_air.z > 0.0) {
		vec2 m = (p.xz - cloud_state.xy + cloud_air.xy) / cloud_air.z + 0.5;
		if (m.x > 0.0 && m.y > 0.0 && m.x < 1.0 && m.y < 1.0) {
			float above = p.y + cloud_air.w - textureLod(air_map, m, 0.0).r;
			// (only around you, a few km, and only while you are below the sea's base: from above, the sea still fills the
			// valleys; climbing a slope into it, the clouds come down around you)
			float near_k = 1.0 - smoothstep(3000.0, 6000.0, length(p.xz - cloud_state.xy));
			float below_k = smoothstep(-50.0, 150.0, cloud_more.w);
			clear_k = mix(1.0, smoothstep(60.0, 320.0, above), SEA_K * near_k * below_k);
			if (clear_k <= 0.0) return 0.0;
		}
	}
	// Flat base, rounded domes: higher up only the denser parts of the map remain.
	// (the map never quite fills: the 3D noise carves lumps and gaps inside clouds too, not only at their edges)
	float dome = mix(0.62, 0.84, smoothstep(1800.0, 4500.0, wx.y)); // towers keep their width and round off only at the top
	float hs = clamp(h + (n.r - 0.5) * 0.14 * shape_k, 0.0, 1.0); // (rounded: a billowed top)
	if (SEA_K > 0.0) {
		// Cloud sea: the thin layer's top in lumps ~1 km across (cells of stratocumulus) and swells over kilometres,
		// not one smooth surface (first render: a snowfield).
		// (two scales: one alone left the lumps faceted, like crumpled paper)
		float c = texture(cloud_noise, p / 1100.0 + vec3(0.21, 0.63, 0.47)).r * 0.6 + texture(cloud_noise, p / 420.0 + vec3(0.71, 0.13, 0.37)).g * 0.4;
		hs = clamp(hs + ((c - 0.5) * 0.5 + (n.r - 0.5) * 0.25) * SEA_K, 0.0, 1.0);
	}
	// (rounded: the threshold passes 1 before the ceiling, so even the densest cloud closes its dome)
	float cov = clamp(vremap(wx.x, smoothstep(dome, 1.0, hs) * mix(0.95, 1.03, shape_k), 1.0, 0.0, 1.0), 0.0, 1.0)
		* smoothstep(0.0, cloud_bases == 1 ? 0.025 : mix(0.06, 0.1, shape_k), h) * mix(wx.x, 1.0, 0.25) * mix(0.8, 0.95, shape_k);
	float wf = n.g * 0.7 + n.b * 0.3;
	float base = clamp(vremap(n.r, wf - 1.0, 1.0, 0.0, 1.0), 0.0, 1.0);
	base = base * base * (3.0 - 2.0 * base); // rounder lumps
	float d = clamp(vremap(base, 1.0 - cov, 1.0, 0.0, 1.0), 0.0, 1.0) * cov * (1.0 + 0.9 * wx.x); // thick parts denser
	if (cloud_caps == 1) d *= 1.0 + 2.5 * land; // (dense caps)
	d *= clear_k;
	if (sea_broken == 1 && SEA_K > 0.0) {
		// Broken cloud sea (D172): holes between the cells, ~1–2 km apart and clear through the layer, so from a valley you
		// see sky through it and sunlight comes down; fewer as the cover rises, none from ~90 % (a closed, grey day).
		float hole = sea_tune.x * (1.0 - smoothstep(0.72, 0.9, cloud_state.z)) * SEA_K;
		// (D179: the holes' outline wanders with height — straight columns stood as tall vertical curtains seen from in
		// the layer, Tom's dump 235543)
		vec2 hw = p.xz + (texture(cloud_noise, p / 800.0 + vec3(0.13, 0.57, 0.29)).gb - 0.5) * 520.0;
		float cell = texture(cloud_noise, vec3(hw / 1700.0, 0.61)).r * 0.7 + texture(cloud_noise, vec3(hw / 650.0, 0.23)).g * 0.3;
		// (rounded sides: wider holes toward the base and the top, ragged with the billows — straight they stood as walls)
		float hole_h = hole + (0.1 * smoothstep(0.4, 0.0, h) + 0.06 * smoothstep(0.65, 1.0, h) + (n.g - 0.5) * 0.12) * step(0.001, hole);
		d *= smoothstep(hole_h, hole_h + 0.16, cell);
	}
	if (cloud_turbulence == 1 && detail && d > 0.0) {
		float k = 1.0 - smoothstep(150.0, 600.0, length(vec3(p.x - cloud_state.x, p.y, p.z - cloud_state.y)));
		if (k > 0.0) {
			// Two octaves moving against each other and rising slowly: eddies forming and coming apart.
			float tt = cloud_more.z;
			vec3 q = p / 60.0;
			float eddy = texture(cloud_noise, q + vec3(tt * 0.035, tt * 0.02, -tt * 0.025)).g * 0.6
				+ texture(cloud_noise, q * 2.3 + vec3(-tt * 0.06, tt * 0.03, tt * 0.05)).b * 0.4;
			d *= mix(1.0, 0.2 + 1.6 * eddy, k);
		}
	}
	if (d <= 0.0 || !detail) return body_k(d);
	// Fine detail: billowy on top, wispy underneath; in wind stretched along it (smudged streaks).
	float windy = cloud_windy();
	vec2 wd = normalize(vec2(wind.x, wind.z) + vec2(1e-4));
	vec2 along = vec2(dot(p.xz, wd), dot(p.xz, vec2(-wd.y, wd.x)));
	vec3 dp = vec3(along.x / mix(1.0, 3.5, windy), p.y - cloud_more.z * 2.0, along.y);
	vec4 m = texture(cloud_noise, dp / 700.0);
	float df = m.g * 0.75 + m.b * 0.25; // (the finest octave would only sparkle at sky distances)
	if (cloud_fine == 1) {
		// (fine: the big billows from the 64³ noise, then two finer layers fading in toward the camera)
		float dist_f = length(vec3(p.x - cloud_state.x, p.y, p.z - cloud_state.y));
		vec4 f = texture(cloud_fine_noise, dp / 520.0 + vec3(0.29, 0.61, 0.17));
		float fine_k = 1.0 - smoothstep(3000.0, 9000.0, dist_f);
		df = mix(df, m.g * 0.45 + f.r * 0.3 + f.g * 0.25, 0.85);
		df = mix(df, df * 0.7 + f.b * 0.3, fine_k);
	}
	if (cloud_shape == 1 && cloud_state.w < 0.5) {
		// Sharper billows (Tom): a second, finer octave of cauliflower near enough to show it (within ~4 km).
		float near = 1.0 - smoothstep(1500.0, 4000.0, length(vec3(p.x - cloud_state.x, p.y, p.z - cloud_state.y)));
		if (near > 0.0) df = mix(df, texture(cloud_noise, dp / 260.0 + vec3(0.37, 0.11, 0.53)).g, 0.35 * near);
	}
	// (reference: billowy almost to the base, which stays flat, and cut deeper: cauliflower edges)
	df = cloud_look == 1 ? mix(1.0 - df, df, smoothstep(0.0, 0.1, h)) : mix(1.0 - df, df, smoothstep(0.1, 0.45, h));
	if (cloud_bases == 1) {
		// (D203) Lobes hanging from the base: over its lowest ~160 m the billow noise decides how far down the cloud
		// reaches, so the underside has relief to shade instead of one sheet.
		// (coarser lobes ~300–600 m and gentler than the billows: the billow noise alone crinkled the base like foil)
		float hm = h * wx.y;
		if (hm < 200.0) {
			float lobe = texture(cloud_noise, dp / 1500.0 + vec3(0.53, 0.29, 0.71)).g * 0.65 + m.g * 0.35;
			d *= smoothstep(-0.1, 0.3, hm / 180.0 + (lobe - 0.5) * 0.9);
		}
	}
	// (rounded: the finer octave above gives the sharper billows; the erosion itself stays, it took the mass away)
	return body_k(clamp(vremap(d, df * mix(cloud_look == 1 || cloud_shape == 1 ? 0.52 : 0.42, 0.25, windy), 1.0, 0.0, 1.0), 0.0, 1.0));
}

// Distance along dir to an approaching front (a vertical curtain cloud_front.z metres away; 1e9 if none or behind).
float front_distance(vec3 dir) {
	if (cloud_front.w <= 0.02) return 1e9;
	float c = dot(dir.xz, cloud_front.xy);
	return c < 0.05 ? 1e9 : cloud_front.z / c;
}

// An approaching front seen from outside (round 19, Noor: "standing in sunshine and seeing a dark storm system 20 km
// away", "sailing toward a giant wall of rain while the clouds get progressively uglier"): on a curtain at its edge, a
// dark bank of cloud towering to 3–7 km (strong fronts higher: thunderstorms) with a lumpy, sunlit top, and under it
// grey slanting rain down to the ground. Premultiplied colour, opacity.
vec4 front_wall(vec3 dir, vec3 ambient, vec3 sun_dir, vec3 sun_col, vec3 haze) {
	float tp = front_distance(dir);
	if (tp >= 1e8) return vec4(0.0);
	float hd = tp * length(dir.xz);
	float y = dir.y * tp + hd * hd / 12.742e6; // height on the curtain (with the Earth's curve)
	vec2 perp = vec2(-cloud_front.y, cloud_front.x);
	float x = dot(dir.xz, perp) * tp;
	float w = cloud_front.w;
	// The bank: its top rises and falls in big lumps and small billows.
	// Towers along the bank: several kilometres apart, some much taller (strong fronts: thunderstorm towers).
	// (smooth waves: the cloud texture's ridged creases made spikes)
	float towers = clamp(0.5 + 0.32 * sin(x / 6200.0 + 1.1) + 0.22 * sin(x / 2700.0 + 4.0) + 0.12 * sin(x / 17000.0 + 2.2), 0.0, 1.0);
	towers = towers * towers * (3.0 - 2.0 * towers);
	float top = VBOTTOM + mix(1600.0, 5200.0, smoothstep(0.55, 1.0, w)) * (0.4 + 0.75 * towers);
	// Rounded billows on top: two overlapping rows of domes of random sizes (a function of x only: no spikes).
	float bill = 0.0;
	for (int r = 0; r < 2; r++) {
		float sz = r == 0 ? 3200.0 : 1900.0, off = float(r) * 0.5;
		float cx = floor(x / sz + off), fx = fract(x / sz + off) * 2.0 - 1.0;
		float hc = fract(sin(cx * 91.7 + float(r) * 13.1) * 43758.5453);
		bill = max(bill, sqrt(max(0.0, 1.0 - fx * fx)) * sz * (0.18 + 0.3 * hc));
	}
	top += bill - 400.0;
	float body = smoothstep(top, top - 300.0, y) * smoothstep(VBOTTOM - 250.0, VBOTTOM + 150.0, y);
	float v = clamp((y - VBOTTOM) / max(top - VBOTTOM, 1.0), 0.0, 1.0);
	float lump = texture(cloud_tex, vec2(x / 1800.0, y / 1800.0)).r * 0.6 + texture(cloud_tex, vec2(x / 600.0, y / 600.0)).g * 0.4;
	// Dark slate underneath, lighter up the towers, the tops catching the sun when it shines on this side.
	vec3 cloud = ambient * vec3(0.92, 0.96, 1.08) * mix(0.32, 0.8, v * v) * (0.75 + 0.5 * lump)
		+ sun_col * max(dot(-vec3(cloud_front.x, 0.0, cloud_front.y), sun_dir) * 0.5 + 0.5, 0.0) * smoothstep(0.5, 1.0, v) * (0.35 + 0.3 * lump);
	// The rain beneath: slanting streaks in heavier and lighter bands.
	float slant = dot(vec2(wind.x, wind.z), perp) * 0.35;
	float streak = texture(cloud_tex, vec2((x - slant * y) / 2200.0, y / 40000.0)).r;
	float bands = smoothstep(0.25, 0.6, texture(cloud_tex, vec2(x / 14000.0, 0.37)).g);
	float rain = smoothstep(VBOTTOM + 100.0, VBOTTOM - 300.0, y) * (0.5 + 0.3 * streak) * mix(0.55, 1.0, bands);
	vec3 rain_col = ambient * (0.55 + 0.2 * streak);
	float a = clamp(max(body, rain), 0.0, 0.95) * w;
	vec3 c = body >= rain ? cloud : rain_col;
	// Far along the curtain (seen obliquely) the front sinks into the haze instead of squeezing into spikes.
	c = mix(c, haze, smoothstep(cloud_front.z * 1.3, cloud_front.z * 3.5 + 20000.0, tp) * 0.8);
	a *= 1.0 - smoothstep(cloud_front.z * 2.5 + 15000.0, cloud_front.z * 4.0 + 40000.0, tp);
	return vec4(c * a, a);
}

float vhg(float c, float g) { float g2 = g * g; return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5); }

// Test only (--cloud-dev=x,y; the cloud look investigation, round 32): x scales the clouds' aerial perspective (1 = as
// in play), y strengthens the self-shadowing (0 = as in play): more light used up toward the sun, darker bases, brighter
// sunlit tops; z > 0 replaces CloudsNear's step budget (max_steps). Not a setting; play always runs with (1, 0, 0, 0).
global uniform vec4 cloud_dev;

// Cloud grain without TAA (setting, D184): 1 = each cloud sample averages two densities over its step; 0 = build 95.
uniform int cloud_smooth = 1;
// Cloud quality level (setting, D196; Tom's rule: today's look stays the top level): 0 = High (as before); 1 = Medium:
// the fine billow detail only within 8 km (was 18), steps ×1.3 longer and ×0.77 fewer (the same reach); 2 = Low: detail
// within 4 km, steps ×1.6 / ×0.62.
uniform int cloud_cost = 0;
// Cloud softness (setting, D197; Tom: "they still look like foam … almost a solid object"): 0 = build 101; 1 = soft: the
// thin outer parts of a cloud thinner (density^1.7, the cores unchanged), light reaching deeper (extinction ×0.7) and the
// inside shaded by its own thickness more gradually — translucent edges and wisps instead of a hard opaque skin.
uniform int cloud_soft = 0;
// Cloud shading (setting, D198): 0 = build 101; 1 = smooth (finer light march, softer light-to-shade).
uniform int cloud_shading = 0;
// The march through the volume from t0 to t1 along dir (camera-relative): premultiplied light, opacity.
vec4 cloud_march(vec3 dir, float t0, float t1, float jitter, int max_steps, float min_step, vec3 sun_dir, vec3 sun_col, float day, float mu, float storm) {
	vec2 cam = cloud_state.xy;
	// The light: the sun by day, the moon at night (dim, silver).
	float night = 1.0 - smoothstep(-0.16, 0.04, sun_dir.y);
	vec3 ldir = night > 0.5 ? night_sky.xyz : sun_dir;
	float lmu = dot(dir, ldir);
	vec3 light_col = night > 0.5 ? vec3(0.09, 0.1, 0.13) * sky_extra.w : sun_col * mix(vec3(1.0), SKY_LIGHT0_COLOR, 0.3) * (1.0 - storm * 0.75);
	// Sky light: blue from above, the lit ground from below; at twilight the bases glow in sunset colours.
	vec3 sky_top = zenith_color * sky_mood.rgb * (0.08 + 0.92 * day) * mix(1.0, 0.55, storm);
	vec3 sky_side = horizon_color * mix(vec3(1.0), sky_mood.rgb, 0.7) * (0.08 + 0.92 * day) * mix(1.0, 0.6, storm);
	// Light bounced up from the sunlit land: warm, a little green (Tom: more colour variance).
	vec3 ground = mix(ground_color, vec3(0.55, 0.47, 0.36), 0.5) * (0.06 + 0.6 * max(sun_dir.y, 0.0)) * (1.0 - storm * 0.6);
	// Sunlight on clouds: a touch warmer than white by day, golden low down.
	// (reference: near-neutral tops with the sun high, as in the midday references; golden as before with it low)
	float warm_k = cloud_look == 1 ? mix(0.6, 0.2, smoothstep(0.25, 0.6, sun_dir.y)) : 0.6;
	light_col *= mix(vec3(1.0), vec3(1.06, 1.0, 0.9), warm_k * (1.0 - night));
	vec3 glow = underlight(dir);
	if (night > 0.5) { sky_top = vec3(0.012, 0.016, 0.03); sky_side = sky_top; ground = vec3(0.004); }
	// (v2 also looks down onto clouds below: their haze is the horizon's; identical to before for rays climbing)
	vec3 hdir = normalize(vec3(dir.x, max(dir.y, 0.0), dir.z));
	vec3 haze = sky_colour(hdir, sun_dir, sun_col, day, dot(hdir, sun_dir));
	float calm = 1.0 - cloud_windy();
	float sigma_k = mix(0.011, 0.019, calm) * (1.0 + storm) * (cloud_look == 1 ? 1.25 : 1.0) * (cloud_body == 1 ? 1.6 : 1.0) * (cloud_soft == 1 ? 0.7 : 1.0); // (reference: denser, crisper; soft D197: light reaches deeper) // calm cumulus: denser, crisper edges; real cloud is opaque within ~100 m
	float trans = 1.0;
	vec3 light = vec3(0.0);
	float phase_f = mix(vhg(lmu, 0.6), vhg(lmu, -0.25), 0.4);
	// Segments from t0 to t1, one sample each at its dithered point (D178, Tom: "what is this grainy pattern on the
	// character?"): the whole grid was shifted by the dither and every sample counted a full step, so in front of
	// something nearer than a step (Alden in thin cloud, the slope beside him) a pixel got either a full 20 m of cloud
	// or none — a fixed grain without TAA. Now the last segment ends at the scene and the dither only moves the sample
	// within its segment.
	float s0 = t0;
	float cost_k = cloud_cost == 2 ? 1.6 : cloud_cost == 1 ? 1.3 : 1.0;
	float detail_far = cloud_cost == 2 ? 4000.0 : cloud_cost == 1 ? 8000.0 : 18000.0;
	max_steps = int(float(max_steps) / cost_k);
	bool smooth2 = cloud_smooth == 1 && cloud_v2 < 1.5;
	// (D184: without TAA two samples per step at ¼ and ¾, no per-pixel offset: no grain on broken cloud. D185: in a thick
	// overcast deck that banded into contour lines — the "marble" sky in build 96; there the full offset comes back with
	// the second sample half a step on (wrapped), toward 85 % cover.)
	float strat_b = 0.5;
	if (smooth2) {
		jitter = mix(0.25, jitter, smoothstep(0.55, 0.85, cloud_state.z));
		strat_b = fract(jitter + 0.5) - jitter;
	}
	for (int i = 0; i < max_steps; i++) {
		if (s0 >= t1) break;
		float step_len = clamp(s0 * 0.005 * cost_k, min_step * cost_k, 450.0 * cost_k);
		float seg = min(step_len, t1 - s0);
		float t = s0 + jitter * seg;
		vec3 p = vec3(cam.x, 0.0, cam.y) + dir * t;
		s0 += seg;
		bool near = s0 < detail_far;
		float d = vdens(p, near);
		if (d < 0.0) { s0 += step_len * 0.5; continue; } // empty column: stride a little (cheaper; more would chip the edges)
		// (D184: without TAA the per-pixel ray offset stays still, and at a sharp cloud edge a pixel's sample fell just
		// inside or just outside: a fixed diagonal grain over thin cloud, Tom's dump 235339 with SMAA. Without TAA: no
		// offset, two density samples per step (¼ and ¾), one more density sample per cloud sample; the six light
		// samples stay one each. A second sample with the offset kept left the wisps speckled.)
		if (smooth2 && d >= 0.0) d = 0.5 * (d + max(vdens(p + dir * seg * strat_b, near), 0.0));
		if (cloud_soft == 1 && d > 0.0) d = min(pow(d, 1.7) * 1.15, d); // (D197: thin parts thinner, cores as before)
		if (d <= 0.002) continue;
		// Toward the light: six steps, longer and longer (≈3 km), cheap density.
		// (D198, Tom: "the clouds look posterized": six 60 m+ steps made the self-shadow near binary — flat lit and
		// flat shaded patches with sharp edges. Smooth shading: nine steps from 20 m, each pixel's steps offset, and the
		// light wrapping into the shade more through the higher scattering orders.)
		float od = 0.0, sl = cloud_shading == 1 ? 20.0 : 60.0;
		vec3 lp = p + (cloud_shading == 1 ? ldir * sl * (jitter - 0.5) : vec3(0.0));
		int ln_s = cloud_shading == 1 ? 9 : 6;
		float lg_s = cloud_shading == 1 ? 1.55 : 1.75;
		for (int k = 0; k < ln_s; k++) {
			lp += ldir * sl;
			od += max(vdens(lp, false), 0.0) * sl;
			sl *= lg_s;
		}
		od *= (1.0 + cloud_dev.y) * (cloud_look == 1 ? 1.15 : 1.0); // (test / reference: deeper self-shadow)
		float h = clamp((p.y - VBOTTOM) / 3000.0, 0.0, 1.0);
		// Multiple scattering (three orders, each spreading wider and reaching deeper) and the "powder" darkening of
		// the cloud's sunlit face when seen from the shadow side.
		float sun_e = 0.0, a = 1.0, b = 1.0;
		for (int o = 0; o < 3; o++) {
			sun_e += a * exp(-od * sigma_k * b) * mix(1.0, phase_f, b);
			if (cloud_shading == 1) { a *= 0.6; b *= 0.35; } else { a *= 0.42; b *= 0.4; } // (weaker higher orders: more self-shadowing; smooth: they carry more)
		}
		float powder = mix(1.0, 1.0 - exp(-d * 6.0 - od * sigma_k * 1.5), 0.6 * (1.0 - max(lmu, 0.0)));
		float hb = smoothstep(0.0, 0.45, (p.y - VBOTTOM) / 1800.0);
		// shadows take the blue of the sky above (greyer low down, where the horizon and the ground light them)
		vec3 amb = mix(mix(sky_side, sky_top, 0.55) * 0.7, sky_top * 1.35, hb) + ground * (1.0 - hb) * 0.8 + glow * (1.0 - hb) * 0.9;
		// Deep inside thick cloud the sky light is used up too: cores and undersides darken toward a cool slate (Noor:
		// "contrast … darker undersides, thicker cores"); thin parts keep the full light.
		float deep = 1.0 - exp(-od * sigma_k * 0.45);
		amb *= mix(1.0, 0.45, deep) * mix(0.75, 1.0, hb);
		if (cloud_shading == 1) {
			// (D198: the shaded parts were lit by flat sky light, one colour per lobe — overlapping lobes read as flat
			// cut-outs. Sky-light occlusion: cloud above and toward the open sky darkens a point's ambient, so folds,
			// undersides and the gaps between lobes shade gradually.)
			float oa = max(vdens(p + vec3(0.0, 90.0, 0.0), false), 0.0) * 90.0 + max(vdens(p + vec3(0.0, 300.0, 0.0), false), 0.0) * 210.0
				+ max(vdens(p - dir * 120.0, false), 0.0) * 120.0;
			amb *= mix(0.35, 1.0, exp(-oa * sigma_k * 0.5));
		}
		amb = mix(amb, amb * vec3(0.86, 0.9, 1.12), deep * 0.6);
		amb *= mix(1.0 - 0.45 * cloud_dev.y, 1.0, hb); // (test: darker bases)
		if (cloud_look == 1) {
			// Reference: flat bases blue-grey (#aeb6c8, hue ~221°), lit by the sky rather than darkened to slate: the
			// base's light keeps its brightness and turns toward the sky's blue-grey, less of the warm ground bounce.
			// (at golden hour the bases stay as before: warm-lit edges, dark-grey cores)
			float gl = dot(amb, vec3(0.2126, 0.7152, 0.0722));
			amb = mix(amb, gl * vec3(0.88, 0.94, 1.12) * 1.3, (1.0 - hb) * 0.75 * smoothstep(0.1, 0.45, sun_dir.y));
		}
		if (cloud_bases == 1 && hb < 1.0 && d > 0.02) {
			// (D203) On the underside (no cloud just below) the sky light from above is taken by the cloud overhead: under
			// thick cores the base darkens, under thin edges it keeps the light; after the flat blue-grey, so the base keeps
			// that colour but is shaded. (A first try on every low sample also greyed the sunlit sides of far cumulus.)
			float under = 1.0 - smoothstep(0.0, 0.15, max(vdens(p - vec3(0.0, 70.0, 0.0), false), 0.0));
			if (under > 0.0) {
				float oc = max(vdens(p + vec3(0.0, 130.0, 0.0), false), 0.0) * 130.0 + max(vdens(p + vec3(0.0, 400.0, 0.0), false), 0.0) * 270.0;
				amb *= mix(1.0, mix(0.5, 1.15, exp(-oc * sigma_k * 0.35)), under * (1.0 - hb));
			}
		}
		// Mountains shade the clouds (D167): a peak between this point and the sun takes its direct light, so ranges cast
		// long shadows and lanes of light across a cloud sea at sunrise; the sky's light stays.
		float tsun = night > 0.5 ? 1.0 : cloud_terrain_sun(p.xz - cam, p.y);
		// (D169: in the shadow the sky's light alone made the cloud tops a saturated sky blue; shaded cloud is lit as
		// much by the sunlit cloud around it, many times scattered: grey, a little cool)
		float gl_s = dot(amb, vec3(0.2126, 0.7152, 0.0722));
		amb = mix(amb, gl_s * vec3(0.94, 0.97, 1.04) + light_col * 0.05, (1.0 - tsun) * 0.75);
		light_col *= tsun;
		// (test / reference: brighter sunlit tops)
		vec3 s = light_col * sun_e * powder * 1.3 * (1.0 + (0.35 * cloud_dev.y + (cloud_look == 1 ? 0.25 : 0.0)) * h) + amb;
		// v2 (round 29): seen from inside a thick deck the light comes from every side, scattered many times — grey-
		// white, not the blue of the sky overhead (the first render inside the deck was a deep blue fog).
		if (cloud_v2 > 0.5) {
			float gl = dot(amb, vec3(0.3, 0.59, 0.11));
			// (second render: still a dark grey fog at 15:30 — the deck glows with sunlight scattered deep into it,
			// most toward the sun)
			vec3 inside = vec3(gl) * 1.6 + light_col * (0.35 + 0.35 * phase_f) * exp(-od * sigma_k * 0.06);
			s = mix(s, max(s, inside), deep * 0.85);
		}
		light_col /= max(tsun, 1e-3); // (back for the next sample: light_col is shared by the whole march)
		// Under a full deck the dense parts are darker, the thin ones brighter (light through them).
		s *= mix(1.0, 1.35 - 0.6 * clamp(d, 0.0, 1.0), smoothstep(0.6, 0.9, cloud_state.z));
		// Aerial perspective, thicker toward the horizon where the clouds meet it (Noor).
		s = mix(s, haze, clamp(1.0 - exp(-t / 36000.0) + (1.0 - smoothstep(0.0, 0.12, abs(dir.y))) * 0.25, 0.0, 0.92) * cloud_dev.x);
		// Far away a step is longer than a cloud edge is thick: soften there so samples do not flicker on and off.
		float sigma = d * sigma_k * mix(1.0, 0.45, smoothstep(150.0, 450.0, step_len));
		float ab = exp(-sigma * seg);
		light += trans * (1.0 - ab) * s;
		trans *= ab;
		if (trans < 0.015) break;
	}
	return vec4(light, 1.0 - trans);
}

vec4 volumetric(vec3 dir, vec3 sun_dir, vec3 sun_col, float day, float mu, float coverage, float storm) {
	if (dir.y < 0.005 || coverage < 0.001) return vec4(0.0);
	float vtop = VBOTTOM + ((350.0 + 950.0 + cloud_types.x * 5200.0 + storm * 700.0) * SHAPE_TALL + 900.0) * SEA_TALL + SHAPE_TOP_EXTRA;
	float vlow = (relief_frame.z > 0.0 ? max(VBOTTOM - 500.0, VLOW_MIN) : VBOTTOM) - SHAPE_LOW_EXTRA;
	float t0 = vlow / dir.y, t1 = min(min(vtop / dir.y, 50000.0), front_distance(dir)); // nothing beyond a front shows
	if (t0 > t1) return vec4(0.0);
	// Steps grow with distance (the same angular detail near and far), ~45 m close up, up to 900 m far away.
	float jitter = fract(sin(dot(dir.xz * 731.0, vec2(12.9898, 78.233))) * 43758.5453);
	vec4 r = cloud_march(dir, t0, t1, jitter, 150, 40.0, sun_dir, sun_col, day, mu, storm);
	float fade = smoothstep(0.005, 0.04, dir.y);
	return r * fade;
}

// One pass in the layer (setting, D176; Tom: "the clouds are uniformly transparent … especially in the mountains"): the
// clouds over the land (CloudsNear, marched to the scene) and over the sky (Sky.gdshader, half resolution) are lit and
// veiled a little differently; standing in or near the layer that seam drew the mountains' outlines into an opaque
// cloud, so the cloud looked see-through. 1 = while the camera is at or above the cloud base (up to ~300 m over the
// layer's top), CloudsNear draws the sky's
// pixels too (one pass, full resolution: costlier, only then); 0 = build 91.
uniform int cloud_one_pass = 0;
// (D179: a weight, faded over ~300 m of height around the base and the top instead of switching there — Tom: "when I got a
// little bit up, it jumped immediately"; the sky pass keeps (1 − k) of its clouds, CloudsNear adds k over the sky)
float clouds_one_pass_k() {
	if (cloud_one_pass == 0 || quality != 3 || cloud_blend != 1 || cloud_state.z < 0.001) return 0.0; // (build 52's blend has its own pass)
	float vtop = VBOTTOM + ((350.0 + 950.0 + cloud_types.x * 5200.0 + cloud_state.w * 700.0) * SHAPE_TALL + 900.0) * SEA_TALL + SHAPE_TOP_EXTRA;
	// (at or above the base, a little margin below it; under the broken cloud sea the clouds keep clear of you and the
	// seam does not show: the first try drew the valley's whole sky at full resolution for nothing, 38 → 26 FPS)
	float margin = sea_broken == 1 && SEA_K > 0.0 ? 0.0 : 150.0;
	return (1.0 - smoothstep(margin - 150.0, margin + 150.0, cloud_more.w)) * smoothstep(-600.0, -300.0, vtop);
}

// Cloud renderer v2: the cloud volume seen from anywhere — below the deck, inside it or above it — out to t_far (the
// scene's depth in front of geometry, unlimited in the sky). Steps start at 20 m so a cloud around the camera is fog.
vec4 volumetric_v2(vec3 dir, float t_far, float jitter, int max_steps, vec3 sun_dir, vec3 sun_col, float day, float mu, float coverage, float storm) {
	if (coverage < 0.001) return vec4(0.0);
	float vtop = VBOTTOM + ((350.0 + 950.0 + cloud_types.x * 5200.0 + storm * 700.0) * SHAPE_TALL + 900.0) * SEA_TALL + SHAPE_TOP_EXTRA;
	float vlow = (relief_frame.z > 0.0 ? max(VBOTTOM - 500.0, VLOW_MIN) : VBOTTOM) - SHAPE_LOW_EXTRA;
	float t0, t1;
	if (abs(dir.y) < 1e-4) {
		if (vlow > 0.0 || vtop < 0.0) return vec4(0.0);
		t0 = 0.0; t1 = 50000.0;
	} else {
		float ta = vlow / dir.y, tb = vtop / dir.y;
		t0 = max(min(ta, tb), 0.0); t1 = max(ta, tb);
	}
	t1 = min(min(t1, 50000.0), min(t_far, front_distance(dir)));
	if (t0 >= t1) return vec4(0.0);
	vec4 r = cloud_march(dir, t0, t1, jitter, max_steps, 20.0, sun_dir, sun_col, day, mu, storm);
	// From under the deck the rays toward the horizon run out to 50 km: the same horizon fade as the first renderer.
	if (vlow > 0.0) r *= smoothstep(0.005, 0.04, dir.y);
	return r;
}

// Interleaved gradient noise (Jimenez 2014): an even, fine-grained pattern of ray offsets — less visible grain than
// white noise at the same number of steps.
float cloud_ign(vec2 frag) { return fract(52.9829189 * fract(dot(frag, vec2(0.06711056, 0.00583715)))); }
// With a temporal anti-aliasing (TAA, FSR 2: cloud_v2 = 2) the pattern moves every frame (by the frame number, as
// Jimenez suggests) so the history averages the grain away; without it, it stays put (a still grain, no flicker).
float cloud_jitter(vec2 frag, float clock) {
	float f = cloud_v2 > 1.5 ? mod(floor(clock * 60.0), 64.0) : 0.0;
	return cloud_ign(frag + f * 5.588238);
}

// Storm sky (round 29, Tom: "they cover the whole sky and turn a bit darker, but just a grey darker sky doesn't build
// atmosphere"): instead of a flat grey wash, a low heavy deck with structure and racing scud under it. Simulated
// weather only (the classic model keeps build 35's overcast). Every texture scale divides the low layer's wrap period
// (108 km), and streaks come from taps smeared along a direction (no rotated coordinates), so nothing jumps.
//   * the deck (~900 m): big swells of thick and thin cloud, rolls lying across the wind, a lumpy underside; dark
//     cores, paler thin parts, thin parts glowing on the sun's side; darker the heavier the storm (thunder: w);
//   * scud (~450 m): ragged dark fragments stretched along the wind, moving twice as fast as the deck;
//   * the horizon: lighter under the deck's far edge, where the storm thins out.
vec3 storm_deck(vec3 dir, float day, float storm, float thunder, vec3 sun_dir) {
	float up = dir.y;
	vec3 lit = vec3(0.5, 0.53, 0.57) * (0.3 + 0.7 * day) * mix(vec3(1.0), sky_mood.rgb, 0.6);
	vec3 horizon = horizon_color * (0.5 + 0.5 * day) * mix(vec3(1.0), sky_mood.rgb, 0.7);
	if (up <= 0.0) return mix(lit * 0.8, horizon * 0.8, 0.5);
	vec2 wd = normalize(wind.xz + vec2(1e-4)), sd = vec2(-wd.y, wd.x);
	vec2 xz = cloud_layers.zw + dir.xz * (900.0 / max(up, 0.025));
	float big = texture(cloud_tex, xz / 13500.0).r;
	float mid = texture(cloud_tex, xz / 4320.0 + vec2(0.3, 0.1)).g;
	float fine = texture(cloud_tex, xz / 1080.0 + vec2(0.7, 0.4)).r;
	float roll = 0.0;
	for (int i = -2; i <= 2; i++) roll += texture(cloud_tex, (xz + sd * float(i) * 900.0) / 2700.0 + vec2(0.55, 0.85)).r;
	roll = smoothstep(0.35, 0.75, roll / 5.0);
	float thick = clamp(big * 0.45 + mid * 0.3 + roll * 0.35 - 0.05, 0.0, 1.0);
	float lump = fine * 0.6 + mid * 0.4;
	// Lumpy underside: bulges dark beneath with paler rims (first render: too soft, an even blue-grey).
	float bulge = smoothstep(0.35, 0.75, fine) * smoothstep(0.3, 0.8, thick);
	float shade = mix(0.16, 1.0, smoothstep(0.3, 0.9, 1.0 - thick)) * (0.72 + 0.5 * lump) * (1.0 - 0.35 * bulge);
	vec3 col = lit * shade * mix(1.0, 0.5, storm) * (1.0 - 0.4 * thunder);
	col = mix(col, col * vec3(0.82, 0.88, 1.0), smoothstep(0.5, 0.9, thick)); // slate-blue in the darkest cores
	// Thin parts glow on the sun's side (light through the deck).
	vec2 sh = normalize(sun_dir.xz + vec2(1e-4));
	float sunside = pow(max(dot(normalize(dir.xz + vec2(1e-4)), sh), 0.0), 3.0) * smoothstep(0.0, 0.3, sun_dir.y);
	col += vec3(0.95, 0.92, 0.86) * day * sunside * smoothstep(0.6, 0.15, thick) * 0.3;
	// Scud racing under the deck.
	vec2 sxz = cloud_layers.zw * 2.0 + dir.xz * (450.0 / max(up, 0.025));
	float s = 0.0;
	for (int i = -2; i <= 2; i++) {
		vec2 q = sxz + wd * float(i) * 420.0;
		s += texture(cloud_tex, q / 2700.0 + vec2(0.11, 0.62)).r * 0.6 + texture(cloud_tex, q / 900.0 + vec2(0.57, 0.23)).g * 0.4;
	}
	s /= 5.0;
	float scud = smoothstep(0.46 - storm * 0.1, 0.62, s) * smoothstep(0.02, 0.12, up) * smoothstep(0.2, 0.7, storm);
	col = mix(col, lit * (0.13 + 0.1 * fine) * (1.0 - 0.3 * thunder), scud * 0.95);
	// Light under the far edge of the deck.
	col = mix(col, horizon * mix(0.85, 0.6, storm), (1.0 - smoothstep(0.0, 0.12, up)) * 0.55);
	return col;
}

