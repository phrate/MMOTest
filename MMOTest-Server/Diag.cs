using System.Collections.Generic;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Centralized diagnostic logging. Every call is tagged with a group; only
/// groups in the enabled set (configured by Main from scene exports) get
/// printed. Verbose mode enables all groups regardless.
///
/// Recognized groups:
///   diag_login, diag_database, diag_world, diag_gfs,
///   diag_network, diag_tick, diag_pathfinding, diag_chat
///
/// Use this only for diagnostics you want to silence in normal use. Real
/// errors that should always fire (unhandled exceptions, packet parse
/// failures) keep using GD.PrintErr directly.
/// </summary>
public static class Diag
{
    private static readonly HashSet<string> _enabled = new();
    private static bool _verbose;

    public static void Configure(bool verbose, IEnumerable<string> groups)
    {
        _verbose = verbose;
        _enabled.Clear();
        foreach (var g in groups) _enabled.Add(g);
    }

    public static bool IsEnabled(string group) => _verbose || _enabled.Contains(group);

    public static void Log(string group, string message)
    {
        if (!IsEnabled(group)) return;
        GD.Print($"[{group}] {message}");
    }

    public static void Warn(string group, string message)
    {
        if (!IsEnabled(group)) return;
        GD.Print($"[{group}] {message}");
    }

    public static void Error(string group, string message)
    {
        if (!IsEnabled(group)) return;
        GD.PrintErr($"[{group}] {message}");
    }
}