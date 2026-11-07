namespace MMOTest.Server.Gfs;

public enum TokenKind
{
    EOF, NEWLINE, INDENT, DEDENT,
    SECTION, TAG_OPEN, TAG_CLOSE,
    IDENT, STRING, NUMBER, BOOL,
    SYSTEM_VAR, ARG_VAR,
    ASSIGN, EQ, NEQ, LT, LTE, GT, GTE,
    LBRACKET, RBRACKET, LBRACE, RBRACE,
    LPAREN, RPAREN,
    COLON, COMMA, DOT,
    CHAT, LOCAL, PUBLIC, OVERHEAD, PRIVATE, PRIVATE_LOCAL,
    IF, ELSE, IS, NOT, EMPTY, NULL,
    TASK, RUN, SKIP, CONTINUE, CODE, ON,
    ANIM,
    TOPIC, SAY
}

public sealed record Token(TokenKind Kind, string Text, int Line, int Col)
{
    public override string ToString() => $"{Kind}('{Text}')@{Line}:{Col}";
}