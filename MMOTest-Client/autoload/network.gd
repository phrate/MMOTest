extends Node
## Autoload "Network": the client's only connection to the server (and, through it, the database).
## Owns the ENet connection, sends requests, and turns server JSON replies into signals.
## UI scripts never touch the peer directly; they call these methods and listen to these signals.
##
## Server failures arrive as an error key + params; they are turned into display text here via Texts,
## so the "error"/"reason" strings emitted by the signals below are already ready to show.

enum State { DISCONNECTED, CONNECTING, CONNECTED }

signal state_changed(state: int)           # a Network.State value
signal disconnected(reason: String)        # manual disconnect, failed attempt, or lost connection (display text)
signal hello_received(sqlite_version: String, total_players: int)
signal register_result(ok: bool, username: String, error: String)
signal login_result(ok: bool, data: Dictionary)   # on failure, data["error"] holds the display text

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


func register_account(username: String, password: String) -> void:
	send({"type": "register", "username": username, "password": password})


func login(username: String, password: String) -> void:
	send({"type": "login", "username": username, "password": password})


## TEMP-DEV: asks the server to log us in without a password (server must allow it).
func dev_auto_login() -> void:
	send({"type": "dev_auto_login"})


## Generic sender. Add new request helpers above and have them call this.
func send(data: Dictionary) -> void:
	if state != State.CONNECTED:
		push_warning("Network.send() called while not connected.")
		return
	var bytes := JSON.stringify(data).to_utf8_buffer()
	_mp.send_bytes(bytes, SERVER_ID, MultiplayerPeer.TRANSFER_MODE_RELIABLE, 0)


# ---------- Connection events ----------
func _set_state(new_state: State) -> void:
	state = new_state
	state_changed.emit(state)


func _on_connected() -> void:
	_set_state(State.CONNECTED)
	send({"type": "hello"})  # asks the server to confirm its database is reachable


func _on_connection_failed() -> void:
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.failed"))


func _on_server_disconnected() -> void:
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.server_disconnected"))


# ---------- Incoming messages ----------
## Add a new server message type by adding a signal above and a branch here.
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
		_:
			push_warning("Unknown message from server: %s" % str(msg))


## Server errors are {"error_key": "...", "error_params": {...}}. Resolve them to display text.
func _error_text(msg: Dictionary) -> String:
	var key: String = msg.get("error_key", "")
	if key.is_empty():
		return ""
	return Texts.get_text(key, msg.get("error_params", {}))
