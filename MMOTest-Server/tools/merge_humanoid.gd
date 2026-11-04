@tool
extends EditorScript
## Builds res://animations/humanoid.tres from the three extracted lib_*.tres.
## Run via File → Run (or Ctrl+Shift+X) with this script open in the editor.
## Idempotent — re-run any time to refresh the merged library.

const SOURCES := [
	# [lib path,                                     source clip,  canonical]
	["res://_source/animations/lib_general.tres",       "Idle_A",    "idle"],
	["res://_source/animations/lib_movement.tres",      "Walking_A", "walk"],
	["res://_source/animations/lib_combat_melee.tres",  "Melee_1H_Attack_Slice_Horizontal",  "attack"],
	["res://_source/animations/lib_general.tres",       "Death_A",   "death"],
	["res://_source/animations/lib_general.tres",       "Hit_A",     "hit"],
]

const OUTPUT_PATH := "res://animations/humanoid.tres"

# Clips in this list get Loop Linear; everything else is LOOP_NONE.
const LOOPING := ["idle", "walk", "attack"]


func _run() -> void:
	var lib := AnimationLibrary.new()
	var lib_cache := {}

	for entry in SOURCES:
		var lib_path: String = entry[0]
		var source_clip: String = entry[1]
		var canonical: String = entry[2]

		if not lib_cache.has(lib_path):
			var loaded := load(lib_path) as AnimationLibrary
			if loaded == null:
				push_warning("Missing library: " + lib_path)
				lib_cache[lib_path] = null
				continue
			lib_cache[lib_path] = loaded

		var source_lib: AnimationLibrary = lib_cache[lib_path]
		if source_lib == null:
			push_warning("Could not load: " + lib_path)
			continue

		var clip := source_lib.get_animation(source_clip)
		if clip == null:
			push_warning("Missing clip '%s' in %s — available: %s" % [
				source_clip, lib_path, str(source_lib.get_animation_list())])
			continue

		var dup := clip.duplicate() as Animation
		if dup == null:
			push_warning("Could not duplicate clip '%s'" % source_clip)
			continue

		dup.loop_mode = (
			Animation.LOOP_LINEAR if canonical in LOOPING
			else Animation.LOOP_NONE)

		lib.add_animation(canonical, dup)
		print("Added '%s' (from '%s', %.2fs)" % [canonical, source_clip, dup.length])

	if lib.get_animation_list().is_empty():
		push_error("No clips loaded; aborting.")
		return

	DirAccess.make_dir_recursive_absolute(
		ProjectSettings.globalize_path("res://animations/"))

	var err := ResourceSaver.save(lib, OUTPUT_PATH)
	if err == OK:
		print("Saved %s with %d clip(s): %s" % [
			OUTPUT_PATH, lib.get_animation_list().size(),
			str(lib.get_animation_list())])
	else:
		push_error("Save failed: " + str(err))
