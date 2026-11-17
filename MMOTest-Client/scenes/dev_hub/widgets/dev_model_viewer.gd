class_name DevModelViewer
extends Control
## SubViewport-based model preview with orbit camera + wheel zoom.
## Exposes `model_holder` so callers can add gizmos alongside the model.
## `pre_input` callback fires before built-in input handling — return true
## to consume an event (used by gizmos to intercept clicks).

var model_holder: Node3D
var camera: Camera3D
var sub_viewport: SubViewport
var container: SubViewportContainer
var pre_input: Callable = Callable()

var orbit_yaw: float = 0.6
var orbit_pitch: float = 0.35
var orbit_distance: float = 4.0
var orbit_target: Vector3 = Vector3(0, 1, 0)

@export var mouse_sensitivity: float = 0.008
@export var zoom_step: float = 0.9
@export var min_distance: float = 0.5
@export var max_distance: float = 50.0
@export var pitch_clamp: float = 1.4
@export var min_size: Vector2 = Vector2(280, 360)

var _orbiting := false


func _init() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	custom_minimum_size = min_size
	clip_contents = true


func _ready() -> void:
	container = SubViewportContainer.new()
	container.stretch = true
	container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	container.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	container.gui_input.connect(_on_viewport_input)
	add_child(container)

	sub_viewport = SubViewport.new()
	sub_viewport.own_world_3d = true
	sub_viewport.handle_input_locally = false
	sub_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	container.add_child(sub_viewport)

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.15, 0.15, 0.18, 1.0)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(1, 1, 1)
	env.ambient_light_energy = 0.5
	var we := WorldEnvironment.new()
	we.environment = env
	sub_viewport.add_child(we)

	camera = Camera3D.new()
	camera.current = true
	sub_viewport.add_child(camera)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50, -30, 0)
	light.light_energy = 1.2
	light.shadow_enabled = true
	sub_viewport.add_child(light)

	model_holder = Node3D.new()
	model_holder.name = "ModelHolder"
	sub_viewport.add_child(model_holder)


func clear_model() -> void:
	for c in model_holder.get_children():
		model_holder.remove_child(c)
		c.queue_free()


func load_model(slug: String) -> Node3D:
	clear_model()
	if slug.is_empty(): return null
	var scene := NpcRender.get_scene(slug)
	if scene == null: return null
	var inst := scene.instantiate() as Node3D
	if inst == null: return null
	model_holder.add_child(inst)
	return inst


func measure_aabb() -> AABB:
	var first := true
	var result := AABB()
	for mi in _find_meshes(model_holder):
		if mi.mesh == null: continue
		var wb: AABB = mi.global_transform * mi.mesh.get_aabb()
		if first: result = wb; first = false
		else: result = result.merge(wb)
	return result


func _find_meshes(root: Node) -> Array:
	var out := []
	if root is MeshInstance3D: out.append(root)
	for c in root.get_children():
		out.append_array(_find_meshes(c))
	return out


func frame_camera() -> void:
	var aabb := measure_aabb()
	if aabb.size == Vector3.ZERO:
		aabb = AABB(Vector3(-1, 0, -1), Vector3(2, 2, 2))
	orbit_target = aabb.get_center()
	var size := maxf(aabb.size.x, maxf(aabb.size.y, aabb.size.z))
	orbit_distance = maxf(size, 0.5) * 2.5
	# Scale zoom bounds with the model. Without this, a model whose native size
	# exceeds ~20 units sets orbit_distance above the fixed max_distance of 50,
	# and the first wheel-down event clamps straight to max_distance (visually a
	# snap-zoom). Bounds only expand (floor at 0.5 / 50), so small models keep
	# the exact previous behavior.
	min_distance = maxf(size * 0.1, 0.5)
	max_distance = maxf(size * 10.0, 50.0)
	apply_orbit()


func apply_orbit() -> void:
	if camera == null: return
	var cp := cos(orbit_pitch)
	var sp := sin(orbit_pitch)
	var cy := cos(orbit_yaw)
	var sy := sin(orbit_yaw)
	var offset := Vector3(orbit_distance * cp * sy, orbit_distance * sp, orbit_distance * cp * cy)
	camera.global_position = orbit_target + offset
	camera.look_at(orbit_target, Vector3.UP)


func _on_viewport_input(event: InputEvent) -> void:
	if pre_input.is_valid() and pre_input.call(event):
		get_viewport().set_input_as_handled()
		return
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_orbiting = mb.pressed
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			orbit_distance = maxf(min_distance, orbit_distance * zoom_step)
			apply_orbit()
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			orbit_distance = minf(max_distance, orbit_distance / zoom_step)
			apply_orbit()
			get_viewport().set_input_as_handled()
			return
	elif event is InputEventMouseMotion and _orbiting:
		var mm := event as InputEventMouseMotion
		orbit_yaw -= mm.relative.x * mouse_sensitivity
		orbit_pitch = clampf(orbit_pitch + mm.relative.y * mouse_sensitivity, -pitch_clamp, pitch_clamp)
		apply_orbit()
