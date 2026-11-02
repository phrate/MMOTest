extends Control
## Main screen: just wires the UI pieces to Network and shows messages in the log.

# TEMP-DEV: connect and log in automatically on startup. Set to false or delete the marked lines to remove.
const DEV_AUTO_LOGIN := true

@onready var tabs: TabContainer = $Margin/VBox/Tabs
@onready var login_form: LoginForm = $Margin/VBox/Tabs/Login
@onready var register_form: RegisterForm = $Margin/VBox/Tabs/Register
@onready var log_panel: LogPanel = $Margin/VBox/LogPanel

var _awaiting_world_sync := false


func _ready() -> void:
	tabs.set_tab_title(login_form.get_index(), Texts.get_text("account.login.tab_title"))
	tabs.set_tab_title(register_form.get_index(), Texts.get_text("account.register.tab_title"))

	login_form.validation_failed.connect(log_panel.warn)
	register_form.validation_failed.connect(log_panel.warn)
	register_form.registered.connect(_on_registered)

	Network.state_changed.connect(_on_state_changed)
	Network.disconnected.connect(log_panel.warn)
	Network.hello_received.connect(_on_hello)
	Network.register_result.connect(_on_register_result)
	Network.login_result.connect(_on_login_result)

	if DEV_AUTO_LOGIN and OS.is_debug_build():  # TEMP-DEV
		Network.connect_to_server()


func load_game() -> void:
	log_panel.info(Texts.get_text("game.common.loading_game"))
	get_tree().change_scene_to_file("res://scenes/game/game.tscn")
"res://scenes/game/game.tscn"

func _on_state_changed(state: int) -> void:
	match state:
		Network.State.CONNECTED:
			if DEV_AUTO_LOGIN and OS.is_debug_build():  # TEMP-DEV
				Network.debug_register_and_login()


func _on_hello(sqlite_version: String, total_players: int) -> void:
	pass


func _on_register_result(ok: bool, username: String, error: String) -> void:
	if ok:
		log_panel.success(Texts.get_text("account.register.success", {"username": username}))
	else:
		log_panel.error(Texts.get_text("account.register.failed", {"error": error}))


func _on_registered(username: String) -> void:
	if Network.is_debug_flow_active():  # TEMP-DEV: no tab switch mid-auto-login
		return
	tabs.current_tab = login_form.get_index()
	login_form.set_username(username)
	login_form.focus_password()


func _on_login_result(ok: bool, data: Dictionary) -> void:
	if not ok:
		log_panel.error(Texts.get_text("account.login.failed", {"error": data["error"]}))
		return
	if _awaiting_world_sync:
		return
	_awaiting_world_sync = true
	World.sync_completed.connect(_on_world_sync_completed, CONNECT_ONE_SHOT)
	World.sync_failed.connect(_on_world_sync_failed, CONNECT_ONE_SHOT)
	World.request_sync()


func _on_world_sync_completed() -> void:
	_awaiting_world_sync = false
	load_game()


func _on_world_sync_failed(reason: String) -> void:
	_awaiting_world_sync = false
	log_panel.error(reason)
