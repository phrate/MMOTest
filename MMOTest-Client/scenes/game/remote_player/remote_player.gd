extends Node3D
## Remote avatar. Moves toward the last server position at a constant linear speed.
## NameLabel is optional — if missing, set_username is a no-op.

@export var move_speed := 2.0

var _target_pos: Vector3
var _target_yaw: float = 0.0

@onready var name_label: Label3D = get_node_or_null("NameLabel") as Label3D


func _ready() -> void:
	_target_pos = global_position
	_target_yaw = rotation.y


func teleport(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw
	if is_inside_tree():
		global_position = pos
		rotation.y = yaw


func set_username(username: String) -> void:
	if name_label != null:
		name_label.text = username


func set_target(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw


func _process(delta: float) -> void:
	var to_target := _target_pos - global_position
	var horiz_sq := to_target.x * to_target.x + to_target.z * to_target.z
	var speed := move_speed
	if horiz_sq > 0.0001:
		var horiz_dist := sqrt(horiz_sq)
		var dist_3d := to_target.length()
		speed = minf(speed, move_speed * 8.0)   # cap at 8× horizontal speed
		
	var step := speed * delta
	if to_target.length_squared() <= step * step:
		global_position = _target_pos
	else:
		global_position = global_position.move_toward(_target_pos, step)
	rotation.y = _target_yaw
