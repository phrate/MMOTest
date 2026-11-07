extends Node
## Client mirror of the player's bank.

signal state_changed
signal closed(error_key: String, error_params: Dictionary)
signal notice_received(text: String)

var session_id: String = ""
var capacity: int = 100
var cert_mode: bool = false
var slots: Array = []       # length == capacity; each entry is null or {item_id, quantity}
var insert_mode: bool = false


func is_active() -> bool: return not session_id.is_empty()


func clear() -> void:
	session_id = ""
	slots = []
	cert_mode = false
	insert_mode = false
	state_changed.emit()


func used_slots() -> int:
	var n := 0
	for s in slots:
		if s != null: n += 1
	return n


func _ready() -> void:
	Network.bank_state_received.connect(_on_state)
	Network.bank_closed.connect(_on_closed)
	Network.bank_notice.connect(_on_notice)
	Network.state_changed.connect(_on_network_state)


func _on_state(state: Dictionary) -> void:
	session_id = str(state.get("session_id", ""))
	capacity = int(state.get("capacity", 100))
	cert_mode = bool(state.get("cert_mode", false))
	slots = state.get("slots", [])
	state_changed.emit()


func _on_closed(_sid: String, error_key: String, error_params: Dictionary) -> void:
	clear()
	closed.emit(error_key, error_params)


func _on_notice(error_key: String, error_params: Dictionary) -> void:
	var sub := error_key.trim_prefix("bank.")
	var text := Texts.get_text("game.bank." + sub, error_params)
	Network.emit_system_message(text)
	notice_received.emit(text)


func _on_network_state(state: int) -> void:
	if state == Network.State.DISCONNECTED and is_active():
		clear()
