class_name StatusBar
extends HBoxContainer
## Shows connection / login state and the Connect-Disconnect button.

@onready var status_label: Label = %StatusLabel
@onready var connect_button: Button = %ConnectButton


func _ready() -> void:
	connect_button.pressed.connect(_on_connect_pressed)
	Network.state_changed.connect(func(_state: int) -> void: _refresh())
	Session.changed.connect(_refresh)
	_refresh()


func _on_connect_pressed() -> void:
	if Network.state == Network.State.CONNECTED:
		Network.disconnect_from_server()
	else:
		Network.connect_to_server()


func _refresh() -> void:
	match Network.state:
		Network.State.DISCONNECTED:
			status_label.text = Texts.get_text("ui.status_bar.disconnected")
			connect_button.text = Texts.get_text("ui.status_bar.button_connect", {"host": Network.HOST, "port": Network.PORT})
			connect_button.disabled = false
		Network.State.CONNECTING:
			status_label.text = Texts.get_text("ui.status_bar.connecting")
			connect_button.text = Texts.get_text("ui.status_bar.button_connecting")
			connect_button.disabled = true
		Network.State.CONNECTED:
			if Session.is_logged_in():
				status_label.text = Texts.get_text("ui.status_bar.connected_logged_in", {"username": Session.username})
			else:
				status_label.text = Texts.get_text("ui.status_bar.connected")
			connect_button.text = Texts.get_text("ui.status_bar.button_disconnect")
			connect_button.disabled = false
