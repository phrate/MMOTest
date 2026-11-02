extends Node3D
## Renders the tile world from World data. Only tiles that exist explicitly in
## the world are drawn. A vertical wall is drawn on any edge where the neighbour's
## effective Z is strictly lower — so raised platforms, steps, and pit edges all
## get walls automatically. Void neighbours count as being at World.default_z.
##
## Rebuilds as the player crosses chunk boundaries.

@export var player: Node3D
@export var chunk_render_radius := 4
@export var floor_color_a := Color(0.45, 0.65, 0.35)   # checkerboard light
@export var floor_color_b := Color(0.35, 0.55, 0.25)   # checkerboard dark
@export var blocked_color := Color(0.65, 0.20, 0.20)   # non-walkable tile top
@export var wall_color := Color(0.45, 0.30, 0.18)      # brown, vertical faces
@export var wall_shade_ns := 0.85                       # north/south walls are darker

var _rendered: Dictionary = {}
var _last_center: Vector2i = Vector2i(0x7FFFFFFF, 0x7FFFFFFF)


func _ready() -> void:
	call_deferred("_initial_refresh")


func _initial_refresh() -> void:
	if player == null:
		player = get_tree().get_first_node_in_group("local_player")
	if player == null:
		return
	_last_center = _current_center()
	_refresh()


func _process(_delta: float) -> void:
	if player == null:
		player = get_tree().get_first_node_in_group("local_player")
		if player == null:
			return

	var center := _current_center()
	if center == _last_center:
		return
	_last_center = center
	_refresh()


func _current_center() -> Vector2i:
	return World.world_to_chunk(
		roundi(player.global_position.x),
		roundi(player.global_position.z))


func _refresh() -> void:
	var want := {}
	for dx in range(-chunk_render_radius, chunk_render_radius + 1):
		for dy in range(-chunk_render_radius, chunk_render_radius + 1):
			var k := Vector2i(_last_center.x + dx, _last_center.y + dy)
			if World.has_chunk(k.x, k.y):
				want[k] = true

	for key in _rendered.keys():
		if not want.has(key):
			_rendered[key].queue_free()
			_rendered.erase(key)

	for key in want:
		if not _rendered.has(key):
			_rendered[key] = _build_chunk(key.x, key.y)


func _build_chunk(cx: int, cy: int) -> MeshInstance3D:
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)

	var origin := World.chunk_origin(cx, cy)
	var size := World.chunk_size

	for lx in size:
		for ly in size:
			var wx := origin.x + lx
			var wy := origin.y + ly
			if not World.has_tile(wx, wy):
				continue

			var z := World.get_z(wx, wy)
			var walk := World.is_walkable(wx, wy)
			_add_top(st, wx, wy, z, walk)
			_add_wall(st, wx, wy, z,  1,  0)
			_add_wall(st, wx, wy, z, -1,  0)
			_add_wall(st, wx, wy, z,  0,  1)
			_add_wall(st, wx, wy, z,  0, -1)

	st.generate_normals()
	var mesh := st.commit()

	var mat := StandardMaterial3D.new()
	mat.vertex_color_use_as_albedo = true
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_PER_PIXEL
	mat.roughness = 1.0
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED

	var mi := MeshInstance3D.new()
	mi.mesh = mesh
	mi.material_override = mat
	add_child(mi)
	return mi


func _add_top(st: SurfaceTool, x: int, y: int, z: int, walkable: bool) -> void:
	var c: Color
	if not walkable:
		c = blocked_color
	else:
		c = floor_color_a if (x + y) % 2 == 0 else floor_color_b

	var zf := float(z)
	var v0 := Vector3(x - 0.5, zf, y - 0.5)
	var v1 := Vector3(x - 0.5, zf, y + 0.5)
	var v2 := Vector3(x + 0.5, zf, y + 0.5)
	var v3 := Vector3(x + 0.5, zf, y - 0.5)
	_tri(st, v0, v1, v2, c)
	_tri(st, v0, v2, v3, c)


func _add_wall(st: SurfaceTool, x: int, y: int, z: int, dx: int, dy: int) -> void:
	# World.get_z() returns World.default_z for void tiles, so isolated raised
	# platforms get walls down to the default height. Flat tiles in void get no
	# wall (their neighbours are "at" the same height).
	var nz := World.get_z(x + dx, y + dy)
	if nz >= z:
		return

	var zb := float(nz)
	var zt := float(z)
	var c := wall_color if dx == 0 else wall_color.darkened(1.0 - wall_shade_ns)

	if dx == 1:
		_tri(st, Vector3(x + 0.5, zb, y - 0.5), Vector3(x + 0.5, zt, y - 0.5), Vector3(x + 0.5, zt, y + 0.5), c)
		_tri(st, Vector3(x + 0.5, zb, y - 0.5), Vector3(x + 0.5, zt, y + 0.5), Vector3(x + 0.5, zb, y + 0.5), c)
	elif dx == -1:
		_tri(st, Vector3(x - 0.5, zb, y + 0.5), Vector3(x - 0.5, zt, y + 0.5), Vector3(x - 0.5, zt, y - 0.5), c)
		_tri(st, Vector3(x - 0.5, zb, y + 0.5), Vector3(x - 0.5, zt, y - 0.5), Vector3(x - 0.5, zb, y - 0.5), c)
	elif dy == 1:
		_tri(st, Vector3(x + 0.5, zb, y + 0.5), Vector3(x + 0.5, zt, y + 0.5), Vector3(x - 0.5, zt, y + 0.5), c)
		_tri(st, Vector3(x + 0.5, zb, y + 0.5), Vector3(x - 0.5, zt, y + 0.5), Vector3(x - 0.5, zb, y + 0.5), c)
	elif dy == -1:
		_tri(st, Vector3(x - 0.5, zb, y - 0.5), Vector3(x - 0.5, zt, y - 0.5), Vector3(x + 0.5, zt, y - 0.5), c)
		_tri(st, Vector3(x - 0.5, zb, y - 0.5), Vector3(x + 0.5, zt, y - 0.5), Vector3(x + 0.5, zb, y - 0.5), c)


func _tri(st: SurfaceTool, a: Vector3, b: Vector3, c: Vector3, color: Color) -> void:
	st.set_color(color); st.add_vertex(a)
	st.set_color(color); st.add_vertex(b)
	st.set_color(color); st.add_vertex(c)
