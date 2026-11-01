class_name LoginForm
extends VBoxContainer
## Login tab. Validates locally, then asks Network to log in.

signal validation_failed(message: String)

@onready var username_edit: LineEdit = %UsernameEdit
@onready var password_edit: LineEdit = %PasswordEdit
@onready var login_button: Button = %LoginButton


func _ready() -> void:
	username_edit.placeholder_text = Texts.get_text("account.login.username_placeholder")
	password_edit.placeholder_text = Texts.get_text("account.login.password_placeholder")
	login_button.text = Texts.get_text("account.login.button")

	login_button.pressed.connect(_submit)
	password_edit.text_submitted.connect(func(_text: String) -> void: _submit())
	Network.state_changed.connect(_on_state_changed)
	Network.login_result.connect(func(_ok: bool, _data: Dictionary) -> void: password_edit.clear())
	_on_state_changed(Network.state)


func set_username(value: String) -> void:
	username_edit.text = value


func focus_password() -> void:
	password_edit.grab_focus()


func _submit() -> void:
	var username := username_edit.text.strip_edges()
	var password := password_edit.text
	if username.is_empty() or password.is_empty():
		validation_failed.emit(Texts.get_text("account.login.validation.missing_fields"))
		return
	Network.login(username, password)


func _on_state_changed(state: int) -> void:
	var enabled: bool = state == Network.State.CONNECTED
	username_edit.editable = enabled
	password_edit.editable = enabled
	login_button.disabled = not enabled
