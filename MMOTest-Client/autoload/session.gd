extends Node
## Autoload "Session": who is logged in on this client.

signal changed

var player_id := 0
var username := ""
var login_count := 0
var is_admin := false
var spawn_position := Vector3.ZERO
var spawn_yaw := 0.0

var _cached_password := ""
var _hotfix_in_progress := false


func is_logged_in() -> bool:
	return player_id != 0


func _ready() -> void:
	Network.login_result.connect(_on_login_result)
	Network.state_changed.connect(_on_state_changed)
	Network.world_updated.connect(_on_world_updated)


func cache_password(password: String) -> void:
	_cached_password = password


func _on_login_result(ok: bool, data: Dictionary) -> void:
	if not ok:
		if _hotfix_in_progress:
			_hotfix_in_progress = false
			push_warning("[Hotfix] Re-login failed; falling back to login screen")
			Network.disconnect_from_server()
		return

	player_id = int(data["player_id"])
	username = data["username"]
	login_count = int(data["login_count"])
	is_admin = data.get("is_admin", false)
	if data.has("spawn"):
		var s: Dictionary = data["spawn"]
		spawn_position = Vector3(float(s["x"]), float(s["y"]), float(s["z"]))
		spawn_yaw = float(s["yaw"])
	changed.emit()

	if _hotfix_in_progress:
		print("[Hotfix] Re-login complete, requesting world sync")
		# CONNECT_ONE_SHOT only auto-disconnects the signal that actually fires.
		# After a successful hotfix, sync_failed stays connected forever, so we
		# must detach both explicitly before reconnecting on the next hotfix.
		if World.sync_completed.is_connected(_on_hotfix_sync_completed):
			World.sync_completed.disconnect(_on_hotfix_sync_completed)
		if World.sync_failed.is_connected(_on_hotfix_sync_failed):
			World.sync_failed.disconnect(_on_hotfix_sync_failed)
		World.sync_completed.connect(_on_hotfix_sync_completed, CONNECT_ONE_SHOT)
		World.sync_failed.connect(_on_hotfix_sync_failed, CONNECT_ONE_SHOT)
		World.force_sync()


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and is_logged_in() and not _hotfix_in_progress:
		player_id = 0
		username = ""
		login_count = 0
		is_admin = false
		spawn_position = Vector3.ZERO
		spawn_yaw = 0.0
		_cached_password = ""
		changed.emit()


func _on_world_updated(_server_version: int, _server_format: int) -> void:
	print("[Hotfix] _on_world_updated: in_progress=", _hotfix_in_progress,
		" logged_in=", is_logged_in(), " has_pwd=", not _cached_password.is_empty())
	if _hotfix_in_progress: return
	if not is_logged_in(): return
	if _cached_password.is_empty():
		push_warning("[Hotfix] No cached password; returning to login screen")
		Network.disconnect_from_server()
		return

	print("[Hotfix] Starting hotfix reconnect")
	_hotfix_in_progress = true

	var saved_username := username
	var saved_password := _cached_password
	_reset_state_for_hotfix()

	Network.disconnect_from_server_quiet()
	await get_tree().create_timer(0.1).timeout

	Network.connect_to_server()
	if Network.state != Network.State.CONNECTING:
		_hotfix_in_progress = false
		push_warning("[Hotfix] Failed to start reconnect")
		return

	# Poll for CONNECTED, with a 5s timeout. Do NOT await state_changed: on
	# localhost the connect can complete before we start waiting, which would
	# deadlock waiting for the *next* state change.
	var start_ms := Time.get_ticks_msec()
	while Network.state == Network.State.CONNECTING and Time.get_ticks_msec() - start_ms < 5000:
		await get_tree().process_frame

	if Network.state != Network.State.CONNECTED:
		_hotfix_in_progress = false
		push_warning("[Hotfix] Reconnect failed; falling back to login screen")
		Network.disconnect_from_server()
		return

	print("[Hotfix] Connected; sending re-login")
	Network.login(saved_username, saved_password, "world_update")
	# _hotfix_in_progress stays true until _on_login_result + sync finish.


func _reset_state_for_hotfix() -> void:
	player_id = 0
	username = ""
	login_count = 0
	is_admin = false
	spawn_position = Vector3.ZERO
	spawn_yaw = 0.0
	# _cached_password survives.
	changed.emit()


func _on_hotfix_sync_completed() -> void:
	print("[Hotfix] Sync complete; reloading scene")
	_hotfix_in_progress = false
	get_tree().reload_current_scene()


func _on_hotfix_sync_failed(reason: String) -> void:
	_hotfix_in_progress = false
	push_warning("[Hotfix] World sync failed: " + reason)
	Network.disconnect_from_server()
