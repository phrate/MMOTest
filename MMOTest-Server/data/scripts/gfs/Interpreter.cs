using System;
using System.Collections.Generic;
using System.Text;

namespace MMOTest.Server.Gfs;

public enum ExecResult { Normal, Skipped, Continued, Ended }

public static class Interpreter
{
    // ============================================================================
    // Chat style constants.
    //
    // THIS IS THE SINGLE SOURCE OF TRUTH for how chat effects are formatted.
    // Change a color, add bold, adjust the prefix here — the rest of the pipeline
    // (parser, AST, serializer, effects routing, client) treats the resulting
    // string as opaque BBCode and never inspects it.
    //
    // `{0}` is the interpolation slot for the runtime value in each format.
    // ============================================================================
    private const string PrivatePrefixFmt = "[color=blue][b]From {0}: [/b][/color]";
    private const string PrivateLocalFmt  = "[color=purple]{0}[/color]";

    public static List<Dictionary<string, object>> Run(
        SuiteNode suite, CallContext ctx, FunctionRegistry functions)
    {
        if (suite.Namespace != "server")
            throw new Exception($"[GFS] Expected [server] script, got [{suite.Namespace}]");
        if (suite.Section != "generic")
            throw new Exception($"[GFS] Expected [generic] section, got [{suite.Section}]");

        ctx.Effects.Clear();
        ExecuteBlock(suite.Statements, ctx, functions);
        return ctx.Effects;
    }

    public static ExecResult ExecuteBlock(List<AstNode> stmts, CallContext ctx, FunctionRegistry functions)
    {
        foreach (var stmt in stmts)
        {
            var r = ExecuteStatement(stmt, ctx, functions);
            if (r != ExecResult.Normal) return r;
            if (ctx.EndScriptRequested) return ExecResult.Ended;
        }
        return ExecResult.Normal;
    }

    private static ExecResult ExecuteStatement(AstNode stmt, CallContext ctx, FunctionRegistry functions)
    {
        switch (stmt)
        {
            case AssignNode a:
                ctx.Locals[a.Name] = Evaluate(a.Value, ctx, functions);
                return ExecResult.Normal;

            case ChatNode c:
                HandleChat(c, ctx, functions);
                return ExecResult.Normal;

            case IfNode i:
                var cond = Evaluate(i.Condition, ctx, functions);
                if (Truthy(cond)) return ExecuteBlock(i.ThenBlock, ctx, functions);
                return ExecuteBlock(i.ElseBlock, ctx, functions);

            case TaskNode t:
                var r = ExecuteBlock(t.Body, ctx, functions);
                if (r == ExecResult.Continued) return ExecResult.Normal;
                return r;

            case RunNode run:
                HandleRun(run, ctx, functions);
                return ExecResult.Normal;

            case SkipNode:
                return ExecResult.Skipped;

            case ContinueNode:
                return ExecResult.Continued;

            case ExprStmtNode e:
                Evaluate(e.Expr, ctx, functions);
                return ExecResult.Normal;

            default:
                throw new Exception($"[GFS] Unsupported statement in [generic]: {stmt.GetType().Name}");
        }
    }

    private static void HandleChat(ChatNode c, CallContext ctx, FunctionRegistry functions)
    {
        string text = RenderChatParts(c.Parts, ctx);

        if (c.Kind == ChatKind.Private || c.Kind == ChatKind.PrivateLocal)
        {
            var targetValue = Evaluate(c.TargetExpr!, ctx, functions);
            long targetPid = targetValue != null ? Convert.ToInt64(targetValue) : 0;
            if (targetPid <= 0) return;

            string body;
            if (c.Kind == ChatKind.Private)
            {
                var selfId = ctx.System.TryGetValue("SELF_ID", out var sid) ? sid?.ToString() ?? "?" : "?";
                body = string.Format(PrivatePrefixFmt, selfId) + text;
            }
            else // PrivateLocal
            {
                body = string.Format(PrivateLocalFmt, text);
            }

            ctx.Effects.Add(new Dictionary<string, object>
            {
                ["kind"] = "chat_private",
                ["target_player_id"] = targetPid,
                ["text"] = body
            });
            return;
        }

        string effectKind = c.Kind switch
        {
            ChatKind.Local => "chat_local",
            ChatKind.Public => "chat_public",
            ChatKind.Overhead => "chat_overhead",
            _ => "chat_public"
        };
        ctx.Effects.Add(new Dictionary<string, object>
        {
            ["kind"] = effectKind,
            ["text"] = text
        });
    }

    private static void HandleRun(RunNode run, CallContext ctx, FunctionRegistry functions)
    {
        if (ctx.RunScript == null) throw new Exception("[GFS] RUN not supported in this context");
        var args = new Dictionary<string, object>();
        int positionalIndex = 0;
        for (int i = 0; i < run.Args.Count; i++)
        {
            var expr = run.Args[i];
            var value = Evaluate(expr, ctx, functions);
            string key;
            if (expr is RefNode r && r.RefKind == "local") key = r.Name;
            else key = $"arg{positionalIndex++}";
            args[key] = value;
        }
        ctx.RunScript(run.ScriptName, args);
    }

    private static object? Evaluate(AstNode node, CallContext ctx, FunctionRegistry functions)
    {
        switch (node)
        {
            case LiteralNode l: return l.Value;
            case NullNode: return null;
            case RefNode r: return ResolveRef(r.RefKind, r.Name, ctx);
            case NotNode n: return !Truthy(Evaluate(n.Operand, ctx, functions));
            case BinaryOpNode b:
                var left = Evaluate(b.Left, ctx, functions);
                var right = Evaluate(b.Right, ctx, functions);
                return b.Op switch
                {
                    "!=" => !ValueEquals(left, right),
                    "==" => ValueEquals(left, right),
                    _ => throw new Exception($"[GFS] Unknown binary op '{b.Op}'")
                };
            case ArrayNode a:
                var arr = new List<object?>();
                foreach (var item in a.Items) arr.Add(Evaluate(item, ctx, functions));
                return arr;
            case DictNode d:
                var dict = new Dictionary<string, object?>();
                foreach (var kv in d.Entries) dict[kv.Key] = Evaluate(kv.Value, ctx, functions);
                return dict;
            case ActionEntryNode ae:
                var outArgs = new Dictionary<string, object?>();
                foreach (var kv in ae.Args) outArgs[kv.Key] = Evaluate(kv.Value, ctx, functions);
                return new Dictionary<string, object?>
                {
                    ["id"] = ae.Key,
                    ["script"] = Evaluate(ae.ScriptNode, ctx, functions),
                    ["display"] = Evaluate(ae.DisplayNode, ctx, functions),
                    ["args"] = outArgs
                };
            case IsEmptyNode ie:
                var operand = Evaluate(ie.Operand, ctx, functions);
                bool empty = IsEmpty(operand);
                return ie.Negated ? !empty : empty;
            case CallNode c:
                var callArgs = new List<object?>();
                foreach (var arg in c.Arguments) callArgs.Add(Evaluate(arg, ctx, functions));
                var prev = ctx.CurrentCall;
                ctx.CurrentCall = c;
                try { return functions.Call(c.QualifiedName, ctx, callArgs); }
                finally { ctx.CurrentCall = prev; }
            case ChatNode:
                throw new Exception("[GFS] chat cannot be used as an expression");
            default:
                throw new Exception($"[GFS] Cannot evaluate {node.GetType().Name}");
        }
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is long la && b is long lb) return la == lb;
        if (a is double da && b is double db) return da == db;
        if (a is long lia && b is double dib) return lia == dib;
        if (a is double dia && b is long lib) return dia == lib;
        return a.ToString() == b.ToString();
    }

    public static string RenderInterpolated(string text, CallContext ctx)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int j = text.IndexOf('`', i + 1);
                if (j < 0) { sb.Append(text[i]); i++; continue; }
                string name = text.Substring(i + 1, j - i - 1);
                object? v = ResolveRef("local", name, ctx);
                if (v == null) v = ResolveRef("arg", name, ctx);
                sb.Append(v?.ToString() ?? "");
                i = j + 1;
            }
            else { sb.Append(text[i]); i++; }
        }
        return sb.ToString();
    }

    private static object? ResolveRef(string kind, string name, CallContext ctx) => kind switch
    {
        "local" => ctx.Locals.TryGetValue(name, out var l) ? l : null,
        "arg" => ctx.Args.TryGetValue(name, out var a) ? a : null,
        "system" => ctx.System.TryGetValue(name, out var s) ? s : null,
        _ => null
    };

    private static string RenderChatParts(List<PrintPart> parts, CallContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (!part.IsRef) { sb.Append(part.Literal); continue; }
            var v = ResolveRef("local", part.RefName, ctx);
            if (v == null) v = ResolveRef("arg", part.RefName, ctx);
            sb.Append(v?.ToString() ?? "");
        }
        return sb.ToString();
    }

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0.0,
        string s => !string.IsNullOrEmpty(s),
        List<object?> list => list.Count > 0,
        Dictionary<string, object?> dict => dict.Count > 0,
        _ => true
    };

    private static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        List<object?> list => list.Count == 0,
        Dictionary<string, object?> dict => dict.Count == 0,
        System.Collections.IList ilist => ilist.Count == 0,
        _ => false
    };
}