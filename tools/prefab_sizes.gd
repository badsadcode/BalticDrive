extends SceneTree
# Prints the bounding box of prefabs (res:// paths after --): size and centre, for fitting them to furniture footprints.
func _init():
	for p in OS.get_cmdline_user_args():
		var s = load(p)
		if s == null:
			print("MISSING ", p); continue
		var n = s.instantiate()
		var box = AABB()
		var first = true
		for c in [n] + n.find_children("*", "MeshInstance3D", true, false):
			if c is MeshInstance3D and c.mesh != null:
				var b = c.transform * c.mesh.get_aabb()
				box = b if first else box.merge(b)
				first = false
		print(p.get_file(), "  size ", box.size, "  min ", box.position)
		n.free()
	quit()
