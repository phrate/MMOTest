extends Node
## Autoload "Session": who is logged in on this client. Must be listed after Network in Autoload.

signal changed

var player_id := 0
var username := ""
var login_count := 0
var is_admin := false  # display only; the server is the one that enforces permissions


func is_logged_in() -> bool:
	return player_id != 0


func _ready() -> void:
	Network.login_result.connect(_on_login_result)
	Network.state_changed.connect(_on_state_changed)


func _on_login_result(ok: bool, data: Dictionary) -> void:
	if not ok:
		return
	player_id = int(data["player_id"])
	username = data["username"]
	login_count = int(data["login_count"])
	is_admin = data.get("is_admin", false)
	changed.emit()


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and is_logged_in():
		player_id = 0
		username = ""
		login_count = 0
		is_admin = false
		changed.emit()
