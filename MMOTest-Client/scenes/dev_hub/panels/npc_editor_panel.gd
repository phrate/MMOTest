# npc_editor_panel.gd
extends DevPanel
## NPC editor — TODO: migrate server-side NpcEditor (model viewer, hitbox
## gizmos, scale, animations, all fields). See Piece 2.

func _ready() -> void:
	_build_placeholder("npc_editor")

func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	# Placeholder: log the requested NPC id so we can verify the right-click
	# → DevHub routing works before the real panel exists.
	print("[NpcEditor] Open requested for %s id=%d" % [entity_type, entity_id])
