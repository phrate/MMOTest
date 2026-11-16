using System;
using System.Collections.Generic;
using System.Text;

namespace MMOTest.Server.Gfs;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(List<Token> tokens) { _tokens = tokens; }
    private bool _isDialogueSection;
    private bool _isRecipeSection;

    public SuiteNode Parse()
    {
        SkipNewlines();
        var nsTok = Expect(TokenKind.SECTION);
        if (nsTok.Text != "client" && nsTok.Text != "server")
            throw Error($"Header 1 must be [client] or [server], got [{nsTok.Text}]");
        SkipNewlines();
        var secTok = Expect(TokenKind.SECTION);
        _isDialogueSection = secTok.Text == "dialogue";
        _isRecipeSection = secTok.Text == "recipe";
        

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

        if (_isRecipeSection)
        {
            var require = ParseRequireBlock();
            var fail = ParseFailBlock();
            var success = ParseSuccessBlock();
            SkipNewlines();
            if (Peek().Kind != TokenKind.EOF)
                throw Error($"Unexpected token after SUCCESS block at {Peek().Line}:{Peek().Col}");
            var recipeNode = new RecipeNode(require, fail, success, secTok.Line);
            return new SuiteNode(nsTok.Text, secTok.Text, tags, new List<AstNode>(), Recipe: recipeNode);
        }

        bool isTaskFile = secTok.Text == "task";
        string? taskName = null;
        List<string>? taskArgs = null;

        var statements = new List<AstNode>();
        var topics = new Dictionary<string, TopicNode>();
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

            if (Peek().Kind == TokenKind.TOPIC)
            {
                if (!_isDialogueSection)
                    throw Error($"Unexpected 'topic' outside [dialogue] section at {Peek().Line}:{Peek().Col}");
                var topic = ParseTopic();
                topics[topic.Name] = topic;
                continue;
            }

            statements.Add(ParseStatement());
        }

        if (isTaskFile && string.IsNullOrEmpty(taskName))
            throw Error($"This [task] file is missing a 'name' declaration at {secTok.Line}");

        return new SuiteNode(nsTok.Text, secTok.Text, tags, statements, taskName, taskArgs, topics);
    }

    private AstNode ParseStatement()
    {
        var t = Peek();
        if (_isRecipeSection && t.Kind == TokenKind.IDENT
            && (t.Text == "consume" || t.Text == "output")
            && Peek(1).Kind == TokenKind.ASSIGN)
        {
            return ParseConsumeOrOutput(t.Text == "consume");
        }
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
            TokenKind.SAY      => _isDialogueSection
                ? ParseSay()
                : throw Error($"Unexpected 'say' outside [dialogue] section at {t.Line}:{t.Col}"),
            TokenKind.CHANCE => ParseChanceStatement(),
            TokenKind.FAIL   => ParseFailCall(),
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
		return new NotNode(ParseComparison(), notTok.Line);
	}
	return ParseComparison();
}


/// <summary>
/// Comparison layer above ParsePostfix: handles `==`, `!=`, `<`, `<=`, `>`, `>=`
/// left-to-right. Postfix (IS EMPTY, infix NOT) binds tighter, so `a IS EMPTY`
/// and `a NOT b` are parsed before any comparison wraps around them.
/// </summary>
private AstNode ParseComparison()
{
	var left = ParsePostfix();
	while (true)
	{
		var t = Peek();
		string op;
		switch (t.Kind)
		{
			case TokenKind.LT:  op = "<";  break;
			case TokenKind.LTE: op = "<="; break;
			case TokenKind.GT:  op = ">";  break;
			case TokenKind.GTE: op = ">="; break;
			case TokenKind.EQ:  op = "=="; break;
			case TokenKind.NEQ: op = "!="; break;
			default: return left;
		}
		Advance();
		var right = ParsePostfix();
		left = new BinaryOpNode(op, left, right, t.Line);
	}
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
            case TokenKind.ITEM_VAR:   Advance(); return new RefNode("item", t.Text, t.Line);
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
            else if (_isDialogueSection && Peek().Kind == TokenKind.STRING && Peek(1).Kind == TokenKind.COLON)
                items.Add(ParseDialogueOption());
            else if (!_isDialogueSection && Peek().Kind == TokenKind.IDENT && Peek(1).Kind == TokenKind.COLON)
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
        if (_isDialogueSection)
        {
            if (Peek().Kind == TokenKind.STRING && Peek(1).Kind == TokenKind.COLON)
                item = ParseDialogueOption();
            else
                item = ParseExpr();
        }
        else
        {
            if (Peek().Kind == TokenKind.IDENT && Peek(1).Kind == TokenKind.COLON)
                item = ParseActionEntry();
            else if (Peek().Kind == TokenKind.IF)
                throw Error($"Nested `if` inside array item not supported at {ifTok.Line}:{ifTok.Col}");
            else
                item = ParseExpr();
        }
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

    private TopicNode ParseTopic()
    {
        var topicTok = Expect(TokenKind.TOPIC);
        var nameTok = Expect(TokenKind.IDENT);
        if (Peek().Kind == TokenKind.COLON) Advance();
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"topic '{nameTok.Text}' must be followed by an indented block at {topicTok.Line}:{topicTok.Col}");
        Advance();
        var body = new List<AstNode>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            body.Add(ParseStatement());
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return new TopicNode(nameTok.Text, body, topicTok.Line);
    }

    private SayNode ParseSay()
    {
        var sayTok = Expect(TokenKind.SAY);
        var textTok = Expect(TokenKind.STRING);
        ConsumeNewline();
        return new SayNode(textTok.Text, SplitInterpolation(textTok.Text), sayTok.Line);
    }

    private DialogueOptionNode ParseDialogueOption()
    {
        var gotoTok = Expect(TokenKind.STRING);
        Expect(TokenKind.COLON);
        var labelTok = Expect(TokenKind.STRING);
        return new DialogueOptionNode(gotoTok.Text, labelTok.Text, gotoTok.Line);
    }

    private List<AstNode> ParseRequireBlock()
    {
        var requireTok = Expect(TokenKind.REQUIRE);
        if (Peek().Kind == TokenKind.COLON) Advance();
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"REQUIRE must be followed by an indented block at {requireTok.Line}:{requireTok.Col}");
        Advance();
        var stmts = new List<AstNode>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var t = Peek();
            if (t.Kind == TokenKind.IDENT && t.Text == "ITEMS" && Peek(1).Kind == TokenKind.COLON)
            {
                stmts.Add(ParseItemsCheck());
                continue;
            }
            if (t.Kind == TokenKind.IDENT && t.Text == "LEVELS" && Peek(1).Kind == TokenKind.COLON)
            {
                stmts.Add(ParseLevelsCheck());
                continue;
            }
            stmts.Add(ParseStatement());
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return stmts;
    }

    private ItemsCheckNode ParseItemsCheck()
    {
        var itemsTok = Advance();  // "ITEMS"
        Expect(TokenKind.COLON);
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"ITEMS must be followed by an indented block at {itemsTok.Line}:{itemsTok.Col}");
        Advance();
        var items = new List<(string, AstNode)>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var slugTok = Advance();
            string slug;
            if (slugTok.Kind == TokenKind.ITEM_VAR) slug = slugTok.Text;
            else if (slugTok.Kind == TokenKind.IDENT) slug = slugTok.Text;  // backwards compat
            else throw Error($"Expected item slug (e.g. @knife) at {slugTok.Line}:{slugTok.Col}");
            Expect(TokenKind.COMMA);
            var qty = ParseExpr();
            items.Add((slug, qty));
            ConsumeNewline();
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return new ItemsCheckNode(items, itemsTok.Line);
    }

    private LevelsCheckNode ParseLevelsCheck()
    {
        var levelsTok = Advance();  // "LEVELS"
        Expect(TokenKind.COLON);
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"LEVELS must be followed by an indented block at {levelsTok.Line}:{levelsTok.Col}");
        Advance();
        var levels = new List<(string, AstNode)>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var skillTok = Expect(TokenKind.IDENT);
            Expect(TokenKind.COMMA);
            var lvl = ParseExpr();
            levels.Add((skillTok.Text, lvl));
            ConsumeNewline();
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return new LevelsCheckNode(levels, levelsTok.Line);
    }

    private List<AstNode> ParseFailBlock()
    {
        var stmts = new List<AstNode>();
        if (Peek().Kind != TokenKind.FAIL) return stmts;
        if (Peek(1).Kind != TokenKind.COLON) return stmts;
        var failTok = Advance();
        Advance();  // colon
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"FAIL block must be indented at {failTok.Line}:{failTok.Col}");
        Advance();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var t = Peek();
            if (t.Kind == TokenKind.IDENT && t.Text == "MESSAGES" && Peek(1).Kind == TokenKind.COLON)
            {
                stmts.Add(ParseMessagesBlock("fail"));
                continue;
            }
            throw Error($"FAIL block only supports MESSAGES: at {t.Line}:{t.Col}");
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return stmts;
    }

    private MessagesNode ParseMessagesBlock(string target)
    {
        var msgTok = Advance();  // "MESSAGES"
        Expect(TokenKind.COLON);
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"MESSAGES must be followed by an indented block at {msgTok.Line}:{msgTok.Col}");
        Advance();
        var entries = new Dictionary<string, List<AstNode>>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var nameTok = Expect(TokenKind.IDENT);
            Expect(TokenKind.COLON);
            ConsumeNewline();
            if (Peek().Kind != TokenKind.INDENT)
                throw Error($"Message '{nameTok.Text}' must be indented at {nameTok.Line}:{nameTok.Col}");
            Advance();
            var body = new List<AstNode>();
            while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
            {
                if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
                body.Add(ParseStatement());
            }
            if (Peek().Kind == TokenKind.DEDENT) Advance();
            entries[nameTok.Text] = body;
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return new MessagesNode(target, entries, msgTok.Line);
    }

        private List<AstNode> ParseSuccessBlock()
    {
        var successTok = Expect(TokenKind.SUCCESS);
        if (Peek().Kind == TokenKind.COLON) Advance();
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"SUCCESS must be followed by an indented block at {successTok.Line}:{successTok.Col}");
        Advance();
        var stmts = new List<AstNode>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            var t = Peek();
            if (t.Kind == TokenKind.IDENT && t.Text == "MESSAGES" && Peek(1).Kind == TokenKind.COLON)
            {
                stmts.Add(ParseMessagesBlock("success"));
                continue;
            }
            stmts.Add(ParseStatement());
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();
        return stmts;
    }

    private AstNode ParseConsumeOrOutput(bool isConsume)
    {
        var nameTok = Advance();
        Expect(TokenKind.ASSIGN);
        Expect(TokenKind.LBRACKET);
        SkipNewlines();
        var pairs = new List<(AstNode, AstNode)>();
        while (Peek().Kind != TokenKind.RBRACKET)
        {
            var slugExpr = ParseExpr();
            Expect(TokenKind.COMMA);
            var qtyExpr = ParseExpr();
            pairs.Add((slugExpr, qtyExpr));
            SkipNewlines();
            if (Peek().Kind == TokenKind.COMMA) { Advance(); SkipNewlines(); }
            if (Peek().Kind == TokenKind.EOF) throw Error("Unterminated consume/output array");
        }
        Expect(TokenKind.RBRACKET);
        ConsumeNewline();
        return isConsume
            ? new ConsumeAssignNode(pairs, nameTok.Line)
            : new OutputAssignNode(pairs, nameTok.Line);
    }

    private ChanceNode ParseChanceStatement()
    {
        var chanceTok = Expect(TokenKind.CHANCE);
        var numTok = Expect(TokenKind.NUMBER);
        if (!int.TryParse(numTok.Text, out int numerator))
            throw Error($"CHANCE numerator must be an integer at {numTok.Line}:{numTok.Col}");
        Expect(TokenKind.SLASH);
        var denTok = Expect(TokenKind.NUMBER);
        if (!int.TryParse(denTok.Text, out int denominator) || denominator <= 0)
            throw Error($"CHANCE denominator must be a positive integer at {denTok.Line}:{denTok.Col}");
        Expect(TokenKind.COLON);
        ConsumeNewline();
        if (Peek().Kind != TokenKind.INDENT)
            throw Error($"CHANCE block must be indented at {chanceTok.Line}:{chanceTok.Col}");
        Advance();
        var thenBlock = new List<AstNode>();
        while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
        {
            if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
            thenBlock.Add(ParseStatement());
        }
        if (Peek().Kind == TokenKind.DEDENT) Advance();

        var elseBlock = new List<AstNode>();
        if (Peek().Kind == TokenKind.CHANCE_FAIL)
        {
            Advance();
            if (Peek().Kind == TokenKind.COLON) Advance();
            ConsumeNewline();
            if (Peek().Kind == TokenKind.INDENT)
            {
                Advance();
                while (Peek().Kind != TokenKind.DEDENT && Peek().Kind != TokenKind.EOF)
                {
                    if (Peek().Kind == TokenKind.NEWLINE) { Advance(); continue; }
                    elseBlock.Add(ParseStatement());
                }
                if (Peek().Kind == TokenKind.DEDENT) Advance();
            }
        }
        return new ChanceNode(numerator, denominator, thenBlock, elseBlock, chanceTok.Line);
    }

    private FailCallNode ParseFailCall()
    {
        var failTok = Expect(TokenKind.FAIL);
        Expect(TokenKind.LPAREN);
        var nameTok = Expect(TokenKind.IDENT);
        Expect(TokenKind.RPAREN);
        ConsumeNewline();
        return new FailCallNode(nameTok.Text, failTok.Line);
    }
}