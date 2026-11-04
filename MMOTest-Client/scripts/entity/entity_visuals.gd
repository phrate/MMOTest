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
const NAME_LABEL_Y         := 2.2
const CHAT_LABEL_Y         := 1.9
const DAMAGE_BASE_Y        := 1.5
const DAMAGE_HIDE_MS       := 2500
const MOVING_THRESHOLD     := 0.5

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
		return
	_name_label = Label3D.new()
	_name_label.name = "NameLabel"
	_name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_name_label.no_depth_test = true
	_name_label.position = Vector3(0, NAME_LABEL_Y, 0)
	add_child(_name_label)


func _build_chat_label() -> void:
	var existing := get_parent().get_node_or_null("ChatLabel") as Label3D
	if existing != null:
		_chat_label = existing
		_chat_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		_chat_label.no_depth_test = true
		_chat_label.visible = false
		return
	_chat_label = Label3D.new()
	_chat_label.name = "ChatLabel"
	_chat_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_chat_label.no_depth_test = true
	_chat_label.font_size = 24
	_chat_label.pixel_size = 0.005
	_chat_label.outline_modulate = Color(0, 0, 0, 1)
	_chat_label.outline_size = 4
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
	_name_label.text = text


func show_chat_message(text: String) -> void:
	_chat_label.text = text
	_chat_label.visible = true
	_chat_hide_ms = Time.get_ticks_msec() + int(CHAT_BUBBLE_SECONDS * 1000.0)


func update_hp(hp: int, max_hp: int) -> void:
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
	_hp_bar.visible = true
	_hp_bar_hide_ms = Time.get_ticks_msec() + HP_BAR_DISPLAY_MS


func show_damage(amount: int) -> void:
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
	_current_anim = ""
	_update_anim()


func get_current_anim() -> String:
	return _current_anim


# Compatibility passthroughs — no-op, kept so any leftover callers don't break.
func set_in_combat(_in_combat: bool) -> void:
	pass


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
	if _chat_label.visible and Time.get_ticks_msec() >= _chat_hide_ms:
		_chat_label.visible = false


func _tick_hp_bar() -> void:
	if _hp_bar.visible and Time.get_ticks_msec() >= _hp_bar_hide_ms:
		_hp_bar.visible = false


func _tick_damage() -> void:
	if _damage_label.visible and Time.get_ticks_msec() >= _damage_hide_ms:
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


func reset_hp_tracking() -> void:
	_last_known_hp = -1
	_hp_bar.visible = false
