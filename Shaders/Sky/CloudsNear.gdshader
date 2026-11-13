// Cloud renderer v2 (round 29, docs/SKY_IDEAS.md "Ideas from other Godot cloud projects", item 1): the cloud volume
// in front of the scene. The sky can only draw clouds behind everything, so a cloud could never hang in front of a
// mountain, wrap a summit or swallow the player; this full-screen pass marches the same volume (SkyCommon.gdshaderinc)
// from the camera to the scene's depth, over geometry only — the sky pixels keep the sky's own half-resolution march.
// Rays that never reach the cloud layer (most of them, under a high deck) cost one slab test.
shader_type spatial;
// Fog on (round 31): Godot's volumetric fog veils the sky, its clouds included; these clouds take the same veil (the
// fragment sits just past the fog's length, where the sky reads it — fog_depth). Colour leaves this shader
// unpremultiplied with PREMUL_ALPHA_FACTOR = opacity, so Godot's colour × transmittance + in-scatter comes out as
// cloud × T + in-scatter × opacity, as over the sky. (Build 52, fog off: CloudsNear52.gdshader.)
render_mode unshaded, blend_premul_alpha, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, skip_vertex_transform;
#include "res://Shaders/Sky/CloudsNear.gdshaderinc"
