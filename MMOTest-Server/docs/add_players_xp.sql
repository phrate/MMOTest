-- Back up the database before running this migration.
-- Run once against the MMOTest database while the server is stopped.
-- If the updated server has already added players.xp_id, skip the ALTER TABLE statement.
-- XP columns are stored as tenths by the server after its one-time precision migration.

PRAGMA foreign_keys = OFF;

BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS players_xp (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    hitpoints   INTEGER NOT NULL DEFAULT 0 CHECK (hitpoints   >= 0),
    accuracy    INTEGER NOT NULL DEFAULT 0 CHECK (accuracy    >= 0),
    strength    INTEGER NOT NULL DEFAULT 0 CHECK (strength    >= 0),
    defence     INTEGER NOT NULL DEFAULT 0 CHECK (defence     >= 0),
    ether       INTEGER NOT NULL DEFAULT 0 CHECK (ether       >= 0),
    woodcutting INTEGER NOT NULL DEFAULT 0 CHECK (woodcutting >= 0),
    mining      INTEGER NOT NULL DEFAULT 0 CHECK (mining      >= 0),
    fishing     INTEGER NOT NULL DEFAULT 0 CHECK (fishing     >= 0)
);

ALTER TABLE players
    ADD COLUMN xp_id INTEGER REFERENCES players_xp(id) ON DELETE SET NULL;

CREATE TEMP TABLE players_xp_migration_map (
    player_id INTEGER PRIMARY KEY,
    xp_id     INTEGER NOT NULL UNIQUE
);

INSERT INTO players_xp_migration_map (player_id, xp_id)
SELECT p.id,
       (SELECT COALESCE(MAX(id), 0) FROM players_xp)
           + ROW_NUMBER() OVER (ORDER BY p.id)
FROM players AS p
WHERE p.xp_id IS NULL;

INSERT INTO players_xp (id)
SELECT xp_id
FROM players_xp_migration_map
ORDER BY xp_id;

UPDATE players
SET xp_id = (
    SELECT migration.xp_id
    FROM players_xp_migration_map AS migration
    WHERE migration.player_id = players.id
)
WHERE xp_id IS NULL
  AND EXISTS (
      SELECT 1
      FROM players_xp_migration_map AS migration
      WHERE migration.player_id = players.id
  );

DROP TABLE players_xp_migration_map;

COMMIT;

PRAGMA foreign_keys = ON;
PRAGMA foreign_key_check;