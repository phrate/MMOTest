extends Node
## One-shot icon generator. Open this scene in the editor, press F6.
## Renders pink_cube.tscn and brown_cube.tscn at a 45° angle, saves both
## PNGs. Run once; re-run if you want a different angle or size.
##
## F6 (not --headless) — SubViewport rendering needs a real graphics context.

const SCENES := {
	"res://resources/items/icons/pink_cube_icon.png":  "res://resources/items/pink_cube.tscn",
	"res://resources/items/icons/brown_cube_icon.png": "res://resources/items/brown_cube.tscn",
}
const ICON_SIZE := 64


func _ready() -> void:
	for output_path in SCENES.keys():
		var scene_path: String = SCENES[output_path]
		if not ResourceLoader.exists(scene_path):
			push_error("Missing: " + scene_path)
			continue
		await _render_icon(scene_path, output_path)
	get_tree().quit()


func _render_icon(scene_path: String, output_path: String) -> void:
	var scene: PackedScene = load(scene_path)
	if scene == null:
		push_error("Failed to load: " + scene_path)
		return

	var viewport := SubViewport.new()
	viewport.size = Vector2i(ICON_SIZE, ICON_SIZE)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.transparent_bg = true
	viewport.own_world_3d = true
	add_child(viewport)

	var env := Environment.new()
	env.background_mode = Environment.BG_CLEAR_COLOR
	env.background_color = Color(0, 0, 0, 0)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(1, 1, 1)
	env.ambient_light_energy = 0.5
	var world_env := WorldEnvironment.new()
	world_env.environment = env
	viewport.add_child(world_env)

	viewport.add_child(scene.instantiate())

	var cam := Camera3D.new()
	cam.fov = 30
	viewport.add_child(cam)
	cam.position = Vector3(1.2, 1.0, 1.2)
	cam.look_at(Vector3(0, 0.175, 0), Vector3.UP)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50, -30, 0)
	light.light_energy = 1.5
	viewport.add_child(light)

	await get_tree().process_frame
	await get_tree().process_frame

	var img := viewport.get_texture().get_image()
	DirAccess.make_dir_recursive_absolute(
		ProjectSettings.globalize_path("res://resources/items/icons/"))
	var err := img.save_png(output_path)
	if err == OK:
		print("[IconGenerator] Saved ", output_path)
	else:
		push_error("Save failed: " + str(err))

	viewport.queue_free()
	await get_tree().process_frame
