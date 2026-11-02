extends MeshInstance3D
## Highlights the walkable tile under the mouse with a semi-transparent plane.
## Ray-marches through the world using the same code path as click-to-move
## (player.get_tile_under_mouse), so hover highlight and click target always
## agree. Hides when the mouse is over UI or no walkable tile is under it.

const LIFT := 0.02                   # above the tile surface; avoids z-fighting
const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)

@export var player: Node3D

var _last_shown_tile := INVALID_TILE


func _ready() -> void:
	visible = false
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF


func _process(_delta: float) -> void:
	if player == null:
		player = get_tree().get_first_node_in_group("local_player")
		if player == null:
			visible = false
			return

	# Mouse over UI (chat box, panel, button) — no highlight.
	if get_viewport().gui_get_hovered_control() != null:
		visible = false
		_last_shown_tile = INVALID_TILE
		return

	var tile: Vector2i = player.get_tile_under_mouse()
	if tile == INVALID_TILE:
		visible = false
		_last_shown_tile = INVALID_TILE
		return
	if not World.is_walkable(tile.x, tile.y):
		visible = false
		_last_shown_tile = INVALID_TILE
		return

	if tile != _last_shown_tile:
		_last_shown_tile = tile
		var z := float(World.get_z(tile.x, tile.y))
		global_position = Vector3(float(tile.x), z + LIFT, float(tile.y))
	visible = true
