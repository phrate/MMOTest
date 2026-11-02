extends Node2D
## Standalone tile editor for world.json.
##
## _tiles is the exact set of tiles in the world. Nothing is auto-added:
##   - Left click  : paint the tile with the current Z / Walkable.
##   - Right click : erase the tile (removes it from the world).
##   - MMB         : pan.  Wheel : zoom.
##
## "New World" seeds a 16×16 flat region so there's a starting canvas.
## "Fill Touched Chunks" is the old auto-fill as an explicit button, in case
## you want to fill everything at once.

const USER_PATH := "user://world.json"
const RES_PATH := "res://data/world.json"
const FORMAT_VERSION := 2
const CHUNK_SIZE := 8
const DEFAULT_Z := 100
const Z_MIN := 0
const Z_MAX := 200
const TILE_PX := 1.0
const SEED_RADIUS := 8

var _world_version := 1
var _tiles: Dictionary = {}   # Vector2i(x, y) -> {z: int, walkable: bool}

var _camera: Camera2D
var _status_label: Label
var _cursor_label: Label
var _z_input: SpinBox
var _walkable_check: CheckBox

var _panning := false
var _last_pan_pos := Vector2.ZERO
var _painting := false
var _paint_button := 0   # 0 = paint, 1 = erase


func _ready() -> void:
	_build_ui()
	_build_camera()
	_load_world()
	queue_redraw()


# ---------- UI ----------

func _build_ui() -> void:
	var layer := CanvasLayer.new()
	add_child(layer)

	var panel := PanelContainer.new()
	panel.position = Vector2(8, 8)
	layer.add_child(panel)

	var vbox := VBoxContainer.new()
	panel.add_child(vbox)

	_status_label = Label.new()
	_status_label.text = "Loading..."
	vbox.add_child(_status_label)

	_cursor_label = Label.new()
	_cursor_label.text = "Tile: -, -"
	vbox.add_child(_cursor_label)

	var z_row := HBoxContainer.new()
	var z_lbl := Label.new()
	z_lbl.text = "Z:"
	z_row.add_child(z_lbl)
	_z_input = SpinBox.new()
	_z_input.min_value = Z_MIN
	_z_input.max_value = Z_MAX
	_z_input.value = DEFAULT_Z
	_z_input.step = 1
	_z_input.custom_minimum_size = Vector2(80, 0)
	z_row.add_child(_z_input)
	vbox.add_child(z_row)

	_walkable_check = CheckBox.new()
	_walkable_check.text = "Walkable"
	_walkable_check.button_pressed = true
	vbox.add_child(_walkable_check)

	var save_btn := Button.new()
	save_btn.text = "Save & Bump Version"
	save_btn.pressed.connect(_on_save_pressed)
	vbox.add_child(save_btn)

	var reload_btn := Button.new()
	reload_btn.text = "Reload from Disk"
	reload_btn.pressed.connect(func() -> void:
		_load_world()
		queue_redraw())
	vbox.add_child(reload_btn)

	var fill_btn := Button.new()
	fill_btn.text = "Fill Touched Chunks"
	fill_btn.pressed.connect(func() -> void:
		var before := _tiles.size()
		_fill_touched_chunks()
		_status_label.text = "Filled %d tiles" % (_tiles.size() - before)
		queue_redraw())
	vbox.add_child(fill_btn)

	var new_btn := Button.new()
	new_btn.text = "New World (16×16)"
	new_btn.pressed.connect(func() -> void:
		_tiles.clear()
		_world_version += 1
		_seed_default_region()
		_status_label.text = "New world v%d · %d tiles" % [_world_version, _tiles.size()]
		queue_redraw())
	vbox.add_child(new_btn)

	var hint := Label.new()
	hint.text = "LMB paint · RMB erase · MMB pan · Wheel zoom"
	hint.modulate = Color(1, 1, 1, 0.7)
	vbox.add_child(hint)


func _build_camera() -> void:
	_camera = Camera2D.new()
	_camera.zoom = Vector2(20, 20)
	_camera.position = Vector2.ZERO
	add_child(_camera)
	_camera.make_current()


# ---------- Input ----------

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb: InputEventMouseButton = event
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_panning = mb.pressed
			_last_pan_pos = mb.position
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_camera.zoom = Vector2(
				clampf(_camera.zoom.x * 1.15, 2.0, 64.0),
				clampf(_camera.zoom.y * 1.15, 2.0, 64.0))
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_camera.zoom = Vector2(
				clampf(_camera.zoom.x / 1.15, 2.0, 64.0),
				clampf(_camera.zoom.y / 1.15, 2.0, 64.0))
			return
		if mb.button_index == MOUSE_BUTTON_LEFT:
			_painting = mb.pressed
			_paint_button = 0
			if mb.pressed:
				_paint_at_cursor()
			return
		if mb.button_index == MOUSE_BUTTON_RIGHT:
			_painting = mb.pressed
			_paint_button = 1
			if mb.pressed:
				_paint_at_cursor()
			return
	elif event is InputEventMouseMotion:
		var mm: InputEventMouseMotion = event
		if _panning:
			var delta := (mm.position - _last_pan_pos) / _camera.zoom
			_camera.position -= delta
			_last_pan_pos = mm.position
		elif _painting:
			_paint_at_cursor()
		_update_cursor_label()


# ---------- Painting ----------

func _paint_at_cursor() -> void:
	var tile := _mouse_to_tile()
	if _paint_button == 1:
		_tiles.erase(tile)
	else:
		_tiles[tile] = {
			"z": int(_z_input.value),
			"walkable": _walkable_check.button_pressed,
		}
	_update_cursor_label()
	queue_redraw()


func _mouse_to_tile() -> Vector2i:
	var world := get_global_mouse_position()
	return Vector2i(
		roundi(world.x / TILE_PX),
		roundi(world.y / TILE_PX))


func _update_cursor_label() -> void:
	var t := _mouse_to_tile()
	var d: Variant = _tiles.get(t)
	if d == null:
		_cursor_label.text = "Tile: %d, %d · (void)" % [t.x, t.y]
	else:
		var walk_s := "walk" if d["walkable"] else "BLOCK"
		_cursor_label.text = "Tile: %d, %d · z=%d · %s" % [t.x, t.y, d["z"], walk_s]


# ---------- Drawing ----------

func _draw() -> void:
	for key in _tiles:
		var x: int = key.x
		var y: int = key.y
		var rect := Rect2(
			x * TILE_PX - TILE_PX / 2,
			y * TILE_PX - TILE_PX / 2,
			TILE_PX, TILE_PX)
		draw_rect(rect, _color_for_tile(_tiles[key]), true)

	var grid_color := Color(1, 1, 1, 0.08)
	var span := 64
	var i := -span
	while i <= span:
		var p := i * TILE_PX
		draw_line(Vector2(p, -span * TILE_PX), Vector2(p, span * TILE_PX), grid_color, 0.02)
		draw_line(Vector2(-span * TILE_PX, p), Vector2(span * TILE_PX, p), grid_color, 0.02)
		i += CHUNK_SIZE


func _color_for_tile(data: Dictionary) -> Color:
	if not data["walkable"]:
		return Color(0.7, 0.2, 0.2)
	return _color_for_z(data["z"])


func _color_for_z(z: int) -> Color:
	var t := clampf((z - DEFAULT_Z) / 20.0, -1.0, 1.0)
	return Color(
		clampf(0.42 - t * 0.15, 0.0, 1.0),
		clampf(0.60 + t * 0.20, 0.0, 1.0),
		clampf(0.32 - t * 0.10, 0.0, 1.0))


# ---------- Load / Save ----------

func _load_world() -> void:
	_tiles.clear()

	var path := ""
	if FileAccess.file_exists(USER_PATH):
		path = USER_PATH
	elif FileAccess.file_exists(RES_PATH):
		path = RES_PATH
	else:
		_world_version = 1
		_seed_default_region()
		_status_label.text = "New world v1 · %d tiles (paint & save)" % _tiles.size()
		return

	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		_status_label.text = "Failed to open " + path
		return
	var text := f.get_as_text()
	f.close()

	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY:
		_status_label.text = "Parse error in " + path
		return
	var root: Dictionary = parsed

	_world_version = int(root.get("world_version", 1))

	var chunks: Variant = root.get("chunks", {})
	if typeof(chunks) == TYPE_DICTIONARY:
		for chunk_key in chunks:
			var arr: Variant = chunks[chunk_key]
			if typeof(arr) != TYPE_ARRAY:
				continue
			for tile in arr:
				if typeof(tile) != TYPE_DICTIONARY:
					continue
				var x := int(tile.get("x", 0))
				var y := int(tile.get("y", 0))
				var z := int(tile.get("z", DEFAULT_Z))
				var walkable := true
				var flags: Variant = tile.get("flags", {})
				if typeof(flags) == TYPE_DICTIONARY and flags.has("walkable"):
					walkable = bool(flags["walkable"])
				_tiles[Vector2i(x, y)] = {"z": z, "walkable": walkable}

	_status_label.text = "Loaded v%d · %d tiles · %s" % [_world_version, _tiles.size(), path]
	print("[Editor] Loaded world v%d with %d tiles from %s" % [_world_version, _tiles.size(), path])


func _on_save_pressed() -> void:
	_world_version += 1

	var chunk_map := {}
	for key in _tiles:
		var x: int = key.x
		var y: int = key.y
		var cx := floori(float(x) / CHUNK_SIZE)
		var cy := floori(float(y) / CHUNK_SIZE)
		var chunk_key := "%d,%d" % [cx, cy]
		if not chunk_map.has(chunk_key):
			chunk_map[chunk_key] = []
		var d: Dictionary = _tiles[key]
		chunk_map[chunk_key].append({
			"x": x, "y": y, "z": d["z"],
			"flags": {"walkable": d["walkable"]}
		})

	for chunk_key in chunk_map:
		chunk_map[chunk_key].sort_custom(_tile_less)

	var sorted_keys := chunk_map.keys()
	sorted_keys.sort()
	var chunks := {}
	for k in sorted_keys:
		chunks[k] = chunk_map[k]

	var world_obj := {
		"format_version": FORMAT_VERSION,
		"world_version": _world_version,
		"chunk_size": CHUNK_SIZE,
		"default_z": DEFAULT_Z,
		"z_min": Z_MIN,
		"z_max": Z_MAX,
		"chunks": chunks
	}

	var json := JSON.stringify(world_obj, "\t")

	var f := FileAccess.open(USER_PATH, FileAccess.WRITE)
	if f == null:
		_status_label.text = "Failed to write " + USER_PATH
		return
	f.store_string(json)
	f.close()

	_status_label.text = "Saved v%d · %d tiles · %s" % [_world_version, _tiles.size(), USER_PATH]
	print("[Editor] Saved world v%d with %d tiles" % [_world_version, _tiles.size()])


## Explicit fill: adds a default tile for every cell in any chunk that already
## has at least one explicit tile. Only runs when the user clicks the button.
func _fill_touched_chunks() -> void:
	var touched := {}
	for key in _tiles:
		var cx := floori(float(key.x) / CHUNK_SIZE)
		var cy := floori(float(key.y) / CHUNK_SIZE)
		touched[Vector2i(cx, cy)] = true

	for ck in touched:
		var ox: int = ck.x * CHUNK_SIZE
		var oy: int = ck.y * CHUNK_SIZE
		for dx in CHUNK_SIZE:
			for dy in CHUNK_SIZE:
				var k := Vector2i(ox + dx, oy + dy)
				if not _tiles.has(k):
					_tiles[k] = {"z": DEFAULT_Z, "walkable": true}


func _seed_default_region() -> void:
	for x in range(-SEED_RADIUS, SEED_RADIUS):
		for y in range(-SEED_RADIUS, SEED_RADIUS):
			_tiles[Vector2i(x, y)] = {"z": DEFAULT_Z, "walkable": true}


func _tile_less(a: Dictionary, b: Dictionary) -> bool:
	if a["y"] != b["y"]:
		return a["y"] < b["y"]
	return a["x"] < b["x"]
