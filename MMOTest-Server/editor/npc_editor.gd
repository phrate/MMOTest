extends Control
## Main scene for the NPC editor. TabContainer handles switching between
## Template and Unique panels automatically — no logic needed here beyond
## window setup.

func _ready() -> void:
	get_window().title = "NPC Editor"
