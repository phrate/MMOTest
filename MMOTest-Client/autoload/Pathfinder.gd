extends Node
## Autoload "Pathfinder": async A* over the tile grid using World.is_walkable().
##
## If normal A* fails, a fallback pass treats non-walkable tiles as passable
## (weighted so walkable ground is preferred) and truncates the resulting path
## at the first step that would cross into — or diagonally corner-cut past — a
## non-walkable tile. The truncated prefix is guaranteed server-valid, so the
## player walks as close to the goal as the world permits.

signal path_found(request_id: int, path: Array)   # Array[Vector2i], excludes start
signal path_failed(request_id: int, reason: String)

const MAX_PATH_LENGTH := 2000
const SEARCH_PADDING := 20
const MAX_CLICK_DISTANCE := 200
const NON_WALKABLE_WEIGHT := 10.0   # fallback pass: prefer walkable ground

var _next_id := 1
var _pending: Dictionary = {}


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

	# Non-walkable goals are NOT rejected here; the fallback will try to get close.

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


func _run_astar_task(rid: int, orig_start: Vector2i, goal: Vector2i, walkable_set: Dictionary) -> void:
	# Worker thread. Do NOT touch World or any Node here.

	var start := orig_start
	var start_adjusted := false
	if not walkable_set.has(orig_start):
		var alt := _nearest_walkable(orig_start, walkable_set)
		if alt.x == 0x7FFFFFFF:
			call_deferred("_emit_result", rid, "start_isolated")
			return
		start = alt
		start_adjusted = true

	# Normal pass: only walkable tiles passable.
	var normal_path := _astar(walkable_set, start, goal, false)
	if not normal_path.is_empty():
		_emit_path_or_too_long(rid, _assemble_out(normal_path, start, start_adjusted))
		return

	# Fallback pass: everything passable, weighted to prefer walkable.
	var all_path := _astar(walkable_set, start, goal, true)
	if all_path.is_empty():
		call_deferred("_emit_result", rid, "no_path")
		return

	var truncated := _truncate_at_first_block(all_path, walkable_set)

	# If truncation yields only the start tile and the start was adjusted, still
	# report the step to the adjusted start — better than "no_path".
	if truncated.size() <= 1:
		if start_adjusted:
			call_deferred("_emit_result", rid, [start])
		else:
			call_deferred("_emit_result", rid, "no_path")
		return

	_emit_path_or_too_long(rid, _assemble_out(truncated, start, start_adjusted))


## Drops the A* start tile and, if the start was adjusted from the player's
## actual rounded tile, prepends the adjusted start so the server sees a valid
## first step from the player's tile.
func _assemble_out(path: Array, start: Vector2i, start_adjusted: bool) -> Array:
	var out: Array = []
	if start_adjusted:
		out.append(start)
	for i in range(1, path.size()):
		out.append(path[i])
	return out


func _astar(walkable_set: Dictionary, start: Vector2i, goal: Vector2i, pretend_all_walkable: bool) -> Array:
	var min_x := 0x7FFFFFFF
	var min_y := 0x7FFFFFFF
	var max_x := -0x7FFFFFFF
	var max_y := -0x7FFFFFFF
	for k in walkable_set:
		min_x = mini(min_x, k.x)
		min_y = mini(min_y, k.y)
		max_x = maxi(max_x, k.x)
		max_y = maxi(max_y, k.y)
	min_x = mini(min_x, mini(start.x, goal.x))
	min_y = mini(min_y, mini(start.y, goal.y))
	max_x = maxi(max_x, maxi(start.x, goal.x))
	max_y = maxi(max_y, maxi(start.y, goal.y))

	var astar := AStarGrid2D.new()
	astar.region = Rect2i(min_x, min_y, max_x - min_x + 1, max_y - min_y + 1)
	astar.cell_size = Vector2.ONE
	astar.diagonal_mode = AStarGrid2D.DIAGONAL_MODE_ONLY_IF_NO_OBSTACLES
	astar.default_compute_heuristic = AStarGrid2D.HEURISTIC_OCTILE
	astar.default_estimate_heuristic = AStarGrid2D.HEURISTIC_OCTILE
	astar.update()

	if pretend_all_walkable:
		for x in range(min_x, max_x + 1):
			for y in range(min_y, max_y + 1):
				var k := Vector2i(x, y)
				if not walkable_set.has(k):
					astar.set_point_weight_scale(k, NON_WALKABLE_WEIGHT)
	else:
		for x in range(min_x, max_x + 1):
			for y in range(min_y, max_y + 1):
				var k := Vector2i(x, y)
				if not walkable_set.has(k):
					astar.set_point_solid(k, true)

	return astar.get_id_path(start, goal)


## Walks the all-walkable path and cuts at the first step that would be blocked
## in the real world: landing on a non-walkable tile, or a diagonal whose corner
## tiles aren't both walkable. Result: a prefix the server will accept.
func _truncate_at_first_block(path: Array, walkable_set: Dictionary) -> Array:
	var result: Array = [path[0]]
	for i in range(1, path.size()):
		var prev: Vector2i = path[i - 1]
		var cur: Vector2i = path[i]
		if not walkable_set.has(cur):
			return result
		if prev.x != cur.x and prev.y != cur.y:
			if not walkable_set.has(Vector2i(cur.x, prev.y)):
				return result
			if not walkable_set.has(Vector2i(prev.x, cur.y)):
				return result
		result.append(cur)
	return result


## Picks a walkable neighbour for an off-tile start. Orthogonal neighbours first
## (server-valid regardless), then diagonals only if both corners are walkable.
func _nearest_walkable(start: Vector2i, walkable_set: Dictionary) -> Vector2i:
	var orth: Array[Vector2i] = [Vector2i(1,0), Vector2i(-1,0), Vector2i(0,1), Vector2i(0,-1)]
	for d in orth:
		var k: Vector2i = start + d
		if walkable_set.has(k):
			return k
	var diag: Array[Vector2i] = [Vector2i(1,1), Vector2i(1,-1), Vector2i(-1,1), Vector2i(-1,-1)]
	for d in diag:
		var k: Vector2i = start + d
		if walkable_set.has(k):
			if walkable_set.has(Vector2i(k.x, start.y)) and walkable_set.has(Vector2i(start.x, k.y)):
				return k
	return Vector2i(0x7FFFFFFF, 0x7FFFFFFF)


func _emit_path_or_too_long(rid: int, out: Array) -> void:
	if out.size() > MAX_PATH_LENGTH:
		call_deferred("_emit_result", rid, "path_too_long")
		return
	call_deferred("_emit_result", rid, out)


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
			WorkerThreadPool.wait_for_task_completion(task_id)
			done.append(task_id)
	for t in done:
		_pending.erase(t)
