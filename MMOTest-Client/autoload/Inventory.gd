extends Node
## Client mirror of the player's inventory.
## Server sends inventory_data on login and after any change. We cache it and
## emit `changed` so UI can refresh.

signal changed

const SLOT_COUNT := 28

# slot_index (0..27) -> {slot, item_id, slug, display, type, type_slug, quantity}
var _slots: Dictionary = {}


func _ready() -> void:
	Network.inventory_data_received.connect(_on_inventory_data)
	Network.state_changed.connect(_on_state_changed)


func get_slot(slot_index: int) -> Dictionary:
	return _slots.get(slot_index, {})


func all_slots() -> Dictionary:
	return _slots


func is_empty(slot_index: int) -> bool:
	return not _slots.has(slot_index)


func _on_inventory_data(slots: Array) -> void:
	_slots.clear()
	for entry in slots:
		if typeof(entry) != TYPE_DICTIONARY: continue
		var idx := int(entry.get("slot", -1))
		if idx < 0 or idx >= SLOT_COUNT: continue
		_slots[idx] = entry
	changed.emit()


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and not _slots.is_empty():
		_slots.clear()
		changed.emit()
