// Impostor baking (round 30, docs/PERFORMANCE_TREES.md): draws a tree part unlit into an impostor view — its colour
// (mode 0) or its view-space normal (mode 1). The viewport output is converted from linear to sRGB, so the normal is
// written through the inverse conversion and arrives as the plain encoded value n * 0.5 + 0.5.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D albedo_tex : source_color, filter_nearest, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform bool has_texture = true;
uniform int mode = 0;

void fragment() {
	vec4 c = (has_texture ? texture(albedo_tex, UV) : vec4(1.0)) * tint;
	ALPHA = c.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	if (mode == 0) {
		ALBEDO = c.rgb;
	} else {
		vec3 n = normalize(FRONT_FACING ? NORMAL : -NORMAL);
		vec3 e = clamp(n * 0.5 + 0.5, 0.0, 1.0);
		ALBEDO = mix(e / 12.92, pow((e + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), e));
	}
}
