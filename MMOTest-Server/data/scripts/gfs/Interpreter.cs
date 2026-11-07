using System;
using System.Collections.Generic;
using System.Text;

namespace MMOTest.Server.Gfs;

public enum ExecResult { Normal, Skipped, Continued, Ended, ReturnFromTask }

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

            case TaskCallNode tc:
                return ExecuteTaskCallWithHandlers(tc, ctx, functions);

            case RunNode run:
                HandleRun(run, ctx, functions);
                return ExecResult.Normal;

            case SkipNode:
                return ExecResult.Skipped;

            case ContinueNode:
                return ExecResult.Continued;

            case CodeNode cn:
                ctx.ReturnCode = cn.Code;
                return ExecResult.ReturnFromTask;

            case ExprStmtNode e:
                Evaluate(e.Expr, ctx, functions);
                return ExecResult.Normal;

            case AnimNode an:
                HandleAnim(an, ctx, functions);
                return ExecResult.Normal;
            
            case SayNode s:
                ctx.Locals["say"] = RenderChatParts(s.Parts, ctx);
                return ExecResult.Normal;

            default:
                throw new Exception($"[GFS] Unsupported statement {stmt.GetType().Name} at line {stmt.Line}");
        }
    }

    private static ExecResult ExecuteTaskCallWithHandlers(TaskCallNode tc, CallContext ctx, FunctionRegistry functions)
    {
        if (ctx.TaskLookup == null)
            throw new Exception("[GFS] TASK calls are not supported in this context");
        var taskDef = ctx.TaskLookup(tc.TaskName);
        if (taskDef == null)
            throw new Exception($"[GFS] Unknown TASK '{tc.TaskName}'");

        var declaredArgs = taskDef.TaskArgs ?? new List<string>();
        if (tc.Arguments.Count != declaredArgs.Count)
            throw new Exception($"[GFS] TASK '{tc.TaskName}' expects {declaredArgs.Count} arg(s), got {tc.Arguments.Count}");

        var childLocals = new Dictionary<string, object>();
        for (int i = 0; i < tc.Arguments.Count; i++)
            childLocals[declaredArgs[i]] = Evaluate(tc.Arguments[i], ctx, functions)!;

        var childCtx = new CallContext
        {
            System = ctx.System,
            Locals = childLocals,
            Args = new Dictionary<string, object>(),
            SelfPlayerId = ctx.SelfPlayerId,
            ScriptPath = tc.TaskName,
            RunScript = ctx.RunScript,
            CurrentCall = ctx.CurrentCall,
            TaskLookup = ctx.TaskLookup,
        };

        var taskResult = ExecuteBlock(taskDef.Statements, childCtx, functions);

        if (childCtx.EndScriptRequested) ctx.EndScriptRequested = true;
        ctx.Effects.AddRange(childCtx.Effects);

        // Legacy direct signals propagate immediately; only CODE-based returns dispatch.
        if (taskResult == ExecResult.Ended) return ExecResult.Ended;
        if (taskResult == ExecResult.Skipped) return ExecResult.Skipped;

        // Everything else (ReturnFromTask, Normal, Continued) resolves to a code,
        // defaulting to "NULL" when no CODE was set.
        string effectiveCode = taskResult == ExecResult.ReturnFromTask
            ? (childCtx.ReturnCode ?? "NULL").ToUpperInvariant()
            : "NULL";

        if (tc.Handlers.Count > 0)
        {
            foreach (var h in tc.Handlers)
            {
                if (h.Code.ToUpperInvariant() == effectiveCode)
                {
                    var handlerResult = ExecuteBlock(h.Body, ctx, functions);
                    return handlerResult == ExecResult.Continued ? ExecResult.Normal : handlerResult;
                }
            }
        }
        return ExecResult.Normal;
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
                // Prefer username; fall back to id if SELF_USERNAME isn't set.
                string displayName;
                if (ctx.System.TryGetValue("SELF_USERNAME", out var uname)
                        && uname is string us && !string.IsNullOrEmpty(us))
                    displayName = us;
                else if (ctx.System.TryGetValue("SELF_ID", out var sid) && sid != null)
                    displayName = sid.ToString()!;
                else
                    displayName = "?";
                body = string.Format(PrivatePrefixFmt, displayName) + text;
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

    private static void HandleAnim(AnimNode an, CallContext ctx, FunctionRegistry functions)
    {
        long durationMs = 0;
        if (an.DurationExpr != null)
        {
            var dv = Evaluate(an.DurationExpr, ctx, functions);
            if (dv != null)
            {
                try { durationMs = Convert.ToInt64(dv); } catch { durationMs = 0; }
            }
        }
        ctx.Effects.Add(new Dictionary<string, object>
        {
            ["kind"] = "anim_set",
            ["anim"] = an.AnimName,
            ["duration_ms"] = durationMs
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
				switch (b.Op)
				{
					case "==": return ValueEquals(left, right);
					case "!=": return !ValueEquals(left, right);
				}
				var cmp = NumericCompare(left, right);
				if (cmp == null)
					throw new Exception($"[GFS] Cannot compare non-numeric operands with '{b.Op}'");
				return b.Op switch
				{
					"<"  => cmp.Value < 0,
					"<=" => cmp.Value <= 0,
					">"  => cmp.Value > 0,
					">=" => cmp.Value >= 0,
					_ => throw new Exception($"[GFS] Unknown binary op '{b.Op}'")
				};
            case ArrayNode a:
                var arr = new List<object?>();
                foreach (var item in a.Items)
                {
                    if (item is ArrayIfNode aif)
                    {
                        if (Truthy(Evaluate(aif.Condition, ctx, functions)))
                            arr.Add(Evaluate(aif.Item, ctx, functions));
                    }
                    else
                    {
                        arr.Add(Evaluate(item, ctx, functions));
                    }
                }
                return arr;
            case ArrayIfNode:
                throw new Exception("[GFS] array_if cannot be evaluated outside an array");
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
            case CodeNode:
                throw new Exception("[GFS] CODE cannot be used as an expression");
            case TaskCallNode:
                throw new Exception("[GFS] TASK call cannot be used as an expression");
            case DialogueOptionNode do_:
                return new Dictionary<string, object?>
                {
                    ["goto"] = do_.Goto,
                    ["label"] = do_.Label,
                };
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

/// <summary>
/// Returns -1/0/1 for numeric comparison, or null if either side isn't a
/// number (long, int, double, float, or a numeric string). Booleans coerce
/// to 0/1. Used by <, <=, >, >= evaluation.
/// </summary>
private static int? NumericCompare(object? a, object? b)
{
	if (!TryToDouble(a, out double da)) return null;
	if (!TryToDouble(b, out double db)) return null;
	return da.CompareTo(db);
}

private static bool TryToDouble(object? v, out double result)
{
	switch (v)
	{
		case long l:   result = l; return true;
		case int i:    result = i; return true;
		case double d: result = d; return true;
		case float f:  result = f; return true;
		case bool bo:  result = bo ? 1 : 0; return true;
		case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out double parsed):
			result = parsed; return true;
		default:
			result = 0; return false;
	}
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
                if (v == null) v = ResolveRef("system", name, ctx);   // NEW
                sb.Append(FormatValue(v));
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
            if (v == null) v = ResolveRef("system", part.RefName, ctx);   // NEW
            sb.Append(FormatValue(v));
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

    /// <summary>
    /// Human-readable rendering of any GFS value. Lists become [a, b], dicts become
    /// {k=v, k=v}, null becomes "null". Without this, `chat local "x=`x`"` renders
    /// the CLR type name (`System.Collections.Generic.List`1[System.Object]`).
    /// </summary>
    public static string FormatValue(object? v)
    {
        if (v == null) return "null";
        if (v is string s) return s;
        if (v is bool b) return b ? "true" : "false";
        if (v is long l) return l.ToString();
        if (v is int i) return i.ToString();
        if (v is double d) return d.ToString();
        if (v is List<object?> list)
        {
            var parts = new List<string>(list.Count);
            foreach (var item in list) parts.Add(FormatValue(item));
            return "[" + string.Join(", ", parts) + "]";
        }
        if (v is Dictionary<string, object?> dict)
        {
            var parts = new List<string>(dict.Count);
            foreach (var kv in dict) parts.Add($"{kv.Key}={FormatValue(kv.Value)}");
            return "{" + string.Join(", ", parts) + "}";
        }
        return v.ToString() ?? "";
    }

    public static void ExecuteDialogueTopic(
        SuiteNode suite, TopicNode topic, CallContext ctx, FunctionRegistry functions)
    {
        if (suite.Namespace != "server")
            throw new Exception($"[GFS] Expected [server] script, got [{suite.Namespace}]");
        if (suite.Section != "dialogue")
            throw new Exception($"[GFS] Expected [dialogue] section, got [{suite.Section}]");

        ctx.Effects.Clear();

        // Top-level assigns run first, every time a topic executes. This seeds
        // Locals with per-session values (e.g. credits = ${PLAYER_CREDITS}) so
        // they reflect the player's current state at each step.
        foreach (var stmt in suite.Statements)
        {
            if (stmt is AssignNode)
                ExecuteStatement(stmt, ctx, functions);
        }

        ExecuteBlock(topic.Body, ctx, functions);
    }
}