class_name NpcEditorRender
extends VBoxContainer
## Render tab: slug browser (left) + viewer with gizmo mode toggle (middle)
## + hitbox/scale fields (right). Save Config / Reset Config buttons at bottom.

const SLUG_TREE_WIDTH := 180
const VIEWER_WIDTH := 280
const FIELDS_WIDTH := 280
const MODE_BUTTON_HEIGHT := 40
const SAVE_BUTTON_HEIGHT := 48

var _slug_tree: DevSearchTree
var _hitbox_btn: Button
var _scale_btn: Button
var _viewer: NpcRenderViewer
var _hitbox_form: NpcRenderHitboxForm
var _scale_form: NpcRenderScaleForm
var _form_container: VBoxContainer
var _save_btn: Button
var _reset_btn: Button

var _selected_slug: String = ""
var _current_mode: String = "hitbox"

var _hitbox_config: Dictionary = {}
var _current_scale: Vector3 = Vector3.ONE
var _hitbox_dirty: bool = false
var _scale_dirty: bool = false


func _init() -> void:
	name = "Render"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	var split := HSplitContainer.new()
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	add_child(split)

	# Left: slug browser
	var left := VBoxContainer.new()
	left.custom_minimum_size = Vector2(SLUG_TREE_WIDTH, 0)
	left.add_theme_constant_override("separation", s.panel_separation)
	split.add_child(left)
	var left_header := Label.new()
	left_header.text = "Render Slugs"
	left_header.add_theme_font_size_override("font_size", s.font_size_tab)
	left_header.add_theme_color_override("font_color", s.text_heading)
	left.add_child(left_header)
	_slug_tree = DevSearchTree.new()
	left.add_child(_slug_tree)
	_slug_tree.setup("Search slug...", [
		{"title": "Slug", "width": 120, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": true},
		{"title": "Types", "width": 50, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
	])
	_slug_tree.row_selected.connect(_on_slug_selected)

	# Right of split: middle (viewer) + right (fields)
	var inner := HSplitContainer.new()
	inner.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	inner.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.add_child(inner)

	var middle := VBoxContainer.new()
	middle.custom_minimum_size = Vector2(VIEWER_WIDTH, 0)
	middle.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	middle.size_flags_vertical = Control.SIZE_EXPAND_FILL
	middle.add_theme_constant_override("separation", s.panel_separation)
	inner.add_child(middle)

	var mode_row := HBoxContainer.new()
	mode_row.add_theme_constant_override("separation", 4)
	middle.add_child(mode_row)
	_hitbox_btn = _make_mode_button("Hitbox", "hitbox")
	mode_row.add_child(_hitbox_btn)
	_scale_btn = _make_mode_button("Scale", "scale")
	mode_row.add_child(_scale_btn)
	_sync_mode_buttons()

	_viewer = NpcRenderViewer.new()
	_viewer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_viewer.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_viewer.hitbox_gizmo_changed.connect(_on_hitbox_gizmo_changed)
	_viewer.scale_gizmo_changed.connect(_on_scale_gizmo_changed)
	middle.add_child(_viewer)

	# Right: form + buttons
	var right_margin := MarginContainer.new()
	right_margin.add_theme_constant_override("margin_left", s.panel_separation)
	right_margin.custom_minimum_size = Vector2(FIELDS_WIDTH, 0)
	right_margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right_margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right_margin.clip_contents = true
	inner.add_child(right_margin)

	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", s.panel_separation)
	right_margin.add_child(right)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	right.add_child(scroll)

	_form_container = VBoxContainer.new()
	_form_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_form_container.add_theme_constant_override("separation", 4)
	scroll.add_child(_form_container)

	var buttons := HBoxContainer.new()
	buttons.add_theme_constant_override("separation", s.panel_separation)
	right.add_child(buttons)

	_save_btn = DevFormFields.action_button("Save Config", Color(0.28, 0.62, 0.28), SAVE_BUTTON_HEIGHT, 18)
	_save_btn.pressed.connect(_on_save_pressed)
	buttons.add_child(_save_btn)

	_reset_btn = DevFormFields.action_button("Reset Config", s.button_close, SAVE_BUTTON_HEIGHT, 18)
	_reset_btn.pressed.connect(_on_reset_pressed)
	buttons.add_child(_reset_btn)


func set_slugs(slugs: Array, type_counts: Dictionary) -> void:
	var rows: Array = []
	var sorted := slugs.duplicate()
	sorted.sort()
	for slug in sorted:
		var s := str(slug)
		rows.append({
			"values": [s, str(int(type_counts.get(s, 0)))],
			"metadata": s,
		})
	_slug_tree.set_rows(rows)
	if not _selected_slug.is_empty():
		_slug_tree.select_by_metadata(_selected_slug)


func _make_mode_button(text: String, mode: String) -> Button:
	var s := DevStyle.instance
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.focus_mode = Control.FOCUS_NONE
	b.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	b.custom_minimum_size = Vector2(0, MODE_BUTTON_HEIGHT)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.alignment = HORIZONTAL_ALIGNMENT_CENTER
	b.add_theme_font_size_override("font_size", s.font_size_body)
	b.add_theme_color_override("font_color", s.text_primary)
	b.add_theme_color_override("font_hover_color", s.text_primary)
	b.add_theme_color_override("font_pressed_color", s.text_heading)
	b.add_theme_color_override("font_hover_pressed_color", s.text_heading)
	b.pressed.connect(_on_mode_pressed.bind(mode))
	return b


func _sync_mode_buttons() -> void:
	if _hitbox_btn: _hitbox_btn.set_pressed_no_signal(_current_mode == "hitbox")
	if _scale_btn: _scale_btn.set_pressed_no_signal(_current_mode == "scale")


func _on_mode_pressed(mode: String) -> void:
	_current_mode = mode
	_sync_mode_buttons()
	_rebuild_form()
	_viewer.set_render_mode(mode)


func _on_slug_selected(slug: Variant) -> void:
	if slug == null: return
	var str_slug := str(slug)
	if str_slug.is_empty() or str_slug == _selected_slug: return
	_selected_slug = str_slug
	_reload_slug()


func _reload_slug() -> void:
	_hitbox_config = _load_hitbox(_selected_slug)
	_current_scale = _load_scale(_selected_slug)
	_hitbox_dirty = false
	_scale_dirty = false
	_viewer.load_render_model(_selected_slug, _current_scale)
	_viewer.push_hitbox(_hitbox_config)
	_viewer.push_scale(_current_scale, _viewer.measure_aabb().size)
	_viewer.set_render_mode(_current_mode)
	_rebuild_form()


func _rebuild_form() -> void:
	for c in _form_container.get_children():
		_form_container.remove_child(c)
		c.queue_free()

	if _selected_slug.is_empty(): return

	if _current_mode == "hitbox":
		_hitbox_form = NpcRenderHitboxForm.new()
		_hitbox_form.set_config(_hitbox_config)
		_hitbox_form.changed.connect(_on_hitbox_form_changed)
		_form_container.add_child(_hitbox_form)
	else:
		_scale_form = NpcRenderScaleForm.new()
		_scale_form.set_scale_value(_current_scale)
		_scale_form.changed.connect(_on_scale_form_changed)
		_form_container.add_child(_scale_form)


func _on_hitbox_form_changed(cfg: Dictionary) -> void:
	_hitbox_config = cfg.duplicate()
	_hitbox_dirty = true
	_viewer.push_hitbox(_hitbox_config)


func _on_scale_form_changed(sc: Vector3) -> void:
	_current_scale = sc
	_scale_dirty = true
	_viewer.push_scale(_current_scale, _viewer.measure_aabb().size)


func _on_hitbox_gizmo_changed(cfg: Dictionary) -> void:
	_hitbox_config = cfg.duplicate()
	_hitbox_dirty = true
	if _hitbox_form: _hitbox_form.set_config(_hitbox_config)


func _on_scale_gizmo_changed(sc: Vector3) -> void:
	_current_scale = sc
	_scale_dirty = true
	if _scale_form: _scale_form.set_scale_value(_current_scale)


func _load_hitbox(slug: String) -> Dictionary:
	var hb := NpcRender.get_hitbox(slug)
	if not hb.is_empty():
		return {
			"type": str(hb.get("type", "capsule")),
			"radius": float(hb.get("radius", 0.4)),
			"height": float(hb.get("height", 1.8)),
			"offset_x": float(hb.get("offset_x", 0.0)),
			"offset_y": float(hb.get("offset_y", 0.9)),
			"offset_z": float(hb.get("offset_z", 0.0)),
		}
	return _default_hitbox()


func _default_hitbox() -> Dictionary:
	var aabb := _viewer.measure_aabb()
	if aabb.size == Vector3.ZERO:
		return {"type": "capsule", "radius": 0.4, "height": 1.8,
				"offset_x": 0.0, "offset_y": 0.9, "offset_z": 0.0}
	var c := aabb.get_center()
	var radius := maxf(maxf(aabb.size.x, aabb.size.z) * 0.5, 0.1)
	var height := maxf(aabb.size.y, radius * 2.0 + 0.1)
	return {"type": "capsule", "radius": radius, "height": height,
			"offset_x": c.x, "offset_y": c.y, "offset_z": c.z}


func _load_scale(slug: String) -> Vector3:
	var rc := NpcRender.get_render_config(slug)
	if rc.is_empty(): return Vector3.ONE
	return Vector3(
		float(rc.get("scale_x", 1.0)),
		float(rc.get("scale_y", 1.0)),
		float(rc.get("scale_z", 1.0)))


func _on_save_pressed() -> void:
	if _selected_slug.is_empty(): return
	if _current_mode == "hitbox":
		_write_hitbox_cache(_selected_slug, _hitbox_config)
		Network.send_npc_render_save_hitbox(_selected_slug, _hitbox_config.duplicate())
	else:
		var render := {"scale_x": _current_scale.x, "scale_y": _current_scale.y, "scale_z": _current_scale.z}
		_write_render_cache(_selected_slug, render)
		Network.send_npc_render_save_render(_selected_slug, render)


func _on_reset_pressed() -> void:
	if _selected_slug.is_empty(): return
	Network.send_npc_render_reset_config(_selected_slug)
	await get_tree().create_timer(0.1).timeout
	_reload_slug()


func _write_hitbox_cache(slug: String, hb: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/hitbox.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(hb, "\t"))
	f.close()


func _write_render_cache(slug: String, render: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/render.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(render, "\t"))
	f.close()
