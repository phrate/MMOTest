using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MMOTest.Server.Migrations;

namespace MMOTest.Server;

public class Database
{
    private readonly string _connectionString;

    public ItemRepository Items { get; }
    public LootRepository Loot { get; }
    public RecipeRepository Recipes { get; }
    public NpcRepository Npcs { get; }
    public BankRepository Bank { get; }
    public PlayerRepository Players { get; }
    public PlayerStatsRepository Stats { get; }
    public InventoryRepository Inventory { get; }

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Items = new ItemRepository(Open);
        Loot = new LootRepository(Open);
        Recipes = new RecipeRepository(Open);
        Npcs = new NpcRepository(Open);
        Bank = new BankRepository(Open);
        Players = new PlayerRepository(Open);
        Stats = new PlayerStatsRepository(Open);
        Inventory = new InventoryRepository(Open);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 1000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();

        MigrateLootToLootLists.Apply(conn);
        MigratePlayersXpToPlayerXp.Apply(conn);
        MigrateCombatLevelsToLevelsCombat.Apply(conn);
        MigrateSkillLevelsToLevelsSkill.Apply(conn);
        MigrateCreditsLootToLootListsCredits.Apply(conn);
        DropMaxStackColumn.Apply(conn);
        MigratePlayersFKColumnNames.Apply(conn);
        MigrateNpcsFKColumnName.Apply(conn);
        MigratePlayersDropBankId.Apply(conn);

        Exec(conn, "PRAGMA journal_mode = WAL;");
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        SchemaCreator.CreateAll(conn);
        CertGenerator.Ensure(conn);
        MigratePlayerXpToTenths.Apply(conn);

        Exec(conn, "DROP TABLE IF EXISTS player_bank;");
        Exec(conn, "PRAGMA foreign_keys = ON;");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string GetSqliteVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return (string)cmd.ExecuteScalar()!;
    }

    public Task<string> GetSqliteVersionAsync() => Task.Run(GetSqliteVersion);
}