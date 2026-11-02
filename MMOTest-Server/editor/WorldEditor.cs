using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MMOTest.Server;

/// <summary>
/// Standalone tile editor. Reads/writes user://world.json (falls back to
/// res://data/world.json on first load). Bumps world_version on save. The server
/// polls the file and notifies connected clients, who reconnect and re-sync.
///
/// Run with:  godot --path . res://editor/world_editor.tscn
/// </summary>
public partial class WorldEditor : Node2D
{
    private const string UserPath = "user://world.json";
    private const string ResPath = "res://data/world.json";
    private const int FormatVersion = 2;
    private const int ChunkSize = 8;
    private const int DefaultZ = 100;
    private const int ZMin = 0;
    private const int ZMax = 200;
    private const float TilePx = 1.0f;   // editor world-units per tile (matches client 1:1)

    private record TileData(int Z, bool Walkable);

    private int _worldVersion = 1;
    private readonly Dictionary<Vector2I, TileData> _tiles = new();
    private bool _dirty = false;

    private Camera2D _camera;
    private Label _statusLabel;
    private Label _cursorLabel;
    private SpinBox _zInput;
    private CheckBox _walkableCheck;

    private bool _panning;
    private Vector2 _lastPanPos;
    private bool _painting;
    private int _paintButton = 0;   // 0 = paint, 1 = erase


    public override void _Ready()
    {
        BuildUi();
        BuildCamera();
        LoadWorld();
        QueueRedraw();
    }


    // ---------- UI construction ----------

    private void BuildUi()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        var panel = new PanelContainer();
        panel.Position = new Vector2(8, 8);
        layer.AddChild(panel);

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _statusLabel = new Label { Text = "Loading..." };
        vbox.AddChild(_statusLabel);

        _cursorLabel = new Label { Text = "Tile: -, -" };
        vbox.AddChild(_cursorLabel);

        var zRow = new HBoxContainer();
        zRow.AddChild(new Label { Text = "Z:" });
        _zInput = new SpinBox { MinValue = ZMin, MaxValue = ZMax, Value = DefaultZ, Step = 1 };
        _zInput.CustomMinimumSize = new Vector2(80, 0);
        zRow.AddChild(_zInput);
        vbox.AddChild(zRow);

        _walkableCheck = new CheckBox { Text = "Walkable", ButtonPressed = true };
        vbox.AddChild(_walkableCheck);

        var saveBtn = new Button { Text = "Save & Bump Version" };
        saveBtn.Pressed += OnSavePressed;
        vbox.AddChild(saveBtn);

        var reloadBtn = new Button { Text = "Reload from Disk" };
        reloadBtn.Pressed += () => { LoadWorld(); QueueRedraw(); };
        vbox.AddChild(reloadBtn);

        var hint = new Label
        {
            Text = "LMB paint · RMB erase · MMB pan · Wheel zoom",
            Modulate = new Color(1, 1, 1, 0.7f)
        };
        vbox.AddChild(hint);
    }

    private void BuildCamera()
    {
        _camera = new Camera2D
        {
            Zoom = new Vector2(20, 20),
            Position = Vector2.Zero
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }


    // ---------- Input ----------

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Middle)
            {
                _panning = mb.Pressed;
                _lastPanPos = mb.Position;
                return;
            }
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                _camera.Zoom = new Vector2(
                    Mathf.Clamp(_camera.Zoom.X * 1.15f, 2f, 64f),
                    Mathf.Clamp(_camera.Zoom.Y * 1.15f, 2f, 64f));
                return;
            }
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                _camera.Zoom = new Vector2(
                    Mathf.Clamp(_camera.Zoom.X / 1.15f, 2f, 64f),
                    Mathf.Clamp(_camera.Zoom.Y / 1.15f, 2f, 64f));
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _painting = mb.Pressed;
                _paintButton = 0;
                if (mb.Pressed) PaintAtCursor();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Right)
            {
                _painting = mb.Pressed;
                _paintButton = 1;
                if (mb.Pressed) PaintAtCursor();
                return;
            }
        }
        else if (ev is InputEventMouseMotion mm)
        {
            if (_panning)
            {
                var delta = (mm.Position - _lastPanPos) / _camera.Zoom;
                _camera.Position -= delta;
                _lastPanPos = mm.Position;
            }
            else if (_painting)
            {
                PaintAtCursor();
            }
            UpdateCursorLabel();
        }
    }


    // ---------- Painting ----------

    private void PaintAtCursor()
    {
        var tile = MouseToTile();
        if (_paintButton == 1)
        {
            if (_tiles.Remove(tile)) _dirty = true;
        }
        else
        {
            var data = new TileData((int)_zInput.Value, _walkableCheck.ButtonPressed);
            if (!_tiles.TryGetValue(tile, out var existing) || existing != data)
            {
                _tiles[tile] = data;
                _dirty = true;
            }
        }
        UpdateCursorLabel();
        QueueRedraw();
    }

    private Vector2I MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new Vector2I(
            Mathf.RoundToInt(world.X / TilePx),
            Mathf.RoundToInt(world.Y / TilePx));
    }

    private void UpdateCursorLabel()
    {
        var t = MouseToTile();
        var suffix = _tiles.TryGetValue(t, out var d)
            ? $" · z={d.Z} · {(d.Walkable ? "walk" : "BLOCK")}"
            : " · (empty)";
        _cursorLabel.Text = $"Tile: {t.X}, {t.Y}{suffix}";
    }


    // ---------- Drawing ----------

    public override void _Draw()
    {
        foreach (var kv in _tiles)
        {
            int x = kv.Key.X, y = kv.Key.Y;
            var rect = new Rect2(
                x * TilePx - TilePx / 2,
                y * TilePx - TilePx / 2,
                TilePx, TilePx);
            DrawRect(rect, ColorForTile(kv.Value), true);
        }

        var gridColor = new Color(1, 1, 1, 0.08f);
        int range = 64;
        for (int i = -range; i <= range; i += ChunkSize)
        {
            float p = i * TilePx;
            DrawLine(new Vector2(p, -range * TilePx), new Vector2(p, range * TilePx), gridColor, 0.02f);
            DrawLine(new Vector2(-range * TilePx, p), new Vector2(range * TilePx, p), gridColor, 0.02f);
        }
    }

    private static Color ColorForTile(TileData data)
    {
        if (!data.Walkable) return new Color(0.7f, 0.2f, 0.2f);
        return ColorForZ(data.Z);
    }

    private static Color ColorForZ(int z)
    {
        float t = Mathf.Clamp((z - DefaultZ) / 20f, -1f, 1f);
        return new Color(
            Mathf.Clamp(0.42f - t * 0.15f, 0f, 1f),
            Mathf.Clamp(0.60f + t * 0.20f, 0f, 1f),
            Mathf.Clamp(0.32f - t * 0.10f, 0f, 1f));
    }


    // ---------- Load / Save ----------

    private void LoadWorld()
    {
        _tiles.Clear();

        string path = "";
        if (FileAccess.FileExists(UserPath)) path = UserPath;
        else if (FileAccess.FileExists(ResPath)) path = ResPath;
        else
        {
            _worldVersion = 1;
            _statusLabel.Text = "New world (empty). Paint tiles and save.";
            _dirty = false;
            return;
        }

        var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            _statusLabel.Text = $"Failed to open {path}";
            return;
        }
        string text = f.GetAsText();
        f.Close();

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            _worldVersion = root.TryGetProperty("world_version", out var v) ? v.GetInt32() : 1;

            if (root.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Object)
            {
                foreach (var chunkProp in chunks.EnumerateObject())
                {
                    if (chunkProp.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var tile in chunkProp.Value.EnumerateArray())
                    {
                        if (tile.ValueKind != JsonValueKind.Object) continue;
                        int x = tile.GetProperty("x").GetInt32();
                        int y = tile.GetProperty("y").GetInt32();
                        int z = tile.GetProperty("z").GetInt32();
                        bool walkable = true;
                        if (tile.TryGetProperty("flags", out var flags) &&
                            flags.ValueKind == JsonValueKind.Object &&
                            flags.TryGetProperty("walkable", out var w))
                            walkable = w.GetBoolean();
                        _tiles[new Vector2I(x, y)] = new TileData(z, walkable);
                    }
                }
            }

            _statusLabel.Text = $"Loaded v{_worldVersion} · {_tiles.Count} tiles · {path}";
            _dirty = false;
            GD.Print($"[Editor] Loaded world v{_worldVersion} with {_tiles.Count} tiles from {path}");
        }
        catch (Exception e)
        {
            _statusLabel.Text = $"Parse error: {e.Message}";
            GD.PrintErr($"[Editor] Parse error: {e.Message}");
        }
    }

    private void OnSavePressed()
    {
        _worldVersion += 1;

        // Group into chunks. Chunk key must match the client's world_to_chunk:
        // floor(x / ChunkSize), floor(y / ChunkSize). Same formula as server.
        var chunks = new SortedDictionary<string, List<Dictionary<string, object>>>();
        foreach (var kv in _tiles)
        {
            int x = kv.Key.X, y = kv.Key.Y;
            int cx = Mathf.FloorToInt((float)x / ChunkSize);
            int cy = Mathf.FloorToInt((float)y / ChunkSize);
            string key = $"{cx},{cy}";
            if (!chunks.ContainsKey(key)) chunks[key] = new List<Dictionary<string, object>>();
            chunks[key].Add(new Dictionary<string, object>
            {
                ["x"] = x,
                ["y"] = y,
                ["z"] = kv.Value.Z,
                ["flags"] = new Dictionary<string, object> { ["walkable"] = kv.Value.Walkable }
            });
        }

        // Sort tiles within each chunk (y, then x) for readable diffs.
        foreach (var list in chunks.Values)
            list.Sort((a, b) =>
            {
                int ay = (int)a["y"], by = (int)b["y"];
                if (ay != by) return ay.CompareTo(by);
                return ((int)a["x"]).CompareTo((int)b["x"]);
            });

        var worldObj = new Dictionary<string, object>
        {
            ["format_version"] = FormatVersion,
            ["world_version"] = _worldVersion,
            ["chunk_size"] = ChunkSize,
            ["default_z"] = DefaultZ,
            ["z_min"] = ZMin,
            ["z_max"] = ZMax,
            ["chunks"] = chunks
        };

        var json = JsonSerializer.Serialize(worldObj, new JsonSerializerOptions { WriteIndented = true });

        var f = FileAccess.Open(UserPath, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            _statusLabel.Text = $"Failed to write {UserPath}";
            return;
        }
        f.StoreString(json);
        f.Close();

        _dirty = false;
        _statusLabel.Text = $"Saved v{_worldVersion} · {_tiles.Count} tiles · {UserPath}";
        GD.Print($"[Editor] Saved world v{_worldVersion} with {_tiles.Count} tiles");
    }
}