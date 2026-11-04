extends PanelContainer
## Chat display + input.
##
## Ways to start chatting:
##   - Press Enter / Keypad Enter.
##   - Press any printable key (letters, digits, symbols, space).
##   - Click inside the input.
## Chat editing is sandboxed: Ctrl/Cmd+C/V/X are consumed while the input has
## focus, and the LineEdit's right-click context menu is disabled.

const MAX_MESSAGES := 40
const CHAT_CHUNK_RADIUS := 2

@onready var log_label: RichTextLabel = $VBox/Log
@onready var input: LineEdit = $VBox/Input

var _messages: Array[String] = []


func _ready() -> void:
	log_label.mouse_filter = Control.MOUSE_FILTER_STOP
	log_label.scroll_active = true
	log_label.scroll_following = true

	# LineEdit's default right-click menu has Cut / Copy / Paste / Select All / Delete.
	# Disabling the menu removes all of them at once.
	input.context_menu_enabled = false

	log_label.get_v_scroll_bar().value_changed.connect(_on_log_scrolled)

	input.text_submitted.connect(_on_submit)
	input.placeholder_text = "Type to chat"
	Network.chat_received.connect(_on_chat_received)
	Network.private_message_received.connect(_on_private_message)


func _input(event: InputEvent) -> void:
	if _is_modal_open():
		return
	# Wheel over the chat box: consume at _input so the camera's _unhandled_input
	# never sees it. Manual scroll of the log below.
	if event is InputEventMouseButton and event.pressed \
			and (event.button_index == MOUSE_BUTTON_WHEEL_UP
				or event.button_index == MOUSE_BUTTON_WHEEL_DOWN):
		if get_global_rect().has_point(event.position):
			var sb := log_label.get_v_scroll_bar()
			if sb.max_value > sb.page:
				var step := maxf(20.0, sb.page * 0.4)
				var max_scroll := sb.max_value - sb.page
				if event.button_index == MOUSE_BUTTON_WHEEL_UP:
					sb.value = maxf(0.0, sb.value - step)
				else:
					sb.value = minf(max_scroll, sb.value + step)
			get_viewport().set_input_as_handled()
			return

	if event is InputEventKey and event.pressed and not event.echo:
		if input.has_focus():
			if event.keycode == KEY_ESCAPE:
				_cancel_input()
				get_viewport().set_input_as_handled()
				return
			# Block clipboard operations while typing. Any of Ctrl+C / Ctrl+V /
			# Ctrl+X (or Cmd on Mac) is consumed here, before LineEdit's
			# _gui_input runs. Shift+Insert is also blocked for completeness.
			if (event.ctrl_pressed or event.meta_pressed) \
					and event.keycode in [KEY_V, KEY_X]:
				get_viewport().set_input_as_handled()
				return
			if event.keycode == KEY_INSERT and event.shift_pressed:
				get_viewport().set_input_as_handled()
				return
			return  # other keys flow through to LineEdit normally

		# Not focused:
		if event.keycode in [KEY_ENTER, KEY_KP_ENTER]:
			input.grab_focus()
			get_viewport().set_input_as_handled()
			return
		if event.keycode == KEY_ESCAPE:
			if not input.text.is_empty():
				input.clear()
				get_viewport().set_input_as_handled()
			return
		if event.unicode >= 32 and event.unicode != 127:
			input.grab_focus()
			var combined := input.text + char(event.unicode)
			input.text = combined
			input.caret_column = combined.length()
			get_viewport().set_input_as_handled()

	if event is InputEventMouseButton \
			and event.pressed \
			and event.button_index == MOUSE_BUTTON_LEFT:
		if input.has_focus():
			var r := input.get_global_rect()
			if not r.has_point(event.position):
				input.release_focus()


func _on_log_scrolled(_value: float) -> void:
	var sb := log_label.get_v_scroll_bar()
	var at_bottom := sb.value >= sb.max_value - sb.page - 1.0
	log_label.scroll_following = at_bottom


func _cancel_input() -> void:
	input.clear()
	input.release_focus()


func _on_submit(text: String) -> void:
	text = text.strip_edges()
	input.clear()
	input.release_focus()
	if text.is_empty(): return
	Network.send_chat(text)


func _on_chat_received(username: String, message: String, from_chunk: Vector2i) -> void:
	var player := get_tree().get_first_node_in_group("local_player")
	if player == null: return
	var here := World.world_to_chunk(
		roundi(player.global_position.x),
		roundi(player.global_position.z))
	if absi(here.x - from_chunk.x) > CHAT_CHUNK_RADIUS \
			or absi(here.y - from_chunk.y) > CHAT_CHUNK_RADIUS:
		return
	var line := "[color=#ffd84a]%s[/color]: %s" % [_escape(username), _escape(message)]
	_append_line(line)


func _on_private_message(text: String) -> void:
	_append_line(text)


func _append_line(line: String) -> void:
	_messages.append(line)

	if _messages.size() > MAX_MESSAGES:
		_messages.pop_front()
		var sb := log_label.get_v_scroll_bar()
		var prev_value := sb.value
		var was_following := log_label.scroll_following
		log_label.text = "\n".join(_messages)
		if not was_following:
			sb.value = prev_value
	elif _messages.size() == 1:
		log_label.append_text(line)
	else:
		log_label.append_text("\n" + line)


func _escape(s: String) -> String:
	return s.replace("[", "[lb]")


func _is_modal_open() -> bool:
	for m in get_tree().get_nodes_in_group("modal_blockers"):
		if m is CanvasItem and (m as CanvasItem).visible:
			return true
	return false
