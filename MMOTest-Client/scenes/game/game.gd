extends Node3D


@onready var world: Node3D = %World



func _ready() -> void:
	print(Session.username, " in game scene")
