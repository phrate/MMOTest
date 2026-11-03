using System;
using System.Collections.Generic;
using System.Text;

namespace MMOTest.Server.Gfs;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(List<Token> tokens) { _tokens = tokens; }

    public SuiteNode Parse()
    {
        SkipNewlines();
        var nsTok = Expect(TokenKind.SECTION);
        if (nsTok.Text != "client" && nsTok.Text != "server")
            throw Error($"Header 1 must be [client] or [server], got [{nsTok.Text}]");
        SkipNewlines();
        var secTok = Expect(TokenKind.SECTION);

        SkipNewlines();
        var tags = new List<string>();
        while (Peek().Kind == TokenKind.TAG_OPEN)
        {
            Advance();
            var nameTok = Expect(TokenKind.IDENT);
            Expect(TokenKind.TAG_CLOSE);
            ConsumeNewline();
            if (!GfsTags.Known.Contains(nameTok.Text))
                throw Error($"Unknown tag <{nameTok.Text}> at {nameTok.Line}:{nameTok.Col}");
            tags.Add(nameTok.Text);
            SkipNewlines();
        }

        bool isTaskFile = secTok.Text == "task";
        string? taskName = null;
        List<string>? taskArgs = null;

        var statements = new List<AstNode>();
        while (Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            if (Peek().Kind == TokenKind.INDENT || Peek().Kind == TokenKind.DEDENT)
                throw Error($"Unexpected indentation at {Peek().Line}:{Peek().Col}");

            if (isTaskFile && taskName == null
                    && Peek().Kind == TokenKind.IDENT && Peek().Text == "name"
                    && Peek(1).Kind == TokenKind.IDENT)
            {
                Advance();
                taskName = Expect(TokenKind.IDENT).Text;
                ConsumeNewline();
                continue;
            }
            if (isTaskFile && taskArgs == null
                    && Peek().Kind == TokenKind.IDENT && Peek().Text == "args"
                    && Peek(1).Kind == TokenKind.STRING)
            {
                Advance();
                taskArgs = new List<string>();
                while (true)
                {
                    taskArgs.Add(Expect(TokenKind.STRING).Text);
                    if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); continue; }
                    break;
                }
                ConsumeNewline();
                continue;
            }

            statements.Add(ParseStatement());
        }

        if (isTaskFile && string.IsNullOrEmpty(taskName))
            throw Error($"This [task] file is missing a 'name' declaration at {secTok.Line}");

        return new SuiteNode(nsTok.Text, secTok.Text, tags, statements, taskName, taskArgs);
    }

    private AstNode ParseStatement()
    {
        var t = Peek();
        return t.Kind switch
        {
            TokenKind.CHAT     => ParseChat(),
            TokenKind.IF       => ParseIf(),
            TokenKind.TASK     => ParseTaskStatement(),
            TokenKind.RUN      => ParseRun(),
            TokenKind.SKIP     => ParseSkip(),
            TokenKind.CONTINUE => ParseContinue(),
            TokenKind.CODE     => ParseCode(),
            TokenKind.IDENT    => ParseIdentStatement(),
            TokenKind.ANIM     => ParseAnim(),
            _ => throw Error($"Unexpected token {t.Kind} at {t.Line}:{t.Col}"),
        };
    }

    private AstNode ParseIdentStatement()
    {
        if (Peek(1).Kind == TokenKind.ASSIGN) return ParseAssign();
        var expr = ParseExpr();
        ConsumeNewline();
        return new ExprStmtNode(expr, Peek().Line);
    }

    private AssignNode ParseAssign()
    {
        var nameTok = Expect(TokenKind.IDENT);
        Expect(TokenKind.ASSIGN);
        var value = ParseExpr();
        ConsumeNewline();
        return new AssignNode(nameTok.Text, value, nameTok.Line);
    }

    private AstNode ParseChat()
    {
        var chatTok = Expect(TokenKind.CHAT);
        var modeTok = Advance();
        ChatKind kind;
        switch (modeTok.Kind)
        {
            case TokenKind.LOCAL:         kind = ChatKind.Local; break;
            case TokenKind.PUBLIC:        kind = ChatKind.Public; break;
            case TokenKind.OVERHEAD:      kind = ChatKind.Overhead; break;
            case TokenKind.PRIVATE:       kind = ChatKind.Private; break;
            case TokenKind.PRIVATE_LOCAL: kind = ChatKind.PrivateLocal; break;
            default:
                throw Error($"Expected chat mode, got {modeTok.Kind} at {modeTok.Line}:{modeTok.Col}");
        }

        bool takesTarget = kind == ChatKind.Private || kind == ChatKind.PrivateLocal;
        if (takesTarget)
        {
            Expect(TokenKind.LPAREN);
            var target = ParseExpr();
            Expect(TokenKind.COMMA);
            var textTok = Expect(TokenKind.STRING);
            Expect(TokenKind.RPAREN);
            ConsumeNewline();
            return new ChatNode(kind, SplitInterpolation(textTok.Text), target, chatTok.Line);
        }
        var strTok = Expect(TokenKind.STRING);
        ConsumeNewline();
        return new ChatNode(kind, SplitInterpolation(strTok.Text), null, chatTok.Line);
    }

    private IfNode ParseIf()
    {
        var ifTok = Expect(TokenKind.IF);
        var cond = ParseExpr();
        var thenBlock = ParseIfBody();
        var elseBlock = new List<AstNode>();
        if (Peek().Kind == TokenKind.ELSE)
        {
            Advance();
            elseBlock = ParseIfBody();
        }
        return new IfNode(cond, thenBlock, elseBlock, ifTok.Line);
    }

    /// <summary>
    /// Handles `IF cond: indented`, `IF cond\nindented` (no colon), and
    /// `IF cond\nsame-indent statement` (batch style). Indentation from the lexer
    /// disambiguates the last two.
    /// </summary>
    private List<AstNode> ParseIfBody()
    {
        bool hadColon = false;
        if (Peek().Kind == TokenKind.COLON) { Advance(); hadColon = true; }
        ConsumeNewline();

        if (Peek().Kind == TokenKind.INDENT)
        {
            Advance();
            var stmts = new List<AstNode>();
            while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
            {
                if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
                stmts.Add(ParseStatement());
            }
            if (Peek().Kind == TokenKind.DEDENT) Advance();
            return stmts;
        }

        if (hadColon || Peek().Kind != TokenKind.EOF)
            return new List<AstNode> { ParseStatement() };
        return new List<AstNode>();
    }

    private AstNode ParseTaskStatement()
    {
        var taskTok = Expect(TokenKind.TASK);
        if (Peek().Kind == TokenKind.COLON)
        {
            Advance();
            ConsumeNewline();
            if (Peek().Kind != TokenKind.INDENT)
                throw Error($"TASK must be followed by an indented block at {taskTok.Line}:{taskTok.Col}");
            Advance();
            var body = new List<AstNode>();
            while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
            {
                if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
                body.Add(ParseStatement());
            }
            if (Peek().Kind == TokenKind.DEDENT) Advance();
            return new TaskNode(body, taskTok.Line);
        }

        if (Peek().Kind == TokenKind.IDENT)
        {
            var nameTok = Advance();
            var args = new List<AstNode>();
            if (Peek().Kind == TokenKind.LPAREN)
            {
                Advance();
                SkipNewlines();
                if (Peek().Kind != TokenKind.RPAREN)
                {
                    while (true)
                    {
                        args.Add(ParseExpr());
                        if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); continue; }
                        break;
                    }
                }
                SkipNewlines();
                Expect(TokenKind.RPAREN);
            }

            var handlers = new List<OnCodeHandlerNode>();
            if (Peek().Kind == TokenKind.COLON)
            {
                Advance();
                ConsumeNewline();
                if (Peek().Kind == TokenKind.INDENT)
                {
                    Advance();
                    while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
                    {
                        if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
                        handlers.Add(ParseOnCodeHandler());
                    }
                    if (Peek().Kind == TokenKind.DEDENT) Advance();
                }
            }
            else
            {
                ConsumeNewline();
            }
            return new TaskCallNode(nameTok.Text, args, handlers, taskTok.Line);
        }
        throw Error($"Expected ':' or task name after TASK at {taskTok.Line}:{taskTok.Col}");
    }

    private OnCodeHandlerNode ParseOnCodeHandler()
    {
        var onTok = Expect(TokenKind.ON);
        Expect(TokenKind.CODE);
        var codeTok = Peek();
        if (!IsCodeWord(codeTok.Kind))
            throw Error($"Expected code name after ON CODE at {codeTok.Line}:{codeTok.Col}");
        Advance();
        string code = codeTok.Text;
        if (Peek().Kind == TokenKind.COLON) Advance();
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"ON CODE {code} must be followed by an indented block at {onTok.Line}:{onTok.Col}");
        Advance();
        var body = new List<AstNode>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            body.Add(ParseStatement());
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return new OnCodeHandlerNode(code, body, onTok.Line);
    }

    private CodeNode ParseCode()
    {
        var codeTok = Expect(TokenKind.CODE);
        var nameTok = Peek();
        if (!IsCodeWord(nameTok.Kind))
            throw Error($"Expected code name after CODE at {nameTok.Line}:{nameTok.Col}");
        Advance();
        ConsumeNewline();
        return new CodeNode(nameTok.Text, codeTok.Line);
    }

    private AnimNode ParseAnim()
    {
        var animTok = Expect(TokenKind.ANIM);
        var nameTok = Expect(TokenKind.STRING);
        AstNode? duration = null;

        // Accept both forms:
        //   anim "wave" 500     (preferred)
        //   anim "wave", 500    (legacy — still works)
        if (Peek().Kind == TokenKind.COMMA)
        {
            Advance();
            duration = ParseExpr();
        }
        else if (Peek().Kind != TokenKind.NEWLINE && Peek().Kind != TokenKind.EOF)
        {
            duration = ParseExpr();
        }

        ConsumeNewline();
        return new AnimNode(nameTok.Text, duration, animTok.Line);
    }

    private RunNode ParseRun()
    {
        var runTok = Expect(TokenKind.RUN);
        var nameTok = Expect(TokenKind.IDENT);
        var args = new List<AstNode>();
        if (Peek().Kind == TokenKind.LPAREN)
        {
            Advance();
            SkipNewlines();
            if (Peek().Kind != TokenKind.RPAREN)
            {
                while (true)
                {
                    args.Add(ParseExpr());
                    if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); continue; }
                    break;
                }
            }
            SkipNewlines();
            Expect(TokenKind.RPAREN);
        }
        ConsumeNewline();
        return new RunNode(nameTok.Text, args, runTok.Line);
    }

    private SkipNode ParseSkip()
    {
        var tok = Advance();
        ConsumeNewline();
        return new SkipNode(tok.Line);
    }

    private ContinueNode ParseContinue()
    {
        var tok = Advance();
        ConsumeNewline();
        return new ContinueNode(tok.Line);
    }

    private static List<PrintPart> SplitInterpolation(string text)
    {
        var parts = new List<PrintPart>();
        var lit = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int j = text.IndexOf('`', i + 1);
                if (j < 0) { lit.Append(text[i]); i++; continue; }
                if (lit.Length > 0) { parts.Add(new PrintPart(false, lit.ToString(), "")); lit.Clear(); }
                parts.Add(new PrintPart(true, "", text.Substring(i + 1, j - i - 1)));
                i = j + 1;
            }
            else { lit.Append(text[i]); i++; }
        }
        if (lit.Length > 0) parts.Add(new PrintPart(false, lit.ToString(), ""));
        return parts;
    }

    private AstNode ParseExpr()
    {
        if (Peek().Kind == TokenKind.NOT)
        {
            var notTok = Advance();
            return new NotNode(ParsePostfix(), notTok.Line);
        }
        return ParsePostfix();
    }

    private AstNode ParsePostfix()
    {
        var node = ParsePrimary();
        while (true)
        {
            var t = Peek();
            if (t.Kind == TokenKind.IS)
            {
                Advance();
                bool negated = false;
                if (Peek().Kind == TokenKind.NOT) { Advance(); negated = true; }
                Expect(TokenKind.EMPTY);
                node = new IsEmptyNode(node, negated, t.Line);
                continue;
            }
            if (t.Kind == TokenKind.NOT)
            {
                Advance();
                var right = ParsePrimary();
                node = new BinaryOpNode("!=", node, right, t.Line);
                continue;
            }
            break;
        }
        return node;
    }

    private AstNode ParsePrimary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.STRING:     Advance(); return new LiteralNode(t.Text, "string", t.Line);
            case TokenKind.NUMBER:
                Advance();
                if (t.Text.Contains('.')) return new LiteralNode(double.Parse(t.Text), "float", t.Line);
                return new LiteralNode(long.Parse(t.Text), "int", t.Line);
            case TokenKind.BOOL:       Advance(); return new LiteralNode(t.Text == "true", "bool", t.Line);
            case TokenKind.NULL:       Advance(); return new NullNode(t.Line);
            case TokenKind.SYSTEM_VAR: Advance(); return new RefNode("system", t.Text, t.Line);
            case TokenKind.ARG_VAR:    Advance(); return new RefNode("arg", t.Text, t.Line);
            case TokenKind.IDENT:      return ParseIdentExpr();
            case TokenKind.LBRACKET:   return ParseArray();
            case TokenKind.LBRACE:     return ParseDict();
            default: throw Error($"Unexpected token {t.Kind} at {t.Line}:{t.Col}");
        }
    }

    private AstNode ParseIdentExpr()
    {
        var nameTok = Advance();
        string qualified = nameTok.Text;
        while (Peek().Kind == TokenKind.DOT)
        {
            Advance();
            var part = Expect(TokenKind.IDENT);
            qualified += "." + part.Text;
        }
        if (Peek().Kind == TokenKind.LPAREN)
        {
            Advance();
            SkipNewlines();
            var args = new List<AstNode>();
            if (Peek().Kind != TokenKind.RPAREN)
            {
                while (true)
                {
                    args.Add(ParseExpr());
                    if (Peek().Kind == TokenKind.COMMA) { Advance(); continue; }
                    break;
                }
            }
            SkipNewlines();
            Expect(TokenKind.RPAREN);
            return new CallNode(qualified, args, nameTok.Line);
        }
        if (qualified.Contains('.'))
            throw Error($"Qualified name '{qualified}' without call parens at {nameTok.Line}:{nameTok.Col}");
        return new RefNode("local", qualified, nameTok.Line);
    }

    private ArrayNode ParseArray()
    {
        var openTok = Expect(TokenKind.LBRACKET);
        SkipNewlines();
        var items = new List<AstNode>();
        while (Peek().Kind != TokenKind.RBRACKET)
        {
            if (Peek().Kind == TokenKind.IF)
                items.Add(ParseArrayIf());
            else if (Peek().Kind == TokenKind.IDENT && Peek(1).Kind == TokenKind.COLON)
                items.Add(ParseActionEntry());
            else
                items.Add(ParseExpr());
            SkipNewlines();
            if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); }
            if (Peek().Kind == TokenKind.EOF) throw Error("Unterminated array");
        }
        Expect(TokenKind.RBRACKET);
        return new ArrayNode(items, openTok.Line);
    }

    /// <summary>
    /// Conditional array entry:
    ///     if cond:
    ///         item
    ///     NEXT
    /// The indented item after IF is the conditional payload. Only one item per
    /// `if` — multiple would need indentation tracking inside arrays, which is
    /// deferred. `:` is optional. Newlines after `if cond:` are consumed; the
    /// item is whatever comes next (action entry or expression).
    /// </summary>
    private ArrayIfNode ParseArrayIf()
    {
        var ifTok = Expect(TokenKind.IF);
        var cond = ParseExpr();
        if (Peek().Kind == TokenKind.COLON) Advance();
        ConsumeNewline();
        SkipNewlines();
        AstNode item;
        if (Peek().Kind == TokenKind.IDENT && Peek(1).Kind == TokenKind.COLON)
            item = ParseActionEntry();
        else if (Peek().Kind == TokenKind.IF)
            throw Error($"Nested `if` inside array item not supported at {ifTok.Line}:{ifTok.Col}");
        else
            item = ParseExpr();
        return new ArrayIfNode(cond, item, ifTok.Line);
    }

    private ActionEntryNode ParseActionEntry()
    {
        var keyTok = Expect(TokenKind.IDENT);
        Expect(TokenKind.COLON);
        var script = ParseExpr();
        Expect(TokenKind.COMMA);
        var display = ParseExpr();
        var args = new Dictionary<string, AstNode>();
        if (Peek().Kind == TokenKind.COMMA && Peek(1).Kind == TokenKind.LBRACE)
        {
            Advance();
            var dict = (DictNode)ParseDict();
            args = new Dictionary<string, AstNode>(dict.Entries);
        }
        return new ActionEntryNode(keyTok.Text, script, display, args, keyTok.Line);
    }

    private DictNode ParseDict()
    {
        var openTok = Expect(TokenKind.LBRACE);
        SkipNewlines();
        var entries = new Dictionary<string, AstNode>();
        while (Peek().Kind != TokenKind.RBRACE)
        {
            var keyTok = Peek();
            string key;
            if (keyTok.Kind == TokenKind.STRING || keyTok.Kind == TokenKind.IDENT) { Advance(); key = keyTok.Text; }
            else throw Error($"Expected dict key at {keyTok.Line}:{keyTok.Col}");
            Expect(TokenKind.ASSIGN);
            entries[key] = ParseExpr();
            SkipNewlines();
            if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); }
            if (Peek().Kind == TokenKind.EOF) throw Error("Unterminated dict");
        }
        Expect(TokenKind.RBRACE);
        return new DictNode(entries, openTok.Line);
    }

    private void SkipNewlines() { while (Peek().Kind == TokenKind.NEWLINE) Advance(); }
    private void ConsumeNewline() { if (Peek().Kind == TokenKind.NEWLINE) Advance(); }
    private Token Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];
    private Token Advance() => _tokens[_pos++];
    private Token Expect(TokenKind kind)
    {
        var t = Peek();
        if (t.Kind != kind) throw Error($"Expected {kind}, got {t.Kind} at {t.Line}:{t.Col}");
        Advance();
        return t;
    }
    private Exception Error(string msg) => new Exception("[GFS] " + msg);

    private static bool IsCodeWord(TokenKind k) =>
        k == TokenKind.IDENT || k == TokenKind.NULL ||
        k == TokenKind.SKIP || k == TokenKind.CONTINUE;
}