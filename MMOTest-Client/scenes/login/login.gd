extends Control
## Main screen: just wires the UI pieces to Network and shows messages in the log.

# TEMP-DEV: connect and log in automatically on startup. Set to false or delete the marked lines to remove.
const DEV_AUTO_LOGIN := true

@onready var tabs: TabContainer = $Margin/VBox/Tabs
@onready var login_form: LoginForm = $Margin/VBox/Tabs/Login
@onready var register_form: RegisterForm = $Margin/VBox/Tabs/Register
@onready var log_panel: LogPanel = $Margin/VBox/LogPanel


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


func _on_state_changed(state: int) -> void:
	match state:
		Network.State.CONNECTING:
			#log_panel.info(Texts.get_text("network.connection.connecting", {"host": Network.HOST, "port": Network.PORT}))
			pass
		Network.State.CONNECTED:
			#log_panel.info(Texts.get_text("network.connection.connected"))
			if DEV_AUTO_LOGIN and OS.is_debug_build():  # TEMP-DEV
				#log_panel.info(Texts.get_text("account.dev.auto_login_requested"))
				Network.dev_auto_login()


func _on_hello(sqlite_version: String, total_players: int) -> void:
	#log_panel.success(Texts.get_text("network.database.check_ok", {
		#"sqlite_version": sqlite_version, "total_players": total_players}))
	pass


func _on_register_result(ok: bool, username: String, error: String) -> void:
	if ok:
		log_panel.success(Texts.get_text("account.register.success", {"username": username}))
	else:
		log_panel.error(Texts.get_text("account.register.failed", {"error": error}))


func _on_registered(username: String) -> void:
	tabs.current_tab = login_form.get_index()
	login_form.set_username(username)
	login_form.focus_password()


func _on_login_result(ok: bool, data: Dictionary) -> void:
	if ok:
		#var key := "account.login.welcome_admin" if data.get("is_admin", false) else "account.login.welcome"
		#log_panel.success(Texts.get_text(key, data))
		load_game()
	else:
		log_panel.error(Texts.get_text("account.login.failed", {"error": data["error"]}))
