class_name LogPanel
extends RichTextLabel
## Scrolling message log with colored levels.


func info(_text: String) -> void:
	_add(_text, "")


func success(_text: String) -> void:
	_add(_text, "green")


func warn(_text: String) -> void:
	_add(_text, "orange")


func error(_text: String) -> void:
	_add(_text, "red")


func _add(_text: String, color: String) -> void:
	var safe := _text.replace("[", "[lb]")  # don't let message _text be parsed as BBCode
	if color.is_empty():
		append_text(safe + "\n")
	else:
		append_text("[color=%s]%s[/color]\n" % [color, safe])
