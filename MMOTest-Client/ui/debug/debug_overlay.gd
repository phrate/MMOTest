extends CanvasLayer
## Toggleable debug HUD. F3 to hide/show. No-op in release builds.
## Reads from Network, Session, World, and the local player.

const PING_INTERVAL_S := 1.0
const UI_INTERVAL_S := 0.15

@onready var _label: Label = $Panel/Label

var _ping_timer := 0.0
var _ui_timer := 0.0


func _ready() -> void:
	if not OS.is_debug_build():
		queue_free()
		return
	_label.add_theme_font_size_override("font_size", 12)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_F3:
			visible = not visible
			get_viewport().set_input_as_handled()


func _process(delta: float) -> void:
	if not visible: return

	_ping_timer += delta
	if _ping_timer >= PING_INTERVAL_S:
		_ping_timer = 0.0
		Network.send_ping()

	_ui_timer += delta
	if _ui_timer >= UI_INTERVAL_S:
		_ui_timer = 0.0
		_refresh()


func _refresh() -> void:
	var lines: Array[String] = []
	lines.append("FPS %d   Ping %d ms   Tick %d" % [
		Engine.get_frames_per_second(), Network.rtt_ms, Network.server_tick])
	lines.append("Net %s   Session %s" % [
		Network.State.keys()[Network.state],
		Session.username if Session.is_logged_in() else "(none)"])

	var player := get_tree().get_first_node_in_group("local_player")
	if player != null and player.has_method("get_debug_info"):
		var info: Dictionary = player.get_debug_info()
		var ct: Vector2i = info.current_tile
		var tt: Vector2i = info.target_tile
		lines.append("Tile (%d,%d) z=%d   Target (%d,%d) z=%d" % [
			ct.x, ct.y, info.current_z, tt.x, tt.y, info.target_z])
		lines.append("Path %s len=%d rid=%d" % [
			info.state, info.path_length, info.request_id])

	lines.append("World v%d, %d tiles, %d chunks" % [
		World.world_version, World.tile_count(), World.chunk_count()])

	_label.text = "\n".join(lines)
