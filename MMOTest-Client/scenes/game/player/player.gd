extends Node3D

@onready var username_label: Label3D = %UsernameLabel



func _ready() -> void:
	username_label.text = Session.username
