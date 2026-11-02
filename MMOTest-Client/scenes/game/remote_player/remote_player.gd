extends Node3D

const CHAT_BUBBLE_SECONDS := 5.0

@export var move_speed := 2.0

var username: String = ""
var _player_id: int = 0                     # NEW
var _target_pos: Vector3
var _target_yaw: float = 0.0
var _chat_hide_at_ms: int = 0

@onready var name_label: Label3D = get_node_or_null("NameLabel") as Label3D
@onready var chat_label: Label3D = _resolve_chat_label()


func _ready() -> void:
	add_to_group("remote_player")
	_target_pos = global_position
	_target_yaw = rotation.y


## Called by game.gd right after instantiation, before add_child / teleport.
func set_player_id(pid: int) -> void:      # NEW
	_player_id = pid

func get_player_id() -> int:                # NEW
	return _player_id


func get_entity_type() -> String:
	return "player"


func _resolve_chat_label() -> Label3D:
	var existing := get_node_or_null("ChatLabel") as Label3D
	if existing != null:
		existing.visible = false
		return existing
	var new_label := Label3D.new()
	new_label.name = "ChatLabel"
	new_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	new_label.no_depth_test = true
	new_label.font_size = 24
	new_label.pixel_size = 0.005
	new_label.outline_modulate = Color(0, 0, 0, 1)
	new_label.outline_size = 4
	new_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	new_label.width = 500
	new_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	new_label.position = Vector3(0, 1.9, 0)
	new_label.visible = false
	add_child(new_label)
	return new_label


func teleport(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw
	if is_inside_tree():
		global_position = pos
		rotation.y = yaw


func set_username(u: String) -> void:
	username = u
	if name_label == null: return
	name_label.text = u
	name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	name_label.no_depth_test = true
	if name_label.position.y < 0.5:
		name_label.position.y = 2.2


func show_chat_message(text: String) -> void:
	if chat_label == null: return
	chat_label.text = text
	chat_label.visible = true
	_chat_hide_at_ms = Time.get_ticks_msec() + int(CHAT_BUBBLE_SECONDS * 1000.0)


func set_target(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw


func _process(delta: float) -> void:
	if _chat_hide_at_ms > 0 and Time.get_ticks_msec() >= _chat_hide_at_ms:
		if chat_label != null:
			chat_label.visible = false
		_chat_hide_at_ms = 0

	var to_target := _target_pos - global_position
	var horiz_sq := to_target.x * to_target.x + to_target.z * to_target.z
	var speed := move_speed
	if horiz_sq > 0.0001:
		var horiz_dist := sqrt(horiz_sq)
		var dist_3d := to_target.length()
		speed = move_speed * (dist_3d / horiz_dist)
	var step := speed * delta
	if to_target.length_squared() <= step * step:
		global_position = _target_pos
	else:
		global_position = global_position.move_toward(_target_pos, step)
	rotation.y = _target_yaw
