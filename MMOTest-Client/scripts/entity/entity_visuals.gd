class_name EntityVisuals
extends Node3D
## Shared visual layer for player, remote_player, and npc.
## Owns: name label, chat bubble, overhead HP bar, damage number, idle/walk animation.
##
## Attach as a child of any entity. The entity's position drives all child
## positions automatically — no per-frame repositioning required.

const CHAT_BUBBLE_SECONDS := 5.0
const HP_BAR_DISPLAY_MS    := 5000
const HP_BAR_Y             := 2.0
const CHAT_FONT_SIZE := 12
const CHAT_OUTLINE_SIZE := 9
const SHOW_NAME_LABELS := false

## Radians to rotate a render scene so its visual front matches Godot's -Z
## forward. Mixamo/Blender rigs face +Z → PI. Godot-native rigs → 0.0.
## Per-slug overrides go in FACING_OFFSET_BY_SLUG.
const MODEL_FACING_OFFSET := PI
const FACING_OFFSET_BY_SLUG := {}   # e.g. {"knight": 0.0}

const NAME_LABEL_Y         := 2.2
const CHAT_LABEL_Y         := 1.9
const NAME_LABEL_OFFSET    := 0.3
const CHAT_LABEL_OFFSET    := 0.35
const DAMAGE_BASE_Y        := 1.5
const DAMAGE_HIDE_MS       := 2500
const MOVING_THRESHOLD     := 0.5

## Maps canonical clip names (what _update_anim looks for) to the actual clip
## names present in whatever render scene was loaded. First hit wins, so the
## ordering within each array is "most likely source → least likely source".
## Add new entries when you add new rigs; existing ones stay as silent no-ops.
const CLIP_ALIASES := {
	"idle":   ["idle_1", "Idle", "Idle_A",
			   "Armature|Idle", "mixamo.com|Idle", "mixamo.com|Armature|Idle"],
	"walk":   ["walk_forward", "Walk", "Walking", "Walking_A",
			   "Armature|Walk", "Armature|Walking",
			   "mixamo.com|Walk", "mixamo.com|Walking",
			   "mixamo.com|Armature|Walk", "mixamo.com|Armature|Walking"],
	"attack": ["attack_1", "Attack", "Sword Slash", "1H_Slice",
			   "Melee_1H_Attack_Slice_Horizontal",
			   "Armature|Attack", "Armature|Sword Slash",
			   "mixamo.com|Attack", "mixamo.com|Sword Slash",
			   "mixamo.com|Armature|Sword Slash"],
	"death":  ["die_1", "Death", "Death_A",
			   "Armature|Death", "mixamo.com|Death", "mixamo.com|Armature|Death"],
	"hit":    ["reaction_1", "Hit", "Hit_A",
			   "Armature|Hit", "mixamo.com|Hit", "mixamo.com|Armature|Hit"],
}
const LOOPING_CLIPS := ["idle", "walk"]


var _name_label: Label3D
var _chat_label: Label3D
var _hp_bar: OverheadHealthBar
var _damage_label: Label3D
var _anim_player: AnimationPlayer

var _chat_hide_ms       := 0
var _hp_bar_hide_ms     := 0
var _damage_hide_ms     := 0
var _last_known_hp      := -1
var _current_anim       := ""
var _scripted_anim      := ""
var _scripted_end_ms    := 0
var _frame_vel          := 0.0
var _last_pos           := Vector3.ZERO
var _pos_initialized    := false


func _ready() -> void:
	_build_name_label()
	_build_chat_label()
	_build_hp_bar()
	_build_damage_label()


func _process(delta: float) -> void:
	if not _pos_initialized:
		var p := get_parent()
		if p is Node3D:
			_last_pos = (p as Node3D).global_position
		_pos_initialized = true
		return
	_update_frame_velocity(delta)
	_tick_chat()
	_tick_hp_bar()
	_tick_damage()
	_update_anim()


# ---------- Build ----------

func _build_name_label() -> void:
	var existing := get_parent().get_node_or_null("NameLabel") as Label3D
	if existing != null:
		_name_label = existing
		_name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		_name_label.no_depth_test = true
		_name_label.visible = SHOW_NAME_LABELS
		return
	_name_label = Label3D.new()
	_name_label.name = "NameLabel"
	_name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_name_label.no_depth_test = true
	_name_label.position = Vector3(0, NAME_LABEL_Y, 0)
	_name_label.visible = SHOW_NAME_LABELS
	add_child(_name_label)


func _build_chat_label() -> void:
	var existing := get_parent().get_node_or_null("ChatLabel") as Label3D
	if existing != null:
		_chat_label = existing
		_chat_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		_chat_label.no_depth_test = true
		_chat_label.visible = false
		_chat_label.font_size = CHAT_FONT_SIZE
		_chat_label.fixed_size = true
		_chat_label.pixel_size = 0.001
		_chat_label.outline_size = CHAT_OUTLINE_SIZE
		_chat_label.outline_modulate = Color(0, 0, 0, 1)
		return
	_chat_label = Label3D.new()
	_chat_label.name = "ChatLabel"
	_chat_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_chat_label.no_depth_test = true
	_chat_label.font_size = CHAT_FONT_SIZE
	_chat_label.fixed_size = true
	_chat_label.pixel_size = 0.001
	_chat_label.outline_size = CHAT_OUTLINE_SIZE
	_chat_label.outline_modulate = Color(0, 0, 0, 1)
	_chat_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_chat_label.width = 500
	_chat_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_chat_label.position = Vector3(0, CHAT_LABEL_Y, 0)
	_chat_label.visible = false
	add_child(_chat_label)


func _build_hp_bar() -> void:
	_hp_bar = OverheadHealthBar.new()
	_hp_bar.name = "HpBar"
	_hp_bar.position = Vector3(0, HP_BAR_Y, 0)
	_hp_bar.visible = false
	add_child(_hp_bar)


func _build_damage_label() -> void:
	_damage_label = Label3D.new()
	_damage_label.name = "DamageLabel"
	_damage_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_damage_label.no_depth_test = true
	_damage_label.font_size = 64
	_damage_label.pixel_size = 0.006
	_damage_label.modulate = HealthBarColors.DAMAGE_HIT_COLOR
	_damage_label.outline_modulate = Color(0, 0, 0, 1)
	_damage_label.outline_size = 10
	_damage_label.position = Vector3(0, DAMAGE_BASE_Y, 0)
	_damage_label.visible = false
	add_child(_damage_label)


# ---------- Public API ----------

func set_name_text(text: String) -> void:
	if _name_label == null: return
	_name_label.text = text


func show_chat_message(text: String) -> void:
	if _chat_label == null: return
	_chat_label.text = text
	_chat_label.visible = true
	_chat_hide_ms = Time.get_ticks_msec() + int(CHAT_BUBBLE_SECONDS * 1000.0)


func update_hp(hp: int, max_hp: int) -> void:
	if _hp_bar == null: return
	_hp_bar.set_fraction(float(hp) / float(maxi(max_hp, 1)))
	if _last_known_hp == -1:
		_last_known_hp = hp
		_hp_bar.visible = false
		return
	if hp == _last_known_hp:
		return
	_last_known_hp = hp
	_hp_bar.visible = true
	_hp_bar_hide_ms = Time.get_ticks_msec() + HP_BAR_DISPLAY_MS


func bump_hp_bar() -> void:
	if _hp_bar == null: return
	_hp_bar.visible = true
	_hp_bar_hide_ms = Time.get_ticks_msec() + HP_BAR_DISPLAY_MS


func reset_hp_tracking() -> void:
	_last_known_hp = -1
	if _hp_bar != null:
		_hp_bar.visible = false


func show_damage(amount: int) -> void:
	if _damage_label == null: return
	_damage_label.text = str(amount)
	_damage_label.modulate = (
		HealthBarColors.DAMAGE_HIT_COLOR if amount >= 1
		else HealthBarColors.DAMAGE_ZERO_COLOR)
	_damage_label.visible = true
	_damage_hide_ms = Time.get_ticks_msec() + DAMAGE_HIDE_MS


func play_scripted_anim(anim: String, duration_ms: int) -> void:
	_scripted_anim = anim
	_scripted_end_ms = (Time.get_ticks_msec() + duration_ms) if duration_ms > 0 else int(0x7FFFFFFF)
	_current_anim = ""
	_update_anim()


func set_anim_player(ap: AnimationPlayer) -> void:
	_anim_player = ap
	_ensure_canonical_clips(ap)
	_current_anim = ""
	_update_anim()


func get_current_anim() -> String:
	return _current_anim


# Compatibility passthroughs — no-op, kept so leftover callers don't break.
func set_in_combat(_in_combat: bool) -> void:
	pass


# ---------- Clip aliasing ----------

## Ensures the AnimationPlayer's default library contains entries under the
## canonical names (idle/walk/attack/death/hit). For each canonical name, if
## it's already present, skip. Otherwise try the alias list in order and add
## the first real clip as a duplicate under the canonical name.
##
## The original clip is left in place so entity scripts can still play it by
## its native name if needed (e.g. play_scripted_anim("walk_forward", …)).
## Ensures the AnimationPlayer's default library contains entries under the
## canonical names (idle/walk/attack/death/hit), and sets appropriate loop
## modes on the duplicated clips:
##   idle, walk    → LOOP_LINEAR
##   attack, hit   → LOOP_NONE (play once, then _update_anim reverts)
##   death         → LOOP_NONE (play once, hold last frame)
func _ensure_canonical_clips(ap: AnimationPlayer) -> void:
	if ap == null: return
	var lib: AnimationLibrary = ap.get_animation_library("")
	if lib == null:
		lib = AnimationLibrary.new()
		ap.add_animation_library("", lib)

	for canonical in CLIP_ALIASES.keys():
		var target_anim: Animation = null

		if lib.has_animation(canonical):
			target_anim = lib.get_animation(canonical)
		else:
			for source in CLIP_ALIASES[canonical]:
				if lib.has_animation(source):
					var anim: Animation = lib.get_animation(source)
					if anim != null:
						var dup := anim.duplicate() as Animation
						lib.add_animation(canonical, dup)
						target_anim = dup
						print("[EntityVisuals] aliased '%s' → '%s'" % [source, canonical])
					break

		if target_anim != null:
			target_anim.loop_mode = (
				Animation.LOOP_LINEAR if canonical in LOOPING_CLIPS
				else Animation.LOOP_NONE)


# ---------- Internal ticks ----------

func _update_frame_velocity(delta: float) -> void:
	var p := get_parent()
	if not p is Node3D:
		return
	var cur := (p as Node3D).global_position
	var dist := cur.distance_to(_last_pos)
	_last_pos = cur
	var inst_vel := dist / delta if delta > 0.0 else 0.0
	_frame_vel = lerpf(_frame_vel, inst_vel, 1.0 - exp(-15.0 * delta))


func _tick_chat() -> void:
	if _chat_label != null and _chat_label.visible and Time.get_ticks_msec() >= _chat_hide_ms:
		_chat_label.visible = false


func _tick_hp_bar() -> void:
	if _hp_bar != null and _hp_bar.visible and Time.get_ticks_msec() >= _hp_bar_hide_ms:
		_hp_bar.visible = false


func _tick_damage() -> void:
	if _damage_label != null and _damage_label.visible and Time.get_ticks_msec() >= _damage_hide_ms:
		_damage_label.visible = false


func _update_anim() -> void:
	if _anim_player == null: return
	var desired: String
	var now := Time.get_ticks_msec()
	if _scripted_anim != "":
		if now < _scripted_end_ms:
			desired = _scripted_anim
		else:
			_scripted_anim = ""
			desired = _derived_anim()
	else:
		desired = _derived_anim()
	if desired == _current_anim: return
	_current_anim = desired
	if _anim_player.has_animation(desired):
		_anim_player.play(desired)


func _derived_anim() -> String:
	return "walk" if _frame_vel > MOVING_THRESHOLD else "idle"


## Auto-fit a loaded render scene to a target height and re-anchor its feet
## at the entity's origin. Handles any source-scale (Mixamo cm-scale rigs,
## KayKit meter-scale rigs) uniformly.
func fit_render_scene_to_height(render_root: Node3D, target_height: float) -> void:
	if render_root == null: return
	var aabb := _measure_world_aabb(render_root)
	if aabb.size.y < 0.001:
		return

	var factor := target_height / aabb.size.y
	render_root.scale = render_root.scale * factor

	# Re-measure after scaling to place feet at entity origin.
	aabb = _measure_world_aabb(render_root)
	var parent := render_root.get_parent()
	if parent is Node3D:
		var parent_y: float = (parent as Node3D).global_position.y
		var delta_y := aabb.position.y - parent_y
		render_root.position.y -= delta_y


## Slug → target height (in world units, 1 unit = 1 meter).
## Unknown slugs default to 1.8 (humanoid).
const TARGET_HEIGHT_BY_SLUG := {
	"player": 1.8,
	"man": 1.8,
	"guard": 1.8,
	"goblin": 1.4,
	"chicken": 0.4,
}


func fit_render_scene_for_slug(render_root: Node3D, slug: String) -> void:
	if render_root == null: return
	var target_h: float = TARGET_HEIGHT_BY_SLUG.get(slug, 1.8)
	var aabb := _measure_world_aabb(render_root)
	if aabb.size.y < 0.001:
		return

	# Uniform auto-fit to the slug's target height.
	var fit_factor := target_h / aabb.size.y

	# Per-slug visual scale — multiplicative on top of the fit. Non-uniform,
	# so you can widen a chicken (X=1.2) or shorten a man (Y=0.9) without
	# touching the hitbox.
	var user_scale := Vector3.ONE
	var rc := NpcRender.get_render_config(slug)
	if not rc.is_empty():
		user_scale.x = float(rc.get("scale_x", 1.0))
		user_scale.y = float(rc.get("scale_y", 1.0))
		user_scale.z = float(rc.get("scale_z", 1.0))

	render_root.scale = render_root.scale * fit_factor * user_scale

	# Re-anchor feet to y=0 after all scaling.
	aabb = _measure_world_aabb(render_root)
	var parent := render_root.get_parent()
	if parent is Node3D:
		var parent_y: float = (parent as Node3D).global_position.y
		render_root.position.y -= (aabb.position.y - parent_y)

	# Facing offset (existing Mixamo/Godot 180° fix).
	var facing: float = FACING_OFFSET_BY_SLUG.get(slug, MODEL_FACING_OFFSET)
	render_root.rotation.y = facing


func _measure_world_aabb(root: Node) -> AABB:
	var first := true
	var result := AABB()
	for mi in _find_meshes_recursive(root):
		if mi.mesh == null: continue
		var local_box: AABB = mi.mesh.get_aabb()
		var world_box: AABB = mi.global_transform * local_box
		if first:
			result = world_box
			first = false
		else:
			result = result.merge(world_box)
	return result


func _find_meshes_recursive(root: Node) -> Array:
	var out := []
	if root is MeshInstance3D:
		out.append(root)
	for c in root.get_children():
		out.append_array(_find_meshes_recursive(c))
	return out


## Position the name label (and chat label) just above the visible mesh.
## Measures the render scene's world AABB after fitting and adjusts the labels
## relative to the entity origin. Call after fit_render_scene_for_slug.
func fit_name_label_above_node(render_root: Node3D) -> void:
	if _name_label == null or render_root == null: return
	var aabb := _measure_world_aabb(render_root)
	if aabb.size.y < 0.001: return
	var parent := get_parent()
	if not parent is Node3D: return

	var world_top := aabb.position.y + aabb.size.y
	var local_top := world_top - (parent as Node3D).global_position.y

	# 0.3 above the mesh top; chat bubble sits higher so it doesn't overlap.
	_name_label.position.y = local_top + NAME_LABEL_OFFSET
	if _chat_label != null:
		_chat_label.position.y = local_top + CHAT_LABEL_OFFSET
