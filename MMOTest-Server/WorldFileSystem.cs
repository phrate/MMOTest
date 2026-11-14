using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns world-file hotfix detection and the world_info / world_fetch packet
/// handlers. Polls WorldService.EnsureLoaded() for mtime changes and
/// broadcasts world_updated to clients when the version bumps.
/// </summary>
internal sealed class WorldFileSystem
{
    private const double CheckIntervalSeconds = 1.0;

    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;

    private double _checkAccumulator;
    private int _lastBroadcastVersion;

    public WorldFileSystem(ServerState state, WorldService world, OutboundChannel outbound)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
        _lastBroadcastVersion = world.IsLoaded ? world.Version : -1;
    }

    public void Tick(double delta)
    {
        _checkAccumulator += delta;
        if (_checkAccumulator < CheckIntervalSeconds) return;
        _checkAccumulator = 0;
        CheckForUpdates();
    }

    private void CheckForUpdates()
    {
        if (!_world.EnsureLoaded()) return;
        if (_world.Version == _lastBroadcastVersion) return;

        _lastBroadcastVersion = _world.Version;
        Diag.Log("diag_world",
            $"World hotfix: v{_world.Version}, notifying {_state.Sessions.Count} client(s)");

        _outbound.Broadcast(new
        {
            type = "world_updated",
            world_version = _world.Version,
            format_version = _world.FormatVersion
        });
    }

    internal void HandleWorldInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "world_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        if (!_world.EnsureLoaded())
        {
            _outbound.SendError(peerId, "world_info_result", ErrorKeys.WorldUnavailable);
            return;
        }
        _outbound.Send(peerId, new
        {
            type = "world_info_result",
            ok = true,
            world_version = _world.Version,
            format_version = _world.FormatVersion
        });
    }

    internal void HandleWorldFetch(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        if (!_world.EnsureLoaded())
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldUnavailable);
            return;
        }
        var root = _world.GetRootElement();
        if (root == null)
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldUnavailable);
            return;
        }
        _outbound.Send(peerId, new
        {
            type = "world_data",
            ok = true,
            world_version = _world.Version,
            format_version = _world.FormatVersion,
            data = root.Value
        });
    }
}