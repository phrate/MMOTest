using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using MMOTest.Server.Gfs;

namespace MMOTest.Server.Tests;

// ============================================================================
// GFS Lexer + Parser tests
// ============================================================================
//
// The GFS DSL is a Python-like language used for tooltips, dialogue, tasks,
// and recipes. Every player-facing interactive feature in the game is
// ultimately written in GFS. Until now it had zero automated tests.
//
// Two kinds of tests here:
//
//   1. **Lexer tests** — verify the token stream for individual constructs
//      (sections, tags, strings, indent/dedent, CRLF handling, comments).
//      These are the "encoding" tier: technical, precise, no prose needed.
//
//   2. **Parser tests** — verify the AST shape for one example of each
//      script archetype (client tooltip, server generic, dialogue, task,
//      recipe). Reader-facing: each test says what script shape it
//      exercises and what language feature it's protecting.
//
// Plus the **killer test** at the end: parse every real .gfs file in the
// repo. This single test is worth more than all the others combined — if
// a future parser change breaks any script in the game, it fails with the
// filename and a clear error rather than waiting for a player to hit that
// feature in-game.
// ============================================================================

public class GfsLexerParserTests
{
    // ------------------------------------------------------------------------
    // Lexer — technical tests
    // ------------------------------------------------------------------------

    [Fact]
    public void Section_Header_Emits_Single_Section_Token()
    {
        // `[name]` at start of line is the only construct that becomes a
        // SECTION token. Everything else (`[1, 2, 3]` array literal) is
        // bracket + items.
        var tokens = new Lexer("[server]\n").Tokenize();
        Assert.Equal(TokenKind.SECTION, tokens[0].Kind);
        Assert.Equal("server", tokens[0].Text);
    }

    [Fact]
    public void Tag_Emits_Open_Ident_Close()
    {
        // `<once_per_tick>` on its own line emits three tokens. The parser
        // only accepts tags immediately after section headers.
        var tokens = new Lexer("<once_per_tick>\n").Tokenize();
        Assert.Equal(TokenKind.TAG_OPEN, tokens[0].Kind);
        Assert.Equal(TokenKind.IDENT, tokens[1].Kind);
        Assert.Equal("once_per_tick", tokens[1].Text);
        Assert.Equal(TokenKind.TAG_CLOSE, tokens[2].Kind);
    }

    [Fact]
    public void String_Unescapes_Standard_Sequences()
    {
        // "\n", "\t", "\\", "\"" are the only escapes. Everything else passes
        // through unchanged (so `\q` becomes `q`). This matches the Lexer's
        // escape table exactly — if that table grows, this test still passes
        // as long as the four known escapes work.
        var tokens = new Lexer("\"line1\\nline2\\ttab\\\\slash\\\"quote\"").Tokenize();
        Assert.Equal(TokenKind.STRING, tokens[0].Kind);
        Assert.Equal("line1\nline2\ttab\\slash\"quote", tokens[0].Text);
    }

    [Fact]
    public void System_Var_And_Arg_Var_Are_Distinct_Kinds()
    {
        // ${NAME} — value pulled from the runtime system dict (e.g. SELF_ID)
        // $"name" — value pulled from the script's args dict (e.g. target_id)
        // Both are RefNodes at parse time; the distinction is whether the
        // runtime looks them up in System vs Args.
        var tokens = new Lexer("${PLAYER_ID} $\"target_id\"").Tokenize();
        Assert.Equal(TokenKind.SYSTEM_VAR, tokens[0].Kind);
        Assert.Equal("PLAYER_ID", tokens[0].Text);
        Assert.Equal(TokenKind.ARG_VAR, tokens[1].Kind);
        Assert.Equal("target_id", tokens[1].Text);
    }

    [Fact]
    public void Numbers_Are_Int_Or_Float_Distinguished_By_Decimal()
    {
        // Lexer emits a single NUMBER token for both; the Parser decides
        // int vs float by checking for '.'. This test locks the lexer half.
        var tokens = new Lexer("42 3.14").Tokenize();
        Assert.Equal(TokenKind.NUMBER, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
        Assert.Equal(TokenKind.NUMBER, tokens[1].Kind);
        Assert.Equal("3.14", tokens[1].Text);
    }

    [Fact]
    public void Comparison_Operators_Are_Unambiguous_Tokens()
    {
        // <= must tokenize as LTE, not LT + ASSIGN. Same for >=, ==, !=.
        //
        // The leading identifier `x ` is deliberate: at line start, `<` is
        // intercepted by the tag-open branch (`<once_per_tick>` etc.). The
        // lexer commits to TAG_OPEN before it knows whether a tag name
        // follows. Prefix with `x ` so the `<` is past line start and
        // reaches the operator switch, where the comparison logic lives.
        var src = "x < <= > >= == != =";
        var tokens = new Lexer(src).Tokenize();
        Assert.Equal(TokenKind.IDENT,  tokens[0].Kind);
        Assert.Equal(TokenKind.LT,     tokens[1].Kind);
        Assert.Equal(TokenKind.LTE,    tokens[2].Kind);
        Assert.Equal(TokenKind.GT,     tokens[3].Kind);
        Assert.Equal(TokenKind.GTE,    tokens[4].Kind);
        Assert.Equal(TokenKind.EQ,     tokens[5].Kind);
        Assert.Equal(TokenKind.NEQ,    tokens[6].Kind);
        Assert.Equal(TokenKind.ASSIGN, tokens[7].Kind);
    }

    [Fact]
    public void Chat_Modes_Map_To_Distinct_Token_Kinds()
    {
        // The five chat modes: local, public, overhead, private, private_local.
        // All are keyword-mapped at lex time so the parser can dispatch cleanly.
        var src = "chat local chat public chat overhead chat private chat private_local";
        var tokens = new Lexer(src).Tokenize();
        Assert.Equal(TokenKind.CHAT,          tokens[0].Kind);
        Assert.Equal(TokenKind.LOCAL,         tokens[1].Kind);
        Assert.Equal(TokenKind.CHAT,          tokens[2].Kind);
        Assert.Equal(TokenKind.PUBLIC,        tokens[3].Kind);
        Assert.Equal(TokenKind.CHAT,          tokens[4].Kind);
        Assert.Equal(TokenKind.OVERHEAD,      tokens[5].Kind);
        Assert.Equal(TokenKind.CHAT,          tokens[6].Kind);
        Assert.Equal(TokenKind.PRIVATE,       tokens[7].Kind);
        Assert.Equal(TokenKind.CHAT,          tokens[8].Kind);
        Assert.Equal(TokenKind.PRIVATE_LOCAL, tokens[9].Kind);
    }

    [Fact]
    public void Indented_Block_Emits_Indent_And_Dedent()
    {
        // Block structure is carried by INDENT/DEDENT, not by braces. The
        // parser relies on these to close blocks at the right point.
        var src = "if true:\n    x = 1\n";
        var tokens = new Lexer(src).Tokenize();
        Assert.Contains(tokens, t => t.Kind == TokenKind.INDENT);
        Assert.Contains(tokens, t => t.Kind == TokenKind.DEDENT);
    }

    [Fact]
    public void Crlf_Blank_Line_Does_Not_Fire_Spurious_Dedent()
    {
        // REGRESSION TEST. The bug: in a CRLF file, a blank line (just \r\n)
        // would enter the indent-check path with _lineIndent=0, which is
        // strictly less than the enclosing block's indent, and fire a
        // spurious DEDENT — splitting the block in half. The fix skips \r
        // entirely when followed by \n, so blank lines are fully transparent.
        //
        // The test: an if-block with a blank line in the middle. There
        // should be exactly ONE DEDENT (at EOF closing the if-block). If
        // the bug regresses, there will be TWO (one from the blank line,
        // one from EOF).
        var src = "if true:\r\n    x = 1\r\n\r\n    y = 2\r\n";
        var tokens = new Lexer(src).Tokenize();
        int dedentCount = tokens.Count(t => t.Kind == TokenKind.DEDENT);
        Assert.Equal(1, dedentCount);
    }

    [Fact]
    public void Comments_Are_Skipped_At_Line_Start_And_Inline()
    {
        // Both `#` and `//` to end-of-line are comments. Neither produces
        // any tokens, so the token stream is just the identifiers.
        var src = "# header comment\nx = 1  // inline\n// another\n";
        var tokens = new Lexer(src).Tokenize();
        var idents = tokens.Where(t => t.Kind == TokenKind.IDENT).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "x" }, idents);
    }

    // ------------------------------------------------------------------------
    // Parser — reader-facing tests
    // ------------------------------------------------------------------------
    //
    // Each test constructs a small but realistic script and asserts the
    // resulting AST shape. The comments describe what script archetype is
    // being covered and why it matters in-game.
    // ------------------------------------------------------------------------

    [Fact]
    public void Client_Tooltip_Parses_With_Conditional_Actions()
    {
        // (Same source as above, assertions corrected.)

        var src =
            "[client]\n" +
            "[tooltip]\n" +
            "\n" +
            "name = ${ITEM_DISPLAY_NAME}\n" +
            "qty = ${ITEM_QUANTITY}\n" +
            "examine_display = \"Examine `name`\"\n" +
            "if qty > 1:\n" +
            "    examine_display = \"Examine `name` x`qty`\"\n" +
            "actions = [\n" +
            "    if qty > 1:\n" +
            "        examine: \"examine.gfs\", examine_display, {\"slot\"=1}\n" +
            "    examine: \"examine.gfs\", \"Examine `name`\", {\"slot\"=1}\n" +
            "]\n";

        var ast = Parse(src);

        Assert.Equal("client", ast.Namespace);
        Assert.Equal("tooltip", ast.Section);
        Assert.Equal(5, ast.Statements.Count);

        // Last statement: `actions = [...]` where actions contains one
        // ArrayIfNode (the conditional examine) and one bare ActionEntryNode.
        var actionsAssign = (AssignNode)ast.Statements[4];
        Assert.Equal("actions", actionsAssign.Name);
        var array = Assert.IsType<ArrayNode>(actionsAssign.Value);
        Assert.Equal(2, array.Items.Count);
        Assert.IsType<ArrayIfNode>(array.Items[0]);
        Assert.IsType<ActionEntryNode>(array.Items[1]);
    }

    [Fact]
    public void Server_Generic_Script_Parses_With_Arg_Read_And_Call()
    {
        // Server generic scripts are the "handlers" behind every player
        // action: examine, drop, consume, talk, attack. The simplest shape
        // reads an arg (e.g. $"npc_id") and passes it to a function.
        //
        // This is the exact shape of combat/combat_start_npc.gfs.

        var src =
            "[server]\n" +
            "[generic]\n" +
            "\n" +
            "npc_id = $\"npc_id\"\n" +
            "EngageNpc(npc_id)\n";

        var ast = Parse(src);

        Assert.Equal("server", ast.Namespace);
        Assert.Equal("generic", ast.Section);
        Assert.Equal(2, ast.Statements.Count);

        var assign = Assert.IsType<AssignNode>(ast.Statements[0]);
        Assert.Equal("npc_id", assign.Name);
        var argRef = Assert.IsType<RefNode>(assign.Value);
        Assert.Equal("arg", argRef.RefKind);
        Assert.Equal("npc_id", argRef.Name);

        var exprStmt = Assert.IsType<ExprStmtNode>(ast.Statements[1]);
        var call = Assert.IsType<CallNode>(exprStmt.Expr);
        Assert.Equal("EngageNpc", call.QualifiedName);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void Dialogue_Script_Parses_Topics_With_Say_And_Options()
    {
        // Dialogue scripts are [server][dialogue]. Each `topic name:` block
        // is one "node" in the conversation graph. A topic can `say` text
        // and offer `options` that route to other topics or "end".
        //
        // Options are a special array literal: "goto": "label" entries
        // (string keys instead of identifier keys, because they're data
        // not function names).

        var src =
            "[server]\n" +
            "[dialogue]\n" +
            "\n" +
            "credits = ${PLAYER_CREDITS}\n" +
            "\n" +
            "topic start:\n" +
            "    say \"Hello `username`.\"\n" +
            "    options = [\n" +
            "        \"rich\": \"I am rich!\"\n" +
            "        \"end\": \"Goodbye.\"\n" +
            "    ]\n" +
            "\n" +
            "topic rich:\n" +
            "    say \"You have `credits` credits.\"\n" +
            "    options = [\n" +
            "        \"end\": \"Thanks!\"\n" +
            "    ]\n";

        var ast = Parse(src);

        Assert.Equal("server", ast.Namespace);
        Assert.Equal("dialogue", ast.Section);
        Assert.NotNull(ast.Topics);
        Assert.Equal(2, ast.Topics!.Count);
        Assert.True(ast.Topics.ContainsKey("start"));
        Assert.True(ast.Topics.ContainsKey("rich"));

        var start = ast.Topics["start"];
        Assert.Equal(2, start.Body.Count);
        Assert.IsType<SayNode>(start.Body[0]);
        Assert.IsType<AssignNode>(start.Body[1]);

        // The options array has two DialogueOptionNode entries.
        var optionsAssign = (AssignNode)start.Body[1];
        var optionsArray = Assert.IsType<ArrayNode>(optionsAssign.Value);
        Assert.Equal(2, optionsArray.Items.Count);
        Assert.IsType<DialogueOptionNode>(optionsArray.Items[0]);
        Assert.IsType<DialogueOptionNode>(optionsArray.Items[1]);
    }

    [Fact]
    public void Task_File_Parses_Name_Args_And_Task_Block()
    {
        // Task files are reusable snippets that other scripts invoke via
        // `TASK name(args)`. They declare their parameter names at the top
        // (`args "a", "b"`) so the caller knows what positional args mean.
        //
        // The body is wrapped in a `TASK:` block — the block is where all
        // the actual statements live. This is the shape of
        // player/move_near_player.gfs.

        var src =
            "[server]\n" +
            "[task]\n" +
            "name MOVE_NEAR_PLAYER\n" +
            "args \"target_id\", \"target_tile\", \"following_player\"\n" +
            "\n" +
            "TASK:\n" +
            "    RUN follow_player(target_id)\n";

        var ast = Parse(src);

        Assert.Equal("task", ast.Section);
        Assert.Equal("MOVE_NEAR_PLAYER", ast.TaskName);
        Assert.Equal(3, ast.TaskArgs!.Count);
        Assert.Equal("target_id", ast.TaskArgs[0]);
        Assert.Single(ast.Statements);
        var taskBlock = Assert.IsType<TaskNode>(ast.Statements[0]);
        Assert.Single(taskBlock.Body);
        Assert.IsType<RunNode>(taskBlock.Body[0]);
    }

    [Fact]
    public void Recipe_Script_Parses_Require_Fail_Success_Blocks()
    {
        // Recipe scripts are [server][recipe]. Every recipe has up to three
        // top-level blocks: REQUIRE (gate check), FAIL (messages for failed
        // gates), SUCCESS (state to apply if all gates pass).
        //
        // REQUIRE contains ITEMS: and LEVELS: sub-blocks. FAIL contains
        // MESSAGES: with per-cause bodies (BAD_ITEMS, BAD_QUANTITIES, etc.).
        // SUCCESS contains consume = [...] and output = [...] arrays plus
        // optional MESSAGES: entries.

        var src =
            "[server]\n" +
            "[recipe]\n" +
            "\n" +
            "REQUIRE:\n" +
            "    ITEMS:\n" +
            "        item_knife, 1\n" +
            "        item_log, 1\n" +
            "\n" +
            "FAIL:\n" +
            "    MESSAGES:\n" +
            "        BAD_ITEMS:\n" +
            "            chat local \"You need a knife and a log.\"\n" +
            "\n" +
            "SUCCESS:\n" +
            "    consume = [\n" +
            "        item_log, 1\n" +
            "    ]\n" +
            "    output = [\n" +
            "        item_stick, 1\n" +
            "    ]\n";

        var ast = Parse(src);

        Assert.Equal("server", ast.Namespace);
        Assert.Equal("recipe", ast.Section);
        Assert.NotNull(ast.Recipe);

        // REQUIRE contains one ItemsCheckNode with two items.
        Assert.Single(ast.Recipe!.Require);
        var itemsCheck = Assert.IsType<ItemsCheckNode>(ast.Recipe.Require[0]);
        Assert.Equal(2, itemsCheck.Items.Count);
        Assert.Equal("item_knife", itemsCheck.Items[0].Slug);
        Assert.Equal("item_log", itemsCheck.Items[1].Slug);

        // FAIL contains one MessagesNode with a single BAD_ITEMS entry.
        Assert.Single(ast.Recipe.Fail);
        var failMessages = Assert.IsType<MessagesNode>(ast.Recipe.Fail[0]);
        Assert.Equal("fail", failMessages.Target);
        Assert.True(failMessages.Entries.ContainsKey("BAD_ITEMS"));

        // SUCCESS contains a consume assignment and an output assignment.
        Assert.Equal(2, ast.Recipe.Success.Count);
        Assert.IsType<ConsumeAssignNode>(ast.Recipe.Success[0]);
        Assert.IsType<OutputAssignNode>(ast.Recipe.Success[1]);
    }

    [Fact]
    public void If_Body_Can_Use_Indented_Block_With_Or_Without_Colon()
    {
        // The parser accepts two body styles for `if`:
        //   if cond:             <- colon, indented block
        //       stmt
        //
        //   if cond              <- no colon, indented block
        //       stmt
        //
        // Both should produce the same IfNode body shape. The no-colon form
        // exists so older scripts don't break when newer ones use the
        // cleaner style. It's not a *feature*, it's a compatibility quirk.

        var withColon =
            "[server]\n[generic]\n" +
            "if true:\n" +
            "    x = 1\n";

        var withoutColon =
            "[server]\n[generic]\n" +
            "if true\n" +
            "    x = 1\n";

        var withColonAst = Parse(withColon);
        var withoutColonAst = Parse(withoutColon);

        Assert.Single(withColonAst.Statements);
        Assert.Single(withoutColonAst.Statements);
        Assert.IsType<IfNode>(withColonAst.Statements[0]);
        Assert.IsType<IfNode>(withoutColonAst.Statements[0]);

        var withBody = (IfNode)withColonAst.Statements[0];
        var withoutBody = (IfNode)withoutColonAst.Statements[0];
        Assert.Single(withBody.ThenBlock);
        Assert.Single(withoutBody.ThenBlock);
        Assert.IsType<AssignNode>(withBody.ThenBlock[0]);
        Assert.IsType<AssignNode>(withoutBody.ThenBlock[0]);
    }

    [Fact]
    public void Chance_With_Chance_Fail_Parses_As_Then_And_Else_Blocks()
    {
        // CHANCE N/M: is a probabilistic block. Inside a recipe's SUCCESS
        // block, `CHANCE 25/100:` picks between two consume arrays (the
        // "lucky" 10-capacitor path and the "unlucky" 20-capacitor path).
        //
        // CHANCE_FAIL: is the optional else-block. The shape is:
        //   CHANCE 25/100:
        //       <then statements>
        //   CHANCE_FAIL:
        //       <else statements>

        var src =
            "[server]\n[recipe]\n" +
            "\n" +
            "REQUIRE:\n" +
            "    ITEMS:\n" +
            "        item_a, 1\n" +
            "\n" +
            "SUCCESS:\n" +
            "    CHANCE 25/100:\n" +
            "        consume = [ item_a, 1 ]\n" +
            "    CHANCE_FAIL:\n" +
            "        consume = [ item_a, 2 ]\n";

        var ast = Parse(src);
        Assert.NotNull(ast.Recipe);

        // The CHANCE node is in the SUCCESS block.
        var chance = Assert.IsType<ChanceNode>(ast.Recipe!.Success[0]);
        Assert.Equal(25, chance.Numerator);
        Assert.Equal(100, chance.Denominator);
        Assert.Single(chance.ThenBlock);
        Assert.Single(chance.ElseBlock);
        Assert.IsType<ConsumeAssignNode>(chance.ThenBlock[0]);
        Assert.IsType<ConsumeAssignNode>(chance.ElseBlock[0]);
    }

    [Fact]
    public void Infix_Not_Parses_As_Inequality()
    {
        // `a NOT b` is sugar for `a != b`. It's used in task scripts like:
        //   IF following_player NOT target_id
        //       RUN follow_player(target_id)
        //
        // The parser routes this through ParsePostfix — after a primary
        // expression, NOT becomes a BinaryOpNode("!=", left, right). This
        // is different from `NOT a` at expression start, which is a
        // unary NotNode (short-circuit).

        var src =
            "[server]\n[generic]\n" +
            "a = 1\n" +
            "b = 2\n" +
            "if a NOT b:\n" +
            "    x = 1\n";

        var ast = Parse(src);
        var ifNode = (IfNode)ast.Statements[2];
        var cond = Assert.IsType<BinaryOpNode>(ifNode.Condition);
        Assert.Equal("!=", cond.Op);
    }

    // ------------------------------------------------------------------------
    // The killer test — parse every real script in the repo
    // ------------------------------------------------------------------------

    [Fact]
    public void Every_Real_Gfs_Script_Parses_Without_Throwing()
    {
        // If any single test in this file could be called the most valuable,
        // it's this one. It exercises the entire parser against ~30 real
        // scripts that collectively cover every construct the DSL supports:
        //
        //   [client] tooltips, [server] generics, [dialogue], [task], [recipe]
        //   Chat modes, tasks and RUN calls, CHANCE, CHANCE_FAIL, FAIL(NAME),
        //   nested IF, array conditionals, dictionary literals, backtick
        //   interpolation, per-tick tags, arg vars, system vars, comparisons,
        //   is-not-empty, entities, strings, escapes — every feature.
        //
        // If a future parser change breaks any script in the game, this test
        // fails with a filename and the exact error message, before a player
        // ever hits the broken feature in-game.
        //
        // Missing scripts dir → silent skip. An empty scripts dir → fail
        // (something is wrong with the checkout).

        var dir = FindGfsScriptsDir();
        if (dir == null) return;   // scripts not co-located with test assembly

        var files = Directory.GetFiles(dir, "*.gfs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            try
            {
                var tokens = new Lexer(text).Tokenize();
                var ast = new Parser(tokens).Parse();
                Assert.NotNull(ast);
            }
            catch (Exception e)
            {
                failures.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} of {files.Length} .gfs file(s) failed to parse:\n  "
                + string.Join("\n  ", failures));
        }
    }

    // ------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------

    private static SuiteNode Parse(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        return new Parser(tokens).Parse();
    }

    private static string? FindGfsScriptsDir()
    {
        // Walk up from the test assembly's bin directory until we find the
        // sibling MMOTest-Server project's scripts dir. Robust to Debug vs
        // Release build config and to target framework changes.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "MMOTest-Server", "data", "scripts", "gfs");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}