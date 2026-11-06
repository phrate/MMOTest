class_name SidePanelTabs
extends RefCounted
## The side panel's tabs, in display order.
## To add a tab: write a script that `extends SidePanelTab`, add an entry below, and add its texts
## (ui/side_panel_texts.json). SidePanel itself never needs to change.

const DEFAULT_TAB := "inventory"

const TABS := [
	{
		"id": "inventory",
		"name_key": "ui.side_panel.tab.inventory.name",
		"short_key": "ui.side_panel.tab.inventory.short",
		"icon": "res://resources/ui/icons/tab_inventory.png",
		"script": "res://scenes/game/inventory/inventory_panel.gd",
	},
	{
		"id": "skills",
		"name_key": "ui.side_panel.tab.skills.name",
		"short_key": "ui.side_panel.tab.skills.short",
		"icon": "res://resources/ui/icons/tab_skills.png",
		"script": "res://scenes/game/skills/skills_tab.gd",
	},
]
