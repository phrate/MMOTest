using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs;

public sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private bool _lineStart = true;
    private bool _inTag = false;
    private int _lineIndent;
    private int _bracketDepth;                            // NEW
    private readonly List<int> _indentStack = new() { 0 };
    private readonly List<Token> _tokens = new();

    public Lexer(string src) { _src = src; }

    public List<Token> Tokenize()
    {
        while (_pos < _src.Length) LexOne();
        while (_indentStack.Count > 1)
        {
            _indentStack.RemoveAt(_indentStack.Count - 1);
            _tokens.Add(new Token(TokenKind.DEDENT, "", _line, _col));
        }
        _tokens.Add(new Token(TokenKind.EOF, "", _line, _col));
        return _tokens;
    }

    private void LexOne()
    {
        char c = Peek();

        // Normalize CRLF: skip the \r, let the \n case handle the newline.
        // Without this, a blank line in a CRLF file (\r\n) enters the indent-check
        // block with _lineIndent = 0, which fires a spurious DEDENT that closes the
        // enclosing block mid-way through.
        if (c == '\r' && Peek(1) == '\n') { Advance(); return; }

        if (c == '\n')
        {
            _inTag = false;
            if (_lineStart) { _lineIndent = 0; Advance(); _line++; _col = 1; return; }
            _tokens.Add(new Token(TokenKind.NEWLINE, "", _line, _col));
            Advance(); _line++; _col = 1;
            _lineStart = true; _lineIndent = 0;
            return;
        }

        if (_lineStart)
        {
            if (c == ' ') { _lineIndent += 1; Advance(); return; }
            if (c == '\t') { _lineIndent += 4; Advance(); return; }
            if (c == '#' || (c == '/' && Peek(1) == '/'))
            {
                while (_pos < _src.Length && Peek() != '\n') Advance();
                return;
            }

            // Only track indentation at the top level. Inside brackets/parens
            // (array literals, dict literals, multi-line calls) newlines and
            // tabs are formatting, not block structure.
            if (_bracketDepth == 0)
            {
                if (_lineIndent > _indentStack[^1])
                {
                    _indentStack.Add(_lineIndent);
                    _tokens.Add(new Token(TokenKind.INDENT, "", _line, _col));
                }
                else if (_lineIndent < _indentStack[^1])
                {
                    while (_indentStack.Count > 1 && _indentStack[^1] > _lineIndent)
                    {
                        _indentStack.RemoveAt(_indentStack.Count - 1);
                        _tokens.Add(new Token(TokenKind.DEDENT, "", _line, _col));
                    }
                    if (_indentStack[^1] != _lineIndent)
                        throw new Exception($"Inconsistent indent at {_line}:{_col} (got {_lineIndent}, stack [{string.Join(",", _indentStack)}])");
                }
            }

            _lineStart = false;

            if (c == '<')
            {
                Advance();
                _tokens.Add(new Token(TokenKind.TAG_OPEN, "<", _line, _col));
                _inTag = true;
                return;
            }
            if (c == '[' && LooksLikeSection())
            {
                LexSection();
                return;
            }
        }

        if (c == ' ' || c == '\t' || c == '\r') { Advance(); return; }
        if (c == '#' || (c == '/' && Peek(1) == '/'))
        {
            while (_pos < _src.Length && Peek() != '\n') Advance();
            return;
        }

        if (c == '"') { LexString(); return; }
        if (c == '$' && Peek(1) == '{') { LexSystemVar(); return; }
        if (c == '$' && Peek(1) == '"') { LexArgVar(); return; }
        if (c >= '0' && c <= '9') { LexNumber(); return; }
        if (IsIdentStart(c)) { LexIdent(); return; }

        switch (c)
        {
            case '=':
                if (Peek(1) == '=') { Advance(); Advance(); Add(TokenKind.EQ, "=="); return; }
                Advance(); Add(TokenKind.ASSIGN, "="); return;
            case '!':
                if (Peek(1) == '=') { Advance(); Advance(); Add(TokenKind.NEQ, "!="); return; }
                break;
            case '<':
                if (Peek(1) == '=') { Advance(); Advance(); Add(TokenKind.LTE, "<="); return; }
                Advance(); Add(TokenKind.LT, "<"); return;
            case '>':
                if (_inTag) { Advance(); _inTag = false; Add(TokenKind.TAG_CLOSE, ">"); return; }
                if (Peek(1) == '=') { Advance(); Advance(); Add(TokenKind.GTE, ">="); return; }
                Advance(); Add(TokenKind.GT, ">"); return;
            case '[': Advance(); _bracketDepth++; Add(TokenKind.LBRACKET, "["); return;
            case ']': Advance(); _bracketDepth = Math.Max(0, _bracketDepth - 1); Add(TokenKind.RBRACKET, "]"); return;
            case '{': Advance(); _bracketDepth++; Add(TokenKind.LBRACE, "{"); return;
            case '}': Advance(); _bracketDepth = Math.Max(0, _bracketDepth - 1); Add(TokenKind.RBRACE, "}"); return;
            case '(': Advance(); _bracketDepth++; Add(TokenKind.LPAREN, "("); return;
            case ')': Advance(); _bracketDepth = Math.Max(0, _bracketDepth - 1); Add(TokenKind.RPAREN, ")"); return;
            case ':': Advance(); Add(TokenKind.COLON, ":"); return;
            case ',': Advance(); Add(TokenKind.COMMA, ","); return;
            case '.': Advance(); Add(TokenKind.DOT, "."); return;
        }

        throw new Exception($"Unexpected char '{c}' at {_line}:{_col}");
    }

    private bool LooksLikeSection()
    {
        int i = _pos + 1;
        while (i < _src.Length && _src[i] != '\n' && _src[i] != ']') i++;
        return i < _src.Length && _src[i] == ']';
    }

    private void LexSection()
    {
        int line = _line, col = _col;
        Advance();
        int start = _pos;
        while (_pos < _src.Length && Peek() != ']' && Peek() != '\n') Advance();
        if (Peek() != ']') throw new Exception($"Unterminated section at {line}:{col}");
        string name = _src.Substring(start, _pos - start);
        Advance();
        _tokens.Add(new Token(TokenKind.SECTION, name, line, col));
    }

    private void LexString()
    {
        Advance();
        var sb = new System.Text.StringBuilder();
        while (_pos < _src.Length)
        {
            char ch = Peek();
            if (ch == '\\')
            {
                Advance();
                char next = Peek();
                sb.Append(next switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => next });
                Advance();
                continue;
            }
            if (ch == '"') { Advance(); break; }
            sb.Append(ch); Advance();
        }
        _tokens.Add(new Token(TokenKind.STRING, sb.ToString(), _line, _col));
    }

    private void LexSystemVar()
    {
        Advance(); Advance();
        int start = _pos;
        while (_pos < _src.Length && Peek() != '}') Advance();
        string name = _src.Substring(start, _pos - start);
        if (Peek() == '}') Advance();
        _tokens.Add(new Token(TokenKind.SYSTEM_VAR, name, _line, _col));
    }

    private void LexArgVar()
    {
        Advance(); Advance();
        var sb = new System.Text.StringBuilder();
        while (_pos < _src.Length && Peek() != '"') { sb.Append(Peek()); Advance(); }
        if (Peek() == '"') Advance();
        _tokens.Add(new Token(TokenKind.ARG_VAR, sb.ToString(), _line, _col));
    }

    private void LexNumber()
    {
        int start = _pos;
        while (_pos < _src.Length && (char.IsDigit(Peek()) || Peek() == '.')) Advance();
        _tokens.Add(new Token(TokenKind.NUMBER, _src.Substring(start, _pos - start), _line, _col));
    }

    private void LexIdent()
    {
        int start = _pos;
        while (_pos < _src.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '_')) Advance();
        string text = _src.Substring(start, _pos - start);
        var kind = text.ToLowerInvariant() switch
        {
            "true" or "false" => TokenKind.BOOL,
            "chat" => TokenKind.CHAT,
            "local" => TokenKind.LOCAL,
            "public" => TokenKind.PUBLIC,
            "overhead" => TokenKind.OVERHEAD,
            "private" => TokenKind.PRIVATE,
            "private_local" => TokenKind.PRIVATE_LOCAL,
            "if" => TokenKind.IF,
            "else" => TokenKind.ELSE,
            "is" => TokenKind.IS,
            "not" => TokenKind.NOT,
            "empty" => TokenKind.EMPTY,
            "null" => TokenKind.NULL,
            "task" => TokenKind.TASK,
            "run" => TokenKind.RUN,
            "skip" => TokenKind.SKIP,
            "continue" => TokenKind.CONTINUE,
            _ => TokenKind.IDENT,
        };
        _tokens.Add(new Token(kind, text, _line, _col));
    }

    private void Add(TokenKind kind, string text) => _tokens.Add(new Token(kind, text, _line, _col));

    private char Peek(int offset = 0) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';
    private void Advance() { _pos++; _col++; }
    private bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
}