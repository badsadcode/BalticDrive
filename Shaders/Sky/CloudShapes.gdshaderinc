// Cloud types from the weather and the land (round 17, docs/SKY_IDEAS.md item 3), shared by Sky.gdshader and the
// water's reflection (WaterExperimental2.gdshader), so what stands on the horizon also stands in the sea:
//   * towering cumulus around the horizon on warm afternoons — each tower bubbles up over minutes, stands, and
//     dissolves, then another grows elsewhere; flat bases at the condensation level, cauliflower tops;
//   * distant thunderstorms — a few huge cells far off (45–110 km) with flat anvil tops blown downwind, grey rain
//     shafts hanging under them, lightning flickering inside (and now and then a bolt to the ground);
//   * lenticular clouds — smooth lens-shaped plates stacked over a mountain peak in strong wind, standing still while
//     everything else drifts.
// Far clouds are drawn on vertical planes facing the camera at their distance (they are kilometres away, so this is
// exact enough), sunk by the Earth's curvature; the lens is a stack of real ellipsoids (seen from any side).
// Needs `cloud_tex` (the sky's tileable noise) declared before the include.

// x: cumulus 0..1, y: distant storm 0..1, z: the storms' azimuth (rad, atan(z, x) of the direction toward them),
// w: lightning in a distant cell (integer part: which cell 0–2, fraction: brightness).
global uniform vec4 cloud_types;
// The lens cloud's centre relative to the camera (m) and its strength (w, 0 = none).
global uniform vec4 cloud_lens;
// x: mackerel sky 0..1, y: the wind's heading (rad), z: cloud time (s, for growth), w: cloud base above the camera (m).
global uniform vec4 cloud_more;

const float CT_TAU = 6.28318530718;

float sq_ct(float x) { x = max(x, 0.0); return x * x; }
float ct_hash(float n) { return fract(sin(n * 12.9898 + 4.1414) * 43758.5453); }

// The ray's point on a vertical plane facing the camera at azimuth az0 and distance d (m): x across (m), y height
// above the camera (m), with the Earth's curvature (far clouds sink); w = 0 when the plane is behind or edge-on.
vec3 ct_plane(vec3 dir, float az0, float d) {
	float horiz = max(length(dir.xz), 1e-4);
	float da = atan(dir.z, dir.x) - az0;
	da = mod(da + 3.14159265, CT_TAU) - 3.14159265;
	float c = cos(da);
	if (c < 0.3) return vec3(0.0, 0.0, 0.0);
	float x = d * tan(da);
	float y = dir.y / horiz * d / c + d * d / 12.742e6;
	return vec3(x, y, 1.0);
}

// Light on a far cloud: n is the outward normal in the plane's frame (x across, y up, z toward the camera).
vec3 ct_light(vec3 n, float az0, vec3 sun_dir, vec3 sun_col, vec3 ambient, float v, vec3 glow) {
	float crease = length(n);
	vec3 f = vec3(cos(az0), 0.0, sin(az0)), r = vec3(-sin(az0), 0.0, cos(az0));
	vec3 nw = normalize(r * n.x + vec3(0.0, n.y, 0.0) - f * n.z + vec3(0.0, 1e-4, 0.0));
	float l = clamp(dot(nw, sun_dir) * 0.7 + 0.3, 0.0, 1.0);
	// Sunlit sides bright, the shadowed side and the base blue-grey (sky light), creases darker; after sunset the
	// tops keep the sunset colours longest (glow).
	float sun_up = smoothstep(-0.1, 0.25, sun_dir.y + 0.1);
	vec3 col = sun_col * l * 1.05 * sun_up + ambient * mix(0.55, 1.0, v) * (0.8 + 0.2 * nw.y);
	// Backlit: the edges toward the sun glow (silver lining), the body stays grey.
	float behind = max(dot(f, sun_dir), 0.0);
	col += sun_col * pow(behind, 3.0) * sq_ct(1.0 - n.z / max(crease, 1e-3)) * 1.2 * sun_up;
	return (col + glow * smoothstep(0.2, 1.0, v) * 1.2) * crease;
}

// A towering cumulus on its plane, built from round puffs (each shaded as a ball — what makes cumulus read as
// cumulus): three puffs per level, narrowing upward, a dome on top, a flat base; normals blend between puffs and the
// creases between them darken. q in metres, base b, height h, half width w. Returns the outward normal (plane frame,
// xyz) and the density (w); the crease shading is folded into the normal's length (≤ 1).
vec4 ct_tower(vec2 q, float b, float h, float w, float seed, bool cheap) {
	float v = (q.y - b) / max(h, 1.0);
	if (v < -0.05 || v > 1.1 || abs(q.x) > w * 1.7) return vec4(0.0);
	float edge = cheap ? 0.5 : texture(cloud_tex, q / (w * 1.1) + seed).g;
	float best = -1.0, second = -1.0, wsum = 0.0;
	vec3 nsum = vec3(0.0);
	int levels = int(clamp(floor(h / (w * 0.7)), 1.0, 7.0));
	float fl = float(levels);
	for (int j = 0; j <= 7; j++) {
		if (j > levels) break;
		float fj = float(j);
		bool top = j == levels;
		for (int side = 0; side < 3; side++) {
			if (top && side > 0) break;
			float k = seed * 17.0 + fj * 3.1 + float(side) * 1.7;
			float lift = fj / fl;
			float spread = (float(side) - 1.0) * w * (0.55 - 0.22 * lift);
			float rad = top ? w * (0.45 + 0.2 * ct_hash(k)) : w * (0.5 - 0.17 * lift) * (0.7 + 0.6 * ct_hash(k + 0.5)) * (side == 1 ? 1.1 : 1.0);
			float cx = top ? (ct_hash(k + 1.0) - 0.5) * w * 0.5 : spread + (ct_hash(k + 1.0) - 0.5) * w * 0.35;
			float cy = top ? b + h - rad * 0.95 : b + rad * 0.45 + lift * (h - rad * 1.3) + (side == 1 ? rad * 0.25 : 0.0) + (ct_hash(k + 2.0) - 0.5) * rad * 0.4;
			vec2 dq = (q - vec2(cx, cy)) / rad;
			float inside = 1.0 - length(dq);
			if (inside > best) { second = best; best = inside; } else if (inside > second) second = inside;
			float wt = exp(clamp(inside * 7.0, -30.0, 30.0));
			vec2 nxy = dq / max(length(dq), 1.0);
			nsum += vec3(nxy, sqrt(max(0.0, 1.0 - dot(nxy, nxy)))) * wt;
			wsum += wt;
		}
	}
	best += (edge - 0.5) * 0.12;
	float dens = smoothstep(0.0, 0.16, best) * smoothstep(-0.02, 0.04, v);
	if (dens <= 0.0) return vec4(0.0);
	// Puffs are bumps on one big rounded body: their normals lean toward the tower's own.
	vec3 body = vec3(q.x / (w * 1.4), max(v - 0.5, 0.0) * 1.2, 0.0);
	body.z = sqrt(max(0.0, 1.0 - min(dot(body.xy, body.xy), 1.0)));
	vec3 n = normalize(mix(normalize(body + vec3(0.0, 0.0, 0.2)), normalize(nsum / max(wsum, 1e-6)), 0.5));
	if (!cheap) {
		// Small lumps on the puffs (a bumped normal from the detail noise), so they are not smooth balls.
		vec2 uv = q / (w * 2.2) + seed * 1.3;
		float c0 = texture(cloud_tex, uv).g, cx = texture(cloud_tex, uv + vec2(0.03, 0.0)).g, cy = texture(cloud_tex, uv + vec2(0.0, 0.03)).g;
		n = normalize(n + vec3(c0 - cx, c0 - cy, 0.0) * 2.6);
	}
	n *= mix(0.72, 1.0, smoothstep(0.0, 0.3, v)); // the flat base in shadow
	float crease = 1.0 - 0.14 * exp(-max(best - second, 0.0) * 6.0) * smoothstep(0.0, 0.25, second);
	return vec4(n * crease, dens);
}

// A cumulonimbus: tower to ~11 km, anvil spread downwind (side: +1/−1 across the plane), rain shaft beneath.
// Returns colour (premultiplied) and opacity.
vec4 ct_storm(vec3 dir, float az0, float d, float b, float s, float side, float seed, float flash, vec3 sun_dir,
		vec3 sun_col, vec3 ambient, vec3 glow, bool cheap) {
	vec3 p = ct_plane(dir, az0, d);
	if (p.z == 0.0) return vec4(0.0);
	vec2 q = p.xy;
	float h = mix(7000.0, 12000.0, s) - b, w = mix(2600.0, 4300.0, s);
	if (abs(q.x) > w * 4.5 || q.y > b + h * 1.15) return vec4(0.0);
	vec4 res = vec4(0.0);
	// Tower.
	vec4 t = ct_tower(q, b, h * 0.97, w, seed, cheap);
	float v = (q.y - b) / h;
	// Anvil: a flat top at ~95 %, spreading downwind with a sharp upper edge and a ragged, sagging underside.
	float ax = (q.x - side * w * 2.0 * smoothstep(0.6, 1.0, v)) / (w * mix(1.6, 4.6, s));
	// (streaky, drawn out along the anvil; its underside sags toward the column that feeds it; thin at the ends)
	float an = texture(cloud_tex, q / vec2(w * 4.0, w * 0.9) + seed * 2.3).r;
	float aa = min(abs(ax), 1.0);
	float under = 0.7 + (0.5 - an) * 0.08 + pow(aa, 1.5) * 0.17, over = 0.975 - aa * aa * 0.07;
	float anvil = smoothstep(1.0, 0.7, abs(ax) + (0.5 - an) * 0.5) * smoothstep(over, over - 0.025, v) * smoothstep(under, under + 0.06, v);
	float dens = max(t.w, anvil);
	// The anvil: lit flat top, shadowed underside.
	vec3 n = t.w >= anvil ? t.xyz : normalize(vec3(ax * 0.35, clamp((v - under - 0.08) * 6.0, -1.0, 1.0), 0.5));
	if (dens > 0.0) {
		vec3 c = ct_light(n, az0, sun_dir, sun_col, ambient, clamp(v, 0.0, 1.0), glow) * mix(0.62, 1.0, smoothstep(0.0, 0.6, v));
		// Lightning inside: a glow somewhere in the column, brightest in its middle.
		if (flash > 0.0) {
			vec2 fc = vec2((ct_hash(seed + floor(cloud_more.z * 3.0)) - 0.5) * w, b + h * (0.2 + 0.4 * ct_hash(seed + 1.7 + floor(cloud_more.z * 3.0))));
			float fd = length((q - fc) / vec2(w, h * 0.4));
			c += vec3(0.75, 0.8, 1.0) * flash * (exp(-fd * fd * 2.5) * 1.8 + 0.1);
		}
		res = vec4(c * dens, dens);
	}
	// Rain shaft: streaks slanting with the wind from the base to the ground, denser in the middle.
	if (q.y < b + 200.0) {
		float slant = side * 0.25;
		float sx = (q.x - slant * (b - q.y) - side * w * 0.1) / (w * 0.85);
		float streak = texture(cloud_tex, vec2(sx * 2.5 + seed, q.y / 9000.0)).r;
		float shaft = smoothstep(1.0, 0.4, abs(sx) + (0.5 - streak) * 0.5) * smoothstep(b + 200.0, b - 150.0, q.y)
			* (0.6 + 0.4 * smoothstep(-300.0, 1200.0, q.y)) * (0.55 + 0.35 * streak) * s;
		vec3 rc = ambient * 0.38 + vec3(0.55, 0.6, 0.7) * flash * 0.25;
		res = vec4(res.rgb + rc * shaft * (1.0 - res.a), res.a + shaft * (1.0 - res.a));
		// Now and then a bolt to the ground under the flickering cell (night: plainly visible).
		if (flash > 0.55) {
			float k = floor(cloud_more.z * 3.0);
			float bx = (ct_hash(seed + k * 1.3) - 0.5) * w * 1.2 + sin(q.y / 260.0 + k) * 220.0 + sin(q.y / 73.0 + k * 2.0) * 90.0;
			float bolt = smoothstep(55.0 + d * 0.0012, 0.0, abs(q.x - bx)) * step(q.y, b);
			res.rgb += vec3(0.85, 0.9, 1.0) * bolt * (flash - 0.55) * 4.0;
			res.a = max(res.a, bolt * (flash - 0.55) * 2.0);
		}
	}
	return res;
}

// Lenticular clouds: a stack of thin smooth plates over the peak (cloud_lens), long across the wind, nearly the same
// size, so they merge into one smooth lens with faint layering; soft translucent rims frayed a little by the noise.
// Lens clouds (setting): 0 = build 54 (four 13 × 6.4 km elliptical plates almost on top of each other: from a few km
// below, one smooth disk filling half the sky — Tom, dump 215924); 1 = stacked: a pile of thinner, smaller plates
// (~6 × 2.8 km, shrinking upward, gaps between them), rims that wander instead of a perfect ellipse, darker undersides,
// and a smaller second lens downwind at the next crest of the mountain wave.
uniform int lens_style = 0;

vec4 ct_lens_stacked(vec3 dir, vec3 sun_dir, vec3 sun_col, vec3 ambient, vec3 glow, float s) {
	float wa = cloud_more.y;
	vec3 along = vec3(cos(wa), 0.0, sin(wa)), across = vec3(-sin(wa), 0.0, cos(wa));
	vec4 res = vec4(0.0);
	for (int i = 0; i < 7; i++) {
		float fi = float(i);
		bool second = i >= 5; // the downwind lens: two plates, ~7 km on, a little lower and smaller
		float k = second ? fi - 5.0 : fi;
		float n = second ? 2.0 : 5.0;
		vec3 c = cloud_lens.xyz + along * (second ? 7000.0 : 0.0) + vec3(0.0, (second ? -150.0 : 0.0) + (k - (n - 1.0) * 0.5) * 230.0, 0.0)
			+ along * (k - (n - 1.0) * 0.5) * 120.0;
		float shrink = (1.0 - 0.13 * k) * (second ? 0.55 : 1.0);
		vec3 rad = vec3(3000.0, 85.0 + 25.0 * ct_hash(fi * 3.1 + 0.4), 1400.0) * shrink;
		vec3 o = -c, ol = vec3(dot(o, across), o.y, dot(o, along)) / rad;
		vec3 dl = vec3(dot(dir, across), dir.y, dot(dir, along)) / rad;
		float a = dot(dl, dl), bq = dot(ol, dl), cq = dot(ol, ol) - 1.0;
		float disc = bq * bq - a * cq;
		if (disc <= 0.0) continue;
		float sr = sqrt(disc);
		float t0 = max((-bq - sr) / a, 0.0), t1 = (-bq + sr) / a;
		if (t1 <= 0.0) continue;
		vec3 pl = ol + dl * (t0 + t1) * 0.5; // the middle of the chord
		// A wandering rim: the plate's edge in or out by up to ~25 % with the angle around it, and frayed finer.
		float ang = atan(pl.z, pl.x);
		float wob = 0.78 + 0.14 * sin(ang * 3.0 + fi * 1.7) + 0.08 * sin(ang * 7.0 + fi * 2.9)
			+ (texture(cloud_tex, pl.xz * 0.6 + fi * 0.37).g - 0.5) * 0.18;
		float r = length(pl.xz) / wob;
		float chord = (t1 - t0) * sqrt(a);
		float dens = smoothstep(1.0, 0.82, r) * smoothstep(0.02, 0.5, chord) * 0.9 * s;
		if (dens <= 0.001) continue;
		vec3 nl = normalize(pl / rad);
		vec3 nw = normalize(across * nl.x + vec3(0.0, nl.y, 0.0) + along * nl.z);
		float l = clamp(dot(nw, sun_dir) * 0.5 + 0.5, 0.0, 1.0);
		// Lit tops, darker undersides (seen from below the pile is grey with bright rims toward the sun).
		vec3 col = sun_col * l * 0.85 * smoothstep(-0.1, 0.2, sun_dir.y + 0.1) + ambient * (0.55 + 0.45 * clamp(nl.y * 0.5 + 0.5, 0.0, 1.0)) + glow * 1.1;
		col += sun_col * pow(max(dot(dir, sun_dir), 0.0), 6.0) * smoothstep(0.85, 1.0, r) * 0.8;
		res = vec4(res.rgb + col * dens * (1.0 - res.a), res.a + dens * (1.0 - res.a));
	}
	return res;
}

vec4 ct_lens(vec3 dir, vec3 sun_dir, vec3 sun_col, vec3 ambient, vec3 glow) {
	float s = cloud_lens.w;
	if (s <= 0.001) return vec4(0.0);
	if (lens_style == 1) {
		float dist = length(cloud_lens.xz);
		return ct_lens_stacked(dir, sun_dir, sun_col, ambient, glow, s) * mix(1.0, 0.55, smoothstep(10000.0, 60000.0, dist));
	}
	float wa = cloud_more.y;
	vec3 along = vec3(cos(wa), 0.0, sin(wa)), across = vec3(-sin(wa), 0.0, cos(wa));
	vec4 res = vec4(0.0);
	for (int i = 0; i < 4; i++) {
		float fi = float(i);
		vec3 c = cloud_lens.xyz + vec3(0.0, (fi - 1.5) * 210.0, 0.0) + along * (fi - 1.5) * 160.0;
		vec3 rad = vec3(6500.0, 260.0, 3200.0) * (1.0 - abs(fi - 1.3) * 0.1);
		// Into the ellipsoid's frame (x across the wind, y up, z along it), scaled to a unit sphere.
		vec3 o = -c, ol = vec3(dot(o, across), o.y, dot(o, along)) / rad;
		vec3 dl = vec3(dot(dir, across), dir.y, dot(dir, along)) / rad;
		float a = dot(dl, dl), bq = dot(ol, dl), cq = dot(ol, ol) - 1.0;
		float disc = bq * bq - a * cq;
		if (disc <= 0.0) continue;
		float sr = sqrt(disc);
		float t0 = max((-bq - sr) / a, 0.0), t1 = (-bq + sr) / a;
		if (t1 <= 0.0) continue;
		// Chord through the plate (unit-sphere units, 0 at the rim … 2 through the middle): soft rims.
		float chord = (t1 - t0) * sqrt(a);
		vec3 pl = ol + dl * t0;
		float fray = texture(cloud_tex, pl.xz * 0.35 + fi * 0.37).g;
		float dens = smoothstep(0.05, 0.9, chord + (fray - 0.5) * 0.4) * 0.85 * s;
		vec3 nl = normalize(pl / rad);
		vec3 nw = normalize(across * nl.x + vec3(0.0, nl.y, 0.0) + along * nl.z);
		float l = clamp(dot(nw, sun_dir) * 0.5 + 0.5, 0.0, 1.0);
		vec3 col = sun_col * l * 0.85 * smoothstep(-0.1, 0.2, sun_dir.y + 0.1) + ambient * (0.75 + 0.25 * nl.y) + glow * 1.1;
		// Thin rims shine toward the sun.
		col += sun_col * pow(max(dot(dir, sun_dir), 0.0), 6.0) * (1.0 - smoothstep(0.1, 0.9, chord)) * 0.8;
		res = vec4(res.rgb + col * dens * (1.0 - res.a), res.a + dens * (1.0 - res.a));
	}
	// Far away the plates fade into the haze.
	float dist = length(cloud_lens.xz);
	return res * mix(1.0, 0.55, smoothstep(10000.0, 60000.0, dist));
}

// All far cloud types along dir (premultiplied colour, opacity), haze toward `haze` with distance. `ambient` is the
// sky light on clouds (day, night, storm), `glow` the sunset light on their tops.
vec4 ct_far(vec3 dir, vec3 sun_dir, vec3 sun_col, vec3 ambient, vec3 haze, vec3 glow, bool cheap, bool towers) {
	float cum = towers ? cloud_types.x : 0.0, storm = cloud_types.y;
	vec4 acc = vec4(0.0);
	if (dir.y < -0.02) return acc;
	float base = cloud_more.w, t = cloud_more.z;
	// Distant thunderstorms (back to front): three cells around their azimuth, the middle one nearest and tallest.
	if (storm > 0.001 && dir.y < 0.45) {
		float side = sin(cloud_more.y - cloud_types.z) > 0.0 ? 1.0 : -1.0; // the anvil blows downwind across the view
		for (int i = 2; i >= 0; i--) {
			float fi = float(i);
			float order = i == 0 ? 0.0 : i == 1 ? -1.0 : 1.0;
			float az = cloud_types.z + order * (0.32 + 0.1 * ct_hash(fi + 3.0));
			float d = mix(85000.0, 30000.0, storm) + fi * 12000.0;
			float s = storm * (1.0 - fi * 0.22);
			float flash = abs(floor(cloud_types.w) - fi) < 0.5 ? fract(cloud_types.w) : 0.0;
			vec4 c = ct_storm(dir, az, d, max(base, 900.0), s, side, fi * 7.31 + 1.0, flash, sun_dir, sun_col, ambient, glow, cheap);
			float fog = smoothstep(30000.0, 160000.0, d) * 0.6 + 0.12;
			c.rgb = mix(c.rgb, haze * c.a, fog);
			c *= smoothstep(0.0, 0.25, s);
			acc = vec4(c.rgb + acc.rgb * (1.0 - c.a), c.a + acc.a * (1.0 - c.a));
		}
	}
	// Afternoon towers: two rings of cells (far, then near), each cell somewhere in its own slice of the horizon,
	// living its own few minutes (it grows, stands, dissolves; then a new one grows elsewhere in the slice).
	if (cum > 0.001 && dir.y < 0.5) {
		float az = atan(dir.z, dir.x);
		for (int ring = 1; ring >= 0; ring--) {
			float fr = float(ring);
			float cells = ring == 0 ? 18.0 : 30.0, step_a = CT_TAU / cells;
			float idx = floor(az / step_a + fr * 0.5);
			for (int k = -1; k <= 1; k++) {
				float ci = idx + float(k);
				float cell = mod(ci, cells);
				float life_len = 420.0 + 240.0 * ct_hash(cell * 1.7 + fr);
				float age = t / life_len + ct_hash(cell * 3.1 + fr * 9.0);
				float gen = floor(age), ph = fract(age);
				float key = cell * 13.7 + fr * 101.0 + gen * 7.77;
				if (ct_hash(key) > 0.25 + cum * 0.7) continue;
				float az0 = (ci + 0.5 - fr * 0.5 + (ct_hash(key + 1.0) - 0.5) * 0.6) * step_a;
				float d = (ring == 0 ? 13000.0 : 30000.0) + 12000.0 * ct_hash(key + 2.0);
				float w = (650.0 + 1100.0 * ct_hash(key + 3.0)) * (ring == 0 ? 1.0 : 1.3);
				float grow = smoothstep(0.0, 0.55, ph), fade = smoothstep(1.0, 0.78, ph);
				float hgt = cum * (1500.0 + 5500.0 * pow(ct_hash(key + 4.0), 1.3)) * (0.2 + 0.8 * grow);
				w = max(w, hgt * 0.28); // tall towers are broad
				vec3 p = ct_plane(dir, az0, d);
				if (p.z == 0.0) continue;
				vec4 tw = ct_tower(p.xy, base, hgt, w * (0.7 + 0.3 * grow), key, cheap);
				if (tw.w <= 0.0) continue;
				float v = clamp((p.y - base) / max(hgt, 1.0), 0.0, 1.0);
				vec3 c = ct_light(tw.xyz, az0, sun_dir, sun_col, ambient, v, glow);
				float dens = tw.w * fade * smoothstep(0.0, 0.2, cum);
				c = mix(c, haze, 0.15 + smoothstep(8000.0, 60000.0, d) * 0.55);
				acc = vec4(c * dens + acc.rgb * (1.0 - dens), dens + acc.a * (1.0 - dens));
			}
		}
	}
	// The lens over the mountain (usually the nearest of these).
	vec4 l = ct_lens(dir, sun_dir, sun_col, ambient, glow);
	acc = vec4(l.rgb + acc.rgb * (1.0 - l.a), l.a + acc.a * (1.0 - l.a));
	return acc;
}

// Mackerel sky (altocumulus, ~4.5 km): rows of small cloudlets across the wind, in patches; smoothed to their
// average where a cloudlet would be smaller than a pixel.
// footprint_k: how much larger a pixel is than the full-resolution estimate (the sky's half-resolution pass: the cells
// aliased into rows of white dashes — D161; the rounded cloud draft passes 3).
float ct_mackerel_f(vec2 xz, float t_dist, float up, float footprint_k) {
	float m = cloud_more.x;
	if (m <= 0.001) return 0.0;
	float wa = cloud_more.y;
	vec2 al = vec2(cos(wa), sin(wa)), ac = vec2(-al.y, al.x);
	vec2 warp = (texture(cloud_tex, xz / 7000.0).rg - 0.5) * 900.0;
	vec2 w = vec2(dot(xz + warp, al), dot(xz + warp, ac));
	float cells = (0.5 + 0.5 * sin(w.x * CT_TAU / 320.0)) * (0.5 + 0.5 * sin(w.y * CT_TAU / 230.0 + sin(w.x / 700.0) * 2.0));
	cells = smoothstep(0.15, 0.7, cells) * (0.7 + 0.3 * texture(cloud_tex, xz / 1500.0).g);
	float footprint = t_dist * 0.0018 / max(up, 0.05) * footprint_k;
	cells = mix(cells, 0.33, smoothstep(50.0, 160.0, footprint));
	float pm = texture(cloud_tex, xz / 16000.0 + vec2(0.41, 0.73)).r;
	float mask = smoothstep(0.95 - m * 0.45, 1.1 - m * 0.45, pm);
	return cells * mask * 0.6;
}
float ct_mackerel(vec2 xz, float t_dist, float up) { return ct_mackerel_f(xz, t_dist, up, 1.0); }
