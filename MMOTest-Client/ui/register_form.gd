class_name RegisterForm
extends VBoxContainer
## Register tab. Validates locally (the server validates again), then asks Network to register.

signal validation_failed(message: String)
signal registered(username: String)

const MIN_PASSWORD_LENGTH := 8

@onready var username_edit: LineEdit = %UsernameEdit
@onready var password_edit: LineEdit = %PasswordEdit
@onready var confirm_edit: LineEdit = %ConfirmEdit
@onready var register_button: Button = %RegisterButton


func _ready() -> void:
	username_edit.placeholder_text = Texts.get_text("account.register.username_placeholder")
	password_edit.placeholder_text = Texts.get_text("account.register.password_placeholder", {"min": MIN_PASSWORD_LENGTH})
	confirm_edit.placeholder_text = Texts.get_text("account.register.confirm_placeholder")
	register_button.text = Texts.get_text("account.register.button")

	register_button.pressed.connect(_submit)
	confirm_edit.text_submitted.connect(func(_text: String) -> void: _submit())
	Network.state_changed.connect(_on_state_changed)
	Network.register_result.connect(_on_register_result)
	_on_state_changed(Network.state)


func _submit() -> void:
	var username := username_edit.text.strip_edges()
	var password := password_edit.text
	if username.is_empty():
		validation_failed.emit(Texts.get_text("account.register.validation.missing_username"))
		return
	if password.length() < MIN_PASSWORD_LENGTH:
		validation_failed.emit(Texts.get_text("account.register.validation.password_too_short", {"min": MIN_PASSWORD_LENGTH}))
		return
	if password != confirm_edit.text:
		validation_failed.emit(Texts.get_text("account.register.validation.password_mismatch"))
		return
	Network.register_account(username, password)


func _on_register_result(ok: bool, username: String, _error: String) -> void:
	if not ok:
		return
	username_edit.clear()
	password_edit.clear()
	confirm_edit.clear()
	registered.emit(username)


func _on_state_changed(state: int) -> void:
	var enabled: bool = state == Network.State.CONNECTED
	username_edit.editable = enabled
	password_edit.editable = enabled
	confirm_edit.editable = enabled
	register_button.disabled = not enabled
