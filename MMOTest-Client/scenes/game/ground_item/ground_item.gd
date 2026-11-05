extends StaticBody3D
## Ground item placeholder node. Chooses pink cube (base item) or brown cube
## (cert) at setup time. Detected by the primary/right-click ray via the
## hitbox layer, same as NPCs and players.

const PINK_CUBE  := preload("res://resources/items/pink_cube.tscn")
const BROWN_CUBE := preload("res://resources/items/brown_cube.tscn")

var _ground_id: int = 0
var item_slug: String = ""
var item_display: String = ""
var item_quantity: int = 0
var is_cert: bool = false


func setup(p_ground_id: int, p_slug: String, p_display: String, p_quantity: int, p_is_cert: bool) -> void:
	_ground_id = p_ground_id
	item_slug = p_slug
	item_display = p_display
	item_quantity = p_quantity
	is_cert = p_is_cert
	var mesh_scene := BROWN_CUBE if is_cert else PINK_CUBE
	add_child(mesh_scene.instantiate())


func set_ground_item_id(id: int) -> void:
	_ground_id = id

func get_ground_item_id() -> int:
	return _ground_id

func get_entity_type() -> String:
	return "ground_item"
