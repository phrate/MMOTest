# gfs_hub_panel.gd
extends DevPanel
## GFS Hub — TODO: nested tabbed interface (Recipe Manager, Tooltip Editor,
## Task Editor). Itself a hub. See Piece 4.

func _ready() -> void:
	_build_placeholder("gfs_hub")
