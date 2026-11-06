extends Node
## Autoload "Network": the client's only connection to the server.

enum State { DISCONNECTED, CONNECTING, CONNECTED }

signal state_changed(state: int)
signal disconnected(reason: String)
signal hello_received(sqlite_version: String, total_players: int)
signal register_result(ok: bool, username: String, error: String)
signal login_result(ok: bool, data: Dictionary)

signal world_state_received(players: Array)
signal player_joined(player_id: int, username: String, is_admin: bool, x: float, y: float, z: float, yaw: float, sid: int)
signal player_left(player_id: int)
signal player_moved(player_id: int, x: float, y: float, z: float, yaw: float)
signal self_position_updated(x: float, y: float, z: float, yaw: float)
signal player_hp_updated(player_id: int, hitpoints: int, max_hitpoints: int)
signal player_combat_state(player_id: int, in_combat: bool, npc_id: int)
signal player_respawned(x: float, y: float, z: float, yaw: float, hitpoints: int, max_hitpoints: int)
signal inventory_data_received(slots: Array)

signal world_info_received(server_version: int, server_format: int)
signal world_data_received(data: Dictionary)
signal world_error(reason: String)
signal world_updated(server_version: int, server_format: int)

signal tooltips_info_received(version: int, format_version: int, entries: Dictionary, scripts: Dictionary)

signal pong_received(rtt_ms: int) 
signal server_shutdown(reason: String, reconnect_after_ms: int) 

signal chat_received(username: String, message: String, from_chunk: Vector2i)
signal overhead_received(username: String, text: String, from_chunk: Vector2i)
signal private_message_received(text: String)

signal gfs_info_received(version: int, entries: Dictionary, scripts: Dictionary)
signal gfs_updated(version: int, scripts: Dictionary)
signal run_action_result(ok: bool, effects: Array)

signal anim_changed_received(player_id: int, anim: String, duration_ms: int)

signal npc_joined(npcs: Array)
signal npc_left(ids: Array)
signal npc_render_info_received(slugs: Dictionary)
signal npc_render_data_received(slug: String, hash: String, files: Dictionary)
signal npc_render_updated_received(slugs: Dictionary)
signal npc_moved(npc_id: int, x: float, y: float, z: float, yaw: float)
signal npc_anim_changed(npc_id: int, anim: String, duration_ms: int)
signal npc_hp_updated(npc_id: int, hitpoints: int, max_hitpoints: int)
signal damage_number(target_type: String, target_id: int, amount: int)
signal npc_updated(npc: Dictionary)
signal npc_combat_state(npc_id: int, in_combat: bool)
signal npc_editor_open(npc_id: int)
signal npc_editor_data(npc: Dictionary)
signal npc_editor_saved(ok: bool, npc_id: int)

signal ground_item_dropped(item: Dictionary)
signal ground_item_removed(id: int)

signal credits_updated(credits: int)
signal skills_data_received(skills: Array)

const HOST := "127.0.0.1"
const PORT := 9999
const SERVER_ID := 1

var state: State = State.DISCONNECTED
var rtt_ms: int = 0          # NEW: last measured round-trip
var server_tick: int = 0     # NEW: last tick_positions.tick

var _mp: SceneMultiplayer

var _intentional_disconnect := false


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


func was_intentional_disconnect() -> bool:
	return _intentional_disconnect


func disconnect_from_server() -> void:
	if state == State.DISCONNECTED:
		return
	_intentional_disconnect = true
	_mp.multiplayer_peer.close()
	_set_state(State.DISCONNECTED)
	disconnected.emit(Texts.get_text("network.connection.disconnected"))
	get_tree().change_scene_to_file("res://scenes/login/login.tscn")
	_intentional_disconnect = false


func disconnect_from_server_quiet() -> void:
	if state == State.DISCONNECTED:
		return
	_mp.multiplayer_peer.close()
	_set_state(State.DISCONNECTED)


func register_account(username: String, password: String, is_dev: bool = false) -> void:
	var msg := {"type": "register", "username": username, "password": password}
	if is_dev:
		msg["is_dev"] = true
	send(msg)


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


func request_gfs_info() -> void:
	
	send({"type": "gfs_info"})


func request_npc_render_info() -> void:
	send({"type": "npc_render_info_request"})


func request_npc_render_fetch(slugs: Array) -> void:
	send({"type": "npc_render_fetch", "slugs": slugs})


func run_action(script_path: String, args: Dictionary, target_id: int) -> void:
	send({"type": "run_action", "script": script_path, "args": args, "target_id": target_id})


func send_walk_path(path: Array) -> void:
	var arr: Array = []
	for t in path:
		arr.append([t.x, t.y])
	send({"type": "player_walk", "path": arr})


func send_npc_editor_get(npc_id: int) -> void:
	send({"type": "npc_editor_get", "npc_id": npc_id})


func send_npc_editor_save(npc_id: int, updates: Dictionary) -> void:
	send({"type": "npc_editor_save", "npc_id": npc_id, "updates": updates})


func send_chat(message: String) -> void:
	send({"type": "chat_send", "message": message})


## NEW: ask the server for a pong. Cheap enough to call every second or so.
func send_ping() -> void:
	if state != State.CONNECTED: return
	send({"type": "ping", "t": Time.get_ticks_msec()})


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
				float(msg.get("yaw", 0.0)),
				int(msg.get("sid", 0)))
		"npc_joined":
			var list: Variant = msg.get("npcs", [])
			if typeof(list) == TYPE_ARRAY:
				npc_joined.emit(list)
			else:
				push_warning("npc_joined missing 'npcs' array")
		"npc_left":
			var id_list: Variant = msg.get("ids", [])
			if typeof(id_list) == TYPE_ARRAY:
				npc_left.emit(id_list)
			else:
				push_warning("npc_left missing 'ids' array")
		"npc_tick_positions":
			var updates: Variant = msg.get("updates", [])
			if typeof(updates) != TYPE_ARRAY: return
			for u in updates:
				if typeof(u) != TYPE_DICTIONARY: continue
				npc_moved.emit(
					int(u.get("npc_id", 0)),
					float(u.get("x", 0.0)),
					float(u.get("y", 0.0)),
					float(u.get("z", 0.0)),
					float(u.get("yaw", 0.0)))
		"npc_render_info_response":
			var slugs_d: Variant = msg.get("slugs", {})
			if typeof(slugs_d) == TYPE_DICTIONARY:
				npc_render_info_received.emit(slugs_d)
			else:
				push_warning("npc_render_info_response missing 'slugs'")
		"npc_render_data":
			var slug := str(msg.get("slug", ""))
			var hash := str(msg.get("hash", ""))
			var files_d: Variant = msg.get("files", {})
			if slug.is_empty() or typeof(files_d) != TYPE_DICTIONARY:
				push_warning("npc_render_data malformed")
			else:
				npc_render_data_received.emit(slug, hash, files_d)
		"npc_render_updated":
			var upd_slugs: Variant = msg.get("slugs", {})
			if typeof(upd_slugs) == TYPE_DICTIONARY:
				npc_render_updated_received.emit(upd_slugs)
			else:
				push_warning("npc_render_updated missing 'slugs'")
		"tick_positions":
			server_tick = int(msg.get("tick", 0))
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
		"pong":
			var sent_t := int(msg.get("t", 0))
			if sent_t > 0:
				rtt_ms = Time.get_ticks_msec() - sent_t
				pong_received.emit(rtt_ms)
		"shutdown":
			server_shutdown.emit(
				String(msg.get("reason", "")),
				int(msg.get("reconnect_after_ms", 1000)))
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
		"tooltips_info_result":
			if msg.get("ok", false):
				tooltips_info_received.emit(
					int(msg.get("version", 0)),
					int(msg.get("format_version", 0)),
					msg.get("entries", {}),
					msg.get("scripts", {}))
			else:
				push_warning("tooltips_info failed: ", msg.get("error_key", ""))
		"gfs_info_result":
			if msg.get("ok", false):
				gfs_info_received.emit(
					int(msg.get("version", 0)),
					msg.get("entries", {}),
					msg.get("scripts", {}))
			else:
				push_warning("gfs_info failed: ", msg.get("error_key", ""))
		"gfs_updated":
			gfs_updated.emit(
				int(msg.get("version", 0)),
				msg.get("scripts", {}))
		"run_action_result":
			run_action_result.emit(
				msg.get("ok", false),
				msg.get("effects", []))
		"chat_message":
			var fc: Variant = msg.get("from_chunk", [])
			var cx := 0
			var cy := 0
			if typeof(fc) == TYPE_ARRAY and fc.size() >= 2:
				cx = int(fc[0])
				cy = int(fc[1])
			chat_received.emit(
				String(msg.get("username", "")),
				String(msg.get("message", "")),
				Vector2i(cx, cy))
		"overhead_message":
			var fc: Variant = msg.get("from_chunk", [])
			var cx := 0
			var cy := 0
			if typeof(fc) == TYPE_ARRAY and fc.size() >= 2:
				cx = int(fc[0])
				cy = int(fc[1])
			overhead_received.emit(
				String(msg.get("username", "")),
				String(msg.get("text", "")),
				Vector2i(cx, cy))
		"private_message":
			private_message_received.emit(String(msg.get("text", "")))
		"anim_changed":
			anim_changed_received.emit(
				int(msg.get("player_id", 0)),
				String(msg.get("anim", "")),
				int(msg.get("duration_ms", 0)))
		"npc_anim_changed":
			npc_anim_changed.emit(
				int(msg.get("npc_id", 0)),
				String(msg.get("anim", "")),
				int(msg.get("duration_ms", 0)))
		"player_respawned":
			player_respawned.emit(
				float(msg.get("x", 0.0)),
				float(msg.get("y", 0.0)),
				float(msg.get("z", 0.0)),
				float(msg.get("yaw", 0.0)),
				int(msg.get("hitpoints", 10)),
				int(msg.get("max_hitpoints", 10)))
		"npc_hp_updated":
			npc_hp_updated.emit(
				int(msg.get("npc_id", 0)),
				int(msg.get("hitpoints", 0)),
				int(msg.get("max_hitpoints", 1)))
		"npc_updated":
			var nupd: Variant = msg.get("npc", {})
			if typeof(nupd) == TYPE_DICTIONARY:
				npc_updated.emit(nupd)
		"player_hp_updated":
			player_hp_updated.emit(
				int(msg.get("player_id", 0)),
				int(msg.get("hitpoints", 10)),
				int(msg.get("max_hitpoints", 10)))
		"player_combat_state":
			player_combat_state.emit(
				int(msg.get("player_id", 0)),
				bool(msg.get("in_combat", false)),
				int(msg.get("npc_id", 0)))
		"npc_combat_state":
			npc_combat_state.emit(
				int(msg.get("npc_id", 0)),
				bool(msg.get("in_combat", false)))
		"npc_editor_open":
			npc_editor_open.emit(int(msg.get("npc_id", 0)))
		"npc_editor_data":
			var ndata: Variant = msg.get("npc", {})
			if typeof(ndata) == TYPE_DICTIONARY:
				npc_editor_data.emit(ndata)
		"npc_editor_saved":
			npc_editor_saved.emit(
				bool(msg.get("ok", false)),
				int(msg.get("npc_id", 0)))
		"damage_number":
			damage_number.emit(
				String(msg.get("target_type", "")),
				int(msg.get("target_id", 0)),
				int(msg.get("amount", 0)))
		"inventory_data":
			var inv_slots: Variant = msg.get("slots", [])
			if typeof(inv_slots) == TYPE_ARRAY:
				inventory_data_received.emit(inv_slots)
			else:
				push_warning("inventory_data missing 'slots' array")
		"ground_item_dropped":
			var gi: Variant = msg.get("item", {})
			if typeof(gi) == TYPE_DICTIONARY:
				ground_item_dropped.emit(gi)
			else:
				push_warning("ground_item_dropped missing 'item'")
		"ground_item_removed":
			ground_item_removed.emit(int(msg.get("id", 0)))
		"credits_updated":
			credits_updated.emit(int(msg.get("credits", 0)))
		"skills_data":
			var skills: Variant = msg.get("skills", [])
			if typeof(skills) == TYPE_ARRAY:
				skills_data_received.emit(skills)
			else:
				push_warning("skills_data missing 'skills' array")
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
	register_account(_debug_username, _debug_password, true)


func _on_register_result_debug(ok: bool, _username: String, error: String) -> void:
	print("[DEV] got register_result ok=", ok, " err=", error, " flow_active=", _debug_flow_active)
	if not _debug_flow_active:
		return
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


func send_inventory_swap(slot_a: int, slot_b: int) -> void:
	send({"type": "inventory_swap", "slot_a": slot_a, "slot_b": slot_b})


func send_pickup_ground_item(id: int) -> void:
	send({"type": "pickup_ground_item", "id": id})
