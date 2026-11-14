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

	// TEMP-DEV: scaffolding to exercise NPC movement before real AI lands.
	// Delete this block (and DiagNpcWander export, DebugWanderTick, and the
	// call to DebugWanderTick in _Process) when AI packages are wired in.
	[ExportGroup("NPC")]
	[Export] public int NpcViewChunkRadius { get; set; } = 10;
	[Export] public bool DiagNpcWander { get; set; } = false;

	private readonly ItemLookup _items = new();
	private readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _lootTablesBySlug = new();

	private readonly Dictionary<string, (int Min, int Max)> _creditsLootBySlug = new();
	private readonly Random _combatRng = new();


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
	private CombatSystem _combat;
	private NpcSystem _npc;
	private ItemSystem _inv;
	private GroundItemSystem _ground;
	private ChatSystem _chat;

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
			GD.Print($"[Server] Ground items: in-memory only (0 at startup; player/npc drops cleared on restart)");

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
			_movement = new MovementSystem(_state, _world, _outbound, _pathfinder);
			_trade = new TradeSystem(this, _state, _items, _db.Inventory, _perTick, _outbound, _coordinator);
			_dialogue = new DialogueSystem(this, _state, _gfs, _gfsFns, _perTick, _outbound, _coordinator);
			_bank = new BankSystem(this, _state, _items, _db.Bank, _db.Inventory, _perTick, _outbound, _coordinator);
			_coordinator.Wire(_trade, _dialogue, _bank);
			_combat = new CombatSystem(this, _state, _world, _outbound, _coordinator, _movement, _db.Stats, _perTick, _combatRng);

			_npcRender = new NpcRenderManager(new[]
			{
				ProjectSettings.GlobalizePath("res://npc_render"),
				ProjectSettings.GlobalizePath("user://npc_render"),
			});
			_npcRender.LoadAll();

			_npc = new NpcSystem(_state, _world, _outbound, _db.Npcs, _gfs, _npcRender, _pathfinder, NpcViewChunkRadius, DiagNpcWander);
			_npc.LoadAll();
			_inv = new ItemSystem(_state, _outbound, _items, _db.Inventory, _db.Stats, _trade, _combatRng);
			_ground = new GroundItemSystem(_state, _world, _outbound, _items, _inv, _trade, _db.Inventory, _pathfinder, NpcViewChunkRadius);
			_chat = new ChatSystem(_state, _outbound, _world, MaxChatLength);
			
			_recipe = new RecipeSystem(this, _state, _items, _db.Recipes, _db.Inventory, _db.Stats, _gfs, _gfsFns, _world, _outbound);
			_recipe.LoadRecipes();



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
		if (_gfs == null || _perTick == null || _npc == null)
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

		_npc.PollRenderBundles();
		_npc.TickWander(delta);

		_gfs.Poll();
	}

	// ---------- Tick ----------

	private void RunTick()
	{
		_state.CurrentTick++;

		// 1. Movement processing (only if there are intents to process)
		_movement.Tick(_state.CurrentTick);

		// 2. NPC movement
		_npc.TickMovement(_state.CurrentTick);

		// 2b. NPC respawn sweep
		_npc.CheckNpcRespawns();

		_ground.ProcessGroundItemDespawn();
		_ground.ProcessPendingPickup();
		_trade.ProcessTradeQueues();
		_trade.ProcessTradeIntents();
		_bank.ProcessBankIntents();
		_recipe.ProcessRecipeIntents();

		// 2c. Combat
		_combat.ProcessHostility();
		_combat.ProcessPendingEngage();
		_npc.ProcessNpcLookAt();
		_dialogue.ProcessPendingTalk();
		_bank.ProcessPendingBank();
		_combat.ProcessCombat();

		// 2d. Healing
		_combat.ProcessNpcHealing();
		_combat.ProcessPlayerHealing();

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
				_npc.RefreshNpcVisibility(peerId, tx, ty);
				_ground.RefreshGroundItemVisibility(peerId, tx, ty);
			}
		}
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
			_perTick?.EndAllFor(session.PlayerId);       

			_combat.ClearPlayer(session.PlayerId);
			_state.PlayerCombat.Remove(session.PlayerId);
			_inv.ClearPlayer(session.PlayerId);
			_state.Inventories.Remove(session.PlayerId);
			_state.Credits.Remove(session.PlayerId);
			_state.SkillsByPlayer.Remove(session.PlayerId);

			_state.PeerByPlayerId.Remove(session.PlayerId);
			_state.PendingPickup.Remove(session.PlayerId);
			_trade.ClearPendingFor(session.PlayerId);
			_ground.ClearPeer(peerId);
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
				case "npc_editor_get":  _npc.HandleNpcEditorGet(peerId, root); break;
				case "npc_editor_save": _npc.HandleNpcEditorSave(peerId, root); break;
				case "dev_auto_login": await HandleDevAutoLoginAsync(peerId); break;
				case "player_walk": HandlePlayerWalkBuffered(peerId, root); break;
				case "inventory_swap": _inv.HandleInventorySwap(peerId, root); break;
				case "ping":         HandlePing(peerId, root); break;
				case "dev_shutdown": HandleDevShutdown(peerId); break;
				case "chat_send": _chat.HandleChatSend(peerId, root); break;
				case "tooltips_info": HandleTooltipsInfo(peerId); break;
				case "gfs_info":    HandleGfsInfo(peerId); break;
				case "run_action":  HandleRunAction(peerId, root); break;
				case "npc_render_info_request": _npc.HandleNpcRenderInfo(peerId); break;
				case "npc_render_fetch":        _npc.HandleNpcRenderFetch(peerId, root); break;
				case "pickup_ground_item": _ground.HandlePickupGroundItem(peerId, root); break;
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

	private void HandlePlayerWalkBuffered(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;

		_perTick?.EndByStem(session.PlayerId, "follow_player");
		_coordinator.OnPlayerWalked(session.PlayerId);
		_state.PendingEngage.Remove(session.PlayerId);
		_state.PendingPickup.Remove(session.PlayerId);

		if (_state.CombatByPlayer.TryGetValue(session.PlayerId, out var combatPair) && !combatPair.StateAnnounced)
			_combat.EndCombat(combatPair);

		if (!root.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.Array)
			return;

		var path = new List<Vector2I>();
		foreach (JsonElement t in pathEl.EnumerateArray())
		{
			if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 2) return;
			if (!t[0].TryGetInt32(out int x) || !t[1].TryGetInt32(out int y)) return;
			path.Add(new Vector2I(x, y));
		}

		if (!_movement.ValidateWalkPath(session.PlayerId, path)) return;

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
				await _inv.GiveLootTableAsync(playerId, "trade_testing", _lootTablesBySlug, forceAll: true);

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
	
internal PlayerPosition ComputeSpawnPosition(long playerId)
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
	_npc.RefreshNpcVisibility(peerId, tx, ty);
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

	internal string? GetSlotItemSlug(long playerId, int slot) => _inv.GetSlotItemSlug(playerId, slot);
	internal int GetSlotQuantity(long playerId, int slot) => _inv.GetSlotQuantity(playerId, slot);
	internal bool ConsumeInventorySlot(long playerId, int slot, int qty) => _inv.ConsumeInventorySlot(playerId, slot, qty);
	internal bool GiveItem(long playerId, string itemSlug, int qty) => _inv.GiveItem(playerId, itemSlug, qty);
	internal Task<bool> GiveItemAsync(long playerId, string itemSlug, int qty) => _inv.GiveItemAsync(playerId, itemSlug, qty);
	internal bool TryConsumeItem(long playerId, int slot, int healAmount) => _inv.TryConsumeItem(playerId, slot, healAmount);
	internal void HealPlayer(long playerId, int amount) => _inv.HealPlayer(playerId, amount);
	internal bool SpawnGroundItemAt(string itemSlug, int quantity, int tileX, int tileY, int tileZ) =>
		_ground.SpawnGroundItemAt(itemSlug, quantity, tileX, tileY, tileZ);
	internal bool DropGroundItem(long playerId, string itemSlug, int quantity) =>
		_ground.DropGroundItem(playerId, itemSlug, quantity);
	internal void RequestPickup(long playerId, long groundItemId) =>
		_ground.RequestPickup(playerId, groundItemId);
	internal bool TryDropFromSlot(long playerId, int slot, int qty) =>
		_ground.TryDropFromSlot(playerId, slot, qty);

	internal void RequestEngage(long playerId, long npcId) =>
		_combat.RequestEngage(playerId, npcId);

	internal void EngageNpc(long playerId, long npcId, bool initiatedByPlayer = true) =>
		_combat.EngageNpc(playerId, npcId, initiatedByPlayer);

	internal void EndCombat(CombatPair pair) =>
		_combat.EndCombat(pair);

	internal void HandlePlayerDeath(long playerId) =>
		_combat.HandlePlayerDeath(playerId);

	internal bool ArePositionsAdjacent(long playerId, ActiveNpc npc) =>
		_movement.ArePositionsAdjacent(playerId, npc);

	internal bool MovePlayerAdjacentToNpc(long playerId, ActiveNpc npc) =>
    _movement.MovePlayerAdjacentToNpc(playerId, npc);

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

	internal void HandleChatSend(long peerId, JsonElement root) =>
		_chat.HandleChatSend(peerId, root);

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
	
	internal void MoveNpcTo(long npcId, int tileX, int tileY) =>
		_npc.MoveNpcTo(npcId, tileX, tileY);
	
	internal long GetPeerIdForPlayer(long playerId)
	{
		if (_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)) return peerId;
		return -1;
	}

	internal void OpenNpcEditorForPlayer(long playerId, long npcId) =>
		_npc.OpenNpcEditorForPlayer(playerId, npcId);

	internal void SnapAndFaceForDialogue(long playerId, ActiveNpc npc) =>
		_npc.SnapAndFaceForDialogue(playerId, npc);

	// ====================================================================
	// TEMP-DEV: debug NPC wander. Delete when real AI packages are wired in.
	// Set DiagNpcWander=true in the scene inspector to enable.
	// ====================================================================
	

	// ---------- NPC death / respawn ----------
	internal void KillNpc(long npcId, long killerPlayerId = 0)
{
	if (!_npc.TryGetNpcById(npcId, out var npc)) return;
	if (npc.IsDead) return;

	// Resolve the killer if not explicitly passed. Combat pairs set it to the
	// attacking player; admin-kill scripts pass it directly.
	if (killerPlayerId == 0 && _state.CombatByNpc.TryGetValue(npcId, out var killerPair))
		killerPlayerId = killerPair.PlayerId;

	RollNpcDeathLoot(npc, killerPlayerId);

	npc.IsDead = true;
	npc.DiedAtMs = (long)Time.GetTicksMsec();
	npc.Path.Clear();
	npc.Hitpoints = 0;
	_ = _db.Npcs.MarkDeadAsync(npc.Id);

	if (_state.CombatByNpc.TryGetValue(npcId, out var owner)) _combat.EndCombat(owner);
	Diag.Log("diag_npc", $"NPC {npc.Name} (id {npc.Id}) killed; respawns in {npc.SpawnTimerSeconds}s");
	_npc.NotifyNpcVisibilityChanged();
}

	


// ---------- Loot + credits ----------

private void RollNpcDeathLoot(ActiveNpc npc, long killerPlayerId)
{
	int deathX = (int)MathF.Round(npc.PosX);
	int deathY = (int)MathF.Round(npc.PosZ);
	var tileInfo = _world.GetTileInfo(deathX, deathY);
	int deathZ = tileInfo.Z;

	if (!string.IsNullOrEmpty(npc.LootTableName)
		&& _lootTablesBySlug.TryGetValue(npc.LootTableName, out var bag)
		&& bag.Count > 0)
	{
		var keys = new List<string>(bag.Keys);
		string itemSlug = keys[_combatRng.Next(keys.Count)];
		int qty = _inv.RollQuantity(bag[itemSlug]);
		if (qty > 0)
		{
			_ground.SpawnGroundItemAt(itemSlug, qty, deathX, deathY, deathZ);
			Diag.Log("diag_item", $"NPC {npc.Name} dropped {itemSlug} x{qty} at ({deathX},{deathY})");
		}
	}

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


	internal ActiveNpc? GetNpcById(long npcId) => _npc.GetNpcById(npcId);
	internal IEnumerable<ActiveNpc> AllNpcs() => _npc.All();

	internal bool TryGetNpcById(long npcId, out ActiveNpc npc) => _npc.TryGetNpcById(npcId, out npc);

	internal bool IsNpcInCombat(long npcId) => _npc.IsNpcInCombat(npcId);


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


	internal void BroadcastInventory(long playerId) => _outbound.BroadcastInventory(playerId);

	private void SendInventory(long peerId) => _outbound.SendInventory(peerId);

	internal void SendSkills(long peerId) => _outbound.SendSkills(peerId);



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
