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
	Network.server_shutdown.connect(_on_server_shutdown)


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
		if World.sync_completed.is_connected(_on_hotfix_sync_completed):
			World.sync_completed.disconnect(_on_hotfix_sync_completed)
		if World.sync_failed.is_connected(_on_hotfix_sync_failed):
			World.sync_failed.disconnect(_on_hotfix_sync_failed)
		World.sync_completed.connect(_on_hotfix_sync_completed, CONNECT_ONE_SHOT)
		World.sync_failed.connect(_on_hotfix_sync_failed, CONNECT_ONE_SHOT)
		World.force_sync()


func _on_state_changed(state: int) -> void:
	if state != Network.State.DISCONNECTED:
		return
	if _hotfix_in_progress:
		return
	if not is_logged_in():
		return

	# User clicked Disconnect (status_bar button). Respect it — no auto-reconnect.
	if Network.was_intentional_disconnect():
		_reset_state_for_hotfix()
		_cached_password = ""
		return

	# Unexpected drop: shutdown message never arrived (server killed, crashed, or
	# stopped from the editor — the editor's Stop button doesn't fire WM_CLOSE_REQUEST).
	if _cached_password.is_empty():
		print("[Reconnect] Unexpected disconnect, no cached password; returning to login")
		Network.disconnect_from_server()
		return

	print("[Reconnect] Unexpected disconnect; attempting auto-reconnect")
	_hotfix_in_progress = true
	var saved_username := username
	var saved_password := _cached_password
	_reset_state_for_hotfix()
	Network.disconnect_from_server_quiet()
	_reconnect_and_login(saved_username, saved_password, "unexpected_disconnect")


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
	_reconnect_and_login(saved_username, saved_password, "world_update")


func _on_server_shutdown(reason: String, reconnect_after_ms: int) -> void:
	print("[Shutdown] Server: ", reason, " (reconnect in ", reconnect_after_ms, "ms)")
	if _hotfix_in_progress: return
	if not is_logged_in(): return
	if _cached_password.is_empty():
		Network.disconnect_from_server()
		return

	_hotfix_in_progress = true
	var saved_username := username
	var saved_password := _cached_password
	_reset_state_for_hotfix()
	Network.disconnect_from_server_quiet()
	await get_tree().create_timer(reconnect_after_ms / 1000.0).timeout
	_reconnect_and_login(saved_username, saved_password, "server_restart")


## Shared reconnect+login path. Retries connect if the server isn't listening yet
## (e.g. it's still restarting). If CONNECTED, sends login and returns — the rest
## of the flow continues in _on_login_result.
func _reconnect_and_login(username_: String, password: String, reason: String) -> void:
	const MAX_ATTEMPTS := 20
	const ATTEMPT_TIMEOUT_MS := 3000
	const RETRY_DELAY_S := 0.5

	for attempt in MAX_ATTEMPTS:
		Network.connect_to_server()
		if Network.state != Network.State.CONNECTING:
			await get_tree().create_timer(RETRY_DELAY_S).timeout
			continue

		var start_ms := Time.get_ticks_msec()
		while Network.state == Network.State.CONNECTING and Time.get_ticks_msec() - start_ms < ATTEMPT_TIMEOUT_MS:
			await get_tree().process_frame

		if Network.state == Network.State.CONNECTED:
			print("[Reconnect] Connected (attempt ", attempt + 1, "); sending login")
			Network.login(username_, password, reason)
			return

		await get_tree().create_timer(RETRY_DELAY_S).timeout

	_hotfix_in_progress = false
	push_warning("[Reconnect] Gave up after ", MAX_ATTEMPTS, " attempts")
	Network.disconnect_from_server()


func _reset_state_for_hotfix() -> void:
	player_id = 0
	username = ""
	login_count = 0
	is_admin = false
	spawn_position = Vector3.ZERO
	spawn_yaw = 0.0
	changed.emit()


func _on_hotfix_sync_completed() -> void:
	print("[Hotfix] World sync complete; requesting tooltips")
	if Gfs.sync_completed.is_connected(_on_hotfix_tooltips_completed):
		Gfs.sync_completed.disconnect(_on_hotfix_tooltips_completed)
	if Gfs.sync_failed.is_connected(_on_hotfix_tooltips_failed):
		Gfs.sync_failed.disconnect(_on_hotfix_tooltips_failed)
	Gfs.sync_completed.connect(_on_hotfix_tooltips_completed, CONNECT_ONE_SHOT)
	Gfs.sync_failed.connect(_on_hotfix_tooltips_failed, CONNECT_ONE_SHOT)
	Gfs.request_sync()


func _on_hotfix_tooltips_completed() -> void:
	print("[Hotfix] Tooltips sync complete; reloading scene")
	_hotfix_in_progress = false
	get_tree().reload_current_scene()


func _on_hotfix_tooltips_failed(reason: String) -> void:
	push_warning("[Hotfix] Tooltips sync failed: " + reason + " (continuing)")
	_hotfix_in_progress = false
	get_tree().reload_current_scene()


func _on_hotfix_sync_failed(reason: String) -> void:
	_hotfix_in_progress = false
	push_warning("[Hotfix] World sync failed: " + reason)
	Network.disconnect_from_server()
