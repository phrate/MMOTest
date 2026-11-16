class_name DevPanelRegistry
extends RefCounted
## Registry of all DevHub tabs. Adding a new panel = one entry here plus a
## .gd file in panels/. Nothing else in the codebase changes.
##
## Fields:
##   id           — internal id used by DevHub.open_at() and on_hub_open_for_entity()
##   name_key     — Texts key for the tab label
##   script       — path to a script whose root class extends DevPanel
##   shortcut     — optional single-key shortcut (Ctrl+N) to jump to this tab
##   entity_types — list of entity types this panel can accept via hub-open-for-entity

const DEFAULT_TAB := "item_editor"

const PANELS := [
	{
		"id": "world_editor",
		"name_key": "ui.dev_hub.tab.world_editor",
		"scene": "res://scenes/dev_hub/panels/world_editor_panel.tscn",
		"shortcut": KEY_1,
		"entity_types": [],
	},
	{
		"id": "npc_editor",
		"name_key": "ui.dev_hub.tab.npc_editor",
		"scene": "res://scenes/dev_hub/panels/npc_editor_panel.tscn",
		"shortcut": KEY_2,
		"entity_types": ["npc"],
	},
	{
		"id": "item_editor",
		"name_key": "ui.dev_hub.tab.item_editor",
		"scene": "res://scenes/dev_hub/panels/item_editor_panel.tscn",
		"shortcut": KEY_3,
		"entity_types": [],
	},
	{
		"id": "player_editor",
		"name_key": "ui.dev_hub.tab.player_editor",
		"scene": "res://scenes/dev_hub/panels/player_editor_panel.tscn",
		"shortcut": KEY_4,
		"entity_types": ["player"],
	},
	{
		"id": "gfs_hub",
		"name_key": "ui.dev_hub.tab.gfs_hub",
		"scene": "res://scenes/dev_hub/panels/gfs_hub_panel.tscn",
		"shortcut": KEY_5,
		"entity_types": [],
	},
]
