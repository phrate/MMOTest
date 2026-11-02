using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs;

public sealed class FunctionRegistry
{
    private readonly Dictionary<string, Func<CallContext, List<object?>, object?>> _fns = new();

    public void Register(string name, Func<CallContext, List<object?>, object?> fn) => _fns[name] = fn;

    public bool Has(string name) => _fns.ContainsKey(name);

    public object? Call(string name, CallContext ctx, List<object?> args)
    {
        if (!_fns.TryGetValue(name, out var fn))
            throw new Exception($"[GFS] Unknown function '{name}'");
        return fn(ctx, args);
    }

    public IEnumerable<string> Names => _fns.Keys;
}