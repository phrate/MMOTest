using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record PlayerRecord(long Id, string Username, string PasswordHash, bool IsAdmin);

/// <summary>
/// Thin SQLite wrapper. Keep all SQL in here so the backend can be swapped later
/// (e.g. PostgreSQL) without touching networking code.
///
/// Every public method except Initialize() is async and runs on the thread pool, so a slow or locked
/// database never freezes the game loop. Each call opens its own connection, which keeps them thread-safe.
/// Never call Godot APIs from inside these methods; they don't run on the main thread.
/// </summary>
public class Database
{
    private const int SqliteConstraintError = 19;

    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Synchronous on purpose: runs once at startup, before the server accepts anyone.</summary>
    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS players (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                is_admin      INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1)),
                login_count   INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                last_login    TEXT
            );";
        cmd.ExecuteNonQuery();

        // Upgrades databases created before this column existed.
        EnsureColumn(conn, "players", "is_admin", "INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1))");
    }

    /// <summary>Adds a column if missing. Only ever call with hard-coded names, never user input.</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == column) return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    /// <summary>Synchronous: only used once at startup for the log line.</summary>
    public string GetSqliteVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return (string)cmd.ExecuteScalar();
    }

    // ---------- Async API (thread pool) ----------

    public Task<string> GetSqliteVersionAsync() => Task.Run(GetSqliteVersion);

    public Task<long> CountPlayersAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM players;";
        return (long)cmd.ExecuteScalar();
    });

    /// <summary>Created is false if the username is already taken (case-insensitive).</summary>
    public Task<(bool Created, long Id)> TryCreatePlayerAsync(string username, string passwordHash) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO players (username, password_hash)
            VALUES ($u, $h)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        try
        {
            return (true, (long)cmd.ExecuteScalar());
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            return (false, 0L);
        }
    });

    /// <summary>
    /// Returns null if no such player. The canonical username is as stored.
    /// is_admin is read-only from the server's point of view: no code path writes it,
    /// except the TEMP-DEV TryCreateDevAdminAsync below. Otherwise set it manually
    /// in the database (e.g. DB Browser for SQLite).
    /// </summary>
    public Task<PlayerRecord> GetPlayerByUsernameAsync(string username) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    /// <summary>Call after a successful login. Returns the new login count.</summary>
    public Task<long> RecordLoginAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE players
            SET login_count = login_count + 1, last_login = datetime('now')
            WHERE id = $id
            RETURNING login_count;";
        cmd.Parameters.AddWithValue("$id", playerId);
        return (long)cmd.ExecuteScalar();
    });

    /// <summary>TEMP-DEV: first account with is_admin = 1, or null. Read-only; used by dev auto-login.</summary>
    public Task<PlayerRecord> GetFirstAdminAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE is_admin = 1 ORDER BY id LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    /// <summary>
    /// TEMP-DEV: creates an account with is_admin = 1. This is the ONLY place code writes is_admin.
    /// Used by dev auto-login; delete it together with Main.DevAutoLogin.cs.
    /// Returns false if the username is already taken.
    /// </summary>
    public Task<bool> TryCreateDevAdminAsync(string username, string passwordHash) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO players (username, password_hash, is_admin) VALUES ($u, $h, 1);";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            return false;
        }
    });
}
