// Car paint (D100): Synty's cars all come in one blue (their colour atlases only change buildings). This re-colours the
// body paint only: texels near the paint's hue and saturated enough take a new hue, saturation and brightness; glass,
// tyres, lights and chrome (other hues, or grey) stay as they are. Grime (wreck_grime) still goes on top as an overlay.
shader_type spatial;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float src_hue = 0.6;           // the paint to replace (Synty blue)
uniform float hue = 0.0;               // the new paint's hue (0..1)
uniform float sat = 1.0;               // × saturation (0: grey, white, black)
uniform float val = 1.0;               // × brightness
uniform float roughness_paint = 0.45;
uniform float metallic_paint = 0.25;

vec3 rgb2hsv(vec3 c) {
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void fragment() {
    vec3 c = texture(albedo_tex, UV).rgb;
    vec3 h = rgb2hsv(c);
    float dh = abs(fract(h.x - src_hue + 0.5) - 0.5);
    float paint = (1.0 - smoothstep(0.06, 0.12, dh)) * smoothstep(0.25, 0.4, h.y);
    vec3 painted = hsv2rgb(vec3(hue, clamp(h.y * sat, 0.0, 1.0), clamp(h.z * val, 0.0, 0.95)));
    ALBEDO = mix(c, painted, paint);
    ROUGHNESS = mix(0.8, roughness_paint, paint);
    METALLIC = mix(0.0, metallic_paint, paint);
}
