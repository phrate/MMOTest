extends Node
## Client-side hitbox registry + debug visualization.
##
## Each entity type has a spec describing its shape (sphere/capsule/box) and
## offset. Hitboxes are StaticBody3D nodes on a dedicated collision layer, so
## right-click queries can hit only clickable entities and ignore terrain.
##
## Debug meshes match each shape, hidden by default. Toggle with F4.

const HITBOX_LAYER_BIT := 3                     # layer 4 in the editor
const HITBOX_COLLISION_LAYER := 1 << HITBOX_LAYER_BIT
const DEBUG_TRANSPARENCY := 0.5   # body meshes when debug hitboxes are visible

signal visibility_changed

var show_debug := false

# Spec keying:
#   "player"     -> local + remote players (shared spec for now)
#   "npc:<name>" -> match npc.display, lowercased
#   "npc:generic"-> fallback for any NPC display not listed
# y is the vertical center offset from the entity's origin (feet)
var specs := {
	"player":       {"type": "capsule", "radius": 0.4, "height": 1.8, "y": 0.9},
	"npc:chicken":  {"type": "sphere",  "radius": 0.4, "y": 0.4},
	"npc:man":      {"type": "capsule", "radius": 0.4, "height": 1.8, "y": 0.9},
	"npc:guard":    {"type": "capsule", "radius": 0.5, "height": 2.0, "y": 1.0},
	"npc:goblin":   {"type": "capsule", "radius": 0.4, "height": 1.5, "y": 0.75},
	"npc:generic":  {"type": "capsule", "radius": 0.4, "height": 1.8, "y": 0.9},
}


func resolve(entity_type: String, npc_display: String) -> Dictionary:
	if entity_type == "npc":
		var key := "npc:" + npc_display.to_lower()
		return specs.get(key, specs["npc:generic"])
	return specs.get(entity_type, specs["player"])


## Creates a StaticBody3D with a shape matching `spec`, plus a debug mesh.
## The returned body is added as a child of `entity`; the caller can free
## and re-attach if the spec changes (e.g. NPC display set after _ready).
func attach_to(entity: Node3D, spec: Dictionary) -> StaticBody3D:
	var body := StaticBody3D.new()
	body.name = "Hitbox"
	body.collision_layer = HITBOX_COLLISION_LAYER
	body.collision_mask = 0
	entity.add_child(body)

	var col := CollisionShape3D.new()
	col.name = "HitboxShape"
	
	# Offset: prefer Vector3 if provided, then offset_x/y/z, then legacy y.
	var offset := Vector3.ZERO
	if spec.has("offset") and spec.offset is Vector3:
		offset = spec.offset
	elif spec.has("offset_x") or spec.has("offset_y") or spec.has("offset_z"):
		offset = Vector3(
			float(spec.get("offset_x", 0.0)),
			float(spec.get("offset_y", 0.9)),
			float(spec.get("offset_z", 0.0)))
	else:
		offset = Vector3(0, float(spec.get("y", 0.9)), 0)
	col.position = offset

	var shape: Shape3D
	var mesh: PrimitiveMesh
	match str(spec.get("type", "capsule")):
		"sphere":
			var s := SphereShape3D.new()
			s.radius = float(spec.get("radius", 0.5))
			shape = s
			var m := SphereMesh.new()
			m.radius = s.radius
			m.height = s.radius * 2.0
			mesh = m
		"box":
			var b := BoxShape3D.new()
			b.size = spec.get("size", Vector3.ONE)
			shape = b
			var m := BoxMesh.new()
			m.size = b.size
			mesh = m
		_:  # capsule
			var c := CapsuleShape3D.new()
			c.radius = float(spec.get("radius", 0.4))
			c.height = float(spec.get("height", 1.8))
			shape = c
			var m := CapsuleMesh.new()
			m.radius = c.radius
			m.height = c.height
			mesh = m
	col.shape = shape
	body.add_child(col)

	var debug_mesh := MeshInstance3D.new()
	debug_mesh.name = "HitboxDebugMesh"
	debug_mesh.mesh = mesh
	debug_mesh.position = col.position
	debug_mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(1, 0.3, 0.3, 0.35)
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	debug_mesh.material_override = mat
	debug_mesh.visible = show_debug
	debug_mesh.add_to_group("hitbox_debug_mesh")
	body.add_child(debug_mesh)
	
	# Register the entity's body meshes so they can go semi-transparent in
	# debug mode. Excludes the debug mesh we just created (it's on `body`,
	# not `entity`, so it won't be caught — but the group check is cheap safety).
	for child in entity.get_children():
		if child is MeshInstance3D and not child.is_in_group("hitbox_debug_mesh"):
			child.add_to_group("hitbox_debug_target")
			(child as MeshInstance3D).transparency = DEBUG_TRANSPARENCY if show_debug else 0.0

	return body


func toggle_debug() -> void:
	show_debug = not show_debug
	_apply_debug_to_all()
	print("[Hitboxes] debug visible: ", show_debug)
	visibility_changed.emit()


func _apply_debug_to_all() -> void:
	# Hitbox debug shapes
	for m in get_tree().get_nodes_in_group("hitbox_debug_mesh"):
		if is_instance_valid(m) and m is MeshInstance3D:
			(m as MeshInstance3D).visible = show_debug

	# Entity body meshes — semi-transparent so hitboxes inside are visible
	for m in get_tree().get_nodes_in_group("hitbox_debug_target"):
		if is_instance_valid(m) and m is MeshInstance3D:
			(m as MeshInstance3D).transparency = DEBUG_TRANSPARENCY if show_debug else 0.0


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_F4:
			toggle_debug()
			get_viewport().set_input_as_handled()
