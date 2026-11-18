// X-ray variant for parts inside the car (engine, battery, brakes, suspension, seat): seen through the body. The workshop's ghost highlight of a car part (D68, Tom: "a ghost highlight of the shape of the car part, green in
// good condition, the more worn the more red"). Drawn over a copy of the car's body, glass or a wheel, pushed out a
// hair along the normals; with a box it keeps only the part's region (in the car's own space): the hood, a door.
// A soft fill with a brighter rim, gently pulsing.
shader_type spatial;
render_mode unshaded, blend_mix, cull_back, depth_draw_never, depth_test_disabled;

uniform vec4 tint : source_color = vec4(0.3, 0.9, 0.3, 1.0);
uniform bool use_box = false;
uniform vec3 box_min = vec3(-10.0);
uniform vec3 box_max = vec3(10.0);
uniform mat4 to_car = mat4(1.0);   // world → the car's local space

varying vec3 car_pos;

void vertex() {
	VERTEX += NORMAL * 0.015;
	car_pos = (to_car * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (use_box && (any(lessThan(car_pos, box_min)) || any(greaterThan(car_pos, box_max)))) discard;
	float rim = pow(1.0 - clamp(abs(dot(NORMAL, VIEW)), 0.0, 1.0), 2.0);
	float pulse = 0.8 + 0.2 * sin(TIME * 4.0);
	ALBEDO = tint.rgb * (0.9 + 0.6 * rim);
	ALPHA = clamp((0.28 + 0.6 * rim) * pulse, 0.0, 0.95);
}
