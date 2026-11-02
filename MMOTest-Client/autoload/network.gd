extends Node
## Autoload "Network": the client's only connection to the server.

enum State { DISCONNECTED, CONNECTING, CONNECTED }

signal state_changed(state: int)
signal disconnected(reason: String)
signal hello_received(sqlite_version: String, total_players: int)
signal register_result(ok: bool, username: String, error: String)
signal login_result(ok: bool, data: Dictionary)

signal world_state_received(players: Array)
signal player_joined(player_id: int, username: String, is_admin: bool, x: float, y: float, z: float, yaw: float)
signal player_left(player_id: int)
signal player_moved(player_id: int, x: float, y: float, z: float, yaw: float)
signal self_position_updated(x: float, y: float, z: float, yaw: float)

signal world_info_received(server_version: int, server_format: int)
signal world_data_received(data: Dictionary)
signal world_error(reason: String)
signal world_updated(server_version: int, server_format: int)

const HOST := "127.0.0.1"
const PORT := 9999
const SERVER_ID := 1

var state: State = State.DISCONNECTED

var _mp: SceneMultiplayer


func _ready() -> void:
	_mp = SceneMultiplayer.new()
	get_tree().set_multiplayer(_mp)
	_mp.connected_to_server.connect(_on_connected)
	_mp.connection_failed.connect(_on_connection_failed)
	_mp.server_disconnected.connect(_on_server_disconnected)
	_mp.peer_packet.connect(_on_packet)
	register_result.connect(_on_register_result_debug)  # TEMP-DEV
	login_result.connect(_on_login_result_debug)        # TEMP-DEV


# ---------- Public API ----------
func connect_to_server() -> void:
	if state != State.DISCONNECTED:
		return
	var peer := ENetMultiplayerPeer.new()
	var err := peer.create_client(HOST, PORT)
	if err != OK:
		disconnected.emit(Texts.get_text("network.connection.client_create_failed", {"error": error_string(err)}))
		return
	_mp.multiplayer_peer = peer
	_set_state(State.CONNECTING)


func disconnect_from_server() -> void:
	if state == State.DISCONNECTED:
		return
	_mp.multiplayer_peer.close()
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.disconnected"))
	get_tree().change_scene_to_file("res://scenes/login/login.tscn")


## Same as disconnect_from_server() but does NOT change scene. Used by the
## world-update hotfix, which intends to immediately reconnect.
func disconnect_from_server_quiet() -> void:
	if state == State.DISCONNECTED:
		return
	_mp.multiplayer_peer.close()
	_set_state(State.DISCONNECTED)


func register_account(username: String, password: String) -> void:
	send({"type": "register", "username": username, "password": password})


func login(username: String, password: String, reason: String = "") -> void:
	var msg := {"type": "login", "username": username, "password": password}
	if not reason.is_empty():
		msg["reason"] = reason
	send(msg)


func dev_auto_login() -> void:
	send({"type": "dev_auto_login"})


func request_world_info() -> void:
	send({"type": "world_info"})


func request_world_data() -> void:
	send({"type": "world_fetch"})


## Sends the full A* path to the server. Server walks the player along it.
## path: Array[Vector2i] (excludes the player's current tile).
func send_walk_path(path: Array) -> void:
	var arr: Array = []
	for t in path:
		arr.append([t.x, t.y])
	send({"type": "player_walk", "path": arr})


func send(data: Dictionary, channel: int = 0,
		mode: MultiplayerPeer.TransferMode = MultiplayerPeer.TRANSFER_MODE_RELIABLE) -> void:
	if state != State.CONNECTED:
		push_warning("Network.send() called while not connected.")
		return
	var bytes := JSON.stringify(data).to_utf8_buffer()
	_mp.send_bytes(bytes, SERVER_ID, mode, channel)


func _set_state(new_state: State) -> void:
	state = new_state
	state_changed.emit(state)


func _on_connected() -> void:
	_set_state(State.CONNECTED)
	send({"type": "hello"})


func _on_connection_failed() -> void:
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.failed"))


func _on_server_disconnected() -> void:
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.server_disconnected"))


func _on_packet(_id: int, packet: PackedByteArray) -> void:
	var parsed: Variant = JSON.parse_string(packet.get_string_from_utf8())
	if typeof(parsed) != TYPE_DICTIONARY:
		push_warning("Received malformed packet.")
		return
	var msg: Dictionary = parsed

	match msg.get("type", ""):
		"hello_ok":
			hello_received.emit(msg["sqlite_version"], int(msg["total_players"]))
		"register_result":
			register_result.emit(msg["ok"], msg.get("username", ""), _error_text(msg))
		"login_result":
			if not msg["ok"]:
				msg["error"] = _error_text(msg)
			login_result.emit(msg["ok"], msg)

		"world_state":
			var players: Variant = msg.get("players", [])
			if typeof(players) == TYPE_ARRAY:
				world_state_received.emit(players)
			else:
				push_warning("world_state missing 'players' array.")
		"player_joined":
			player_joined.emit(
				int(msg["player_id"]),
				String(msg.get("username", "")),
				bool(msg.get("is_admin", false)),
				float(msg.get("x", 0.0)),
				float(msg.get("y", 0.0)),
				float(msg.get("z", 0.0)),
				float(msg.get("yaw", 0.0)))
		"player_left":
			player_left.emit(int(msg["player_id"]))

		"tick_positions":
			var updates: Variant = msg.get("updates", [])
			if typeof(updates) != TYPE_ARRAY:
				push_warning("tick_positions missing 'updates' array.")
				return
			for u in updates:
				if typeof(u) != TYPE_DICTIONARY:
					continue
				var pid := int(u.get("player_id", 0))
				var ux := float(u.get("x", 0.0))
				var uy := float(u.get("y", 0.0))
				var uz := float(u.get("z", 0.0))
				var uyaw := float(u.get("yaw", 0.0))
				if pid == Session.player_id:
					self_position_updated.emit(ux, uy, uz, uyaw)
				else:
					player_moved.emit(pid, ux, uy, uz, uyaw)

		"world_info_result":
			if msg.get("ok", false):
				world_info_received.emit(
					int(msg.get("world_version", 0)),
					int(msg.get("format_version", 0)))
			else:
				world_error.emit(_error_text(msg))
		"world_data":
			if msg.get("ok", false):
				var data: Variant = msg.get("data", {})
				if typeof(data) == TYPE_DICTIONARY:
					world_data_received.emit(data)
				else:
					push_warning("world_data: 'data' is not an object.")
					world_error.emit(Texts.get_text("world.error.bad_data"))
			else:
				world_error.emit(_error_text(msg))

		"world_updated":
			print("[Network] world_updated received: v%d" % int(msg.get("world_version", 0)))
			world_updated.emit(
				int(msg.get("world_version", 0)),
				int(msg.get("format_version", 0)))

		_:
			push_warning("Unknown message from server: %s" % str(msg))


func _error_text(msg: Dictionary) -> String:
	var key: String = msg.get("error_key", "")
	if key.is_empty():
		return ""
	return Texts.get_text(key, msg.get("error_params", {}))


# ============================================================================
# TEMP-DEV: auto-register
# ============================================================================
var _debug_flow_active := false
var _debug_username := ""
var _debug_password := ""

func is_debug_flow_active() -> bool: return _debug_flow_active

func debug_register_and_login() -> void:
	if not OS.is_debug_build() or state != State.CONNECTED or _debug_flow_active: return
	_debug_username = _debug_random_username()
	_debug_password = _debug_random_password()
	_debug_flow_active = true
	print("[DEV] auto-registering as '%s'" % _debug_username)
	register_account(_debug_username, _debug_password)

func _on_register_result_debug(ok: bool, _username: String, error: String) -> void:
	if not _debug_flow_active: return
	if not ok:
		_debug_flow_active = false
		push_warning("[DEV] auto-register failed: " + error)
		return
	login(_debug_username, _debug_password)

func _on_login_result_debug(ok: bool, data: Dictionary) -> void:
	if not _debug_flow_active: return
	_debug_flow_active = false
	if not ok:
		push_warning("[DEV] auto-login failed: " + String(data.get("error", "?")))
		return
	Session.cache_password(_debug_password)
	print("[DEV] logged in as '%s' (id %d)" % [Session.username, Session.player_id])

func _debug_random_username() -> String:
	const CHARS := "abcdefghijklmnopqrstuvwxyz0123456789"
	var s := ""
	for i in randi_range(8, 12): s += CHARS[randi() % CHARS.length()]
	return s

func _debug_random_password() -> String:
	const CHARS := "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	var s := ""
	for i in 20: s += CHARS[randi() % CHARS.length()]
	return s
