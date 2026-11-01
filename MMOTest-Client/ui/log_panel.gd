class_name LogPanel
extends RichTextLabel
## Scrolling message log with colored levels.


func info(text: String) -> void:
	_add(text, "")


func success(text: String) -> void:
	_add(text, "green")


func warn(text: String) -> void:
	_add(text, "orange")


func error(text: String) -> void:
	_add(text, "red")


func _add(text: String, color: String) -> void:
	var safe := text.replace("[", "[lb]")  # don't let message text be parsed as BBCode
	if color.is_empty():
		append_text(safe + "\n")
	else:
		append_text("[color=%s]%s[/color]\n" % [color, safe])
