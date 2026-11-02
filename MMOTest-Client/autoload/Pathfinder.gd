extends Node
## Autoload "Pathfinder": async A* over the tile grid using World.is_walkable().
##
## Snapshot the search area on the main thread, run AStarGrid2D on a worker thread,
## emit results back on the main thread via call_deferred.
##
## Request IDs are monotonic. Callers tracking the latest request can ignore
## results from stale ones (clicks that were superseded before the worker finished).

signal path_found(request_id: int, path: Array)   # Array[Vector2i], excludes start
signal path_failed(request_id: int, reason: String)

const MAX_PATH_LENGTH := 2000
const SEARCH_PADDING := 20       # extra tiles around the start↔goal bounding box
const MAX_CLICK_DISTANCE := 200  # tiles; reject silly-far clicks

var _next_id := 1
var _pending: Dictionary = {}    # WorkerThreadPool task_id -> request_id


func request_path(start: Vector2i, goal: Vector2i) -> int:
	var rid := _next_id
	_next_id += 1

	if not World.is_loaded:
		path_failed.emit(rid, "world_not_loaded")
		return rid
	if start == goal:
		path_found.emit(rid, [])
		return rid
	var dist := maxi(absi(goal.x - start.x), absi(goal.y - start.y))
	if dist > MAX_CLICK_DISTANCE:
		path_failed.emit(rid, "too_far")
		return rid
	if not World.is_walkable(goal.x, goal.y):
		path_failed.emit(rid, "goal_not_walkable")
		return rid

	var walkable_set := _snapshot_walkable(start, goal, SEARCH_PADDING)
	var task_id := WorkerThreadPool.add_task(
		Callable(self, "_run_astar_task").bind(rid, start, goal, walkable_set),
		false,
		"A* pathfind")
	_pending[task_id] = rid
	return rid


func _snapshot_walkable(start: Vector2i, goal: Vector2i, padding: int) -> Dictionary:
	var min_x := mini(start.x, goal.x) - padding
	var max_x := maxi(start.x, goal.x) + padding
	var min_y := mini(start.y, goal.y) - padding
	var max_y := maxi(start.y, goal.y) + padding
	var out := {}
	for x in range(min_x, max_x + 1):
		for y in range(min_y, max_y + 1):
			if World.is_walkable(x, y):
				out[Vector2i(x, y)] = true
	return out


func _run_astar_task(rid: int, start: Vector2i, goal: Vector2i, walkable_set: Dictionary) -> void:
	# Worker thread. Do NOT touch World or any Node here.

	if not walkable_set.has(goal):
		call_deferred("_emit_result", rid, "goal_not_walkable")
		return
	if not walkable_set.has(start):
		# Player is between tiles (mid-step) or standing on void. Try the nearest
		# walkable neighbor as the effective start; if none, fail cleanly.
		var alt := _nearest_walkable(start, walkable_set)
		if alt.x == 0x7FFFFFFF:
			call_deferred("_emit_result", rid, "start_isolated")
			return
		start = alt

	var min_x := 0x7FFFFFFF
	var min_y := 0x7FFFFFFF
	var max_x := -0x7FFFFFFF
	var max_y := -0x7FFFFFFF
	for k in walkable_set:
		min_x = mini(min_x, k.x)
		min_y = mini(min_y, k.y)
		max_x = maxi(max_x, k.x)
		max_y = maxi(max_y, k.y)

	var astar := AStarGrid2D.new()
	astar.region = Rect2i(min_x, min_y, max_x - min_x + 1, max_y - min_y + 1)
	astar.cell_size = Vector2.ONE
	astar.diagonal_mode = AStarGrid2D.DIAGONAL_MODE_ONLY_IF_NO_OBSTACLES
	astar.default_compute_heuristic = AStarGrid2D.HEURISTIC_OCTILE
	astar.default_estimate_heuristic = AStarGrid2D.HEURISTIC_OCTILE
	astar.update()

	for x in range(min_x, max_x + 1):
		for y in range(min_y, max_y + 1):
			var k := Vector2i(x, y)
			if not walkable_set.has(k):
				astar.set_point_solid(k, true)

	var raw := astar.get_id_path(start, goal)
	if raw.is_empty():
		call_deferred("_emit_result", rid, "no_path")
		return

	var out: Array = []
	for i in range(1, raw.size()):   # drop start
		out.append(raw[i])
	if out.size() > MAX_PATH_LENGTH:
		call_deferred("_emit_result", rid, "path_too_long")
		return
	call_deferred("_emit_result", rid, out)


func _nearest_walkable(start: Vector2i, walkable_set: Dictionary) -> Vector2i:
	for dx in range(-1, 2):
		for dy in range(-1, 2):
			if dx == 0 and dy == 0: continue
			var k := start + Vector2i(dx, dy)
			if walkable_set.has(k):
				return k
	return Vector2i(0x7FFFFFFF, 0x7FFFFFFF)


func _emit_result(rid: int, result: Variant) -> void:
	if typeof(result) == TYPE_STRING:
		path_failed.emit(rid, result)
	else:
		path_found.emit(rid, result)


func _process(_delta: float) -> void:
	if _pending.is_empty(): return
	var done: Array = []
	for task_id in _pending:
		if WorkerThreadPool.is_task_completed(task_id):
			WorkerThreadPool.wait_for_task_completion(task_id)   # returns immediately
			done.append(task_id)
	for t in done:
		_pending.erase(t)
