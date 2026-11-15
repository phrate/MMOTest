using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MMOTest.Server;

public partial class Main : Node
{
	private const int Port = 9999;
	private const int MaxClients = 64;
	private const int MaxChatLength = 60;
	private const double TickIntervalSeconds = 0.3;
	private const float WalkSpeed = 2.0f;
	private WorldService _world;

	private Database _db;
	private SceneMultiplayer _mp;
	private ENetMultiplayerPeer _enet;
	internal readonly ServerState _state = new();
	private readonly HashSet<long> _connectedPeers = new();

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
	private AuthSystem _auth;
	private WorldFileSystem _worldFiles;
	private GfsIntegration _gfsIntegration;
	private WalkHandler _walk;

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
			AuthSystem.EnsureDevAccountSync(_db, "phrate", "12345678");

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

			GD.Print($"[Server] SQLite {_db.GetSqliteVersion()} ready at {dbPath}");

			int respawned = _db.Npcs.RespawnAllDeadSync();
			if (respawned > 0)
				GD.Print($"[Server] Auto-respawned {respawned} dead NPC(s) on startup");

			_world = new WorldService(WorldFileSystem.WorldFileCandidates, GD.PrintErr);
			if (_world.EnsureLoaded())
			{
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
			
			if (File.Exists(GfsIntegration.TooltipsFileCandidates[0])
				|| File.Exists(GfsIntegration.TooltipsFileCandidates[1]))
				GD.Print("[Server] Tooltips: available");
			else
				GD.PrintErr("[Server] Tooltips: not found (right-click menus will be empty)");


			_gfs = new Gfs.GfsManager(GfsIntegration.GfsScriptDirs, GfsIntegration.TooltipsFileCandidates);
			_gfs.InitialLoad();
			_gfsFns = new Gfs.FunctionRegistry();
			_perTick = new Gfs.PerTickManager(_gfsFns);
			_outbound = new OutboundChannel(_state, _items, SendBytes);
			_coordinator = new Coordinator();
			_movement = new MovementSystem(_state, _world, _outbound, _pathfinder);
			_trade    = new TradeSystem(_state, _items, _db.Inventory, _perTick, _outbound, _coordinator);

			_npcRender = new NpcRenderManager(new[]
			{
				ProjectSettings.GlobalizePath("res://npc_render"),
				ProjectSettings.GlobalizePath("user://npc_render"),
			});
			_npcRender.LoadAll();

			_npc = new NpcSystem(_state, _world, _outbound, _db.Npcs, _gfs, _npcRender, _pathfinder, NpcViewChunkRadius, DiagNpcWander,
    _lootTablesBySlug, _creditsLootBySlug);
			_npc.LoadAll();
			_inv = new ItemSystem(_state, _outbound, _items, _db.Inventory, _db.Stats, _trade, _combatRng, _db.Players);
			_ground = new GroundItemSystem(_state, _world, _outbound, _items, _inv, _trade, _db.Inventory, _pathfinder, NpcViewChunkRadius);

			_combat = new CombatSystem(_state, _world, _outbound, _coordinator, _movement, _pathfinder,
	_db.Stats, _perTick, _npc, _inv, _ground, _lootTablesBySlug, _creditsLootBySlug, _combatRng);
			_walk = new WalkHandler(_state, _movement, _combat, _coordinator, _perTick);
			_dialogue = new DialogueSystem(_state, _gfs, _gfsFns, _perTick, _outbound, _coordinator, _npc, _movement, _combat);
			_bank     = new BankSystem(_state, _items, _db.Bank, _db.Inventory, _perTick, _outbound, _coordinator, _npc, _movement, _combat, _dialogue);

			_coordinator.Wire(_trade, _dialogue, _bank);

			_chat = new ChatSystem(_state, _outbound, _world, MaxChatLength);
			_auth = new AuthSystem(_state, _outbound, _world, _db, _npc, _bank, _inv,
				_connectedPeers, _lootTablesBySlug, _creditsLootBySlug,
				peerId =>
				{
					var addr = _enet?.GetPeer((int)peerId)?.GetRemoteAddress();
					return addr == "127.0.0.1" || addr == "::1";
				},
				_combatRng);

			_recipe = new RecipeSystem(_state, _items, _db.Recipes, _db.Inventory, _db.Stats, _gfs, _gfsFns, _world, _outbound);
			_recipe.LoadRecipes();

			_worldFiles = new WorldFileSystem(_state, _world, _outbound);
			_gfsIntegration = new GfsIntegration(_state, _outbound, _world, _gfs, _gfsFns, _perTick, _coordinator);

			Gfs.Utils.TilesUtils.Register(_gfsFns, _state, _world);
			Gfs.Utils.PlayerUtils.Register(_gfsFns, _state, _world, _pathfinder, _perTick);
			Gfs.Utils.CombatUtils.Register(_gfsFns, _combat, _trade, _npc);
			Gfs.Utils.ItemUtils.Register(_gfsFns, _inv, _ground, _state);
			Gfs.Utils.DialogueUtils.Register(_gfsFns, _dialogue);
			Gfs.Utils.BankUtils.Register(_gfsFns, _bank);

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
		if (_gfs == null || _perTick == null || _npc == null || _worldFiles == null)
		{
			return;
		}

		_tickAccumulator += delta;
		while (_tickAccumulator >= TickIntervalSeconds)
		{
			_tickAccumulator -= TickIntervalSeconds;
			RunTick();
		}

		_worldFiles.Tick(delta);

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

				_outbound.RouteChatEffects(pid, username, pcx, pcy, effects, selfPeer);
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
		_auth.ClearAuthInProgress(peerId);
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

			_outbound.Broadcast(
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
				case "hello":       await _auth.HandleHelloAsync(peerId); break;
				case "register":
				{
					bool isDev = OS.IsDebugBuild()
						&& root.TryGetProperty("is_dev", out var devEl)
						&& devEl.ValueKind == JsonValueKind.True;
					await _auth.HandleRegisterAsync(peerId, GetString(root, "username"), GetString(root, "password"), isDev);
					break;
				}
				case "login":       await _auth.HandleLoginAsync(peerId, GetString(root, "username"), GetString(root, "password"), GetString(root, "reason")); break;
				case "world_info":  _worldFiles.HandleWorldInfo(peerId); break;
				case "world_fetch": _worldFiles.HandleWorldFetch(peerId); break;
				case "dev_npc_editor_options": _npc.HandleDevNpcEditorOptions(peerId); break;
				case "dev_npc_type_list":    _npc.HandleDevNpcTypeList(peerId, root); break;
				case "dev_npc_type_get":     _npc.HandleDevNpcTypeGet(peerId, root); break;
				case "dev_npc_type_create":  _npc.HandleDevNpcTypeCreate(peerId, root); break;
				case "dev_npc_type_update":  _npc.HandleDevNpcTypeUpdate(peerId, root); break;
				case "dev_npc_type_delete":  _npc.HandleDevNpcTypeDelete(peerId, root); break;
				case "dev_auto_login": await _auth.HandleDevAutoLoginAsync(peerId); break;
				case "player_walk": _walk.HandlePlayerWalk(peerId, root); break;
				case "inventory_swap": _inv.HandleInventorySwap(peerId, root); break;
				case "ping":         HandlePing(peerId, root); break;
				case "dev_shutdown": HandleDevShutdown(peerId); break;
				case "chat_send": _chat.HandleChatSend(peerId, root); break;
				case "tooltips_info": _gfsIntegration.HandleTooltipsInfo(peerId); break;
				case "gfs_info":    _gfsIntegration.HandleGfsInfo(peerId); break;
				case "run_action":  _gfsIntegration.HandleRunAction(peerId, root); break;
				case "npc_render_info_request": _npc.HandleNpcRenderInfo(peerId); break;
				case "npc_render_fetch":        _npc.HandleNpcRenderFetch(peerId, root); break;
				case "npc_render_save_hitbox": _npc.HandleNpcRenderSaveHitbox(peerId, root); break;
				case "npc_render_save_render": _npc.HandleNpcRenderSaveRender(peerId, root); break;
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

	private void HandlePing(long peerId, JsonElement root)
	{
		if (!_state.Sessions.ContainsKey(peerId)) return;
		if (!root.TryGetProperty("t", out JsonElement tEl) || !tEl.TryGetInt64(out long clientT)) return;
		_outbound.Send(peerId, new { type = "pong", t = clientT });
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

		_outbound.Broadcast(new
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

	// ---------- Outbound ----------


	private void SendBytes(long peerId, byte[] bytes, int channel, MultiplayerPeer.TransferModeEnum mode)
	{
		if (!_connectedPeers.Contains(peerId)) return;
		_mp.SendBytes(bytes, (int)peerId, mode, channel);
	}

	internal bool TryGetPosition(long playerId, out PlayerPosition pos) =>
		_state.Positions.TryGetValue(playerId, out pos);
}
