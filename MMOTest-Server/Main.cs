using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MMOTest.Server;

public partial class Main : Node
{
	private const int Port = 9999;
	private const int MaxClients = 64;
	private const int MinUsernameLength = 3;
	private const int MaxUsernameLength = 20;
	private const int MinPasswordLength = 8;
	private const int MaxPasswordLength = 128;

	private const double TickIntervalSeconds = 0.3;
	private const float WalkSpeed = 2.0f;
	private const double WorldCheckIntervalSeconds = 1.0;

	private const int MaxPathLength = 2000;

	private static readonly string[] WorldFileCandidates =
	{
		ProjectSettings.GlobalizePath("user://world.json"),
		ProjectSettings.GlobalizePath("res://data/world.json"),
	};

	private static readonly Regex UsernameRegex = new($"^[A-Za-z0-9_]{{{MinUsernameLength},{MaxUsernameLength}}}$");

	private record PlayerSession(long PlayerId, string Username, bool IsAdmin, long Sid);
	internal record PlayerPosition(float X, float Y, float Z, float Yaw);
	internal record TileInfo(int Z, bool Walkable);

	private Database _db;
	private SceneMultiplayer _mp;
	private ENetMultiplayerPeer _enet;

	private readonly HashSet<long> _connectedPeers = new();
	private readonly HashSet<long> _authInProgress = new();
	private readonly Dictionary<long, PlayerSession> _sessions = new();
	private readonly Dictionary<long, long> _peerByPlayerId = new();
	private readonly Dictionary<long, PlayerPosition> _positions = new();

	private double _npcVisRefreshAccum;
	private const double NpcVisRefreshIntervalSeconds = 1.5;
	private int _npcVisRefreshCounter;
	private readonly Dictionary<long, int> _npcCombatRefCount = new();

	// TEMP-DEV: scaffolding to exercise NPC movement before real AI lands.
	// Delete this block (and DiagNpcWander export, DebugWanderTick, and the
	// call to DebugWanderTick in _Process) when AI packages are wired in.
	[ExportGroup("NPC")]
	[Export] public int NpcViewChunkRadius { get; set; } = 10;
	[Export] public bool DiagNpcWander { get; set; } = false;
	private double _wanderAccumulator;
	private const double WanderIntervalSeconds = 3.0;
	private readonly Random _wanderRng = new();

	private readonly Dictionary<long, ActiveNpc> _npcs = new();
	private readonly Dictionary<long, HashSet<long>> _visibleNpcs = new();  // peerId -> npc ids
	// ---------- Combat state ----------
	internal sealed class CombatPair
{
	public long PlayerId { get; init; }
	public long NpcId { get; init; }
	public long LastPlayerSwingTick { get; set; }
	public long LastNpcSwingTick { get; set; }
	public bool StateAnnounced { get; set; }
	public bool InitiatedByPlayer { get; set; } = true;
	public Vector2I LastChaseTarget { get; set; } = new(-99999, -99999);
}
	private readonly Dictionary<long, CombatPair> _combatByPlayer = new();
	private readonly Dictionary<long, long> _pendingEngage = new(); // playerId -> npcId (approach phase)
	private readonly Dictionary<long, CombatPair> _combatByNpc = new();
	private readonly Dictionary<long, Database.PlayerCombatData> _playerCombat = new();
	private readonly Dictionary<long, int> _playerHealAccumTicks = new();
	private readonly Random _combatRng = new();
private const int PlayerAttackSpeedTicks = 10;   // 3.0s unarmed default; equipment will override
private const int AttackAnimDurationMs = 400;    // short swing visual, independent of cooldown	private const int CombatMaxDistanceTiles = 12;
	private const int NpcLeashRadiusTiles = 15;

	private readonly Dictionary<long, Vector2I> _lastPlayerChunk = new();   // peerId -> chunk

	// Per-peer remaining path. Front of the list is the next waypoint. Empty list
	// (transient) means "arrived, about to be cleaned up in the same tick".
	private readonly Dictionary<long, List<Vector2I>> _intents = new();

	private readonly Dictionary<(int X, int Y), TileInfo> _tiles = new();

	private double _tickAccumulator = 0.0;
	private long _currentTick = 0;

	private double _worldCheckAccumulator = 0.0;
	private int _lastBroadcastVersion = -1;

	private string _dummyHash;

	private JsonDocument _worldDoc;
	private string _worldPath;
	private int _worldVersion;
	private int _worldFormatVersion;
	private int _chunkSize = 8;
	private DateTime _worldMtime;
	private long _worldSize;

	private Gfs.GfsManager _gfs;
	private Gfs.FunctionRegistry _gfsFns;
	private Gfs.PerTickManager _perTick;
	private NpcRenderManager _npcRender;

	public override void _Ready()
	{
		// Wire the Diag groups from the scene's export toggles.
		var enabledGroups = new List<string>();
		if (DiagLogin)       enabledGroups.Add("diag_login");
		if (DiagDatabase)    enabledGroups.Add("diag_database");
		if (DiagWorld)       enabledGroups.Add("diag_world");
		if (DiagGfs)         enabledGroups.Add("diag_gfs");
		if (DiagNetwork)     enabledGroups.Add("diag_network");
		if (DiagTick)        enabledGroups.Add("diag_tick");
		if (DiagPathfinding) enabledGroups.Add("diag_pathfinding");
		if (DiagChat)        enabledGroups.Add("diag_chat");
		if (DiagNpc)         enabledGroups.Add("diag_npc");
		Diag.Configure(DiagVerbose, enabledGroups);
		GD.Print($"[Server] Diagnostics: verbose={DiagVerbose}, enabled=[{string.Join(",", enabledGroups)}]");
		
		try
		{
			string dbPath = ProjectSettings.GlobalizePath("user://mmotest.db");
			Diag.Log("diag_database", $"DB path: {dbPath}");
			Diag.Log("diag_database", $"DB exists: {File.Exists(dbPath)}");

			_db = new Database(dbPath);
			_db.Initialize();
			using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				conn.Open();
				using var cmd = conn.CreateCommand();
				cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
				using var reader = cmd.ExecuteReader();
				var tables = new System.Collections.Generic.List<string>();
				while (reader.Read()) tables.Add(reader.GetString(0));
				GD.Print($"[Server] DB tables: {string.Join(", ", tables)}");
			}
			_dummyHash = PasswordHasher.Hash("dummy-password-for-timing");
			GD.Print($"[Server] SQLite {_db.GetSqliteVersion()} ready at {dbPath}");

			int respawned = _db.RespawnAllDeadNpcsSync();
			if (respawned > 0)
				GD.Print($"[Server] Auto-respawned {respawned} dead NPC(s) on startup");

			if (EnsureWorldLoaded())
			{
				_lastBroadcastVersion = _worldVersion;
				GD.Print($"[Server] World ready: v{_worldVersion} format {_worldFormatVersion}, {_tiles.Count} tiles ({_worldPath})");
			}
			else
			{
				GD.PrintErr("[Server] No world file loaded; world_info/world_fetch will report unavailable");
			}

			try
			{
				var npcList = _db.GetAllNpcsSync();
				foreach (var npc in npcList) _npcs[npc.Id] = npc;
				GD.Print($"[Server] Loaded {_npcs.Count} NPC(s) from DB");
			}
			catch (Exception e)
			{
				GD.PrintErr($"[Server] Failed to load NPCs: {e.Message}");
			}
			

			if (File.Exists(TooltipsFileCandidates[0]) || File.Exists(TooltipsFileCandidates[1]))
				GD.Print("[Server] Tooltips: available");
			else
				GD.PrintErr("[Server] Tooltips: not found (right-click menus will be empty)");


			_gfs = new Gfs.GfsManager(GfsScriptDirs, GfsTooltipsFileCandidates);
			_gfs.InitialLoad();
			_gfsFns = new Gfs.FunctionRegistry();
			Gfs.Utils.TilesUtils.Register(_gfsFns, this);
			Gfs.Utils.PlayerUtils.Register(_gfsFns, this);
			Gfs.Utils.CombatUtils.Register(_gfsFns, this);
			_perTick = new Gfs.PerTickManager(_gfsFns);
			_gfs.ScriptsChanged += OnGfsScriptsChanged;

			_npcRender = new NpcRenderManager(new[]
			{
				ProjectSettings.GlobalizePath("res://npc_render"),
				ProjectSettings.GlobalizePath("user://npc_render"),
			});
			_npcRender.LoadAll();


			var peer = new ENetMultiplayerPeer();
			Error err = peer.CreateServer(Port, MaxClients);
			if (err != Error.Ok)
			{
				GD.PrintErr($"[Server] Failed to listen on port {Port}: {err}");
				GetTree().Quit(1);
				return;
			}

			_enet = peer;
			_mp = new SceneMultiplayer();
			_mp.MultiplayerPeer = peer;
			GetTree().SetMultiplayer(_mp);

			_mp.PeerConnected += OnPeerConnected;
			_mp.PeerDisconnected += OnPeerDisconnected;
			_mp.PeerPacket += OnPacket;

			GD.Print($"[Server] Listening on port {Port} (tick = {TickIntervalSeconds * 1000}ms)");
		}
		catch(Exception e)
		{
			GD.PrintErr($"[Server] FATAL: _Ready failed — {e.GetType().Name}: {e.Message}");
			GD.PrintErr(e.StackTrace);
			GetTree().Quit(1);
			throw;
		}
	}

	public override void _ExitTree()
	{
		_worldDoc?.Dispose();
		_worldDoc = null;
	}

	public override void _Process(double delta)
	{
		if (_gfs == null || _perTick == null)
		{
			// _Ready hasn't finished (or failed). Don't spam errors while we wait
			// for the real diagnostic to surface.
			return;
		}

		_tickAccumulator += delta;
		while (_tickAccumulator >= TickIntervalSeconds)
		{
			_tickAccumulator -= TickIntervalSeconds;
			RunTick();
		}

		_worldCheckAccumulator += delta;
		if (_worldCheckAccumulator >= WorldCheckIntervalSeconds)
		{
			_worldCheckAccumulator = 0;
			CheckWorldForUpdates();
		}

		if (_npcRender != null && _npcRender.Poll())
		{
			Broadcast(new
			{
				type = "npc_render_updated",
				slugs = _npcRender.GetManifest()
			});
			Diag.Log("diag_npc", $"Render bundles updated, notified {_sessions.Count} client(s)");
		}

		DebugWanderTick(delta);   // TEMP-DEV

		_gfs.Poll();
	}

	// ---------- Tick ----------

	private void RunTick()
	{
		_currentTick++;

		// 1. Movement processing (only if there are intents to process)
		if (_intents.Count > 0)
		{
			var updates = new List<object>();
			var donePeers = new List<long>();

			foreach (long peerId in _intents.Keys.ToList())
			{
				if (!_sessions.TryGetValue(peerId, out PlayerSession session)) { donePeers.Add(peerId); continue; }
				if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition current)) { donePeers.Add(peerId); continue; }

				var path = _intents[peerId];
				if (path.Count == 0) { donePeers.Add(peerId); continue; }

				var (next, finished) = StepTowardPath(current, path);
				_positions[session.PlayerId] = next;
				updates.Add(new { player_id = session.PlayerId, x = next.X, y = next.Y, z = next.Z, yaw = next.Yaw });

				if (finished) donePeers.Add(peerId);
			}

			foreach (long p in donePeers) _intents.Remove(p);
			if (updates.Count > 0)
			{
				Broadcast(new { type = "tick_positions", tick = _currentTick, updates },
					channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
			}
		}

		// 2. NPC movement
		if (_npcs.Count > 0)
		{
			var npcUpdates = new List<object>();
			foreach (var npc in _npcs.Values)
			{
				if (npc.IsDead) continue;
				if (npc.ReturningHome && npc.Path.Count == 0 && DistanceFromSpawn(npc) <= 1)
				{
					npc.ReturningHome = false;
					// No instant heal. Over-time healing (ProcessNpcHealing) handles regen.
				}
				if (npc.Path.Count == 0) continue;

				StepNpcMovement(npc);

				if (MathF.Abs(npc.PosX - npc.LastSentX) > 0.001f ||
					MathF.Abs(npc.PosY - npc.LastSentY) > 0.001f ||
					MathF.Abs(npc.PosZ - npc.LastSentZ) > 0.001f ||
					MathF.Abs(npc.Yaw  - npc.LastSentYaw) > 0.01f)
				{
					npc.LastSentX = npc.PosX;
					npc.LastSentY = npc.PosY;
					npc.LastSentZ = npc.PosZ;
					npc.LastSentYaw = npc.Yaw;
					npcUpdates.Add(new
					{
						npc_id = npc.Id,
						x = npc.PosX,
						y = npc.PosY,
						z = npc.PosZ,
						yaw = npc.Yaw
					});
				}
			}
			if (npcUpdates.Count > 0)
			{
				Broadcast(new { type = "npc_tick_positions", updates = npcUpdates },
					channel: 1, mode : MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
			}
		}

		// 2b. NPC respawn sweep
		CheckNpcRespawns();

		// 2c. Combat
		ProcessHostility();
		ProcessPendingEngage();
		ProcessCombat();

		// 2d. Healing
		ProcessNpcHealing();
		ProcessPlayerHealing();

		// 3. Per-tick scripts (ALWAYS run, even when no movement is in flight).
		var tickEffects = _perTick.TickAll();
		if (tickEffects.Count > 0)
		{
			foreach (var (pid, effects) in tickEffects)
			{
				if (!_positions.TryGetValue(pid, out var ppos)) continue;
				int pcx = (int)MathF.Floor((float)MathF.Round(ppos.X) / _chunkSize);
				int pcy = (int)MathF.Floor((float)MathF.Round(ppos.Z) / _chunkSize);

				string username = "";
				long selfPeer = -1;
				foreach (var kv in _sessions)
					if (kv.Value.PlayerId == pid) { username = kv.Value.Username; selfPeer = kv.Key; break; }

				RouteChatEffects(pid, username, pcx, pcy, effects, selfPeer);
			}
		}

		// NPC visibility: refresh only when a player crosses a chunk boundary.
		_npcVisRefreshCounter++;
		bool periodicNpcRefresh = _npcVisRefreshCounter >= 5;
		if (periodicNpcRefresh) _npcVisRefreshCounter = 0;

		foreach (long peerId in _sessions.Keys.ToList())
		{
			if (!_sessions.TryGetValue(peerId, out var sess)) continue;
			if (!_positions.TryGetValue(sess.PlayerId, out var pos)) continue;
			int tx = (int)MathF.Round(pos.X);
			int ty = (int)MathF.Round(pos.Z);
			var chunk = new Vector2I(
				ActiveNpc.FloorDiv(tx, _chunkSize),
				ActiveNpc.FloorDiv(ty, _chunkSize));

			bool playerChunkChanged =
				!_lastPlayerChunk.TryGetValue(peerId, out var last) || last != chunk;
			if (playerChunkChanged) _lastPlayerChunk[peerId] = chunk;

			if (playerChunkChanged || periodicNpcRefresh)
				RefreshNpcVisibility(peerId, tx, ty);
		}
	}

	/// <summary>
	/// Advances up to WalkSpeed*Tick seconds of distance along the path, consuming waypoints
	/// as they're reached and carrying leftover distance into the next segment. The result
	/// is a constant-velocity trajectory with no stops at intermediate tiles.
	/// </summary>
	private (PlayerPosition, bool) StepTowardPath(PlayerPosition current, List<Vector2I> path)
	{
		float remaining = WalkSpeed * (float)TickIntervalSeconds;
		float x = current.X;
		float z = current.Z;
		float yaw = current.Yaw;

		while (remaining > 0.0001f && path.Count > 0)
		{
			var target = path[0];
			float dx = target.X - x;
			float dz = target.Y - z;
			float dist = MathF.Sqrt(dx * dx + dz * dz);

			if (dist < 0.0001f)
			{
				path.RemoveAt(0);
				continue;
			}

			yaw = MathF.Atan2(-dx, -dz);

			if (dist <= remaining)
			{
				// Reach the waypoint and continue with whatever distance is left.
				x = target.X;
				z = target.Y;
				remaining -= dist;
				path.RemoveAt(0);
			}
			else
			{
				// Partial step; we're mid-segment at the end of this tick.
				float ratio = remaining / dist;
				x += dx * ratio;
				z += dz * ratio;
				remaining = 0;
			}
		}

		int tileX = (int)MathF.Round(x);
		int tileY = (int)MathF.Round(z);
		var tinfo = GetTileInfo(tileX, tileY);
		return (new PlayerPosition(x, tinfo.Z, z, yaw), path.Count == 0);
	}

	private void StepNpcMovement(ActiveNpc npc)
	{
		float remaining = WalkSpeed * (float)TickIntervalSeconds;
		float x = npc.PosX;
		float z = npc.PosZ;
		float yaw = npc.Yaw;

		while (remaining > 0.0001f && npc.Path.Count > 0)
		{
			var target = npc.Path[0];
			float dx = target.X - x;
			float dz = target.Y - z;
			float dist = MathF.Sqrt(dx * dx + dz * dz);

			if (dist < 0.0001f)
			{
				npc.Path.RemoveAt(0);
				continue;
			}

			yaw = MathF.Atan2(-dx, -dz);

			if (dist <= remaining)
			{
				x = target.X;
				z = target.Y;
				remaining -= dist;
				npc.Path.RemoveAt(0);
			}
			else
			{
				float ratio = remaining / dist;
				x += dx * ratio;
				z += dz * ratio;
				remaining = 0;
			}
		}

		int tileX = (int)MathF.Round(x);
		int tileY = (int)MathF.Round(z);
		var tinfo = GetTileInfo(tileX, tileY);

		npc.PosX = x;
		npc.PosY = tinfo.Z;
		npc.PosZ = z;
		npc.Yaw = yaw;
	}

	private TileInfo GetTileInfo(int x, int y)
	{
		if (_tiles.TryGetValue((x, y), out TileInfo t)) return t;
		return new TileInfo(100, true);
	}

	// ---------- World file updates ----------

	private void CheckWorldForUpdates()
	{
		if (!EnsureWorldLoaded()) return;
		if (_worldVersion == _lastBroadcastVersion) return;

		_lastBroadcastVersion = _worldVersion;
		Diag.Log("diag_world", $"World hotfix: v{_worldVersion}, notifying {_sessions.Count} client(s)");

		Broadcast(new
		{
			type = "world_updated",
			world_version = _worldVersion,
			format_version = _worldFormatVersion
		});
	}

	private static readonly string[] TooltipsFileCandidates =
	{
		ProjectSettings.GlobalizePath("user://tooltips.json"),
		ProjectSettings.GlobalizePath("res://data/tooltips.json"),
	};

	private static readonly string[] TooltipsScriptDirs =
	{
		ProjectSettings.GlobalizePath("user://data/scripts/gfs/tooltips"),
		ProjectSettings.GlobalizePath("res://data/scripts/gfs/tooltips"),
	};

	private static readonly string[] GfsScriptDirs =
	{
		ProjectSettings.GlobalizePath("user://data/scripts/gfs"),
		ProjectSettings.GlobalizePath("res://data/scripts/gfs"),
	};

	private static readonly string[] GfsTooltipsFileCandidates =
	{
		ProjectSettings.GlobalizePath("user://tooltips.json"),
		ProjectSettings.GlobalizePath("res://data/tooltips.json"),
	};

	// ============================================================================
	// Diagnostics
	// ============================================================================
	// Configure in the scene inspector. All groups default to off — the server
	// runs silent except for essential startup confirmations. Turn on Verbose to
	// enable all groups at once (useful for a fresh debugging session); the
	// individual toggles let you narrow down what you're watching.
	//
	// Changes require a server restart to take effect.
	// ============================================================================

	[ExportGroup("Diagnostics")]
	[Export] public bool DiagVerbose { get; set; } = false;

	[ExportGroup("Diagnostics/Groups")]
	[Export] public bool DiagLogin { get; set; } = false;
	[Export] public bool DiagDatabase { get; set; } = false;
	[Export] public bool DiagWorld { get; set; } = false;
	[Export] public bool DiagGfs { get; set; } = false;
	[Export] public bool DiagNetwork { get; set; } = false;
	[Export] public bool DiagTick { get; set; } = false;
	[Export] public bool DiagPathfinding { get; set; } = false;
	[Export] public bool DiagChat { get; set; } = false;
	[Export] public bool DiagNpc { get; set; } = false;

	// ---------- Connection events ----------

	private void OnPeerConnected(long peerId)
	{
		_connectedPeers.Add(peerId);
		Diag.Log("diag_network", $"Peer connected: {peerId}");
	}

	private void OnPeerDisconnected(long peerId)
	{
		_connectedPeers.Remove(peerId);
		_authInProgress.Remove(peerId);
		_intents.Remove(peerId);

		if (_sessions.Remove(peerId, out PlayerSession session))
		{
			_perTick?.EndAllFor(session.PlayerId);          // MOVED INSIDE — session is non-null here
			if (_combatByPlayer.TryGetValue(session.PlayerId, out var combatPair))
				EndCombat(combatPair);
			_playerCombat.Remove(session.PlayerId);
			_pendingEngage.Remove(session.PlayerId);
			_playerHealAccumTicks.Remove(session.PlayerId);
			_peerByPlayerId.Remove(session.PlayerId);

			if (_positions.TryGetValue(session.PlayerId, out PlayerPosition pos))
			{
				int tx = (int)MathF.Round(pos.X);
				int ty = (int)MathF.Round(pos.Z);
				int tz = (int)MathF.Round(pos.Y);
				_ = _db.SetPlayerTileAsync(session.PlayerId, tx, ty, tz);
			}
			_positions.Remove(session.PlayerId);
			_lastPlayerChunk.Remove(peerId);
			_visibleNpcs.Remove(peerId);

			Broadcast(
				new { type = "player_left", player_id = session.PlayerId, username = session.Username },
				exceptPeerId: peerId);
		}

		Diag.Log("diag_network", $"Peer disconnected: {peerId}");
	}

	private void OnPacket(long peerId, byte[] packet) => _ = HandlePacketAsync(peerId, packet);

	private async Task HandlePacketAsync(long peerId, byte[] packet)
	{
		try
		{
			using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(packet));
			JsonElement root = doc.RootElement;
			string type = root.GetProperty("type").GetString();

			switch (type)
			{
				case "hello":       await HandleHelloAsync(peerId); break;
				case "register":
				{
					bool isDev = OS.IsDebugBuild()
						&& root.TryGetProperty("is_dev", out var devEl)
						&& devEl.ValueKind == JsonValueKind.True;
					await HandleRegisterAsync(peerId, GetString(root, "username"), GetString(root, "password"), isDev);
					break;
				}
				case "login":       await HandleLoginAsync(peerId, GetString(root, "username"), GetString(root, "password"), GetString(root, "reason")); break;
				case "world_info":  HandleWorldInfo(peerId); break;
				case "world_fetch": HandleWorldFetch(peerId); break;
				case "npc_editor_get":  HandleNpcEditorGet(peerId, root); break;
				case "npc_editor_save": HandleNpcEditorSave(peerId, root); break;
				case "dev_auto_login": await HandleDevAutoLoginAsync(peerId); break;
				case "player_walk": HandlePlayerWalkBuffered(peerId, root); break;
				case "ping":         HandlePing(peerId, root); break;
				case "dev_shutdown": HandleDevShutdown(peerId); break;
				case "chat_send": HandleChatSend(peerId, root); break;
				case "tooltips_info": HandleTooltipsInfo(peerId); break;
				case "gfs_info":    HandleGfsInfo(peerId); break;
				case "run_action":  HandleRunAction(peerId, root); break;
				case "npc_render_info_request": HandleNpcRenderInfo(peerId); break;
				case "npc_render_fetch":        HandleNpcRenderFetch(peerId, root); break;
				default:
					GD.PrintErr($"[Server] Unknown message type '{type}' from {peerId}");
					break;
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"[Server] Error handling packet from {peerId}: {e.Message}");
		}
	}

	private bool _shuttingDown = false;

	private void HandleTooltipsInfo(long peerId)
	{
		if (!_sessions.ContainsKey(peerId))
		{
			SendError(peerId, "tooltips_info_result", ErrorKeys.WorldNotLoggedIn);
			return;
		}

		string tooltipsText = null;
		foreach (string path in TooltipsFileCandidates)
		{
			if (File.Exists(path)) { tooltipsText = File.ReadAllText(path); break; }
		}
		if (tooltipsText == null)
		{
			SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
			return;
		}

		try
		{
			using var doc = JsonDocument.Parse(tooltipsText);
			var root = doc.RootElement;
			if (!root.TryGetProperty("format_version", out JsonElement fmt) ||
				!root.TryGetProperty("version", out JsonElement ver) ||
				!root.TryGetProperty("entries", out JsonElement entries) ||
				entries.ValueKind != JsonValueKind.Object)
			{
				SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
				return;
			}

			// Entries from tooltips.json: entity_type -> script filename.
			var entriesDict = new Dictionary<string, string>();
			foreach (JsonProperty entry in entries.EnumerateObject())
			{
				if (entry.Value.ValueKind != JsonValueKind.String) continue;
				string scriptName = entry.Value.GetString();
				if (string.IsNullOrEmpty(scriptName)) continue;
				entriesDict[entry.Name] = scriptName;
			}

			// Enumerate every .gfs file in both script directories. user:// takes
			// precedence over res:// on a name collision, matching Godot's loading order.
			var scriptsDict = new Dictionary<string, string>();
			foreach (string dir in TooltipsScriptDirs)
			{
				if (!Directory.Exists(dir)) continue;
				foreach (string file in Directory.GetFiles(dir, "*.gfs"))
				{
					string name = Path.GetFileName(file);
					if (scriptsDict.ContainsKey(name)) continue;
					try { scriptsDict[name] = File.ReadAllText(file); }
					catch (Exception e) { GD.PrintErr($"[Server] Cannot read {file}: {e.Message}"); }
				}
			}

			if (OS.IsDebugBuild())
			{
				GD.Print($"[Server] Tooltips: entries={entriesDict.Count}, scripts={scriptsDict.Count}");
				foreach (var kv in scriptsDict)
					GD.Print($"[Server]   script: {kv.Key} ({kv.Value.Length} bytes)");
			}

			Send(peerId, new
			{
				type = "tooltips_info_result",
				ok = true,
				version = ver.GetInt32(),
				format_version = fmt.GetInt32(),
				entries = entriesDict,
				scripts = scriptsDict
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[Server] tooltips_info error: {e.Message}");
			SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
		}
	}

	private void HandleGfsInfo(long peerId)
	{
		if (!_sessions.ContainsKey(peerId))
		{
			SendError(peerId, "gfs_info_result", ErrorKeys.WorldNotLoggedIn);
			return;
		}
		var bundle = _gfs.BuildBundle();
		bundle["type"] = "gfs_info_result";
		bundle["ok"] = true;
		Send(peerId, bundle);
	}

	private void OnGfsScriptsChanged(Dictionary<string, Dictionary<string, object>> changed, int version)
	{
		if (_sessions.Count == 0) return;
		Broadcast(new
		{
			type = "gfs_updated",
			version,
			scripts = changed
		});
		GD.Print($"[Server] GFS: broadcast {changed.Count} changed script(s), version {version}");
	}

	private void HandleRunAction(long peerId, JsonElement root)
	{
		if (!_sessions.TryGetValue(peerId, out PlayerSession session)) { GD.Print("[DIAG] run_action: no session"); return; }
		if (!root.TryGetProperty("script", out var scriptEl)) { GD.Print("[DIAG] run_action: no script"); return; }
		string scriptPath = scriptEl.GetString() ?? "";
		var ast = _gfs.GetAst(scriptPath);
		//GD.Print($"[DIAG] run_action from {peerId}: script={scriptPath} target={(root.TryGetProperty("target_id", out var t) ? t.ToString() : "none")} ast={(ast == null ? "NULL" : "ok")}");
		if (ast == null)
		{
			SendError(peerId, "run_action_result", "gfs.script_not_found");
			return;
		}

		var args = new Dictionary<string, object>();
		if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
		{
			foreach (var prop in argsEl.EnumerateObject())
			{
				args[prop.Name] = prop.Value.ValueKind switch
				{
					JsonValueKind.String => prop.Value.GetString(),
					JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
					JsonValueKind.True => true,
					JsonValueKind.False => false,
					_ => null
				};
			}
		}
		int targetId = 0;
		if (root.TryGetProperty("target_id", out var tidEl) && tidEl.TryGetInt32(out int tid)) targetId = tid;

		var system = new Dictionary<string, object>
		{
			["SELF_ID"] = session.PlayerId,
			["PLAYER_ID"] = targetId,
			["SELF_USERNAME"] = session.Username,
		};

		var ctx = new Gfs.CallContext
		{
			System = system,
			Args = args,
			Locals = new Dictionary<string, object>(),
			SelfPlayerId = session.PlayerId,
			ScriptPath = scriptPath,
			TaskLookup = _gfs.GetTask,
		};
		ctx.RunScript = (s, a) => RunScriptFromGfs(session.PlayerId, s, a, ctx);

		List<Dictionary<string, object>> effects;
		try { effects = Gfs.Interpreter.Run(ast, ctx, _gfsFns); }
		catch (Exception e)
		{
			Diag.Error("diag_gfs", $"run_action error: {e.Message}");
			SendError(peerId, "run_action_result", "gfs.runtime_error");
			return;
		}
		//GD.Print($"[DIAG] after run: effects={effects.Count} endReq={ctx.EndScriptRequested} tags=[{string.Join(",", ast.Tags)}]");

		if (_positions.TryGetValue(session.PlayerId, out var pos))
		{
			int cx = (int)MathF.Floor((float)MathF.Round(pos.X) / _chunkSize);
			int cy = (int)MathF.Floor((float)MathF.Round(pos.Z) / _chunkSize);
			RouteChatEffects(session.PlayerId, session.Username, cx, cy, effects, peerId);
		}

		if (ast.Tags.Contains(Gfs.GfsTags.OncePerTick) && !ctx.EndScriptRequested)
			_perTick.Start(session.PlayerId, scriptPath, ast, ctx);

		Send(peerId, new { type = "run_action_result", ok = true, effects });
	}

	private void HandlePing(long peerId, JsonElement root)
	{
		if (!_sessions.ContainsKey(peerId)) return;
		if (!root.TryGetProperty("t", out JsonElement tEl) || !tEl.TryGetInt64(out long clientT)) return;
		Send(peerId, new { type = "pong", t = clientT });
	}

	/// <summary>
	/// Debug-only: an admin client can trigger a graceful shutdown over the network.
	/// Handy when the server runs headless and the editor stop button isn't available.
	/// </summary>
	private void HandleDevShutdown(long peerId)
	{
		if (!OS.IsDebugBuild()) return;
		if (!_sessions.TryGetValue(peerId, out PlayerSession s) || !s.IsAdmin) return;
		GD.Print($"[Server] dev_shutdown requested by {s.Username}");
		GracefulShutdown();
	}

	public override void _Notification(int what)
	{
		if (what == (int)NotificationWMCloseRequest)
		{
			GracefulShutdown();
		}
	}

	/// <summary>
	/// Broadcasts a shutdown message so clients can auto-reconnect, lets the main
	/// loop run long enough for ENet to flush, then closes the peer and quits.
	///
	/// Async on purpose. OS.DelayMsec blocks the main thread — and ENet's poll
	/// runs on the main thread — so the shutdown packet would sit in the outgoing
	/// queue and be discarded by Close(). Awaiting a SceneTreeTimer yields to the
	/// main loop, letting the poll flush the packet before the peer closes.
	/// </summary>
	private async void GracefulShutdown()
	{
		if (_shuttingDown) return;
		_shuttingDown = true;
		GD.Print($"[Server] Graceful shutdown: notifying {_sessions.Count} client(s)");

		Broadcast(new
		{
			type = "shutdown",
			reason = "restart",
			reconnect_after_ms = 500
		});

		// ~18 frames at 60 fps. ENet flushes on the first poll after the broadcast,
		// so this is generous headroom for a slow frame.
		await ToSignal(GetTree().CreateTimer(0.3), "timeout");

		if (_mp != null && _mp.MultiplayerPeer != null)
			_mp.MultiplayerPeer.Close();

		await ToSignal(GetTree().CreateTimer(0.1), "timeout");

		GetTree().Quit();
	}

	private static string GetString(JsonElement root, string property)
	{
		return root.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? "" : "";
	}

	// ---------- Gameplay ----------

	/// <summary>
	/// Buffers a walk path. Validates: session, path length, all tiles walkable,
	/// consecutive tiles adjacent (8-connected, no diagonal corner-cutting), and
	/// first step adjacent to the player's current tile (guards against
	/// teleport-by-crafted-path).
	///
	/// Diagonal corner rule: a step from (x, y) to (x±1, y±1) is only valid if
	/// both (x±1, y) and (x, y±1) are walkable. Matches the client's A* setting
	/// (AStarGrid2D.DIAGONAL_MODE_ONLY_IF_NO_OBSTACLES); if these ever diverge,
	/// a client's valid-looking path will be silently rejected server-side.
	/// </summary>
	private void HandlePlayerWalkBuffered(long peerId, JsonElement root)
	{
		if (!_sessions.TryGetValue(peerId, out PlayerSession session)) return;
		// Cancel any pending approach when the player manually walks.
		_pendingEngage.Remove(session.PlayerId);

		// Cancel any unannounced combat pair (defensive — shouldn't exist in
		// the new model, but protects against future changes).
		if (_combatByPlayer.TryGetValue(session.PlayerId, out var combatPair) && !combatPair.StateAnnounced)
		{
			EndCombat(combatPair);
		}
		// Manual click-to-walk cancels script-driven movement AND intent.
		_perTick?.EndByStem(session.PlayerId, "follow_player");
		_perTick?.EndByStem(session.PlayerId, "trade_player");
		if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition current)) return;

		if (!root.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.Array)
			return;

		var path = new List<Vector2I>();
		Vector2I? prev = null;
		foreach (JsonElement t in pathEl.EnumerateArray())
		{
			if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 2) return;
			if (!t[0].TryGetInt32(out int x) || !t[1].TryGetInt32(out int y)) return;
			if (Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000) return;
			if (!IsWalkableTile(x, y)) return;

			if (prev.HasValue)
			{
				int ddx = Math.Abs(x - prev.Value.X);
				int ddy = Math.Abs(y - prev.Value.Y);
				if (ddx > 1 || ddy > 1 || (ddx == 0 && ddy == 0)) return;
				if (ddx == 1 && ddy == 1)
				{
					if (!IsWalkableTile(prev.Value.X, y)) return;
					if (!IsWalkableTile(x, prev.Value.Y)) return;
				}
			}
			path.Add(new Vector2I(x, y));
			prev = new Vector2I(x, y);
		}

		if (path.Count == 0 || path.Count > MaxPathLength) return;

		// First step must be adjacent to the server's current tile.
		int curX = (int)MathF.Round(current.X);
		int curY = (int)MathF.Round(current.Z);
		int ddx0 = Math.Abs(path[0].X - curX);
		int ddy0 = Math.Abs(path[0].Y - curY);
		if (ddx0 > 1 || ddy0 > 1) return;
		if (ddx0 == 1 && ddy0 == 1)
		{
			if (!IsWalkableTile(curX, path[0].Y)) return;
			if (!IsWalkableTile(path[0].X, curY)) return;
		}

		_intents[peerId] = path;
	}

	/// <summary>
	/// True only if (x, y) is an explicit, walkable tile. Unlike GetTileInfo, this
	/// does NOT treat unlisted tiles as walkable: void tiles block movement.
	/// </summary>
	private bool IsWalkableTile(int x, int y)
	{
		return _tiles.TryGetValue((x, y), out TileInfo info) && info.Walkable;
	}

	// ---------- Auth ----------

	private async Task HandleHelloAsync(long peerId)
	{
		string version = await _db.GetSqliteVersionAsync();
		long total = await _db.CountPlayersAsync();
		Send(peerId, new { type = "hello_ok", sqlite_version = version, total_players = total });
	}

	private async Task HandleRegisterAsync(long peerId, string username, string password, bool isDev = false)
	{
		if (!UsernameRegex.IsMatch(username))
		{ SendError(peerId, "register_result", ErrorKeys.RegisterUsernameInvalid, new { min = MinUsernameLength, max = MaxUsernameLength }); return; }
		if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
		{ SendError(peerId, "register_result", ErrorKeys.RegisterPasswordLength, new { min = MinPasswordLength, max = MaxPasswordLength }); return; }
		if (!_authInProgress.Add(peerId))
		{ SendError(peerId, "register_result", ErrorKeys.RequestInProgress); return; }

		try
		{
			string hash = await PasswordHasher.HashAsync(password);
			bool makeAdmin = isDev && OS.IsDebugBuild();
			var (created, playerId) = await _db.TryCreatePlayerAsync(username, hash, makeAdmin);
			if (!created) { SendError(peerId, "register_result", ErrorKeys.RegisterUsernameTaken); return; }
			GD.Print($"[Server] Registered: {username} (id {playerId}{(makeAdmin ? ", dev admin" : "")})");
			if (makeAdmin)
			{
				await _db.EnsurePlayerStatsAsync(playerId);
				await _db.EnsurePlayerInventoryAndBankAsync(playerId);
				await _db.SetPlayerCombatStatsAsync(playerId, 50, 50, 50, 10, 10);
				GD.Print($"[Server] Dev admin stats bumped for {username}");
			}
			else
			{
				await _db.EnsurePlayerInventoryAndBankAsync(playerId);
			}
			Send(peerId, new { type = "register_result", ok = true, username });
		}
		finally { _authInProgress.Remove(peerId); }
	}

	private async Task HandleLoginAsync(long peerId, string username, string password, string reason = "")
	{
		GD.Print($"[Server] Got login from peer {peerId}: user='{username}'");
		if (_sessions.ContainsKey(peerId))
		{ SendError(peerId, "login_result", ErrorKeys.LoginAlreadyLoggedIn); return; }
		if (!_authInProgress.Add(peerId))
		{ SendError(peerId, "login_result", ErrorKeys.RequestInProgress); return; }

		try
		{
			PlayerRecord player = await _db.GetPlayerByUsernameAsync(username);
			bool passwordOk = await PasswordHasher.VerifyAsync(password, player?.PasswordHash ?? _dummyHash);
			if (player == null || !passwordOk)
			{
				Diag.Log("diag_login", $"Failed login for '{username}' from peer {peerId}");
				SendError(peerId, "login_result", ErrorKeys.LoginInvalidCredentials);
				return;
			}
			if (reason == "world_update")
				Diag.Log("diag_login", $"Hotfix reconnect: {player.Username} (id {player.Id})");
			await CompleteLoginAsync(peerId, player);
		}
		finally { _authInProgress.Remove(peerId); }
	}

private async Task CompleteLoginAsync(long peerId, PlayerRecord player)
{
    long loginCount = await _db.RecordLoginAsync(player.Id);
    long totalPlayers = await _db.CountPlayersAsync();
    if (!_connectedPeers.Contains(peerId)) return;

    if (!await _db.EnsurePlayerStatsAsync(player.Id))
    {
        GD.PrintErr($"[Server] Failed to ensure stats for {player.Username} (id {player.Id}); login aborted");
        SendError(peerId, "login_result", ErrorKeys.LoginStatsInitFailed);
        return;
    }

    if (!await _db.EnsurePlayerInventoryAndBankAsync(player.Id))
    {
        GD.PrintErr($"[Server] Failed to ensure inventory/bank for {player.Username} (id {player.Id}); login aborted");
        SendError(peerId, "login_result", ErrorKeys.LoginStatsInitFailed);
        return;
    }

    var combatData = await _db.GetPlayerCombatAsync(player.Id);
    _playerCombat[player.Id] = combatData ?? new Database.PlayerCombatData(1, 1, 1, 0, 0, 10, 10, 1);

    // Compute spawn position and publish it BEFORE the session. Any concurrent
    // EnterWorld (from another player logging in) reads _positions to build
    // world_state — the position must be set before the peer becomes visible.
    PlayerPosition spawnPos;
    var saved = await _db.GetPlayerTileAsync(player.Id);
    if (saved.HasValue)
    {
        if (IsValidTile(saved.Value.X, saved.Value.Y))
        {
            var info = _tiles[(saved.Value.X, saved.Value.Y)];
            spawnPos = new PlayerPosition(saved.Value.X, info.Z, saved.Value.Y, 0f);
            GD.Print($"[Server] Resuming {player.Username} at tile ({saved.Value.X}, {saved.Value.Y})");
        }
        else
        {
            spawnPos = FindNearestValidTile(saved.Value.X, saved.Value.Y);
            GD.Print($"[Server] Saved tile ({saved.Value.X}, {saved.Value.Y}) for {player.Username} is invalid; spawned at ({spawnPos.X}, {spawnPos.Z})");
        }
    }
    else
    {
        spawnPos = ComputeSpawnPosition(player.Id);
    }
    _positions[player.Id] = spawnPos;

    // Session published after position. No awaits between these lines, so the
    // pair is atomic from the perspective of other ticks.
    long sid = Random.Shared.NextInt64(1, long.MaxValue);
    _ = _db.SetPlayerSidAsync(player.Id, sid);
    var session = new PlayerSession(player.Id, player.Username, player.IsAdmin, sid);
    _sessions[peerId] = session;
    _peerByPlayerId[player.Id] = peerId;

    GD.Print($"[Server] Login: {player.Username} (id {player.Id}, count {loginCount}{(player.IsAdmin ? ", admin" : "")})");
    Send(peerId, new
    {
        type = "login_result",
        ok = true,
        player_id = player.Id,
        username = player.Username,
        is_admin = player.IsAdmin,
        login_count = loginCount,
        total_players = totalPlayers,
        hitpoints = combatData?.CurrentHp ?? 10,
        max_hitpoints = combatData?.MaxHp ?? 10,
        spawn = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw }
    });

    EnterWorld(peerId, session, spawnPos);
}
	
	private PlayerPosition ComputeSpawnPosition(long playerId)
	{
		int pid = (int)Math.Max(playerId, 1);
		int sx = (pid * 3) % 16 - 8;
		int sz = (pid * 7) % 16 - 8;
		return FindNearestValidTile(sx, sz);
	}

	private void EnterWorld(long peerId, PlayerSession session, PlayerPosition spawnPos)
{
    var others = new List<object>();
    foreach (var (otherPeerId, other) in _sessions)
    {
        if (otherPeerId == peerId) continue;
        if (!_positions.TryGetValue(other.PlayerId, out var pos)) continue;
        others.Add(new
        {
            player_id = other.PlayerId,
            username = other.Username,
            is_admin = other.IsAdmin,
            x = pos.X, y = pos.Y, z = pos.Z, yaw = pos.Yaw,
            sid = other.Sid,
        });
    }

    Send(peerId, new { type = "world_state", players = others });

    Broadcast(new
    {
        type = "player_joined",
        player_id = session.PlayerId,
        username = session.Username,
        is_admin = session.IsAdmin,
        x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw,
        sid = session.Sid,
    }, exceptPeerId: peerId);

    int tx = (int)MathF.Round(spawnPos.X);
    int ty = (int)MathF.Round(spawnPos.Z);
    _lastPlayerChunk[peerId] = new Vector2I(
        ActiveNpc.FloorDiv(tx, _chunkSize),
        ActiveNpc.FloorDiv(ty, _chunkSize));
    RefreshNpcVisibility(peerId, tx, ty);
}

	// ---------- World file ----------

	private bool EnsureWorldLoaded()
	{
		foreach (string path in WorldFileCandidates)
		{
			if (!File.Exists(path)) continue;

			DateTime mtime = File.GetLastWriteTimeUtc(path);
			long size = new FileInfo(path).Length;
			if (_worldDoc != null && path == _worldPath && mtime == _worldMtime && size == _worldSize)
				return true;

			try
			{
				string text = File.ReadAllText(path);
				JsonDocument doc = JsonDocument.Parse(text);
				JsonElement root = doc.RootElement;

				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("format_version", out JsonElement fmt) ||
					!root.TryGetProperty("world_version", out JsonElement ver))
				{
					GD.PrintErr($"[Server] {path}: missing format_version or world_version");
					doc.Dispose();
					continue;
				}

				if (root.TryGetProperty("chunk_size", out JsonElement cs)) _chunkSize = cs.GetInt32();

				var newTiles = new Dictionary<(int, int), TileInfo>();
				if (root.TryGetProperty("chunks", out JsonElement chunks) && chunks.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty chunkProp in chunks.EnumerateObject())
					{
						if (chunkProp.Value.ValueKind != JsonValueKind.Array) continue;
						foreach (JsonElement tile in chunkProp.Value.EnumerateArray())
						{
							if (tile.ValueKind != JsonValueKind.Object) continue;
							int x = tile.GetProperty("x").GetInt32();
							int y = tile.GetProperty("y").GetInt32();
							int z = tile.GetProperty("z").GetInt32();
							bool walkable = true;
							if (tile.TryGetProperty("flags", out JsonElement flags) &&
								flags.ValueKind == JsonValueKind.Object &&
								flags.TryGetProperty("walkable", out JsonElement w))
								walkable = w.GetBoolean();
							newTiles[(x, y)] = new TileInfo(z, walkable);
						}
					}
				}

				_worldDoc?.Dispose();
				_worldDoc = doc;
				_worldPath = path;
				_worldFormatVersion = fmt.GetInt32();
				_worldVersion = ver.GetInt32();
				_worldMtime = mtime;
				_worldSize = size;

				_tiles.Clear();
				foreach (var kv in newTiles) _tiles[kv.Key] = kv.Value;
				return true;
			}
			catch (JsonException e) { GD.PrintErr($"[Server] {path}: parse error: {e.Message}"); }
			catch (Exception e) { GD.PrintErr($"[Server] {path}: {e.Message}"); }
		}
		return false;
	}

	private void HandleWorldInfo(long peerId)
	{
		if (!_sessions.ContainsKey(peerId)) { SendError(peerId, "world_info_result", ErrorKeys.WorldNotLoggedIn); return; }
		if (!EnsureWorldLoaded())         { SendError(peerId, "world_info_result", ErrorKeys.WorldUnavailable); return; }
		Send(peerId, new { type = "world_info_result", ok = true, world_version = _worldVersion, format_version = _worldFormatVersion });
	}

	private void HandleWorldFetch(long peerId)
	{
		if (!_sessions.ContainsKey(peerId)) { SendError(peerId, "world_data", ErrorKeys.WorldNotLoggedIn); return; }
		if (!EnsureWorldLoaded())         { SendError(peerId, "world_data", ErrorKeys.WorldUnavailable); return; }
		Send(peerId, new { type = "world_data", ok = true, world_version = _worldVersion, format_version = _worldFormatVersion, data = _worldDoc.RootElement });
	}

	// ---------- Outbound ----------

	private void SendError(long peerId, string type, string errorKey, object errorParams = null)
		=> Send(peerId, new { type, ok = false, error_key = errorKey, error_params = errorParams ?? new { } });

	private void Send(long peerId, object message, int channel = 0,
		MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
	{
		if (!_connectedPeers.Contains(peerId)) return;
		SendBytes(peerId, JsonSerializer.SerializeToUtf8Bytes(message), channel, mode);
	}

	private void RouteChatEffects(long pid, string username, int cx, int cy,
		List<Dictionary<string, object>> effects, long selfPeer)
	{
		foreach (var effect in effects)
		{
			string kind = effect.TryGetValue("kind", out var k) ? k as string ?? "" : "";
			string text = effect.TryGetValue("text", out var t) ? t as string ?? "" : "";
			switch (kind)
			{
				case "chat_local":
					if (selfPeer >= 0) Send(selfPeer, new { type = "private_message", text });
					break;
				case "chat_public":
					Broadcast(new { type = "chat_message", username, message = text, from_chunk = new[] { cx, cy } });
					break;
				case "chat_overhead":
					Broadcast(new { type = "overhead_message", username, text, from_chunk = new[] { cx, cy } });
					break;
				case "chat_private":
					long targetPid = effect.TryGetValue("target_player_id", out var tpid) ? Convert.ToInt64(tpid) : 0;
					if (targetPid > 0)
					{
						long targetPeer = -1;
						foreach (var kv in _sessions) if (kv.Value.PlayerId == targetPid) { targetPeer = kv.Key; break; }
						if (targetPeer >= 0) Send(targetPeer, new { type = "private_message", text });
					}
					break;
				case "anim_set":
					string anim = effect.TryGetValue("anim", out var a) ? a as string ?? "" : "";
					long dur = effect.TryGetValue("duration_ms", out var d) ? Convert.ToInt64(d) : 0;
					Broadcast(new
					{
						type = "anim_changed",
						player_id = pid,
						anim,
						duration_ms = dur
					});
					break;
			}
		}
	}

	private void Broadcast(object message, long exceptPeerId = 0, int channel = 0,
		MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
		foreach (long peerId in _sessions.Keys)
		{
			if (peerId == exceptPeerId) continue;
			SendBytes(peerId, bytes, channel, mode);
		}
	}

	private void SendBytes(long peerId, byte[] bytes, int channel, MultiplayerPeer.TransferModeEnum mode)
	{
		if (!_connectedPeers.Contains(peerId)) return;
		_mp.SendBytes(bytes, (int)peerId, mode, channel);
	}
	private void HandleChatSend(long peerId, JsonElement root)
		{
			if (!_sessions.TryGetValue(peerId, out PlayerSession session)) return;
			if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition pos)) return;
			if (!root.TryGetProperty("message", out JsonElement msgEl)) return;

			string text = (msgEl.GetString() ?? "").Trim();
			if (text.Length == 0 || text.Length > 200) return;

			int tileX = (int)MathF.Round(pos.X);
			int tileY = (int)MathF.Round(pos.Z);
			int cx = (int)MathF.Floor((float)tileX / _chunkSize);
			int cy = (int)MathF.Floor((float)tileY / _chunkSize);

			Broadcast(new
			{
				type = "chat_message",
				username = session.Username,
				message = text,
				from_chunk = new[] { cx, cy }
			});

			GD.Print($"[Chat] {session.Username}: {text}");
		}
	

	internal bool TryGetPosition(long playerId, out PlayerPosition pos) =>
		_positions.TryGetValue(playerId, out pos);

	internal bool TryGetTileInfo(int x, int y, out TileInfo info) =>
		_tiles.TryGetValue((x, y), out info);

	private bool IsValidTile(int x, int y)
	{
		return _tiles.TryGetValue((x, y), out var info) && info.Walkable;
	}

	/// <summary>
	/// Walks outward in expanding rings until a valid walkable tile is found.
	/// Returns the start tile if it's already valid. Falls back to the start
	/// tile (with a safe Z) if nothing is found within 32 tiles.
	/// </summary>
	private PlayerPosition FindNearestValidTile(int startX, int startY)
	{
		if (IsValidTile(startX, startY))
		{
			var info = _tiles[(startX, startY)];
			return new PlayerPosition(startX, info.Z, startY, 0f);
		}
		for (int radius = 1; radius <= 32; radius++)
		{
			for (int dx = -radius; dx <= radius; dx++)
			{
				for (int dy = -radius; dy <= radius; dy++)
				{
					if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
					int nx = startX + dx;
					int ny = startY + dy;
					if (IsValidTile(nx, ny))
					{
						var info = _tiles[(nx, ny)];
						return new PlayerPosition(nx, info.Z, ny, 0f);
					}
				}
			}
		}
		var fallbackZ = GetTileInfo(startX, startY).Z;
		return new PlayerPosition(startX, fallbackZ, startY, 0f);
	}

	internal bool TileWalkable(int x, int y) =>
		_tiles.TryGetValue((x, y), out var info) && info.Walkable;


	internal int ChunkSize => _chunkSize;

	internal void SetIntent(long playerId, List<Vector2I> route)
	{
		long peerId = -1;
		foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId >= 0) _intents[peerId] = new List<Vector2I>(route);
	}

	internal Dictionary<string, object>? GetActiveScriptArgs(long playerId, string stem) =>
		_perTick?.GetArgsForActive(playerId, stem);

	internal bool EndPerTickScript(long playerId, string stem) =>
		_perTick != null && _perTick.EndByStem(playerId, stem);

	private void RunScriptFromGfs(long playerId, string stem, Dictionary<string, object> args, Gfs.CallContext parentCtx)
	{
		var (path, ast) = _gfs.ResolveByStem(stem);
		if (ast == null) { GD.PrintErr($"[Server] RUN: script not found: {stem}"); return; }
		var newCtx = new Gfs.CallContext
		{
			System = new Dictionary<string, object>(parentCtx.System),
			Args = args,
			Locals = new Dictionary<string, object>(),
			SelfPlayerId = playerId,
			ScriptPath = path,
			TaskLookup = _gfs.GetTask
		};
		newCtx.RunScript = (s, a) => RunScriptFromGfs(playerId, s, a, newCtx);
		_perTick.Start(playerId, path, ast, newCtx);
	}


	internal List<Vector2I> PathfindToNearest(long selfPlayerId, int goalX, int goalY)
	{
		if (!_positions.TryGetValue(selfPlayerId, out var pos)) return new();
		return PathfindBetween((int)MathF.Round(pos.X), (int)MathF.Round(pos.Z), goalX, goalY);
	}

	internal List<Vector2I> PathfindBetween(int startX, int startY, int goalX, int goalY)
	{
		if (!TryGetTileInfo(startX, startY, out _)) return new();

		var queue = new Queue<Vector2I>();
		var cameFrom = new Dictionary<Vector2I, Vector2I?>();
		var start = new Vector2I(startX, startY);
		var goal = new Vector2I(goalX, goalY);
		queue.Enqueue(start);
		cameFrom[start] = null;
		Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
							new(1,1), new(1,-1), new(-1,1), new(-1,-1) };

		Vector2I? best = null;
		int bestDist = int.MaxValue;
		int expansions = 0;
		const int MAX_EXPANSIONS = 20000;

		while (queue.Count > 0)
		{
			if (++expansions > MAX_EXPANSIONS)
			{
				Diag.Error("diag_pathfinding", $"PathfindBetween hit {MAX_EXPANSIONS} expansions; using best-so-far");
				break;
			}
			var cur = queue.Dequeue();
			int d = Math.Max(Math.Abs(cur.X - goal.X), Math.Abs(cur.Y - goal.Y));
			if (d < bestDist) { bestDist = d; best = cur; }
			if (cur == goal) break;

			foreach (var dir in dirs)
			{
				var next = cur + dir;
				if (cameFrom.ContainsKey(next)) continue;
				if (!TryGetTileInfo(next.X, next.Y, out var info) || !info.Walkable) continue;
				if (dir.X != 0 && dir.Y != 0)
				{
					if (!TryGetTileInfo(cur.X + dir.X, cur.Y, out var a) || !a.Walkable) continue;
					if (!TryGetTileInfo(cur.X, cur.Y + dir.Y, out var b) || !b.Walkable) continue;
				}
				cameFrom[next] = cur;
				queue.Enqueue(next);
			}
		}

		if (best == null) return new();
		var path = new List<Vector2I>();
		Vector2I? node = best;
		while (node.HasValue && node.Value != start)
		{
			path.Insert(0, node.Value);
			node = cameFrom[node.Value];
		}
		return path;
	}

internal List<Vector2I> FindAdjacentWalkablePath(int x, int y)
{
	Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
						new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
	foreach (var d in dirs)
	{
		var cand = new Vector2I(x + d.X, y + d.Y);
		if (IsValidTile(cand.X, cand.Y))
			return new List<Vector2I> { cand };
	}
	return new List<Vector2I>();
}

	private HashSet<long> ComputeVisibleNpcIds(int tileX, int tileY)
	{
		var visible = new HashSet<long>();
		int pcx = ActiveNpc.FloorDiv(tileX, _chunkSize);
		int pcy = ActiveNpc.FloorDiv(tileY, _chunkSize);
		foreach (var npc in _npcs.Values)
		{
			if (npc.IsDead) continue;
			int ncx = npc.ChunkX(_chunkSize);
			int ncy = npc.ChunkY(_chunkSize);
			if (Math.Abs(pcx - ncx) <= NpcViewChunkRadius &&
				Math.Abs(pcy - ncy) <= NpcViewChunkRadius)
				visible.Add(npc.Id);
		}
		return visible;
	}

	private object NpcToPacket(ActiveNpc npc) => new
	{
		id = npc.Id,
		name = npc.Name,
		display = npc.Display,
		x = npc.PosX,
		y = npc.PosY,
		z = npc.PosZ,
		yaw = npc.Yaw,
		anim = npc.PlayingAnimation,
		attackable = npc.Attackable,
		final_level = npc.FinalLevel,
		render_slug = npc.RenderSlug,
		hitpoints = npc.Hitpoints,
		max_hitpoints = npc.MaxHitpoints,
	};

	/// <summary>
	/// Diff the player's current NPC visibility against the last snapshot, sending
	/// npc_joined for additions and npc_left for removals. Called on login (empty
	/// prior set — everything gets sent) and on chunk-boundary crossings.
	/// </summary>
	private void RefreshNpcVisibility(long peerId, int tileX, int tileY)
	{
		if (!_visibleNpcs.TryGetValue(peerId, out var oldSet))
		{
			oldSet = new HashSet<long>();
			_visibleNpcs[peerId] = oldSet;
		}
		var newSet = ComputeVisibleNpcIds(tileX, tileY);

		var added = new List<object>();
		foreach (var id in newSet)
		{
			if (!oldSet.Contains(id) && _npcs.TryGetValue(id, out var npc))
				added.Add(NpcToPacket(npc));
		}
		if (added.Count > 0)
		{
			Send(peerId, new { type = "npc_joined", npcs = added });
			Diag.Log("diag_npc", $"peer {peerId} sees {added.Count} new NPC(s)");
		}

		var removed = new List<long>();
		foreach (var id in oldSet)
			if (!newSet.Contains(id)) removed.Add(id);
		if (removed.Count > 0)
		{
			Send(peerId, new { type = "npc_left", ids = removed });
			Diag.Log("diag_npc", $"peer {peerId} lost {removed.Count} NPC(s)");
		}

		_visibleNpcs[peerId] = newSet;
	}

	private void HandleNpcRenderInfo(long peerId)
	{
		if (!_sessions.ContainsKey(peerId)) return;
		Send(peerId, new
		{
			type = "npc_render_info_response",
			slugs = _npcRender.GetManifest()
		});
	}

	private void HandleNpcRenderFetch(long peerId, JsonElement root)
	{
		if (!_sessions.ContainsKey(peerId)) return;
		if (!root.TryGetProperty("slugs", out var slugsEl) || slugsEl.ValueKind != JsonValueKind.Array) return;

		foreach (var s in slugsEl.EnumerateArray())
		{
			string slug = s.GetString() ?? "";
			if (string.IsNullOrEmpty(slug)) continue;
			var bundle = _npcRender.GetBundle(slug);
			if (bundle == null) continue;

			var filesDict = new Dictionary<string, string>();
			foreach (var f in bundle.Files)
				filesDict[f.RelativePath] = Convert.ToBase64String(f.Bytes);

			Send(peerId, new
			{
				type = "npc_render_data",
				slug = bundle.Slug,
				hash = bundle.Hash,
				files = filesDict
			});
			Diag.Log("diag_npc", $"Sent render bundle '{slug}' to peer {peerId} ({bundle.Files.Count} files)");
		}
	}

	/// <summary>
	/// Movement entry point. Any caller (AI package, script, debug) asks an NPC
	/// to walk to a tile. Computes a path via the shared BFS. Idempotent if the
	/// NPC is already moving toward that exact tile.
	/// </summary>
	internal void MoveNpcTo(long npcId, int tileX, int tileY)
	{
		if (!_npcs.TryGetValue(npcId, out var npc)) return;
		if (npc.IsDead) return;
		if (!IsValidTile(tileX, tileY)) return;

		int curX = (int)MathF.Round(npc.PosX);
		int curY = (int)MathF.Round(npc.PosZ);
		if (curX == tileX && curY == tileY) return;

		var path = PathfindBetween(curX, curY, tileX, tileY);
		if (path.Count == 0) return;
		npc.Path = path;
	}

	// ====================================================================
	// TEMP-DEV: debug NPC wander. Delete when real AI packages are wired in.
	// Set DiagNpcWander=true in the scene inspector to enable.
	// ====================================================================
	private void DebugWanderTick(double delta)
	{
		if (!DiagNpcWander) return;
		_wanderAccumulator += delta;
		if (_wanderAccumulator < WanderIntervalSeconds) return;
		_wanderAccumulator = 0;

		foreach (var npc in _npcs.Values)
		{
			if (npc.IsDead) continue;
			if (npc.Path.Count > 0) continue;
			if (npc.AiPackageState != "wander") continue;
			if (_combatByNpc.ContainsKey(npc.Id)) continue;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				int dx = _wanderRng.Next(-3, 4);
				int dy = _wanderRng.Next(-3, 4);
				if (dx == 0 && dy == 0) continue;
				int tx = npc.TileX + dx;
				int ty = npc.TileY + dy;
				if (IsValidTile(tx, ty))
				{
					MoveNpcTo(npc.Id, tx, ty);
					break;
				}
			}
		}
	}

	// ---------- NPC death / respawn ----------
	internal void KillNpc(long npcId)
	{
		if (!_npcs.TryGetValue(npcId, out var npc)) return;
		if (npc.IsDead) return;

		npc.IsDead = true;
		npc.DiedAtMs = (long)Time.GetTicksMsec();
		npc.Path.Clear();
		npc.Hitpoints = 0;
		BroadcastNpcHp(npc);
		_ = _db.MarkNpcDeadAsync(npc.Id);

		if (_combatByNpc.TryGetValue(npcId, out var owner)) EndCombat(owner);
		Diag.Log("diag_npc", $"NPC {npc.Name} (id {npc.Id}) killed; respawns in {npc.SpawnTimerSeconds}s");
		NotifyNpcVisibilityChanged();
	}

	private void ReviveNpc(ActiveNpc npc)
	{
		npc.IsDead = false;
		npc.DiedAtMs = 0;
		npc.Hitpoints = npc.MaxHitpoints;
		npc.HealAccumulatorTicks = 0;
		npc.ResetPositionToHome();
		npc.Path.Clear();
		_ = _db.ReviveNpcAsync(npc.Id);
		Diag.Log("diag_npc", $"NPC {npc.Name} (id {npc.Id}) respawned with {npc.Hitpoints} HP");
	}

	private void CheckNpcRespawns()
	{
		long now = (long)Time.GetTicksMsec();
		bool changed = false;
		foreach (var npc in _npcs.Values)
		{
			if (!npc.IsDead) continue;
			if (npc.DiedAtMs <= 0) continue;
			if (now - npc.DiedAtMs >= npc.SpawnTimerSeconds * 1000L)
			{
				ReviveNpc(npc);
				changed = true;
			}
		}
		if (changed) NotifyNpcVisibilityChanged();
	}

	// ---------- Combat orchestration ----------

	private static bool CanSwingTick(long lastSwingTick, int attackSpeedTicks, long currentTick)
		=> currentTick - lastSwingTick >= attackSpeedTicks;

	/// <summary>
/// Public entry point — called when the player clicks Attack on an NPC.
/// If already adjacent, engages immediately. Otherwise records a pending
/// pursuit. The NPC is not informed (no combat pair registered) until the
/// player actually reaches them.
/// </summary>
internal void RequestEngage(long playerId, long npcId)
{
	if (!_npcs.TryGetValue(npcId, out var npc)) return;
	if (npc.IsDead) return;
	if (!npc.Attackable) return;

	// Already in combat with some NPC?
	if (_combatByPlayer.TryGetValue(playerId, out var existing))
	{
		if (existing.NpcId == npcId) return;
		return;
	}

	// NPC is already engaged by another player.
	if (_combatByNpc.ContainsKey(npcId))
	{
		if (_pendingEngage.TryGetValue(playerId, out var pendingNpc) && pendingNpc == npcId)
			_pendingEngage.Remove(playerId);
		ChatToPlayer(playerId, "That's already in a fight, I cannot engage.");
		Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}); rejected");
		return;
	}

	// Adjacent right now — engage immediately.
	if (ArePositionsAdjacent(playerId, npc))
	{
		_pendingEngage.Remove(playerId);
		EngageNpc(playerId, npcId);
		return;
	}

	// Otherwise, pursue.
	_pendingEngage[playerId] = npcId;
	long peerId = -1;
	foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId >= 0) _intents.Remove(peerId);
	MovePlayerAdjacentToNpc(playerId, npc);
	Diag.Log("diag_combat", $"Player {playerId} approaching NPC {npc.Name} (id {npcId})");
}


/// <summary>
/// Called only when the player is already adjacent. Registers the pair and
/// begins combat. The NPC becomes aware of the player from this moment.
/// </summary>
internal void EngageNpc(long playerId, long npcId, bool initiatedByPlayer = true)
{
	if (!_npcs.TryGetValue(npcId, out var npc)) return;
	if (npc.IsDead) return;

	// Already in combat with some NPC?
	if (_combatByPlayer.TryGetValue(playerId, out var existing))
	{
		if (existing.NpcId == npcId) return;
		return;
	}

	// NPC is already engaged by another player.
	if (_combatByNpc.ContainsKey(npcId))
	{
		// Only notify the player if this was their own attempt. Hostility-driven
		// engages should stay silent — the player never asked to attack.
		if (initiatedByPlayer)
			ChatToPlayer(playerId, "That's already in a fight, I cannot engage.");
		Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}) at promotion; rejected");
		return;
	}

	var pair = new CombatPair
	{
		PlayerId = playerId,
		NpcId = npcId,
		LastPlayerSwingTick = _currentTick - PlayerAttackSpeedTicks,
		LastNpcSwingTick = _currentTick - npc.AttackSpeedTicks,
		StateAnnounced = false,
		InitiatedByPlayer = initiatedByPlayer,
	};
	_combatByPlayer[playerId] = pair;
	if (!_combatByNpc.ContainsKey(npcId)) _combatByNpc[npcId] = pair;

	Diag.Log("diag_combat", $"Player {playerId} engaged NPC {npc.Name} (id {npcId}) initiatedByPlayer={initiatedByPlayer}");
}

	private void EndCombat(CombatPair pair)
	{
		_combatByPlayer.Remove(pair.PlayerId);
		bool wasOwner = _combatByNpc.TryGetValue(pair.NpcId, out var owner) && owner == pair;
		if (wasOwner) _combatByNpc.Remove(pair.NpcId);

		if (pair.StateAnnounced)
		{
			int prevRef = _npcCombatRefCount.TryGetValue(pair.NpcId, out var pc) ? pc : 0;
			int newRef = Math.Max(0, prevRef - 1);
			if (newRef == 0) _npcCombatRefCount.Remove(pair.NpcId);
			else _npcCombatRefCount[pair.NpcId] = newRef;
			if (prevRef > 0 && newRef == 0)
				Broadcast(new { type = "npc_combat_state", npc_id = pair.NpcId, in_combat = false });
			Broadcast(new
			{
				type = "player_combat_state",
				player_id = pair.PlayerId,
				in_combat = false,
				npc_id = 0
			});
		}

		Diag.Log("diag_combat", $"Combat ended: player {pair.PlayerId}, npc {pair.NpcId}");
	}

	private void ProcessCombat()
{
	if (_combatByPlayer.Count == 0) return;
	var toEnd = new List<CombatPair>();

	foreach (var pair in _combatByPlayer.Values)
	{
		if (!_npcs.TryGetValue(pair.NpcId, out var npc)) { toEnd.Add(pair); continue; }
		if (npc.IsDead) { toEnd.Add(pair); continue; }
		if (!_positions.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
		if (!_playerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }

		if (!ArePositionsAdjacent(pair.PlayerId, npc))
		{
			// Player-initiated pre-engage: player walks toward the NPC. NPC
			// stays put. Only applies before the first swing lands.
			if (pair.InitiatedByPlayer && !pair.StateAnnounced)
			{
				MovePlayerAdjacentToNpc(pair.PlayerId, npc);
				continue;
			}

			// Otherwise the NPC is the one pursuing. Leash check first.
			if (DistanceFromSpawn(npc) > NpcLeashRadiusTiles)
			{
				EndCombat(pair);
				npc.ReturningHome = true;
				npc.Path.Clear();
				MoveNpcTo(npc.Id, npc.TileX, npc.TileY);
				Diag.Log("diag_combat", $"NPC {npc.Name} (id {npc.Id}) leash broken");
				continue;
			}

			// NPC chases player, stopping adjacent.
			if (_positions.TryGetValue(pair.PlayerId, out var ppos))
			{
				var playerTile = new Vector2I(
					(int)MathF.Round(ppos.X),
					(int)MathF.Round(ppos.Z));
				if (pair.LastChaseTarget != playerTile)
				{
					pair.LastChaseTarget = playerTile;
					int nx = (int)MathF.Round(npc.PosX);
					int ny = (int)MathF.Round(npc.PosZ);

					if (nx == playerTile.X && ny == playerTile.Y)
					{
						Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
											new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
						foreach (var d in dirs)
						{
							var cand = playerTile + d;
							if (IsValidTile(cand.X, cand.Y))
							{
								npc.Path = new List<Vector2I> { cand };
								break;
							}
						}
					}
					else
					{
						var route = PathfindBetween(nx, ny, playerTile.X, playerTile.Y);
						if (route.Count > 0)
						{
							if (route[^1] == playerTile) route.RemoveAt(route.Count - 1);
							if (route.Count > 0) npc.Path = route;
						}
					}
				}
			}
			continue;
		}

		// Adjacent — announce state on first tick we're actually in range.
		if (!pair.StateAnnounced)
		{
			pair.StateAnnounced = true;
			int prevRef = _npcCombatRefCount.TryGetValue(npc.Id, out var pc) ? pc : 0;
			_npcCombatRefCount[npc.Id] = prevRef + 1;
			if (prevRef == 0)
				Broadcast(new { type = "npc_combat_state", npc_id = npc.Id, in_combat = true });
			Broadcast(new
			{
				type = "player_combat_state",
				player_id = pair.PlayerId,
				in_combat = true,
				npc_id = npc.Id
			});
		}

		if (!IsPlayerMoving(pair.PlayerId)
			&& CanSwingTick(pair.LastPlayerSwingTick, PlayerAttackSpeedTicks, _currentTick))
		{
			ResolvePlayerAttack(pair, npc);
			pair.LastPlayerSwingTick = _currentTick;
			if (npc.IsDead || !_playerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
		}

		if (_combatByNpc.TryGetValue(pair.NpcId, out var npcOwner) && npcOwner == pair)
		{
			if (CanSwingTick(pair.LastNpcSwingTick, npc.AttackSpeedTicks, _currentTick))
			{
				ResolveNpcAttack(pair, npc);
				pair.LastNpcSwingTick = _currentTick;
				if (!_playerCombat.TryGetValue(pair.PlayerId, out var pd) || pd.CurrentHp <= 0)
				{
					HandlePlayerDeath(pair.PlayerId);
					toEnd.Add(pair);
					continue;
				}
			}
		}
	}

	foreach (var pair in toEnd) EndCombat(pair);
}

private void ProcessNpcHealing()
{
	// ~20s in combat, ~8s out of combat (tick = 0.3s).
	const int IN_COMBAT_HEAL_TICKS  = 268;
	const int OUT_COMBAT_HEAL_TICKS = 27;
	const int IN_COMBAT_HEAL_PCT  = 10;
	const int OUT_COMBAT_HEAL_PCT = 25;

	foreach (var npc in _npcs.Values)
	{
		if (npc.IsDead) continue;
		if (npc.Hitpoints >= npc.MaxHitpoints) continue;

		npc.HealAccumulatorTicks++;
		bool inCombat = _combatByNpc.ContainsKey(npc.Id);
		int threshold = inCombat ? IN_COMBAT_HEAL_TICKS : OUT_COMBAT_HEAL_TICKS;
		int pct       = inCombat ? IN_COMBAT_HEAL_PCT  : OUT_COMBAT_HEAL_PCT;

		if (npc.HealAccumulatorTicks < threshold) continue;

		npc.HealAccumulatorTicks = 0;
		int heal = Math.Max(1, (npc.MaxHitpoints * pct) / 100);
		int newHp = Math.Min(npc.MaxHitpoints, npc.Hitpoints + heal);
		if (newHp == npc.Hitpoints) continue;

		npc.Hitpoints = newHp;
		_ = _db.SetNpcHpAsync(npc.Id, newHp);
		BroadcastNpcHp(npc);
		Diag.Log("diag_combat", $"NPC {npc.Name} healed to {newHp}/{npc.MaxHitpoints} ({(inCombat ? "in combat" : "out of combat")})");
	}
}

private void ProcessPlayerHealing()
{
	// ~25s (tick = 0.3s). Applies regardless of combat state.
	const int HEAL_INTERVAL_TICKS = 83;
	const int HEAL_PCT = 10;

	foreach (var kv in _playerCombat)
	{
		long playerId = kv.Key;
		var data = kv.Value;
		if (data.CurrentHp >= data.MaxHp) { _playerHealAccumTicks[playerId] = 0; continue; }
		if (!_positions.ContainsKey(playerId)) continue;

		int accum = (_playerHealAccumTicks.TryGetValue(playerId, out var a) ? a : 0) + 1;
		if (accum < HEAL_INTERVAL_TICKS)
		{
			_playerHealAccumTicks[playerId] = accum;
			continue;
		}

		_playerHealAccumTicks[playerId] = 0;
		int heal = Math.Max(1, (data.MaxHp * HEAL_PCT) / 100);
		int newHp = Math.Min(data.MaxHp, data.CurrentHp + heal);
		if (newHp == data.CurrentHp) continue;

		_playerCombat[playerId] = data with { CurrentHp = newHp };
		_ = _db.SetPlayerHpAsync(playerId, newHp);
		BroadcastPlayerHp(playerId, newHp, data.MaxHp);
		Diag.Log("diag_combat", $"Player {playerId} regen to {newHp}/{data.MaxHp}");
	}
}

	private void ResolvePlayerAttack(CombatPair pair, ActiveNpc npc)
{
	if (!_playerCombat.TryGetValue(pair.PlayerId, out var stats)) return;
	BroadcastPlayerAnim(pair.PlayerId, "attack", AttackAnimDurationMs);

	bool hit = RollHit(stats.Accuracy, stats.MeleeBonus, npc.Defence, npc.MeleeDefense);
	int damage = hit ? RollDamage(stats.Strength, stats.MeleeBonus) : 0;

	if (damage > 0)
	{
		npc.Hitpoints = Math.Max(0, npc.Hitpoints - damage);
		_ = _db.SetNpcHpAsync(npc.Id, npc.Hitpoints);
		BroadcastNpcHp(npc);
	}

	// Always broadcast a damage number, even on a 0-hit.
	Broadcast(new { type = "damage_number", target_type = "npc", target_id = npc.Id, amount = damage });

	if (npc.Hitpoints <= 0)
	{
		KillNpc(npc.Id);
		return;
	}
	if (!_combatByNpc.ContainsKey(npc.Id)) _combatByNpc[npc.Id] = pair;
}

	private void ResolveNpcAttack(CombatPair pair, ActiveNpc npc)
{
	if (!_playerCombat.TryGetValue(pair.PlayerId, out var pd)) return;
	BroadcastNpcAnim(npc.Id, "attack", AttackAnimDurationMs);

	bool hit = RollHit(npc.Accuracy, npc.MeleeBonus, pd.Defence, pd.MeleeDefense);
	int damage = hit ? RollDamage(npc.Strength, npc.MeleeBonus) : 0;

	if (damage > 0)
	{
		int newHp = Math.Max(0, pd.CurrentHp - damage);
		_playerCombat[pair.PlayerId] = pd with { CurrentHp = newHp };
		_ = _db.SetPlayerHpAsync(pair.PlayerId, newHp);
		BroadcastPlayerHp(pair.PlayerId, newHp, pd.MaxHp);
	}

	// Always broadcast a damage number, even on a 0-hit.
	Broadcast(new { type = "damage_number", target_type = "player", target_id = pair.PlayerId, amount = damage });
}

	private void HandlePlayerDeath(long playerId)
{
	long peerId = -1;
	foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId < 0) return;

	if (_combatByPlayer.TryGetValue(playerId, out var pair)) EndCombat(pair);

	// Clear all movement state so the player doesn't resume walking toward
	// their last destination (or re-route to an NPC) from spawn position.
	_intents.Remove(peerId);
	_pendingEngage.Remove(playerId);

	var spawnPos = ComputeSpawnPosition(playerId);
	_positions[playerId] = spawnPos;

	int hp = 10;
	if (_playerCombat.TryGetValue(playerId, out var pd))
	{
		_playerCombat[playerId] = pd with { CurrentHp = pd.MaxHp };
		_ = _db.SetPlayerHpAsync(playerId, pd.MaxHp);
		hp = pd.MaxHp;
	}
	_playerHealAccumTicks[playerId] = 0;

	Send(peerId, new
	{
		type = "player_respawned",
		x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = 0f,
		hitpoints = hp, max_hitpoints = hp
	});
	BroadcastPlayerAnim(playerId, "death", 1000);
	Diag.Log("diag_combat", $"Player {playerId} died and respawned at ({spawnPos.X}, {spawnPos.Z})");
}

	private bool ArePositionsAdjacent(long playerId, ActiveNpc npc)
	{
		if (!_positions.TryGetValue(playerId, out var ppos)) return false;
		int px = (int)MathF.Round(ppos.X);
		int py = (int)MathF.Round(ppos.Z);
		int nx = (int)MathF.Round(npc.PosX);
		int ny = (int)MathF.Round(npc.PosZ);
		int dx = Math.Abs(px - nx);
		int dy = Math.Abs(py - ny);
		if (dx > 1 || dy > 1) return false;
		if (dx == 0 && dy == 0) return false;
		return true;
	}

	private int DistanceTiles(long playerId, ActiveNpc npc)
	{
		if (!_positions.TryGetValue(playerId, out var ppos)) return int.MaxValue;
		int dx = (int)MathF.Round(ppos.X) - (int)MathF.Round(npc.PosX);
		int dy = (int)MathF.Round(ppos.Z) - (int)MathF.Round(npc.PosZ);
		return Math.Max(Math.Abs(dx), Math.Abs(dy));
	}

	/// <summary>
/// True if the player has an active movement intent (auto-path or manual click).
/// Used to gate auto-retaliate: swings only happen when standing still.
/// </summary>
private bool IsPlayerMoving(long playerId)
{
	foreach (var kv in _sessions)
	{
		if (kv.Value.PlayerId != playerId) continue;
		return _intents.TryGetValue(kv.Key, out var path) && path.Count > 0;
	}
	return false;
}

private int DistanceFromSpawn(ActiveNpc npc)
{
	int dx = (int)MathF.Round(npc.PosX) - npc.TileX;
	int dy = (int)MathF.Round(npc.PosZ) - npc.TileY;
	return Math.Max(Math.Abs(dx), Math.Abs(dy));
}

	private void MovePlayerAdjacentToNpc(long playerId, ActiveNpc npc)
	{
		long peerId = -1;
		foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId < 0) return;
		if (_intents.ContainsKey(peerId)) return;
		int nx = (int)MathF.Round(npc.PosX);
		int ny = (int)MathF.Round(npc.PosZ);
		var route = PathfindToNearest(playerId, nx, ny);
		if (route.Count == 0) return;
		var goal = new Vector2I(nx, ny);
		if (route[^1] == goal) route.RemoveAt(route.Count - 1);
		if (route.Count == 0) return;
		SetIntent(playerId, route);
	}

	private bool RollHit(int accuracy, int accuracyBonus, int defence, int defenceBonus)
	{
		int effectiveAcc = Math.Max(1, accuracy) + 8;
		int attackRoll = effectiveAcc * (accuracyBonus + 64);
		int effectiveDef = Math.Max(1, defence) + 8;
		int defenceRoll = effectiveDef * (defenceBonus + 64);
		if (attackRoll > defenceRoll)
			return _combatRng.NextDouble() < 1.0 - (defenceRoll + 2.0) / (2.0 * (attackRoll + 1.0));
		return _combatRng.NextDouble() < (double)attackRoll / (2.0 * (defenceRoll + 1.0));
	}

	private int RollDamage(int strength, int strengthBonus)
	{
		int effectiveStr = Math.Max(1, strength) + 8;
		int maxHit = (int)MathF.Floor(0.5f + effectiveStr * (strengthBonus + 64) / 640f);
		if (maxHit < 1) maxHit = 1;
		return _combatRng.Next(0, maxHit + 1);
	}

	private void ChatToPlayer(long playerId, string message)
	{
		long peerId = -1;
		foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId < 0) return;
		Send(peerId, new { type = "private_message", text = message });
	}

	private void BroadcastPlayerAnim(long playerId, string anim, int durationMs)
	{
		Broadcast(new { type = "anim_changed", player_id = playerId, anim = anim, duration_ms = durationMs });
	}

	private void BroadcastNpcAnim(long npcId, string anim, int durationMs)
	{
		Broadcast(new { type = "npc_anim_changed", npc_id = npcId, anim = anim, duration_ms = durationMs });
	}

	/// <summary>
	/// Re-run NPC visibility diff against all logged-in players. Called after
	/// death, revive, or any state change that flips IsDead.
	/// </summary>
	private void NotifyNpcVisibilityChanged()
	{
		foreach (long peerId in _sessions.Keys.ToList())
		{
			if (!_sessions.TryGetValue(peerId, out var sess)) continue;
			if (!_positions.TryGetValue(sess.PlayerId, out var pos)) continue;
			int tx = (int)MathF.Round(pos.X);
			int ty = (int)MathF.Round(pos.Z);
			RefreshNpcVisibility(peerId, tx, ty);
		}
	}

	private void BroadcastNpcHp(ActiveNpc npc)
	{
		Broadcast(new
		{
			type = "npc_hp_updated",
			npc_id = npc.Id,
			hitpoints = npc.Hitpoints,
			max_hitpoints = npc.MaxHitpoints
		});
	}

	private void BroadcastPlayerHp(long playerId, int hp, int maxHp)
	{
		Broadcast(new
		{
			type = "player_hp_updated",
			player_id = playerId,
			hitpoints = hp,
			max_hitpoints = maxHp
		});
	}

	// ---------- Admin NPC editor ----------

	internal void OpenNpcEditorForPlayer(long playerId, long npcId)
	{
		long peerId = -1;
		foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId < 0) return;
		if (!_sessions[peerId].IsAdmin)
		{
			ChatToPlayer(playerId, "Admin only.");
			return;
		}
		if (!_npcs.ContainsKey(npcId)) return;
		Send(peerId, new { type = "npc_editor_open", npc_id = npcId });
	}

	private void HandleNpcEditorGet(long peerId, JsonElement root)
	{
		if (!_sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("npc_id", out var idEl) || !idEl.TryGetInt64(out long npcId)) return;
		if (!_npcs.TryGetValue(npcId, out var npc)) return;

		Send(peerId, new
		{
			type = "npc_editor_data",
			npc = new
			{
				id = npc.Id,
				name = npc.Name,
				display = npc.Display,
				attackable = npc.Attackable,
				hitpoints = npc.Hitpoints,
				max_hitpoints = npc.MaxHitpoints,
				accuracy = npc.Accuracy,
				strength = npc.Strength,
				defence = npc.Defence,
				melee_bonus = npc.MeleeBonus,
				melee_defense = npc.MeleeDefense,
				spawn_timer = npc.SpawnTimerSeconds,
				ai_package_state = npc.AiPackageState,
				ai_action_state = npc.AiActionState,
				loot_table_name = npc.LootTableName,
				aggressive = npc.Aggressive,
				aggro_range = npc.AggroRangeTiles,
			}
		});
	}

	private void HandleNpcEditorSave(long peerId, JsonElement root)
	{
		if (!_sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("npc_id", out var idEl) || !idEl.TryGetInt64(out long npcId)) return;
		if (!_npcs.TryGetValue(npcId, out var npc)) return;
		if (!root.TryGetProperty("updates", out var upEl) || upEl.ValueKind != JsonValueKind.Object) return;

		var u = new Dictionary<string, object>();
		foreach (var prop in upEl.EnumerateObject())
		{
			u[prop.Name] = prop.Value.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				JsonValueKind.String => prop.Value.GetString() ?? "",
				JsonValueKind.Number => prop.Value.TryGetInt32(out int iv) ? iv : 0,
				_ => (object)0,
			};
		}

		try
		{
			if (u.ContainsKey("display"))          npc.Display = u["display"].ToString() ?? npc.Display;
			if (u.ContainsKey("attackable"))       npc.Attackable = (bool)u["attackable"];
			if (u.ContainsKey("ai_package_state")) npc.AiPackageState = u["ai_package_state"].ToString() ?? "idle";
			if (u.ContainsKey("ai_action_state"))  npc.AiActionState = u["ai_action_state"].ToString() ?? "none";
			if (u.ContainsKey("loot_table_name"))  npc.LootTableName = u["loot_table_name"].ToString() ?? "common_loot_1";
			if (u.ContainsKey("spawn_timer"))      npc.SpawnTimerSeconds = Math.Max(1, Convert.ToInt32(u["spawn_timer"]));
			if (u.ContainsKey("hitpoints"))        npc.Hitpoints = Math.Max(0, Convert.ToInt32(u["hitpoints"]));
			if (u.ContainsKey("max_hitpoints"))    npc.MaxHitpoints = Math.Max(1, Convert.ToInt32(u["max_hitpoints"]));
			// HP > max: bump max to match. Admin edits shouldn't silently clamp.
			if (npc.Hitpoints > npc.MaxHitpoints)  npc.MaxHitpoints = npc.Hitpoints;
			if (u.ContainsKey("accuracy"))         npc.Accuracy = Math.Max(1, Convert.ToInt32(u["accuracy"]));
			if (u.ContainsKey("strength"))         npc.Strength = Math.Max(1, Convert.ToInt32(u["strength"]));
			if (u.ContainsKey("defence"))          npc.Defence = Math.Max(1, Convert.ToInt32(u["defence"]));
			if (u.ContainsKey("melee_bonus"))      npc.MeleeBonus = Math.Max(0, Convert.ToInt32(u["melee_bonus"]));
			if (u.ContainsKey("melee_defense"))    npc.MeleeDefense = Math.Max(0, Convert.ToInt32(u["melee_defense"]));
			if (u.ContainsKey("aggressive"))  npc.Aggressive = (bool)u["aggressive"];
			if (u.ContainsKey("aggro_range")) npc.AggroRangeTiles = Math.Max(1, Convert.ToInt32(u["aggro_range"]));

			// Sync the save dict to the FINAL values so the DB matches memory.
			u["display"]          = npc.Display;
			u["attackable"]       = npc.Attackable;
			u["ai_package_state"] = npc.AiPackageState;
			u["ai_action_state"]  = npc.AiActionState;
			u["loot_table_name"]  = npc.LootTableName;
			u["spawn_timer"]      = npc.SpawnTimerSeconds;
			u["hitpoints"]        = npc.Hitpoints;
			u["max_hitpoints"]    = npc.MaxHitpoints;
			u["accuracy"]         = npc.Accuracy;
			u["strength"]         = npc.Strength;
			u["defence"]          = npc.Defence;
			u["melee_bonus"]      = npc.MeleeBonus;
			u["melee_defense"]    = npc.MeleeDefense;
			u["aggressive"]  = npc.Aggressive;
			u["aggro_range"] = npc.AggroRangeTiles;

			_ = _db.SaveAdminNpcEditAsync(npcId, u);
			BroadcastNpcHp(npc);
			Broadcast(new { type = "npc_updated", npc = NpcToPacket(npc) });
			NotifyNpcVisibilityChanged();
			Send(peerId, new { type = "npc_editor_saved", ok = true, npc_id = npcId });
		}
		catch (Exception e)
		{
			GD.PrintErr($"[Server] npc_editor_save: {e.Message}");
			SendError(peerId, "npc_editor_saved", "npc.save_failed");
		}
	}

/// <summary>
/// Each tick, check all pending pursuits. Promote to real combat when the
/// player becomes adjacent. Re-route the player if the NPC moved.
/// </summary>
private void ProcessPendingEngage()
{
	if (_pendingEngage.Count == 0) return;

	var toRemove = new List<long>();
	var toEngage = new List<(long playerId, long npcId)>();

	foreach (var kv in _pendingEngage)
	{
		long playerId = kv.Key;
		long npcId = kv.Value;

		if (!_npcs.TryGetValue(npcId, out var npc) || npc.IsDead)
		{
			toRemove.Add(playerId);
			continue;
		}
		if (!_positions.ContainsKey(playerId))
		{
			toRemove.Add(playerId);
			continue;
		}
		if (ArePositionsAdjacent(playerId, npc))
		{
			toRemove.Add(playerId);
			toEngage.Add((playerId, npcId));
			continue;
		}
		// Keep pursuing. MovePlayerAdjacentToNpc no-ops if player already routing.
		MovePlayerAdjacentToNpc(playerId, npc);
	}

	foreach (var p in toRemove) _pendingEngage.Remove(p);
	foreach (var (pid, nid) in toEngage) EngageNpc(pid, nid);
}

/// <summary>
/// Aggressive NPCs scan for players within aggro_range and engage those whose
/// final_level is within [1, npc.FinalLevel * 2]. Skips NPCs already in combat,
/// returning home, dead, or past their leash.
/// </summary>
private void ProcessHostility()
{
	foreach (var npc in _npcs.Values)
	{
		if (!npc.Aggressive) continue;
		if (npc.IsDead) continue;
		if (_combatByNpc.ContainsKey(npc.Id)) continue;
		if (npc.ReturningHome) continue;
		if (DistanceFromSpawn(npc) > NpcLeashRadiusTiles) continue;

		int nx = (int)MathF.Round(npc.PosX);
		int ny = (int)MathF.Round(npc.PosZ);

		long bestPlayer = 0;
		int bestDist = int.MaxValue;
		int levelCap = npc.FinalLevel * 2;

		foreach (var kv in _sessions)
		{
			long playerId = kv.Value.PlayerId;
			if (!_positions.ContainsKey(playerId)) continue;
			if (_combatByPlayer.ContainsKey(playerId)) continue;
			if (!_playerCombat.TryGetValue(playerId, out var pdata)) continue;
			if (pdata.FinalLevel < 1 || pdata.FinalLevel > levelCap) continue;
			if (pdata.CurrentHp <= 0) continue;

			var ppos = _positions[playerId];
			int px = (int)MathF.Round(ppos.X);
			int py = (int)MathF.Round(ppos.Z);
			int dist = Math.Max(Math.Abs(px - nx), Math.Abs(py - ny));
			if (dist > npc.AggroRangeTiles) continue;
			if (dist < bestDist)
			{
				bestDist = dist;
				bestPlayer = playerId;
			}
		}

		if (bestPlayer != 0)
		{
			Diag.Log("diag_combat", $"Hostile NPC {npc.Name} (id {npc.Id}) aggroed on player {bestPlayer} (dist {bestDist})");
			EngageNpc(bestPlayer, npc.Id, initiatedByPlayer: false);
		}
	}
}
}
