using System.Collections.Generic;

namespace MMOTest.Server.Gfs;

public abstract record AstNode(int Line);

public sealed record SuiteNode(
    string Namespace, string Section, List<string> Tags, List<AstNode> Statements,
    string? TaskName = null, List<string>? TaskArgs = null) : AstNode(0);

public sealed record AssignNode(string Name, AstNode Value, int LineNum) : AstNode(LineNum);

public sealed record PrintPart(bool IsRef, string Literal, string RefName);

public enum ChatKind { Local, Public, Overhead, Private, PrivateLocal }
public sealed record ChatNode(ChatKind Kind, List<PrintPart> Parts, AstNode? TargetExpr, int LineNum) : AstNode(LineNum);

public sealed record IfNode(AstNode Condition, List<AstNode> ThenBlock, List<AstNode> ElseBlock, int LineNum) : AstNode(LineNum);
public sealed record TaskNode(List<AstNode> Body, int LineNum) : AstNode(LineNum);
public sealed record OnCodeHandlerNode(string Code, List<AstNode> Body, int LineNum);
public sealed record TaskCallNode(string TaskName, List<AstNode> Arguments, List<OnCodeHandlerNode> Handlers, int LineNum) : AstNode(LineNum);
public sealed record RunNode(string ScriptName, List<AstNode> Args, int LineNum) : AstNode(LineNum);
public sealed record SkipNode(int LineNum) : AstNode(LineNum);
public sealed record ContinueNode(int LineNum) : AstNode(LineNum);
public sealed record CodeNode(string Code, int LineNum) : AstNode(LineNum);

public sealed record ExprStmtNode(AstNode Expr, int LineNum) : AstNode(LineNum);
public sealed record CallNode(string QualifiedName, List<AstNode> Arguments, int LineNum) : AstNode(LineNum);
public sealed record IsEmptyNode(AstNode Operand, bool Negated, int LineNum) : AstNode(LineNum);
public sealed record NotNode(AstNode Operand, int LineNum) : AstNode(LineNum);
public sealed record BinaryOpNode(string Op, AstNode Left, AstNode Right, int LineNum) : AstNode(LineNum);

public sealed record LiteralNode(object Value, string ValueType, int LineNum) : AstNode(LineNum);
public sealed record NullNode(int LineNum) : AstNode(LineNum);
public sealed record RefNode(string RefKind, string Name, int LineNum) : AstNode(LineNum);
public sealed record ArrayNode(List<AstNode> Items, int LineNum) : AstNode(LineNum);
public sealed record DictNode(Dictionary<string, AstNode> Entries, int LineNum) : AstNode(LineNum);
public sealed record ActionEntryNode(string Key, AstNode ScriptNode, AstNode DisplayNode, Dictionary<string, AstNode> Args, int LineNum) : AstNode(LineNum);
public sealed record AnimNode(string AnimName, AstNode? DurationExpr, int LineNum) : AstNode(LineNum);
public sealed record ArrayIfNode(AstNode Condition, AstNode Item, int LineNum) : AstNode(LineNum);

public static class GfsTags
{
    public const string OncePerTick = "once_per_tick";
    public static readonly HashSet<string> Known = new() { OncePerTick };
}