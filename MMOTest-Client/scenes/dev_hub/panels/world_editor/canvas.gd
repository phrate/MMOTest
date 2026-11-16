class_name WorldEditorCanvas
extends Control
## 2D tile render + input surface for the world editor.
##
## Owns: camera pan/zoom, tile drawing, NPC marker drawing (all 4 states),
## pending-create marker drawing, hover/selection overlays, and the paint/erase
## operations themselves. Panel sets paint tool + mode via set_*; canvas handles
## all input internally and only emits `left_clicked` / `right_clicked` for the
## modes that require panel dispatch (inspect, brush).

signal left_clicked(tile: Vector2i)
signal right_clicked(tile: Vector2i)

const MODE_INSPECT := 0
const MODE_PAINT := 1
const MODE_ERASE := 2
const MODE_BRUSH := 3

const TILE_PX := 1.0
const DEFAULT_ZOOM := 12.0
const ZOOM_MIN := 3.0
const ZOOM_MAX := 40.0
const ZOOM_STEP := 1.15
const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)

const TILE_WALK_A := Color(0.42, 0.58, 0.36)
const TILE_WALK_B := Color(0.36, 0.52, 0.30)
const TILE_BLOCKED := Color(0.65, 0.22, 0.22)
const TILE_HOVER := Color(1, 1, 1, 0.30)
const TILE_SELECTED_BORDER := Color(1.0, 0.82, 0.30, 1.0)
const NPC_MARKER := Color(0.30, 0.55, 1.0, 1.0)
const NPC_MARKER_SELECTED := Color(1.0, 0.82, 0.30, 1.0)
const NPC_MARKER_PENDING := Color(0.55, 0.85, 0.55, 1.0)
const NPC_MARKER_DELETING := Color(0.95, 0.35, 0.35, 1.0)

const MARKER_RADIUS_BASE := 3.0
const BORDER_WIDTH := 2.0

var _camera_pos := Vector2.ZERO
var _zoom := DEFAULT_ZOOM
var _panning := false
var _pan_last := Vector2.ZERO

var _hovered_tile := INVALID_TILE
var _selected_tile := INVALID_TILE
var _selected_npc_id := 0

var _mode := MODE_INSPECT
var _z_value := 100
var _walkable := true
var _is_painting := false
var _last_painted_tile := INVALID_TILE

var _instances: Dictionary = {}
var _pending_creates: Array = []
var _pending_deletes: Array = []


func _init() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	clip_contents = true
	mouse_filter = Control.MOUSE_FILTER_STOP
	mouse_default_cursor_shape = Control.CURSOR_CROSS


# ---------- Public API ----------

func set_mode(mode: int) -> void:
	_mode = mode

func set_paint_tool(z: int, walkable: bool) -> void:
	_z_value = z
	_walkable = walkable

func set_selection(tile: Vector2i, npc_id: int) -> void:
	_selected_tile = tile
	_selected_npc_id = npc_id
	queue_redraw()

func set_instances(instances: Dictionary) -> void:
	_instances = instances
	queue_redraw()

func set_pending_creates(creates: Array) -> void:
	_pending_creates = creates
	queue_redraw()

func set_pending_deletes(deletes: Array) -> void:
	_pending_deletes = deletes
	queue_redraw()


# ---------- Input ----------

func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_panning = mb.pressed
			_pan_last = mb.position
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_zoom_at_mouse(mb.position, ZOOM_STEP)
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_zoom_at_mouse(mb.position, 1.0 / ZOOM_STEP)
			return
		if mb.button_index == MOUSE_BUTTON_LEFT:
			if mb.pressed:
				if _mode == MODE_PAINT or _mode == MODE_ERASE:
					_is_painting = true
					_last_painted_tile = _mouse_to_tile(mb.position)
					_paint_or_erase(_last_painted_tile)
				else:
					left_clicked.emit(_mouse_to_tile(mb.position))
			else:
				_is_painting = false
				_last_painted_tile = INVALID_TILE
			return
		if mb.button_index == MOUSE_BUTTON_RIGHT and mb.pressed:
			right_clicked.emit(_mouse_to_tile(mb.position))
			return
	elif event is InputEventMouseMotion:
		var mm := event as InputEventMouseMotion
		if _panning:
			_camera_pos -= (mm.position - _pan_last) / _zoom
			_pan_last = mm.position
			queue_redraw()
			return
		var t := _mouse_to_tile(mm.position)
		_hovered_tile = t
		if _is_painting and t != _last_painted_tile:
			_last_painted_tile = t
			_paint_or_erase(t)
		queue_redraw()


func _paint_or_erase(tile: Vector2i) -> void:
	if _mode == MODE_PAINT:
		World._tiles[tile] = {"z": _z_value, "flags": {"walkable": _walkable}}
	elif _mode == MODE_ERASE:
		World._tiles.erase(tile)
	queue_redraw()


# ---------- Coord conversion ----------

func _world_to_screen(world: Vector2) -> Vector2:
	return (world - _camera_pos) * _zoom + size * 0.5

func _screen_to_world(screen: Vector2) -> Vector2:
	return (screen - size * 0.5) / _zoom + _camera_pos

func _mouse_to_tile(screen: Vector2) -> Vector2i:
	var w := _screen_to_world(screen)
	return Vector2i(roundi(w.x / TILE_PX), roundi(w.y / TILE_PX))

func _zoom_at_mouse(mouse: Vector2, factor: float) -> void:
	var before := _screen_to_world(mouse)
	_zoom = clampf(_zoom * factor, ZOOM_MIN, ZOOM_MAX)
	var after := _screen_to_world(mouse)
	_camera_pos += before - after
	queue_redraw()


# ---------- Drawing ----------

func _draw() -> void:
	if not World.is_loaded: return
	if size.x <= 0 or size.y <= 0: return

	var half_w := size.x * 0.5 / _zoom
	var half_h := size.y * 0.5 / _zoom
	var min_world := _camera_pos - Vector2(half_w, half_h)
	var max_world := _camera_pos + Vector2(half_w, half_h)
	var min_tile := Vector2i(floori(min_world.x / TILE_PX) - 1, floori(min_world.y / TILE_PX) - 1)
	var max_tile := Vector2i(ceili(max_world.x / TILE_PX) + 1, ceili(max_world.y / TILE_PX) + 1)
	var tile_px_screen := TILE_PX * _zoom

	for x in range(min_tile.x, max_tile.x + 1):
		for y in range(min_tile.y, max_tile.y + 1):
			var key := Vector2i(x, y)
			if not World._tiles.has(key): continue
			var t: Dictionary = World._tiles[key]
			var walkable: bool = bool(t.get("flags", {}).get("walkable", true))
			var z: int = int(t.get("z", World.default_z))
			var color := _tile_color(z, walkable, (x + y) % 2 == 0)
			var screen := _world_to_screen(Vector2(x * TILE_PX, y * TILE_PX))
			var rect := Rect2(screen - Vector2(tile_px_screen, tile_px_screen) * 0.5,
				Vector2(tile_px_screen, tile_px_screen))
			draw_rect(rect, color, true)

	if _hovered_tile != INVALID_TILE:
		var hs := _world_to_screen(Vector2(_hovered_tile.x * TILE_PX, _hovered_tile.y * TILE_PX))
		var hrect := Rect2(hs - Vector2(tile_px_screen, tile_px_screen) * 0.5,
			Vector2(tile_px_screen, tile_px_screen))
		draw_rect(hrect, TILE_HOVER, true)

	if _selected_tile != INVALID_TILE:
		var ss := _world_to_screen(Vector2(_selected_tile.x * TILE_PX, _selected_tile.y * TILE_PX))
		var srect := Rect2(ss - Vector2(tile_px_screen, tile_px_screen) * 0.5,
			Vector2(tile_px_screen, tile_px_screen))
		draw_rect(srect, TILE_SELECTED_BORDER, false, BORDER_WIDTH)

	var marker_radius := maxf(MARKER_RADIUS_BASE, _zoom * 0.35)
	for id in _instances:
		var inst: Dictionary = _instances[id]
		var wx := int(inst.get("x", 0))
		var wy := int(inst.get("y", 0))
		if wx < min_tile.x or wx > max_tile.x or wy < min_tile.y or wy > max_tile.y: continue
		var pos := _world_to_screen(Vector2(wx * TILE_PX, wy * TILE_PX))
		var c: Color
		if id in _pending_deletes:
			c = NPC_MARKER_DELETING
		elif int(id) == _selected_npc_id:
			c = NPC_MARKER_SELECTED
		else:
			c = NPC_MARKER
		draw_circle(pos, marker_radius, c)

	for pc in _pending_creates:
		var wx := int(pc["x"])
		var wy := int(pc["y"])
		if wx < min_tile.x or wx > max_tile.x or wy < min_tile.y or wy > max_tile.y: continue
		var pos := _world_to_screen(Vector2(wx * TILE_PX, wy * TILE_PX))
		draw_circle(pos, marker_radius, NPC_MARKER_PENDING)


func _tile_color(z: int, walkable: bool, alt: bool) -> Color:
	if not walkable: return TILE_BLOCKED
	var base := TILE_WALK_A if alt else TILE_WALK_B
	var t := clampf((float(z) - (float(World.default_z) - 20.0)) / 40.0, 0.0, 1.0)
	var factor := 0.35 + t * 0.65
	return base.darkened(1.0 - factor)