class_name NpcRenderViewer
extends DevModelViewer
## Render-tab 3D preview: extends DevModelViewer with hitbox and scale gizmos.
## Coordinates gizmo drag → model transform updates in the render tab context.

signal hitbox_gizmo_changed(config: Dictionary)
signal scale_gizmo_changed(scale: Vector3)

var hitbox_gizmo: HitboxGizmo = null
var scale_gizmo: ScaleGizmo = null

var render_mode: String = "hitbox"  # "hitbox" | "scale"

var _base_fit: float = 1.0
var _bounds_at_scale_one: Vector3 = Vector3.ZERO
var _current_scale: Vector3 = Vector3.ONE


func _ready() -> void:
	super._ready()

	hitbox_gizmo = HitboxGizmo.new()
	hitbox_gizmo.camera = camera
	model_holder.add_child(hitbox_gizmo)
	hitbox_gizmo.hitbox_changed.connect(func(c: Dictionary) -> void: hitbox_gizmo_changed.emit(c))
	hitbox_gizmo.set_gizmo_visible(false)

	scale_gizmo = ScaleGizmo.new()
	scale_gizmo.camera = camera
	model_holder.add_child(scale_gizmo)
	scale_gizmo.scale_changed.connect(func(s: Vector3) -> void:
		_current_scale = s
		_apply_model_scale()
		scale_gizmo_changed.emit(s)
	)
	scale_gizmo.set_gizmo_visible(false)

	pre_input = _on_viewport_pre_input


func load_render_model(slug: String, user_scale: Vector3) -> void:
	var inst := load_model(slug)
	_current_scale = user_scale
	if inst == null: return
	_base_fit = _compute_base_fit(inst, slug)
	_apply_model_scale()
	_bounds_at_scale_one = _compute_bounds_at_scale_one(inst)
	frame_camera()
	scale_gizmo.set_scale_and_bounds(_current_scale, _bounds_at_scale_one)
	scale_gizmo.position = measure_aabb().get_center()


func set_render_mode(mode: String) -> void:
	render_mode = mode
	hitbox_gizmo.set_gizmo_visible(mode == "hitbox" and not is_current_slug_empty())
	scale_gizmo.set_gizmo_visible(mode == "scale" and not is_current_slug_empty())


func is_current_slug_empty() -> bool:
	return get_model() == null


func get_model() -> Node3D:
	for c in model_holder.get_children():
		if c is HitboxGizmo or c is ScaleGizmo: continue
		if c is Node3D: return c
	return null


func push_hitbox(config: Dictionary) -> void:
	hitbox_gizmo.set_hitbox(config)


func push_scale(scale: Vector3, base_bounds: Vector3) -> void:
	_current_scale = scale
	scale_gizmo.set_scale_and_bounds(scale, base_bounds)
	scale_gizmo.position = measure_aabb().get_center()


func is_scale_dragging() -> bool:
	return scale_gizmo != null and scale_gizmo.is_dragging()


func _compute_base_fit(model: Node3D, slug: String) -> float:
	var aabb := measure_aabb()
	if aabb.size.y < 0.001: return 1.0
	var target := 1.8
	match slug:
		"player", "man", "guard": target = 1.8
		"goblin": target = 1.4
		"chicken": target = 0.4
	return target / aabb.size.y


func _apply_model_scale() -> void:
	var model := get_model()
	if model == null: return
	model.scale = Vector3.ONE * _base_fit * _current_scale
	model.position = Vector3.ZERO
	var aabb := measure_aabb()
	model.position.y -= aabb.position.y


func _compute_bounds_at_scale_one(model: Node3D) -> Vector3:
	var aabb := measure_aabb()
	if _current_scale.x > 0.001 and _current_scale.y > 0.001 and _current_scale.z > 0.001:
		return Vector3(
			aabb.size.x / _current_scale.x,
			aabb.size.y / _current_scale.y,
			aabb.size.z / _current_scale.z
		)
	return aabb.size


func _on_viewport_pre_input(event: InputEvent) -> bool:
	if event is InputEventMouseButton or event is InputEventMouseMotion:
		if render_mode == "hitbox" and hitbox_gizmo != null:
			if hitbox_gizmo.handle_input(event, event.position):
				return true
		elif render_mode == "scale" and scale_gizmo != null:
			if scale_gizmo.handle_input(event, event.position):
				return true
	return false