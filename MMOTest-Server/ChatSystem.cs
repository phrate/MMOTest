using System;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns player chat send: message sanitization, length enforcement,
/// proximity broadcast. Message routing from effects (dialogue, recipes) is
/// OutboundChannel's job — ChatSystem only handles what the player types.
/// </summary>
internal sealed class ChatSystem
{
    private readonly ServerState _state;
    private readonly OutboundChannel _outbound;
    private readonly WorldService _world;
    private readonly int _maxChatLength;

    public ChatSystem(ServerState state, OutboundChannel outbound, WorldService world,
                      int maxChatLength)
    {
        _state = state;
        _outbound = outbound;
        _world = world;
        _maxChatLength = maxChatLength;
    }

    internal void HandleChatSend(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!_state.Positions.TryGetValue(session.PlayerId, out var pos)) return;
        if (!root.TryGetProperty("message", out JsonElement msgEl)) return;

        string text = (msgEl.GetString() ?? "").Trim();
        if (text.Length == 0) return;
        text = CollapseSpaces(text);
        if (text.Length > _maxChatLength) text = text.Substring(0, _maxChatLength);

        int tileX = (int)MathF.Round(pos.X);
        int tileY = (int)MathF.Round(pos.Z);
        int cx = (int)MathF.Floor((float)tileX / _world.ChunkSize);
        int cy = (int)MathF.Floor((float)tileY / _world.ChunkSize);

        _outbound.Broadcast(new
        {
            type = "chat_message",
            username = session.Username,
            message = text,
            from_chunk = new[] { cx, cy }
        });

        GD.Print($"[Chat] {session.Username}: {text}");
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
}