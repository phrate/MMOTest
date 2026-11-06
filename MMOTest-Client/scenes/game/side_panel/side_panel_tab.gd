class_name SidePanelTab
extends MarginContainer
## Base class for the content of one side-panel tab. It is a MarginContainer so its minimum size
## comes from its children; the panel sizes itself to the largest tab.

## Called when this tab becomes the visible one.
func tab_selected() -> void:
	pass


## Called when another tab replaces this one.
func tab_deselected() -> void:
	pass
