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
	private const int MaxChatLength = 60;

	private const double TickIntervalSeconds = 0.3;
	private const float WalkSpeed = 2.0f;
	private WorldService _world;
	internal bool TileWalkable(int x, int y) => _world.IsWalkable(x, y);
	private bool IsValidTile(int x, int y) => _world.IsWalkable(x, y);
	private bool IsWalkableTile(int x, int y) => _world.IsWalkable(x, y);
	private const double WorldCheckIntervalSeconds = 1.0;

	private const int MaxPathLength = 2000;

	private static readonly string[] WorldFileCandidates =
	{
		ProjectSettings.GlobalizePath("user://world.json"),
		ProjectSettings.GlobalizePath("res://data/world.json"),
	};

	private static readonly Regex UsernameRegex = new($"^[A-Za-z0-9_]{{{MinUsernameLength},{MaxUsernameLength}}}$");


	private Database _db;
	private SceneMultiplayer _mp;
	private ENetMultiplayerPeer _enet;
	internal readonly ServerState _state = new();
	private readonly HashSet<long> _connectedPeers = new();
	private readonly HashSet<long> _authInProgress = new();


	private double _npcVisRefreshAccum;
	private const double NpcVisRefreshIntervalSeconds = 1.5;
	private int _npcVisRefreshCounter;
	private readonly Dictionary<long, int> _npcCombatRefCount = new();
	private readonly Dictionary<long, (long PlayerId, long Sid)> _npcLookAt = new();

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

	private readonly Dictionary<long, CombatPair> _combatByNpc = new();
	// ---------- Ground items ----------
	internal sealed class GroundItem
	{
		public long Id { get; init; }
		public string ItemSlug { get; init; } = "";
		public int Quantity { get; init; }
		public int TileX { get; init; }
		public int TileY { get; init; }
		public int TileZ { get; init; }
		public long SpawnedAtMs { get; init; }
		/// <summary>
		/// True for designer-placed items (from a future map editor). Static items
		/// are exempt from the 90s despawn sweep and persist across server restarts
		/// once persistence lands. False for player/npc drops.
		/// </summary>
		public bool IsStatic { get; init; } = false;
	}

	private readonly Dictionary<long, GroundItem> _groundItems = new();
	private readonly Dictionary<long, HashSet<long>> _visibleGroundItems = new();
	private long _nextGroundItemId = 1;
	private const long GroundItemDespawnMs = 90 * 1000;  // 90s — player/npc drops only
	private readonly ItemLookup _items = new();
	private readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _lootTablesBySlug = new();

	private readonly Dictionary<string, (int Min, int Max)> _creditsLootBySlug = new();
	private readonly Dictionary<long, int> _playerHealAccumTicks = new();
	private readonly Random _combatRng = new();
	// Tracks the last tick on which a player consumed a consumable. Enforces "one
	// consume per tick" as described in the item tick-gating design. A food/
	// elixir split will add a second dict when food items exist; both categories
	// will be independently gated at that time.
	private readonly Dictionary<long, long> _lastConsumeTick = new();
	private const int ConsumeCooldownTicks = 1;   // 1 per tick; set to 2 for 0.6s OSRS feel
	private const int PlayerAttackSpeedTicks = 10;   // 3.0s unarmed default; equipment will override
	private const int AttackAnimDurationMs = 1400;    // matches humanoid.tres attack clip
	private const int NpcLeashRadiusTiles = 15;

	private readonly Dictionary<long, Vector2I> _lastPlayerChunk = new();   // peerId -> chunk



	private double _tickAccumulator = 0.0;

	private double _worldCheckAccumulator = 0.0;
	private int _lastBroadcastVersion = -1;

	private string _dummyHash;



	private Gfs.GfsManager _gfs;
	private Gfs.FunctionRegistry _gfsFns;
	private Gfs.PerTickManager _perTick;
	private Pathfinder _pathfinder;
	private NpcRenderManager _npcRender;
	private TradeSystem _trade;
	private DialogueSystem _dialogue;
	private BankSystem _bank;
	private RecipeSystem _recipe;
	private OutboundChannel _outbound;
	private Coordinator _coordinator;
	private MovementSystem _movement;

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
		if (DiagRecipe)      enabledGroups.Add("diag_recipe");
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

			// Ensure the canonical dev account exists before anything else.
			// Cheap no-op when it already exists.
			EnsureDevAccountSync("phrate", "12345678");

			if (ResetPlayersOnStartup)
			{
				int wiped = _db.Players.WipeAllExceptSync("phrate");
				GD.Print($"[Server] Reset: wiped {wiped} player(s), preserved 'phrate'");
			}

			var items = _db.Items.GetAllSync().Result;
			_items.Load(items);
			GD.Print($"[Server] Loaded {_items.Count} item definition(s)");

			var loot = _db.Loot.GetLootTablesSync();
			foreach (var kv in loot)
				_lootTablesBySlug[kv.Key] = kv.Value;
			GD.Print($"[Server] Loaded {loot.Count} loot table(s)");

			var creditsLoot = _db.Loot.GetCreditsLootSync();
			foreach (var kv in creditsLoot) _creditsLootBySlug[kv.Key] = kv.Value;
			GD.Print($"[Server] Loaded {creditsLoot.Count} credits loot table(s)");

			// Ground items are in-memory only. Anything on the ground when the server
			// stops is gone on restart — no persistence. Designer-placed static items
			// will live in a separate collection later and be exempt from this.
			GD.Print($"[Server] Ground items: in-memory only ({_groundItems.Count} at startup; player/npc drops cleared on restart)");

			_dummyHash = PasswordHasher.Hash("dummy-password-for-timing");
			GD.Print($"[Server] SQLite {_db.GetSqliteVersion()} ready at {dbPath}");

			int respawned = _db.Npcs.RespawnAllDeadSync();
			if (respawned > 0)
				GD.Print($"[Server] Auto-respawned {respawned} dead NPC(s) on startup");

			_world = new WorldService(WorldFileCandidates, GD.PrintErr);
			if (_world.EnsureLoaded())
			{
				_lastBroadcastVersion = _world.Version;
				GD.Print($"[Server] World ready: v{_world.Version} format {_world.FormatVersion}, {_world.TileCount} tiles ({_world.Path})");
			}
			else
			{
				GD.PrintErr("[Server] No world file loaded; world_info/world_fetch will report unavailable");
			}

			_pathfinder = new Pathfinder((x, y) =>
			{
				if (_world.TryGetTile(x, y, out TileInfo t)) return (true, t.Walkable);
				return (false, false);
			});

			try
			{
				var npcList = _db.Npcs.GetAllSync();
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
			Gfs.Utils.ItemUtils.Register(_gfsFns, this);
			Gfs.Utils.DialogueUtils.Register(_gfsFns, this);
			Gfs.Utils.BankUtils.Register(_gfsFns, this);
			_perTick = new Gfs.PerTickManager(_gfsFns);
			_gfs.ScriptsChanged += OnGfsScriptsChanged;
			_outbound = new OutboundChannel(_state, _items, SendBytes);
			_coordinator = new Coordinator();
			_movement = new MovementSystem(_state, _world, _outbound);
			_trade = new TradeSystem(this, _state, _items, _db.Inventory, _perTick, _outbound, _coordinator);
			_dialogue = new DialogueSystem(this, _state, _gfs, _gfsFns, _perTick, _outbound, _coordinator);
			_bank = new BankSystem(this, _state, _items, _db.Bank, _db.Inventory, _perTick, _outbound, _coordinator);
			_recipe = new RecipeSystem(this, _state, _items, _db.Recipes, _db.Inventory, _db.Stats, _gfs, _gfsFns, _world, _outbound);
			_coordinator.Wire(_trade, _dialogue, _bank);
			_recipe.LoadRecipes();

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
		_world?.Dispose();
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
			Diag.Log("diag_npc", $"Render bundles updated, notified {_state.Sessions.Count} client(s)");
		}

		DebugWanderTick(delta);   // TEMP-DEV

		_gfs.Poll();
	}

	// ---------- Tick ----------

	private void RunTick()
	{
		_state.CurrentTick++;

		// 1. Movement processing (only if there are intents to process)
		_movement.Tick(_state.CurrentTick);

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

		ProcessGroundItemDespawn();
		ProcessPendingPickup();
		_trade.ProcessTradeQueues();
		_trade.ProcessTradeIntents();
		_bank.ProcessBankIntents();
		_recipe.ProcessRecipeIntents();

		// 2c. Combat
		ProcessHostility();
		ProcessPendingEngage();
		ProcessNpcLookAt();
		_dialogue.ProcessPendingTalk();
		_bank.ProcessPendingBank();
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
				if (!_state.Positions.TryGetValue(pid, out var ppos)) continue;
				int pcx = (int)MathF.Floor((float)MathF.Round(ppos.X) / _world.ChunkSize);
				int pcy = (int)MathF.Floor((float)MathF.Round(ppos.Z) / _world.ChunkSize);

				string username = "";
				long selfPeer = -1;
				foreach (var kv in _state.Sessions)
					if (kv.Value.PlayerId == pid) { username = kv.Value.Username; selfPeer = kv.Key; break; }

				RouteChatEffects(pid, username, pcx, pcy, effects, selfPeer);
			}
		}

		// NPC visibility: refresh only when a player crosses a chunk boundary.
		_npcVisRefreshCounter++;
		bool periodicNpcRefresh = _npcVisRefreshCounter >= 5;
		if (periodicNpcRefresh) _npcVisRefreshCounter = 0;

		foreach (long peerId in _state.Sessions.Keys.ToList())
		{
			if (!_state.Sessions.TryGetValue(peerId, out var sess)) continue;
			if (!_state.Positions.TryGetValue(sess.PlayerId, out var pos)) continue;
			int tx = (int)MathF.Round(pos.X);
			int ty = (int)MathF.Round(pos.Z);
			var chunk = new Vector2I(
				ActiveNpc.FloorDiv(tx, _world.ChunkSize),
				ActiveNpc.FloorDiv(ty, _world.ChunkSize));

			bool playerChunkChanged =
				!_lastPlayerChunk.TryGetValue(peerId, out var last) || last != chunk;
			if (playerChunkChanged) _lastPlayerChunk[peerId] = chunk;

			if (playerChunkChanged || periodicNpcRefresh)
			{
				RefreshNpcVisibility(peerId, tx, ty);
				RefreshGroundItemVisibility(peerId, tx, ty);
			}
		}
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
			yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

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
		var tinfo = _world.GetTileInfo(tileX, tileY);

		npc.PosX = x;
		npc.PosY = tinfo.Z;
		npc.PosZ = z;
		npc.Yaw = yaw;
	}



	// ---------- World file updates ----------

	private void CheckWorldForUpdates()
	{
		if (!_world.EnsureLoaded()) return;
		if (_world.Version == _lastBroadcastVersion) return;

		_lastBroadcastVersion = _world.Version;
		Diag.Log("diag_world", $"World hotfix: v{_world.Version}, notifying {_state.Sessions.Count} client(s)");

		Broadcast(new
		{
			type = "world_updated",
			world_version = _world.Version,
			format_version = _world.FormatVersion
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
	[Export] public bool DiagRecipe { get; set; } = false;

	// ============================================================================
	// Player lifecycle
	// ============================================================================
	// ResetPlayersOnStartup: when true, wipes every player except the canonical
	// dev account ('phrate') on boot. Useful for clearing accumulated test
	// accounts without touching your primary dev character. Leave false in
	// production.
	//
	// The canonical dev account is created (with fresh defaults) if missing on
	// every startup regardless of this flag.

	[Export] public bool ResetPlayersOnStartup { get; set; } = false;

	// ---------- Connection events ----------

	private void OnPeerConnected(long peerId)
	{
		_connectedPeers.Add(peerId);
		Diag.Log("diag_network", $"Peer connected: {peerId}");
	}

	private void OnPeerDisconnected(long peerId)
	{
		if (_state.Sessions.TryGetValue(peerId, out var departingSession))
			_coordinator.OnPlayerDisconnected(departingSession.PlayerId);
		_connectedPeers.Remove(peerId);
		_authInProgress.Remove(peerId);
		_state.Intents.Remove(peerId);

		if (_state.Sessions.Remove(peerId, out PlayerSession session))
		{
			_perTick?.EndAllFor(session.PlayerId);          // MOVED INSIDE — session is non-null here
			if (_state.CombatByPlayer.TryGetValue(session.PlayerId, out var combatPair))
				EndCombat(combatPair);
			_state.PlayerCombat.Remove(session.PlayerId);
			_lastConsumeTick.Remove(session.PlayerId);
			_state.Inventories.Remove(session.PlayerId);
			_state.Credits.Remove(session.PlayerId);
			_state.SkillsByPlayer.Remove(session.PlayerId);
			_state.PendingEngage.Remove(session.PlayerId);
			_playerHealAccumTicks.Remove(session.PlayerId);
			_state.PeerByPlayerId.Remove(session.PlayerId);
			_state.PendingPickup.Remove(session.PlayerId);
			_trade.ClearPendingFor(session.PlayerId);
			_visibleGroundItems.Remove(peerId);
			_state.DialogueByPlayer.Remove(session.PlayerId);
			_dialogue.ClearPendingFor(session.PlayerId);
			_bank.ClearPlayer(session.PlayerId);
			_recipe.ClearPendingFor(session.PlayerId);

			if (_state.Positions.TryGetValue(session.PlayerId, out PlayerPosition pos))
			{
				int tx = (int)MathF.Round(pos.X);
				int ty = (int)MathF.Round(pos.Z);
				int tz = (int)MathF.Round(pos.Y);
				_ = _db.Players.SetTileAsync(session.PlayerId, tx, ty, tz);
			}
			_state.Positions.Remove(session.PlayerId);
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
				case "inventory_swap": HandleInventorySwap(peerId, root); break;
				case "ping":         HandlePing(peerId, root); break;
				case "dev_shutdown": HandleDevShutdown(peerId); break;
				case "chat_send": HandleChatSend(peerId, root); break;
				case "tooltips_info": HandleTooltipsInfo(peerId); break;
				case "gfs_info":    HandleGfsInfo(peerId); break;
				case "run_action":  HandleRunAction(peerId, root); break;
				case "npc_render_info_request": HandleNpcRenderInfo(peerId); break;
				case "npc_render_fetch":        HandleNpcRenderFetch(peerId, root); break;
				case "pickup_ground_item": HandlePickupGroundItem(peerId, root); break;
				case "trade_invite_accept": _trade.HandleTradeInviteResponse(peerId, root, accept: true); break;
				case "trade_invite_decline": _trade.HandleTradeInviteResponse(peerId, root, accept: false); break;
				case "trade_offer_add": _trade.HandleTradeOfferAdd(peerId, root); break;
				case "trade_offer_withdraw": _trade.HandleTradeOfferWithdraw(peerId, root); break;
				case "trade_credits_set": _trade.HandleTradeCreditsSet(peerId, root); break;
				case "trade_offer_accept": _trade.HandleTradeAcceptOffer(peerId, root); break;
				case "trade_confirm": _trade.HandleTradeConfirm(peerId, root); break;
				case "trade_cancel": _trade.HandleTradeCancel(peerId, root); break;
				case "dialogue_choose": _dialogue.HandleDialogueChoose(peerId, root); break;
				case "bank_deposit": _bank.HandleBankDeposit(peerId, root); break;
				case "bank_withdraw": _bank.HandleBankWithdraw(peerId, root); break;
				case "bank_move": _bank.HandleBankMove(peerId, root); break;
				case "bank_close": _bank.HandleBankClose(peerId, root); break;
				case "bank_cert_mode": _bank.HandleBankCertMode(peerId, root); break;
				case "combine_attempt": _recipe.HandleCombineAttempt(peerId, root); break;
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
		if (!_state.Sessions.ContainsKey(peerId))
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
		if (!_state.Sessions.ContainsKey(peerId))
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
		if (_state.Sessions.Count == 0) return;
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
		if (!_state.Sessions.TryGetValue(peerId, out PlayerSession session)) { GD.Print("[DIAG] run_action: no session"); return; }
		if (!root.TryGetProperty("script", out var scriptEl)) { GD.Print("[DIAG] run_action: no script"); return; }
		string scriptPath = scriptEl.GetString() ?? "";
		var ast = _gfs.GetAst(scriptPath);
		//GD.Print($"[DIAG] run_action from {peerId}: script={scriptPath} target={(root.TryGetProperty("target_id", out var t) ? t.ToString() : "none")} ast={(ast == null ? "NULL" : "ok")}");
		if (ast == null)
		{
			SendError(peerId, "run_action_result", ErrorKeys.GfsScriptNotFound);
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
			SendError(peerId, "run_action_result", ErrorKeys.GfsRuntimeError);
			return;
		}
		//GD.Print($"[DIAG] after run: effects={effects.Count} endReq={ctx.EndScriptRequested} tags=[{string.Join(",", ast.Tags)}]");

		if (_state.Positions.TryGetValue(session.PlayerId, out var pos))
		{
			int cx = (int)MathF.Floor((float)MathF.Round(pos.X) / _world.ChunkSize);
			int cy = (int)MathF.Floor((float)MathF.Round(pos.Z) / _world.ChunkSize);
			RouteChatEffects(session.PlayerId, session.Username, cx, cy, effects, peerId);
		}

		if (ast.Tags.Contains(Gfs.GfsTags.OncePerTick) && !ctx.EndScriptRequested)
		{
			if (scriptPath.EndsWith("follow_player.gfs"))
				_coordinator.OnFollowStarted(session.PlayerId);
			_perTick.Start(session.PlayerId, scriptPath, ast, ctx);
		}

		Send(peerId, new { type = "run_action_result", ok = true, effects });
	}

	private void HandlePing(long peerId, JsonElement root)
	{
		if (!_state.Sessions.ContainsKey(peerId)) return;
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
		if (!_state.Sessions.TryGetValue(peerId, out PlayerSession s) || !s.IsAdmin) return;
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
		GD.Print($"[Server] Graceful shutdown: notifying {_state.Sessions.Count} client(s)");

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
	if (!_state.Sessions.TryGetValue(peerId, out PlayerSession session)) return;

	// Manual click-to-walk cancels all script-driven movement and pending
	// queues. Without clearing pickup, a queued pickup abandoned by walking
	// elsewhere would fire the next time the player stepped on the item.
	_perTick?.EndByStem(session.PlayerId, "follow_player");
	_coordinator.OnPlayerWalked(session.PlayerId);
	_state.PendingEngage.Remove(session.PlayerId);
	_state.PendingPickup.Remove(session.PlayerId);

	if (_state.CombatByPlayer.TryGetValue(session.PlayerId, out var combatPair) && !combatPair.StateAnnounced)
	{
		EndCombat(combatPair);
	}

	if (!_state.Positions.TryGetValue(session.PlayerId, out PlayerPosition current)) return;

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

	_state.Intents[peerId] = path;
}



	// ---------- Auth ----------

	private async Task HandleHelloAsync(long peerId)
	{
		string version = await _db.GetSqliteVersionAsync();
		long total = await _db.Players.CountAsync();
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
			var (created, playerId) = await _db.Players.TryCreateAsync(username, hash, makeAdmin);
			if (!created) { SendError(peerId, "register_result", ErrorKeys.RegisterUsernameTaken); return; }
			GD.Print($"[Server] Registered: {username} (id {playerId}{(makeAdmin ? ", dev admin" : "")})");
			await _db.Stats.EnsureStatsAsync(playerId);
			await _db.Stats.EnsureInventoryAsync(playerId);

			if (makeAdmin)
			{
				// Dev trade-testing kit. forceAll=true means every loot entry is
				// given (with its rolled quantity), not a single random pick.
				await GiveLootTableAsync(playerId, "trade_testing", forceAll: true);

				// Roll starting credits. Written straight to the DB; the player
				// is not yet in _state.Credits (that cache is populated on
				// login in CompleteLoginAsync), so we can't route through
				// GivePlayerCredits here.
				if (_creditsLootBySlug.TryGetValue("trade_testing_credits", out var creditRange))
				{
					int amount = _combatRng.Next(creditRange.Min, creditRange.Max + 1);
					if (amount > 0) await _db.Players.SetCreditsAsync(playerId, amount);
					GD.Print($"[Server] Dev trade-testing kit: rolled {amount} credits for {username}");
				}
				else
				{
					GD.PrintErr($"[Server] Dev trade-testing kit: credits_loot 'trade_testing_credits' missing");
				}

				GD.Print($"[Server] Dev trade-testing kit for {username}");
			}

			Send(peerId, new { type = "register_result", ok = true, username });
		}
		finally { _authInProgress.Remove(peerId); }
	}

	private async Task HandleLoginAsync(long peerId, string username, string password, string reason = "")
	{
		GD.Print($"[Server] Got login from peer {peerId}: user='{username}'");
		if (_state.Sessions.ContainsKey(peerId))
		{ SendError(peerId, "login_result", ErrorKeys.LoginAlreadyLoggedIn); return; }
		if (!_authInProgress.Add(peerId))
		{ SendError(peerId, "login_result", ErrorKeys.RequestInProgress); return; }

		try
		{
			PlayerRecord player = await _db.Players.GetByUsernameAsync(username);
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
	long loginCount = await _db.Players.RecordLoginAsync(player.Id);
	long totalPlayers = await _db.Players.CountAsync();
	if (!_connectedPeers.Contains(peerId)) return;

	if (!await _db.Stats.EnsureStatsAsync(player.Id))
	{
		GD.PrintErr($"[Server] Failed to ensure stats for {player.Username} (id {player.Id}); login aborted");
		SendError(peerId, "login_result", ErrorKeys.LoginStatsInitFailed);
		return;
	}

	if (!await _db.Stats.EnsureInventoryAsync(player.Id))
	{
		GD.PrintErr($"[Server] Failed to ensure inventory/bank for {player.Username} (id {player.Id}); login aborted");
		SendError(peerId, "login_result", ErrorKeys.LoginStatsInitFailed);
		return;
	}

	var combatData = await _db.Stats.GetCombatAsync(player.Id);
	_state.PlayerCombat[player.Id] = combatData ?? new PlayerCombatData(1, 1, 1, 0, 0, 10, 10, 1);

	var inv = await _db.Inventory.GetSlotsAsync(player.Id);
	if (inv == null) inv = new long[28];
	_state.Inventories[player.Id] = inv;

	_bank.LoadForPlayer(player.Id);

	int credits = await _db.Players.GetCreditsAsync(player.Id);
	_state.Credits[player.Id] = credits;


	_state.SkillsByPlayer[player.Id] = await _db.Stats.GetSkillsAsync(player.Id);

	// Compute spawn position and publish it BEFORE the session. Any concurrent
	// EnterWorld (from another player logging in) reads _state.Positions to build
	// world_state — the position must be set before the peer becomes visible.
	PlayerPosition spawnPos;
	var saved = await _db.Players.GetTileAsync(player.Id);
	if (saved.HasValue)
	{
		if (IsValidTile(saved.Value.X, saved.Value.Y))
		{
			var info = _world.GetTileInfo(saved.Value.X, saved.Value.Y);
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
	_state.Positions[player.Id] = spawnPos;

	// Session published after position. No awaits between these lines, so the
	// pair is atomic from the perspective of other ticks.
	long sid = Random.Shared.NextInt64(1, long.MaxValue);
	_ = _db.Players.SetSidAsync(player.Id, sid);
	var session = new PlayerSession(player.Id, player.Username, player.IsAdmin, sid);
	_state.Sessions[peerId] = session;
	_state.PeerByPlayerId[player.Id] = peerId;

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
		credits = credits,
		hitpoints = combatData?.CurrentHp ?? 10,
		max_hitpoints = combatData?.MaxHp ?? 10,
		spawn = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw }
	});

	SendInventory(peerId);
	SendSkills(peerId);

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
	foreach (var (otherPeerId, other) in _state.Sessions)
	{
		if (otherPeerId == peerId) continue;
		if (!_state.Positions.TryGetValue(other.PlayerId, out var pos)) continue;
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
		ActiveNpc.FloorDiv(tx, _world.ChunkSize),
		ActiveNpc.FloorDiv(ty, _world.ChunkSize));
	RefreshNpcVisibility(peerId, tx, ty);
}

	// ---------- World file ----------

	private void HandleWorldInfo(long peerId)
	{
		if (!_state.Sessions.ContainsKey(peerId)) { SendError(peerId, "world_info_result", ErrorKeys.WorldNotLoggedIn); return; }
		if (!_world.EnsureLoaded())         { SendError(peerId, "world_info_result", ErrorKeys.WorldUnavailable); return; }
		Send(peerId, new { type = "world_info_result", ok = true, world_version = _world.Version, format_version = _world.FormatVersion });
	}

	private void HandleWorldFetch(long peerId)
	{
		if (!_state.Sessions.ContainsKey(peerId)) { SendError(peerId, "world_data", ErrorKeys.WorldNotLoggedIn); return; }
		if (!_world.EnsureLoaded())         { SendError(peerId, "world_data", ErrorKeys.WorldUnavailable); return; }
		var root = _world.GetRootElement();
		if (root == null) { SendError(peerId, "world_data", ErrorKeys.WorldUnavailable); return; }
		Send(peerId, new { type = "world_data", ok = true, world_version = _world.Version, format_version = _world.FormatVersion, data = root.Value });
	}

	// ---------- Outbound ----------

	private void SendError(long peerId, string type, string errorKey, object errorParams = null)
		=> Send(peerId, new { type, ok = false, error_key = errorKey, error_params = errorParams ?? new { } });

	internal void Send(long peerId, object message, int channel = 0,
		MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
		=> _outbound.Send(peerId, message, channel, mode);

	internal void RouteChatEffects(long pid, string username, int cx, int cy,
	List<Dictionary<string, object>> effects, long selfPeer)
	=> _outbound.RouteChatEffects(pid, username, cx, cy, effects, selfPeer);

	private void Broadcast(object message, long exceptPeerId = 0, int channel = 0,
		MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
		=> _outbound.Broadcast(message, exceptPeerId, channel, mode);

	private void SendBytes(long peerId, byte[] bytes, int channel, MultiplayerPeer.TransferModeEnum mode)
	{
		if (!_connectedPeers.Contains(peerId)) return;
		_mp.SendBytes(bytes, (int)peerId, mode, channel);
	}
	private void HandleChatSend(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out PlayerSession session)) return;
		if (!_state.Positions.TryGetValue(session.PlayerId, out PlayerPosition pos)) return;
		if (!root.TryGetProperty("message", out JsonElement msgEl)) return;

		string text = (msgEl.GetString() ?? "").Trim();
		if (text.Length == 0) return;
		text = CollapseSpaces(text);
		if (text.Length > MaxChatLength) text = text.Substring(0, MaxChatLength);

		int tileX = (int)MathF.Round(pos.X);
		int tileY = (int)MathF.Round(pos.Z);
		int cx = (int)MathF.Floor((float)tileX / _world.ChunkSize);
		int cy = (int)MathF.Floor((float)tileY / _world.ChunkSize);

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
		_state.Positions.TryGetValue(playerId, out pos);

	internal bool TryGetTileInfo(int x, int y, out TileInfo info)
	{
		if (_world.TryGetTile(x, y, out info)) return true;
		info = new TileInfo(100, true);
		return false;
	}

	internal bool TryGetCredits(long playerId, out int credits)
	{
		credits = 0;
		return _state.Credits.TryGetValue(playerId, out credits);
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
			var info = _world.GetTileInfo(startX, startY);
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
						var info = _world.GetTileInfo(nx, ny);
						return new PlayerPosition(nx, info.Z, ny, 0f);
					}
				}
			}
		}
		var fallbackZ = _world.GetTileInfo(startX, startY).Z;
		return new PlayerPosition(startX, fallbackZ, startY, 0f);
	}




	internal int ChunkSize => _world.ChunkSize;

	internal void SetIntent(long playerId, List<Vector2I> route)
	{
		long peerId = -1;
		foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId >= 0) _state.Intents[peerId] = new List<Vector2I>(route);
	}

	internal void RequestTrade(long requesterId, long targetPlayerId) =>
		_trade.RequestTrade(requesterId, targetPlayerId);

	internal void CancelTradeForPlayer(long playerId, string errorKey) =>
		_trade.CancelTradeForPlayer(playerId, errorKey);

	internal void CancelTradeQueuesForPlayer(long playerId, string errorKey) =>
		_trade.CancelTradeQueuesForPlayer(playerId, errorKey);

	internal void RequestTalk(long playerId, long npcId) =>
		_dialogue.RequestTalk(playerId, npcId);

	internal void CancelDialogueForPlayer(long playerId, string errorKey) =>
		_dialogue.CancelDialogueForPlayer(playerId, errorKey);

	internal void CancelPendingTalkFor(long playerId) =>
		_dialogue.CancelPendingTalkFor(playerId);

	internal void ProcessPendingTalk() =>
		_dialogue.ProcessPendingTalk();

	internal void HandleDialogueChoose(long peerId, JsonElement root) =>
		_dialogue.HandleDialogueChoose(peerId, root);

	internal bool HasDialogueScript(ActiveNpc npc) =>
		_dialogue.HasDialogueScript(npc);

	internal void ClearDialoguePendingFor(long playerId) =>
		_dialogue.ClearPendingFor(playerId);

	internal void RequestBank(long playerId, long npcId) =>
		_bank.RequestBank(playerId, npcId);

	internal void CancelBankForPlayer(long playerId, string errorKey) =>
		_bank.CancelBankForPlayer(playerId, errorKey);

	internal void CancelPendingBankFor(long playerId) =>
		_bank.CancelPendingBankFor(playerId);

	internal void ProcessPendingBank() =>
		_bank.ProcessPendingBank();

	internal void ProcessBankIntents() =>
		_bank.ProcessBankIntents();

	internal void HandleBankDeposit(long peerId, JsonElement root) =>
		_bank.HandleBankDeposit(peerId, root);

	internal void HandleBankWithdraw(long peerId, JsonElement root) =>
		_bank.HandleBankWithdraw(peerId, root);

	internal void HandleBankMove(long peerId, JsonElement root) =>
		_bank.HandleBankMove(peerId, root);

	internal void HandleBankClose(long peerId, JsonElement root) =>
		_bank.HandleBankClose(peerId, root);

	internal void HandleBankCertMode(long peerId, JsonElement root) =>
		_bank.HandleBankCertMode(peerId, root);

	internal bool IsPlayerInBank(long playerId) =>
		_bank.IsPlayerInBank(playerId);
	
	internal void RemovePlayerIntent(long playerId)
	{
		if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId)) _state.Intents.Remove(peerId);
	}

	private static int CountInventoryOfItem(long[] inventory, int itemId)
	{
		int total = 0;
		foreach (long packed in inventory)
		{
			if (packed == 0) continue;
			if (ItemStack.ItemId(packed) == itemId) total += ItemStack.Quantity(packed);
		}
		return total;
	}

	internal Dictionary<string, object>? GetActiveScriptArgs(long playerId, string stem) =>
		_perTick?.GetArgsForActive(playerId, stem);

	internal bool EndPerTickScript(long playerId, string stem) =>
		_perTick != null && _perTick.EndByStem(playerId, stem);

	private void RunScriptFromGfs(long playerId, string stem, Dictionary<string, object> args, Gfs.CallContext parentCtx)
	{
		if (stem == "follow_player")
			_coordinator.OnFollowStarted(playerId);
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
		if (!_state.Positions.TryGetValue(selfPlayerId, out var pos)) return new();
		return PathfindBetween((int)MathF.Round(pos.X), (int)MathF.Round(pos.Z), goalX, goalY);
	}

	internal List<Vector2I> PathfindBetween(int startX, int startY, int goalX, int goalY)
		=> _pathfinder.FindPath(startX, startY, goalX, goalY);

	internal List<Vector2I> FindAdjacentWalkablePath(int x, int y)
		=> _pathfinder.FindAdjacentWalkable(x, y);

	private HashSet<long> ComputeVisibleNpcIds(int tileX, int tileY)
	{
		var visible = new HashSet<long>();
		int pcx = ActiveNpc.FloorDiv(tileX, _world.ChunkSize);
		int pcy = ActiveNpc.FloorDiv(tileY, _world.ChunkSize);
		foreach (var npc in _npcs.Values)
		{
			if (npc.IsDead) continue;
			int ncx = npc.ChunkX(_world.ChunkSize);
			int ncy = npc.ChunkY(_world.ChunkSize);
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
		has_dialogue = HasDialogueScript(npc),
		dialogue_script = npc.DialogueScript,
		is_banker = npc.IsBanker
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
		if (!_state.Sessions.ContainsKey(peerId)) return;
		Send(peerId, new
		{
			type = "npc_render_info_response",
			slugs = _npcRender.GetManifest()
		});
	}

	private void HandleNpcRenderFetch(long peerId, JsonElement root)
	{
		if (!_state.Sessions.ContainsKey(peerId)) return;
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
	internal void KillNpc(long npcId, long killerPlayerId = 0)
{
	if (!_npcs.TryGetValue(npcId, out var npc)) return;
	if (npc.IsDead) return;

	// Resolve the killer if not explicitly passed. Combat pairs set it to the
	// attacking player; admin-kill scripts pass it directly.
	if (killerPlayerId == 0 && _combatByNpc.TryGetValue(npcId, out var killerPair))
		killerPlayerId = killerPair.PlayerId;

	RollNpcDeathLoot(npc, killerPlayerId);

	npc.IsDead = true;
	npc.DiedAtMs = (long)Time.GetTicksMsec();
	npc.Path.Clear();
	npc.Hitpoints = 0;
	_ = _db.Npcs.MarkDeadAsync(npc.Id);

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
		_ = _db.Npcs.ReviveAsync(npc.Id);
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

// ---------- Loot + credits ----------

private void RollNpcDeathLoot(ActiveNpc npc, long killerPlayerId)
{
	int deathX = (int)MathF.Round(npc.PosX);
	int deathY = (int)MathF.Round(npc.PosZ);
	var tileInfo = _world.GetTileInfo(deathX, deathY);
	int deathZ = tileInfo.Z;

	// Item drop
	if (!string.IsNullOrEmpty(npc.LootTableName)
		&& _lootTablesBySlug.TryGetValue(npc.LootTableName, out var bag)
		&& bag.Count > 0)
	{
		var keys = new List<string>(bag.Keys);
		string itemSlug = keys[_combatRng.Next(keys.Count)];
		int qty = RollQuantity(bag[itemSlug]);
		if (qty > 0)
		{
			SpawnGroundItemAt(itemSlug, qty, deathX, deathY, deathZ);
			Diag.Log("diag_item", $"NPC {npc.Name} dropped {itemSlug} x{qty} at ({deathX},{deathY})");
		}
	}

	// Credits
	if (killerPlayerId > 0
		&& !string.IsNullOrEmpty(npc.CreditsTableName)
		&& _creditsLootBySlug.TryGetValue(npc.CreditsTableName, out var range)
		&& _state.Positions.ContainsKey(killerPlayerId))
	{
		int amount = _combatRng.Next(range.Min, range.Max + 1);
		if (amount > 0)
		{
			GivePlayerCredits(killerPlayerId, amount);
			Diag.Log("diag_item", $"NPC {npc.Name} awarded {amount} credits to player {killerPlayerId}");
		}
	}
}

internal bool SpawnGroundItemAt(string itemSlug, int quantity, int tileX, int tileY, int tileZ)
{
	if (quantity < 1) return false;
	if (!_items.ContainsSlug(itemSlug)) return false;

	var item = new GroundItem
	{
		Id = _nextGroundItemId++,
		ItemSlug = itemSlug,
		Quantity = quantity,
		TileX = tileX,
		TileY = tileY,
		TileZ = tileZ,
		SpawnedAtMs = (long)Time.GetTicksMsec(),
	};
	_groundItems[item.Id] = item;
	BroadcastGroundItemToNearby(item);
	return true;
}

internal void GivePlayerCredits(long playerId, int amount)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return;
	if (amount <= 0) return;
	if (!_state.Credits.TryGetValue(playerId, out int current)) return;
	int newTotal = current + amount;
	_state.Credits[playerId] = newTotal;
	_db.Players.SetCreditsSync(playerId, newTotal);
	BroadcastCredits(playerId);
}

private void BroadcastCredits(long playerId)
{
	if (!_state.PeerByPlayerId.TryGetValue(playerId, out long peerId)) return;
	if (!_state.Credits.TryGetValue(playerId, out int c)) return;
	Send(peerId, new { type = "credits_updated", credits = c });
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

	if (_state.CombatByPlayer.TryGetValue(playerId, out var existing))
	{
		if (existing.NpcId == npcId) return;
		return;
	}

	if (_combatByNpc.ContainsKey(npcId))
	{
		if (_state.PendingEngage.TryGetValue(playerId, out var pendingNpc) && pendingNpc == npcId)
			_state.PendingEngage.Remove(playerId);
		ChatToPlayer(playerId, "That's already in a fight, I cannot engage in attack.");
		Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}); rejected");
		return;
	}

	_coordinator.OnCombatStarted(playerId);

	if (ArePositionsAdjacent(playerId, npc))
	{
		_state.PendingEngage.Remove(playerId);
		EngageNpc(playerId, npcId);
		return;
	}

	_state.PendingEngage[playerId] = npcId;

	// Cancel follow at click time, not at promotion time. During the approach
	// phase the follow script would keep re-issuing its intent every tick and
	// out-compete MovePlayerAdjacentToNpc — the player would visibly bounce
	// between walking to the NPC and walking to the followed player.
	_perTick?.EndByStem(playerId, "follow_player");

	long peerId = -1;
	foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId >= 0) _state.Intents.Remove(peerId);
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

	if (_state.CombatByPlayer.TryGetValue(playerId, out var existing))
	{
		if (existing.NpcId == npcId) return;
		return;
	}

	if (_combatByNpc.ContainsKey(npcId))
	{
		if (initiatedByPlayer)
			ChatToPlayer(playerId, "That's already in a fight, I cannot engage in attack.");
		Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}) at promotion; rejected");
		return;
	}

	_coordinator.OnCombatStarted(playerId);

	var pair = new CombatPair
	{
		PlayerId = playerId,
		NpcId = npcId,
		LastPlayerSwingTick = _state.CurrentTick - PlayerAttackSpeedTicks,
		// Player-initiated combat: NPC's first swing is delayed by a full
		// cooldown so the player always gets a clean opening hit. Hostility
		// (initiatedByPlayer=false): NPC is free to swing on the first
		// adjacent tick, matching "monster aggro hits immediately."
		LastNpcSwingTick = initiatedByPlayer
			? _state.CurrentTick
			: _state.CurrentTick - npc.AttackSpeedTicks,
		StateAnnounced = false,
		InitiatedByPlayer = initiatedByPlayer,
	};
	_state.CombatByPlayer[playerId] = pair;
	if (!_combatByNpc.ContainsKey(npcId)) _combatByNpc[npcId] = pair;

	_perTick?.EndByStem(playerId, "follow_player");

	Diag.Log("diag_combat", $"Player {playerId} engaged NPC {npc.Name} (id {npcId}) initiatedByPlayer={initiatedByPlayer}");
}

	internal void EndCombat(CombatPair pair)
	{
		_state.CombatByPlayer.Remove(pair.PlayerId);
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
	if (_state.CombatByPlayer.Count == 0) return;
	var toEnd = new List<CombatPair>();

	foreach (var pair in _state.CombatByPlayer.Values)
	{
		if (!_npcs.TryGetValue(pair.NpcId, out var npc)) { toEnd.Add(pair); continue; }
		if (npc.IsDead) { toEnd.Add(pair); continue; }
		if (!_state.Positions.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
		if (!_state.PlayerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }

		if (!ArePositionsAdjacent(pair.PlayerId, npc))
		{
			if (pair.InitiatedByPlayer && !pair.StateAnnounced)
			{
				MovePlayerAdjacentToNpc(pair.PlayerId, npc);
				continue;
			}

			if (DistanceFromSpawn(npc) > NpcLeashRadiusTiles)
			{
				EndCombat(pair);
				npc.ReturningHome = true;
				npc.Path.Clear();
				MoveNpcTo(npc.Id, npc.TileX, npc.TileY);
				Diag.Log("diag_combat", $"NPC {npc.Name} (id {npc.Id}) leash broken");
				continue;
			}

			if (_state.Positions.TryGetValue(pair.PlayerId, out var chasePos))
			{
				var playerTile = new Vector2I(
					(int)MathF.Round(chasePos.X),
					(int)MathF.Round(chasePos.Z));
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

		// Combat facing:
		//   - Player: only snap to face the NPC when standing still. While
		//     walking, movement yaw drives facing (so fleeing looks correct).
		//   - NPC:   always faces its target (it's actively engaging).
		if (pair.StateAnnounced && _state.Positions.TryGetValue(pair.PlayerId, out var facingPos))
		{
			int ptx = (int)MathF.Round(facingPos.X);
			int pty = (int)MathF.Round(facingPos.Z);
			int ntx = (int)MathF.Round(npc.PosX);
			int nty = (int)MathF.Round(npc.PosZ);
			float nYaw = MathF.Atan2(-(ptx - ntx), -(pty - nty));
			nYaw = MathF.Round(nYaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

			if (!_movement.IsMoving(pair.PlayerId))
			{
				float pYaw = MathF.Atan2(-(ntx - ptx), -(nty - pty));
				pYaw = MathF.Round(pYaw / (MathF.PI / 4f)) * (MathF.PI / 4f);
				if (MathF.Abs(pYaw - facingPos.Yaw) > 0.05f)
				{
					_state.Positions[pair.PlayerId] = facingPos with { Yaw = pYaw };
					Broadcast(new
					{
						type = "tick_positions",
						tick = _state.CurrentTick,
						updates = new[] {
							new { player_id = pair.PlayerId, x = facingPos.X, y = facingPos.Y, z = facingPos.Z, yaw = pYaw }
						}
					}, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
				}
			}

			if (MathF.Abs(nYaw - npc.Yaw) > 0.05f)
			{
				npc.Yaw = nYaw;
				npc.LastSentYaw = nYaw;
				Broadcast(new
				{
					type = "npc_tick_positions",
					updates = new[] {
						new { npc_id = npc.Id, x = npc.PosX, y = npc.PosY, z = npc.PosZ, yaw = nYaw }
					}
				}, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
			}
		}

		if (!_movement.IsMoving(pair.PlayerId)
			&& CanSwingTick(pair.LastPlayerSwingTick, PlayerAttackSpeedTicks, _state.CurrentTick))
		{
			ResolvePlayerAttack(pair, npc);
			pair.LastPlayerSwingTick = _state.CurrentTick;
			if (npc.IsDead || !_state.PlayerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
		}

		if (_combatByNpc.TryGetValue(pair.NpcId, out var npcOwner) && npcOwner == pair)
		{
			if (CanSwingTick(pair.LastNpcSwingTick, npc.AttackSpeedTicks, _state.CurrentTick))
			{
				ResolveNpcAttack(pair, npc);
				pair.LastNpcSwingTick = _state.CurrentTick;
				if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var pd) || pd.CurrentHp <= 0)
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
		_ = _db.Stats.SetNpcHpAsync(npc.Id, newHp);
		BroadcastNpcHp(npc);
		Diag.Log("diag_combat", $"NPC {npc.Name} healed to {newHp}/{npc.MaxHitpoints} ({(inCombat ? "in combat" : "out of combat")})");
	}
}

private void ProcessPlayerHealing()
{
	// ~25s (tick = 0.3s). Applies regardless of combat state.
	const int HEAL_INTERVAL_TICKS = 83;
	const int HEAL_PCT = 10;

	foreach (var kv in _state.PlayerCombat)
	{
		long playerId = kv.Key;
		var data = kv.Value;
		if (data.CurrentHp >= data.MaxHp) { _playerHealAccumTicks[playerId] = 0; continue; }
		if (!_state.Positions.ContainsKey(playerId)) continue;

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

		_state.PlayerCombat[playerId] = data with { CurrentHp = newHp };
		_ = _db.Stats.SetPlayerHpAsync(playerId, newHp);
		BroadcastPlayerHp(playerId, newHp, data.MaxHp);
		Diag.Log("diag_combat", $"Player {playerId} regen to {newHp}/{data.MaxHp}");
	}
}

	private void ResolvePlayerAttack(CombatPair pair, ActiveNpc npc)
{
	if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var stats)) return;
	BroadcastPlayerAnim(pair.PlayerId, "attack", AttackAnimDurationMs);

	bool hit = CombatRolls.RollHit(stats.Accuracy, stats.MeleeBonus, npc.Defence, npc.MeleeDefense, _combatRng);
	int damage = hit ? CombatRolls.RollDamage(stats.Strength, stats.MeleeBonus, _combatRng) : 0;

	if (damage > 0)
	{
		AwardCombatExperience(pair.PlayerId, damage);
		npc.Hitpoints = Math.Max(0, npc.Hitpoints - damage);
		_ = _db.Stats.SetNpcHpAsync(npc.Id, npc.Hitpoints);
		BroadcastNpcHp(npc);
	}

	Broadcast(new { type = "damage_number", target_type = "npc", target_id = npc.Id, amount = damage });

	if (npc.Hitpoints <= 0)
	{
		KillNpc(npc.Id);
		return;
	}
	if (!_combatByNpc.ContainsKey(npc.Id)) _combatByNpc[npc.Id] = pair;
}

	private void AwardCombatExperience(long playerId, int damage)
	{
		if (damage <= 0 || !_state.SkillsByPlayer.TryGetValue(playerId, out var skills)) return;

		int accuracyIndex = skills.FindIndex(skill => skill.Id == "accuracy");
		int hitpointsIndex = skills.FindIndex(skill => skill.Id == "hitpoints");
		if (accuracyIndex < 0 || hitpointsIndex < 0) return;

		var accuracy = skills[accuracyIndex];
		var hitpoints = skills[hitpointsIndex];
		long accuracyXpTenths = accuracy.CurrentXpTenths + (long)damage * SkillExperience.AccuracyXpTenthsPerDamage;
		long hitpointsXpTenths = hitpoints.CurrentXpTenths + (long)damage * SkillExperience.HitpointsXpTenthsPerDamage;
		int accuracyLevel = Math.Max(accuracy.Max, SkillExperience.GetLevelForXpTenths(accuracyXpTenths));
		int hitpointsMax = Math.Max(hitpoints.Max, SkillExperience.GetLevelForXpTenths(hitpointsXpTenths));

		skills[accuracyIndex] = accuracy with
		{
			Level = accuracyLevel,
			Max = accuracyLevel,
			CurrentXpTenths = accuracyXpTenths,
		};
		skills[hitpointsIndex] = hitpoints with
		{
			Max = hitpointsMax,
			CurrentXpTenths = hitpointsXpTenths,
		};

		if (_state.PlayerCombat.TryGetValue(playerId, out var combat))
		{
			int newMaxHp = Math.Max(combat.MaxHp, hitpointsMax);
			_state.PlayerCombat[playerId] = combat with
			{
				Accuracy = Math.Max(combat.Accuracy, accuracyLevel),
				MaxHp = newMaxHp,
			};
			if (newMaxHp != combat.MaxHp)
				BroadcastPlayerHp(playerId, combat.CurrentHp, newMaxHp);
		}

		_ = _db.Stats.AwardCombatXpAsync(playerId, damage, accuracyLevel, hitpointsMax);
		if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
			SendSkills(peerId);
	}

	private void ResolveNpcAttack(CombatPair pair, ActiveNpc npc)
{
	if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var pd)) return;
	BroadcastNpcAnim(npc.Id, "attack", AttackAnimDurationMs);

	bool hit = CombatRolls.RollHit(npc.Accuracy, npc.MeleeBonus, pd.Defence, pd.MeleeDefense, _combatRng);
	int damage = hit ? CombatRolls.RollDamage(npc.Strength, npc.MeleeBonus, _combatRng) : 0;

	if (damage > 0)
	{
		int newHp = Math.Max(0, pd.CurrentHp - damage);
		_state.PlayerCombat[pair.PlayerId] = pd with { CurrentHp = newHp };
		_ = _db.Stats.SetPlayerHpAsync(pair.PlayerId, newHp);
		BroadcastPlayerHp(pair.PlayerId, newHp, pd.MaxHp);
	}

	Broadcast(new { type = "damage_number", target_type = "player", target_id = pair.PlayerId, amount = damage });
}

	private void HandlePlayerDeath(long playerId)
	{
		_coordinator.OnPlayerDied(playerId);

		long peerId = -1;
		foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId < 0) return;

		if (_state.CombatByPlayer.TryGetValue(playerId, out var pair)) EndCombat(pair);

		// Clear all movement state so the player doesn't resume walking toward
		// their last destination (or re-route to an NPC) from spawn position.
		_state.Intents.Remove(peerId);
		_state.PendingEngage.Remove(playerId);

		var spawnPos = ComputeSpawnPosition(playerId);
		_state.Positions[playerId] = spawnPos;

		int hp = 10;
		if (_state.PlayerCombat.TryGetValue(playerId, out var pd))
		{
			_state.PlayerCombat[playerId] = pd with { CurrentHp = pd.MaxHp };
			_ = _db.Stats.SetPlayerHpAsync(playerId, pd.MaxHp);
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

	internal bool ArePositionsAdjacent(long playerId, ActiveNpc npc)
	{
		if (!_state.Positions.TryGetValue(playerId, out var ppos)) return false;
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
		if (!_state.Positions.TryGetValue(playerId, out var ppos)) return int.MaxValue;
		int dx = (int)MathF.Round(ppos.X) - (int)MathF.Round(npc.PosX);
		int dy = (int)MathF.Round(ppos.Z) - (int)MathF.Round(npc.PosZ);
		return Math.Max(Math.Abs(dx), Math.Abs(dy));
	}

	/// <summary>
/// True if the player has an active movement intent (auto-path or manual click).
/// Used to gate auto-retaliate: swings only happen when standing still.
/// </summary>


private int DistanceFromSpawn(ActiveNpc npc)
{
	int dx = (int)MathF.Round(npc.PosX) - npc.TileX;
	int dy = (int)MathF.Round(npc.PosZ) - npc.TileY;
	return Math.Max(Math.Abs(dx), Math.Abs(dy));
}

	internal bool MovePlayerAdjacentToNpc(long playerId, ActiveNpc npc)
{
	long peerId = -1;
	foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId < 0) return false;
	if (_state.Intents.ContainsKey(peerId)) return true;   // already walking — progress possible

	if (!_state.Positions.TryGetValue(playerId, out var ppos)) return false;
	int px = (int)MathF.Round(ppos.X);
	int py = (int)MathF.Round(ppos.Z);
	int nx = (int)MathF.Round(npc.PosX);
	int ny = (int)MathF.Round(npc.PosZ);

	// Same tile as the target NPC: step off to an adjacent walkable tile.
	if (px == nx && py == ny)
	{
		Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
							new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
		foreach (var d in dirs)
		{
			var cand = new Vector2I(px + d.X, py + d.Y);
			if (IsValidTile(cand.X, cand.Y))
			{
				_state.Intents[peerId] = new List<Vector2I> { cand };
				return true;
			}
		}
		return false;
	}

	var route = PathfindToNearest(playerId, nx, ny);
	if (route.Count == 0) return false;
	var goal = new Vector2I(nx, ny);
	if (route[^1] == goal) route.RemoveAt(route.Count - 1);
	if (route.Count == 0) return false;
	SetIntent(playerId, route);
	return true;
}


private int RollQuantity((int Min, int Max) range)
{
	if (range.Min <= 0 && range.Max <= 0) return 0;
	if (range.Min == range.Max) return range.Min;
	return _combatRng.Next(range.Min, range.Max + 1);
}

	internal ActiveNpc? GetNpcById(long npcId) =>
		_npcs.TryGetValue(npcId, out var n) ? n : null;

	internal bool TryGetNpcById(long npcId, out ActiveNpc npc) =>
		_npcs.TryGetValue(npcId, out npc!);

	internal bool IsNpcInCombat(long npcId) => _combatByNpc.ContainsKey(npcId);

	internal void SnapAndFaceForDialogue(long playerId, ActiveNpc npc)
	{
		if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

		int px = (int)MathF.Round(pos.X);
		int pz = (int)MathF.Round(pos.Z);
		var tinfo = _world.GetTileInfo(px, pz);
		int nx = (int)MathF.Round(npc.PosX);
		int nz = (int)MathF.Round(npc.PosZ);

		float yaw = MathF.Atan2(-(nx - px), -(nz - pz));
		yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

		var newPos = new PlayerPosition(px, tinfo.Z, pz, yaw);
		_state.Positions[playerId] = newPos;

		Broadcast(new
		{
			type = "tick_positions",
			tick = _state.CurrentTick,
			updates = new[] {
				new {
					player_id = playerId,
					x = newPos.X, y = newPos.Y, z = newPos.Z, yaw = newPos.Yaw,
				}
			}
		}, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);

		long sid = 0;
		long peerId = GetPeerIdForPlayer(playerId);
		if (peerId >= 0 && _state.Sessions.TryGetValue(peerId, out var sess))
			sid = sess.Sid;
		QueueNpcLookAt(npc.Id, playerId, sid);
	}

	private void QueueNpcLookAt(long npcId, long playerId, long sid)
	{
		if (_npcLookAt.TryGetValue(npcId, out var existing))
		{
			if (sid >= existing.Sid) return;
		}
		_npcLookAt[npcId] = (playerId, sid);
	}

	private void ProcessNpcLookAt()
	{
		if (_npcLookAt.Count == 0) return;
		foreach (var kv in _npcLookAt)
		{
			long npcId = kv.Key;
			long playerId = kv.Value.PlayerId;
			if (!_npcs.TryGetValue(npcId, out var npc)) continue;
			if (npc.IsDead) continue;
			if (_combatByNpc.ContainsKey(npcId)) continue;
			if (!_state.Positions.TryGetValue(playerId, out var ppos)) continue;

			int nx = (int)MathF.Round(npc.PosX);
			int nz = (int)MathF.Round(npc.PosZ);
			int px = (int)MathF.Round(ppos.X);
			int pz = (int)MathF.Round(ppos.Z);
			float yaw = MathF.Atan2(-(px - nx), -(pz - nz));
			yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

			if (MathF.Abs(yaw - npc.Yaw) > 0.05f)
			{
				npc.Yaw = yaw;
				npc.LastSentYaw = yaw;
				Broadcast(new
				{
					type = "npc_tick_positions",
					updates = new[] {
						new { npc_id = npcId, x = npc.PosX, y = npc.PosY, z = npc.PosZ, yaw = yaw }
					}
				}, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
			}
		}
		_npcLookAt.Clear();
	}

	internal long GetPeerIdForPlayer(long playerId)
	{
		if (_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)) return peerId;
		return -1;
	}

	internal void ChatToPlayer(long playerId, string message) =>
		_outbound.ChatToPlayer(playerId, message);

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
		foreach (long peerId in _state.Sessions.Keys.ToList())
		{
			if (!_state.Sessions.TryGetValue(peerId, out var sess)) continue;
			if (!_state.Positions.TryGetValue(sess.PlayerId, out var pos)) continue;
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
		foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
		if (peerId < 0) return;
		if (!_state.Sessions[peerId].IsAdmin)
		{
			ChatToPlayer(playerId, "Admin only.");
			return;
		}
		if (!_npcs.ContainsKey(npcId)) return;
		Send(peerId, new { type = "npc_editor_open", npc_id = npcId });
	}

private void HandleNpcEditorGet(long peerId, JsonElement root)
{
	if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
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
			render_slug = npc.RenderSlug,
			attackable = npc.Attackable,
			aggressive = npc.Aggressive,
			aggro_range = npc.AggroRangeTiles,
			ai_package_state = npc.AiPackageState,
			ai_action_state = npc.AiActionState,
			loot_table_name = npc.LootTableName,
			spawn_timer = npc.SpawnTimerSeconds,
			hitpoints = npc.Hitpoints,
			max_hitpoints = npc.MaxHitpoints,
			accuracy = npc.Accuracy,
			strength = npc.Strength,
			defence = npc.Defence,
			ether = npc.Ether,
			max_ether = npc.MaxEther,
			melee_bonus = npc.MeleeBonus,
			ranged_bonus = npc.RangedBonus,
			ether_bonus = npc.EtherBonus,
			melee_defense = npc.MeleeDefense,
			ranged_defense = npc.RangedDefense,
			ether_defense = npc.EtherDefense,
			attack_speed = npc.AttackSpeedTicks,
			final_level = npc.FinalLevel,
		}
	});
}

private void HandleNpcEditorSave(long peerId, JsonElement root)
{
	if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
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
		if (u.ContainsKey("aggressive"))       npc.Aggressive = (bool)u["aggressive"];
		if (u.ContainsKey("aggro_range"))      npc.AggroRangeTiles = Math.Max(1, Convert.ToInt32(u["aggro_range"]));
		if (u.ContainsKey("ai_package_state")) npc.AiPackageState = u["ai_package_state"].ToString() ?? "idle";
		if (u.ContainsKey("ai_action_state"))  npc.AiActionState = u["ai_action_state"].ToString() ?? "none";
		if (u.ContainsKey("loot_table_name"))  npc.LootTableName = u["loot_table_name"].ToString() ?? "common_loot_1";
		if (u.ContainsKey("spawn_timer"))      npc.SpawnTimerSeconds = Math.Max(1, Convert.ToInt32(u["spawn_timer"]));
		if (u.ContainsKey("hitpoints"))        npc.Hitpoints = Math.Max(0, Convert.ToInt32(u["hitpoints"]));
		if (u.ContainsKey("max_hitpoints"))    npc.MaxHitpoints = Math.Max(1, Convert.ToInt32(u["max_hitpoints"]));
		if (npc.Hitpoints > npc.MaxHitpoints)  npc.MaxHitpoints = npc.Hitpoints;
		if (u.ContainsKey("accuracy"))         npc.Accuracy = Math.Max(1, Convert.ToInt32(u["accuracy"]));
		if (u.ContainsKey("strength"))         npc.Strength = Math.Max(1, Convert.ToInt32(u["strength"]));
		if (u.ContainsKey("defence"))          npc.Defence = Math.Max(1, Convert.ToInt32(u["defence"]));
		if (u.ContainsKey("ether"))            npc.Ether = Math.Max(1, Convert.ToInt32(u["ether"]));
		if (u.ContainsKey("max_ether"))        npc.MaxEther = Math.Max(1, Convert.ToInt32(u["max_ether"]));
		if (npc.Ether > npc.MaxEther)          npc.MaxEther = npc.Ether;
		if (u.ContainsKey("melee_bonus"))      npc.MeleeBonus = Math.Max(0, Convert.ToInt32(u["melee_bonus"]));
		if (u.ContainsKey("ranged_bonus"))     npc.RangedBonus = Math.Max(0, Convert.ToInt32(u["ranged_bonus"]));
		if (u.ContainsKey("ether_bonus"))      npc.EtherBonus = Math.Max(0, Convert.ToInt32(u["ether_bonus"]));
		if (u.ContainsKey("melee_defense"))    npc.MeleeDefense = Math.Max(0, Convert.ToInt32(u["melee_defense"]));
		if (u.ContainsKey("ranged_defense"))   npc.RangedDefense = Math.Max(0, Convert.ToInt32(u["ranged_defense"]));
		if (u.ContainsKey("ether_defense"))    npc.EtherDefense = Math.Max(0, Convert.ToInt32(u["ether_defense"]));
		if (u.ContainsKey("attack_speed"))     npc.AttackSpeedTicks = Math.Max(1, Convert.ToInt32(u["attack_speed"]));

		// Sync the save dict to final values
		u["display"]          = npc.Display;
		u["attackable"]       = npc.Attackable;
		u["aggressive"]       = npc.Aggressive;
		u["aggro_range"]      = npc.AggroRangeTiles;
		u["ai_package_state"] = npc.AiPackageState;
		u["ai_action_state"]  = npc.AiActionState;
		u["loot_table_name"]  = npc.LootTableName;
		u["spawn_timer"]      = npc.SpawnTimerSeconds;
		u["hitpoints"]        = npc.Hitpoints;
		u["max_hitpoints"]    = npc.MaxHitpoints;
		u["accuracy"]         = npc.Accuracy;
		u["strength"]         = npc.Strength;
		u["defence"]          = npc.Defence;
		u["ether"]            = npc.Ether;
		u["max_ether"]        = npc.MaxEther;
		u["melee_bonus"]      = npc.MeleeBonus;
		u["ranged_bonus"]     = npc.RangedBonus;
		u["ether_bonus"]      = npc.EtherBonus;
		u["melee_defense"]    = npc.MeleeDefense;
		u["ranged_defense"]   = npc.RangedDefense;
		u["ether_defense"]    = npc.EtherDefense;
		u["attack_speed"]     = npc.AttackSpeedTicks;

		_ = _db.Npcs.SaveAdminEditAsync(npcId, u);
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
	if (_state.PendingEngage.Count == 0) return;

	var toRemove = new List<long>();
	var toEngage = new List<(long playerId, long npcId)>();

	foreach (var kv in _state.PendingEngage)
	{
		long playerId = kv.Key;
		long npcId = kv.Value;

		if (!_npcs.TryGetValue(npcId, out var npc) || npc.IsDead)
		{
			toRemove.Add(playerId);
			continue;
		}
		if (!_state.Positions.ContainsKey(playerId))
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
		// Keep pursuing. If no progress is possible, cancel and notify.
		if (!MovePlayerAdjacentToNpc(playerId, npc))
		{
			toRemove.Add(playerId);
			ChatToPlayer(playerId, "I can't seem to reach that.");
		}
	}

	foreach (var p in toRemove) _state.PendingEngage.Remove(p);
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

		foreach (var kv in _state.Sessions)
		{
			long playerId = kv.Value.PlayerId;
			if (!_state.Positions.ContainsKey(playerId)) continue;
			if (_state.CombatByPlayer.ContainsKey(playerId)) continue;
			if (!_state.PlayerCombat.TryGetValue(playerId, out var pdata)) continue;
			if (pdata.FinalLevel < 1 || pdata.FinalLevel > levelCap) continue;
			if (pdata.CurrentHp <= 0) continue;

			var ppos = _state.Positions[playerId];
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

// ---------- Inventory ----------

internal string? GetSlotItemSlug(long playerId, int slot)
{
	if (!_state.Inventories.TryGetValue(playerId, out var inv)) return null;
	if (slot < 0 || slot >= 28) return null;
	long packed = inv[slot];
	if (packed == 0) return null;
	int itemId = ItemStack.ItemId(packed);
	return _items.TryGetById(itemId, out var meta) ? meta.Slug : null;
}

internal int GetSlotQuantity(long playerId, int slot)
{
	if (!_state.Inventories.TryGetValue(playerId, out var inv)) return 0;
	if (slot < 0 || slot >= 28) return 0;
	long packed = inv[slot];
	return packed == 0 ? 0 : ItemStack.Quantity(packed);
}

internal bool ConsumeInventorySlot(long playerId, int slot, int qty)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return false;
	if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
	if (slot < 0 || slot >= 28) return false;
	long packed = inv[slot];
	if (packed == 0) return false;
	int currentQty = ItemStack.Quantity(packed);
	if (currentQty < qty) return false;
	int itemId = ItemStack.ItemId(packed);
	int newQty = currentQty - qty;
	inv[slot] = newQty > 0 ? ItemStack.Encode(itemId, newQty) : 0;
	_db.Inventory.SaveSlotSync(playerId, slot, inv[slot]);
	BroadcastInventory(playerId);
	return true;
}

// ---------- Item giving ----------

/// <summary>
/// Sync wrapper for GFS scripts: fires the async give and returns optimistically.
/// Pre-validates inputs so callers get a real `false` for bad arguments, but
/// inventory-full failures are silent (use GiveItemAsync to observe those).
/// </summary>
internal bool GiveItem(long playerId, string itemSlug, int qty)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return false;
	if (qty < 1) return false;
	if (!_items.ContainsSlug(itemSlug)) return false;
	if (!_state.Inventories.ContainsKey(playerId)) return false;
	_ = GiveItemAsync(playerId, itemSlug, qty);
	return true;
}

/// <summary>
/// Awaits the DB save before returning, so a subsequent read from the DB sees
/// the item. Used by loot giving during registration, where the inventory is
/// loaded fresh at login and a fire-and-forget save could race.
/// </summary>
internal async Task<bool> GiveItemAsync(long playerId, string itemSlug, int qty)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return false;
	if (qty < 1) return false;
	if (!_items.TryGetIdBySlug(itemSlug, out long itemId))
	{
		GD.PrintErr($"[GiveItem] Unknown slug '{itemSlug}' (player {playerId})");
		return false;
	}
	if (!_items.TryGetById(itemId, out var meta))
	{
		GD.PrintErr($"[GiveItem] Item id {itemId} ('{itemSlug}') missing from _itemsById");
		return false;
	}

	// `out var inv` here is the single declaration of `inv` for the whole
	// function. Every use below relies on it.
	if (!_state.Inventories.TryGetValue(playerId, out var inv))
	{
		inv = await _db.Inventory.GetSlotsAsync(playerId);
		if (inv == null) inv = new long[28];
		_state.Inventories[playerId] = inv;
	}

	if (meta.Stackable)
	{
		// Stackable: merge into an existing slot first.
		for (int i = 0; i < 28; i++)
		{
			long packed = inv[i];
			if (packed == 0) continue;
			if (ItemStack.ItemId(packed) != itemId) continue;
			int newQty = ItemStack.Quantity(packed) + qty;
			inv[i] = ItemStack.Encode((int)itemId, newQty);
			_db.Inventory.SaveSlotSync(playerId, i, inv[i]);
			BroadcastInventory(playerId);
			return true;
		}
		// Otherwise the first empty slot.
		for (int i = 0; i < 28; i++)
		{
			if (inv[i] != 0) continue;
			inv[i] = ItemStack.Encode((int)itemId, qty);
			_db.Inventory.SaveSlotSync(playerId, i, inv[i]);
			BroadcastInventory(playerId);
			return true;
		}
		return false;
	}
	else
	{
		// Non-stackable: every unit gets its own slot, quantity 1.
		int placed = 0;
		for (int i = 0; i < 28 && placed < qty; i++)
		{
			if (inv[i] != 0) continue;
			inv[i] = ItemStack.Encode((int)itemId, 1);
			_db.Inventory.SaveSlotSync(playerId, i, inv[i]);
			placed++;
		}
		if (placed > 0) BroadcastInventory(playerId);
		return placed == qty;
	}
}

/// <summary>
/// Give every item in the loot table (forceAll=true), or roll one random entry
/// and give its full quantity (forceAll=false).
/// </summary>
internal async Task GiveLootTableAsync(long playerId, string lootSlug, bool forceAll = false)
{
	if (!_lootTablesBySlug.TryGetValue(lootSlug, out var table))
	{
		GD.PrintErr($"[GiveLootTable] Loot table '{lootSlug}' not loaded (was the server restarted after DB insert?)");
		return;
	}
	if (table.Count == 0)
	{
		GD.PrintErr($"[GiveLootTable] Loot table '{lootSlug}' is empty");
		return;
	}

	if (forceAll)
	{
		foreach (var kv in table)
		{
			int qty = RollQuantity(kv.Value);
			if (qty <= 0) continue;
			bool ok = await GiveItemAsync(playerId, kv.Key, qty);
			if (!ok) GD.PrintErr($"[GiveLootTable] Failed to give '{kv.Key}' x{qty} from '{lootSlug}'");
		}
		Diag.Log("diag_login", $"Gave full loot table '{lootSlug}' to player {playerId}");
	}
	else
	{
		var keys = new List<string>(table.Keys);
		string pick = keys[_combatRng.Next(keys.Count)];
		int qty = RollQuantity(table[pick]);
		if (qty > 0) await GiveItemAsync(playerId, pick, qty);
		Diag.Log("diag_login", $"Rolled '{pick}' x{qty} from loot table '{lootSlug}' for player {playerId}");
	}
}

internal void HealPlayer(long playerId, int amount)
{
	if (!_state.PlayerCombat.TryGetValue(playerId, out var pd)) return;
	int newHp = Math.Min(pd.MaxHp, pd.CurrentHp + amount);
	if (newHp == pd.CurrentHp) return;
	_state.PlayerCombat[playerId] = pd with { CurrentHp = newHp };
	_ = _db.Stats.SetPlayerHpAsync(playerId, newHp);
	BroadcastPlayerHp(playerId, newHp, pd.MaxHp);
}

/// <summary>
/// Atomic consume: tick gate → decrement slot → heal. Returns true only if
/// something was actually consumed. Caller (GFS script) usually prints a
/// message on success and stays silent on failure.
/// </summary>
internal bool TryConsumeItem(long playerId, int slot, int healAmount)
{
	if (_lastConsumeTick.TryGetValue(playerId, out long lastTick)
		&& _state.CurrentTick - lastTick < ConsumeCooldownTicks)
		return false;

	if (!ConsumeInventorySlot(playerId, slot, 1)) return false;

	_lastConsumeTick[playerId] = _state.CurrentTick;
	if (healAmount > 0) HealPlayer(playerId, healAmount);
	return true;
}

	internal void BroadcastInventory(long playerId) => _outbound.BroadcastInventory(playerId);

	private void SendInventory(long peerId) => _outbound.SendInventory(peerId);

	internal void SendSkills(long peerId) => _outbound.SendSkills(peerId);

private void HandleInventorySwap(long peerId, JsonElement root)
{
	if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
	if (_trade.IsTradeInventoryLocked(session.PlayerId)) return;
	if (!root.TryGetProperty("slot_a", out var aEl) || !aEl.TryGetInt32(out int slotA)) return;
	if (!root.TryGetProperty("slot_b", out var bEl) || !bEl.TryGetInt32(out int slotB)) return;
	if (slotA == slotB) return;
	if (slotA < 0 || slotA >= 28 || slotB < 0 || slotB >= 28) return;
	if (!_state.Inventories.TryGetValue(session.PlayerId, out var inv)) return;

	long tmp = inv[slotA];
	inv[slotA] = inv[slotB];
	inv[slotB] = tmp;

	_db.Inventory.SaveSlotSync(session.PlayerId, slotA, inv[slotA]);
	_db.Inventory.SaveSlotSync(session.PlayerId, slotB, inv[slotB]);
	BroadcastInventory(session.PlayerId);
	Diag.Log("diag_item", $"Swap player {session.PlayerId}: slot {slotA} <-> {slotB}");
}

// ---------- Ground items ----------

private object GroundItemToPacket(GroundItem item)
{
	string display = "";
	bool isCert = false;
	if (_items.TryGetIdBySlug(item.ItemSlug, out long iid) && _items.TryGetById(iid, out var meta))
	{
		display = meta.Display;
		isCert = !string.IsNullOrEmpty(meta.CertOfSlug);
	}
	return new
	{
		id = item.Id,
		slug = item.ItemSlug,
		display = display,
		is_cert = isCert,
		quantity = item.Quantity,
		x = item.TileX,
		y = item.TileZ,
		z = item.TileY,
	};
}

internal bool DropGroundItem(long playerId, string itemSlug, int quantity)
{
	if (quantity < 1) return false;
	if (!_items.ContainsSlug(itemSlug)) return false;
	if (!_state.Positions.TryGetValue(playerId, out var pos)) return false;

	int tx = (int)MathF.Round(pos.X);
	int ty = (int)MathF.Round(pos.Z);
	int tz = (int)MathF.Round(pos.Y);

	var item = new GroundItem
	{
		Id = _nextGroundItemId++,
		ItemSlug = itemSlug,
		Quantity = quantity,
		TileX = tx,
		TileY = ty,
		TileZ = tz,
		SpawnedAtMs = (long)Time.GetTicksMsec(),
	};
	_groundItems[item.Id] = item;
	BroadcastGroundItemToNearby(item);
	Diag.Log("diag_item", $"Dropped {itemSlug} x{quantity} (id {item.Id}) at ({tx},{ty})");
	return true;
}

internal void RequestPickup(long playerId, long groundItemId)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return;
	if (!_groundItems.TryGetValue(groundItemId, out var item)) return;
	if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

	int px = (int)MathF.Round(pos.X);
	int py = (int)MathF.Round(pos.Z);
	if (px == item.TileX && py == item.TileY)
	{
		ExecutePickup(playerId, item);
		return;
	}

	_state.PendingPickup[playerId] = groundItemId;
	MovePlayerToTile(playerId, item.TileX, item.TileY);
	Diag.Log("diag_item", $"Player {playerId} approaching ground item {groundItemId}");
}

private void ProcessPendingPickup()
{
	if (_state.PendingPickup.Count == 0) return;
	var toRemove = new List<long>();
	foreach (var kv in _state.PendingPickup)
	{
		long playerId = kv.Key;
		long itemId = kv.Value;
		if (!_groundItems.TryGetValue(itemId, out var item)) { toRemove.Add(playerId); continue; }
		if (!_state.Positions.TryGetValue(playerId, out var pos)) { toRemove.Add(playerId); continue; }
		int px = (int)MathF.Round(pos.X);
		int py = (int)MathF.Round(pos.Z);
		if (px == item.TileX && py == item.TileY)
		{
			ExecutePickup(playerId, item);
			toRemove.Add(playerId);
		}
	}
	foreach (var p in toRemove) _state.PendingPickup.Remove(p);
}

private void MovePlayerAdjacentToTile(long playerId, int tx, int ty)
{
	long peerId = -1;
	foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId < 0) return;
	if (_state.Intents.ContainsKey(peerId)) return;
	if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

	int px = (int)MathF.Round(pos.X);
	int py = (int)MathF.Round(pos.Z);

	if (px == tx && py == ty)
	{
		Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
							new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
		foreach (var d in dirs)
		{
			var cand = new Vector2I(px + d.X, py + d.Y);
			if (IsValidTile(cand.X, cand.Y))
			{
				_state.Intents[peerId] = new List<Vector2I> { cand };
				return;
			}
		}
		return;
	}

	var route = PathfindToNearest(playerId, tx, ty);
	if (route.Count == 0) return;
	var goal = new Vector2I(tx, ty);
	if (route[^1] == goal) route.RemoveAt(route.Count - 1);
	if (route.Count == 0) return;
	SetIntent(playerId, route);
}


private void ExecutePickup(long playerId, GroundItem item)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return;
	if (!_groundItems.Remove(item.Id)) return;
	foreach (var kv in _visibleGroundItems)
	{
		if (kv.Value.Remove(item.Id))
			Send(kv.Key, new { type = "ground_item_removed", id = item.Id });
	}
	Diag.Log("diag_item", $"Player {playerId} picked up {item.ItemSlug} x{item.Quantity}");
	_ = GiveItemAsync(playerId, item.ItemSlug, item.Quantity);
}

private void ProcessGroundItemDespawn()
{
	long now = (long)Time.GetTicksMsec();
	var toRemove = new List<long>();
	foreach (var kv in _groundItems)
	{
		if (kv.Value.IsStatic) continue;
		if (now - kv.Value.SpawnedAtMs >= GroundItemDespawnMs)
			toRemove.Add(kv.Key);
	}
	foreach (var id in toRemove)
	{
		_groundItems.Remove(id);
		foreach (var vis in _visibleGroundItems)
			if (vis.Value.Remove(id))
				Send(vis.Key, new { type = "ground_item_removed", id = id });
	}
}

private void BroadcastGroundItemToNearby(GroundItem item)
{
	foreach (long peerId in _state.Sessions.Keys)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var sess)) continue;
		if (!_state.Positions.TryGetValue(sess.PlayerId, out var pos)) continue;
		int pcx = ActiveNpc.FloorDiv((int)MathF.Round(pos.X), _world.ChunkSize);
		int pcy = ActiveNpc.FloorDiv((int)MathF.Round(pos.Z), _world.ChunkSize);
		int icx = ActiveNpc.FloorDiv(item.TileX, _world.ChunkSize);
		int icy = ActiveNpc.FloorDiv(item.TileY, _world.ChunkSize);
		if (Math.Abs(pcx - icx) > NpcViewChunkRadius) continue;
		if (Math.Abs(pcy - icy) > NpcViewChunkRadius) continue;

		Send(peerId, new { type = "ground_item_dropped", item = GroundItemToPacket(item) });
		if (!_visibleGroundItems.TryGetValue(peerId, out var set))
		{
			set = new HashSet<long>();
			_visibleGroundItems[peerId] = set;
		}
		set.Add(item.Id);
	}
}

private void RefreshGroundItemVisibility(long peerId, int tx, int ty)
{
	if (!_visibleGroundItems.TryGetValue(peerId, out var oldSet))
	{
		oldSet = new HashSet<long>();
		_visibleGroundItems[peerId] = oldSet;
	}
	var newSet = new HashSet<long>();
	int pcx = ActiveNpc.FloorDiv(tx, _world.ChunkSize);
	int pcy = ActiveNpc.FloorDiv(ty, _world.ChunkSize);
	foreach (var item in _groundItems.Values)
	{
		int icx = ActiveNpc.FloorDiv(item.TileX, _world.ChunkSize);
		int icy = ActiveNpc.FloorDiv(item.TileY, _world.ChunkSize);
		if (Math.Abs(pcx - icx) > NpcViewChunkRadius) continue;
		if (Math.Abs(pcy - icy) > NpcViewChunkRadius) continue;
		newSet.Add(item.Id);
	}
	foreach (var id in newSet)
	{
		if (oldSet.Contains(id)) continue;
		if (!_groundItems.TryGetValue(id, out var item)) continue;
		Send(peerId, new { type = "ground_item_dropped", item = GroundItemToPacket(item) });
	}
	foreach (var id in oldSet)
	{
		if (newSet.Contains(id)) continue;
		Send(peerId, new { type = "ground_item_removed", id = id });
	}
	_visibleGroundItems[peerId] = newSet;
}

private void HandlePickupGroundItem(long peerId, JsonElement root)
{
	if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
	if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long groundItemId)) return;
	RequestPickup(session.PlayerId, groundItemId);
}

internal bool TryDropFromSlot(long playerId, int slot, int qty)
{
	if (_trade.IsTradeInventoryLocked(playerId)) return false;
	if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
	if (slot < 0 || slot >= 28) return false;
	long packed = inv[slot];
	if (packed == 0) return false;
	int currentQty = ItemStack.Quantity(packed);
	if (currentQty < qty) return false;
	int itemId = ItemStack.ItemId(packed);
	if (!_items.TryGetById(itemId, out var meta)) return false;

	int newQty = currentQty - qty;
	inv[slot] = newQty > 0 ? ItemStack.Encode(itemId, newQty) : 0;
	_db.Inventory.SaveSlotSync(playerId, slot, inv[slot]);
	BroadcastInventory(playerId);
	return DropGroundItem(playerId, meta.Slug, qty);
}

private void MovePlayerToTile(long playerId, int tx, int ty)
{
	long peerId = -1;
	foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
	if (peerId < 0) return;
	if (_state.Intents.ContainsKey(peerId)) return;
	if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

	int px = (int)MathF.Round(pos.X);
	int py = (int)MathF.Round(pos.Z);
	if (px == tx && py == ty) return;

	var route = PathfindToNearest(playerId, tx, ty);
	if (route.Count == 0) return;
	// Keep the goal tile in the route — pickup requires standing on it.
	SetIntent(playerId, route);
}

private static string CollapseSpaces(string text)
{
	var sb = new System.Text.StringBuilder(text.Length);
	bool prevSpace = false;
	foreach (char c in text)
	{
		if (c == ' ')
		{
			if (prevSpace) continue;
			prevSpace = true;
		}
		else prevSpace = false;
		sb.Append(c);
	}
	return sb.ToString();
}

	/// <summary>
	/// Ensures the canonical dev account exists with admin flag set. If the
	/// account is missing, creates it with fresh defaults (HP 10, all other
	/// skills level 1, 0 credits, empty inventory, empty bank). If it exists
	/// but isn't admin, elevates it. Does not touch existing player data.
	/// </summary>
	private void EnsureDevAccountSync(string username, string password)
	{
		var existing = _db.Players.GetByUsernameAsync(username).Result;
		if (existing != null)
		{
			if (!existing.IsAdmin)
			{
				_db.Players.SetAdminSync(existing.Id, true);
				GD.Print($"[Server] Elevated dev account '{username}' to admin");
			}
			return;
		}

		string hash = PasswordHasher.Hash(password);
		var (created, playerId) = _db.Players.TryCreateAsync(username, hash, isAdmin: true).Result;
		if (!created)
		{
			GD.PrintErr($"[Server] Failed to create dev account '{username}'");
			return;
		}
		_db.Stats.EnsureStatsAsync(playerId).Wait();
		_db.Stats.EnsureInventoryAsync(playerId).Wait();
		GD.Print($"[Server] Created dev account '{username}' (id {playerId})");
	}
}
