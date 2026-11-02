using System;
using System.Collections.Generic;
using System.Linq;

namespace MMOTest.Server.Gfs;

public static class Serializer
{
    public static Dictionary<string, object> Serialize(SuiteNode suite) => new()
    {
        ["kind"] = "suite",
        ["namespace"] = suite.Namespace,
        ["section"] = suite.Section,
        ["tags"] = suite.Tags,
        ["statements"] = suite.Statements.Select(SerializeNode).ToList()
    };

    private static Dictionary<string, object> SerializeNode(AstNode node) => node switch
    {
        AssignNode a => new() { ["kind"] = "assign", ["name"] = a.Name, ["value"] = SerializeNode(a.Value) },
        ChatNode c => new()
        {
            ["kind"] = "chat",
            ["chat_kind"] = c.Kind switch
            {
                ChatKind.Local => "local",
                ChatKind.Public => "public",
                ChatKind.Overhead => "overhead",
                ChatKind.Private => "private",
                ChatKind.PrivateLocal => "private_local",
                _ => "public"
            },
            ["parts"] = c.Parts.Select(SerializePart).ToList(),
            ["target"] = c.TargetExpr == null ? null : SerializeNode(c.TargetExpr)
        },
        IfNode i => new()
        {
            ["kind"] = "if",
            ["condition"] = SerializeNode(i.Condition),
            ["then"] = i.ThenBlock.Select(SerializeNode).ToList(),
            ["else"] = i.ElseBlock.Select(SerializeNode).ToList()
        },
        TaskNode t => new() { ["kind"] = "task", ["body"] = t.Body.Select(SerializeNode).ToList() },
        RunNode r => new() { ["kind"] = "run", ["name"] = r.ScriptName, ["arguments"] = r.Args.Select(SerializeNode).ToList() },
        SkipNode => new() { ["kind"] = "skip" },
        ContinueNode => new() { ["kind"] = "continue" },
        ExprStmtNode e => new() { ["kind"] = "expr_stmt", ["expr"] = SerializeNode(e.Expr) },
        CallNode c => new() { ["kind"] = "call", ["name"] = c.QualifiedName, ["arguments"] = c.Arguments.Select(SerializeNode).ToList() },
        IsEmptyNode ie => new() { ["kind"] = "is_empty", ["operand"] = SerializeNode(ie.Operand), ["negated"] = ie.Negated },
        NotNode n => new() { ["kind"] = "not", ["operand"] = SerializeNode(n.Operand) },
        BinaryOpNode b => new() { ["kind"] = "binary", ["op"] = b.Op, ["left"] = SerializeNode(b.Left), ["right"] = SerializeNode(b.Right) },
        LiteralNode l => new() { ["kind"] = "literal", ["value"] = l.Value, ["value_type"] = l.ValueType },
        NullNode => new() { ["kind"] = "null" },
        RefNode r2 => new() { ["kind"] = "ref", ["ref_kind"] = r2.RefKind, ["name"] = r2.Name },
        ArrayNode arr => new() { ["kind"] = "array", ["items"] = arr.Items.Select(SerializeNode).ToList() },
        DictNode d => new() { ["kind"] = "dict", ["entries"] = d.Entries.ToDictionary(kv => kv.Key, kv => SerializeNode(kv.Value)) },
        ActionEntryNode ae => new()
        {
            ["kind"] = "action",
            ["key"] = ae.Key,
            ["script_node"] = SerializeNode(ae.ScriptNode),
            ["display_node"] = SerializeNode(ae.DisplayNode),
            ["args"] = ae.Args.ToDictionary(kv => kv.Key, kv => SerializeNode(kv.Value))
        },
        _ => throw new Exception($"Unknown AST node type {node.GetType()}")
    };

    private static Dictionary<string, object> SerializePart(PrintPart p) => p.IsRef
        ? new() { ["is_ref"] = true, ["ref_name"] = p.RefName }
        : new() { ["is_ref"] = false, ["literal"] = p.Literal };
}