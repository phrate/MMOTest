# Data/Texts

Every player-visible string lives here, in JSON, and is looked up by key through the `Texts` autoload:

	Texts.get_text("account.login.welcome", {"username": "Bob", "player_id": 1, ...})

## How keys are built

	folder names + file name (minus "_texts") + nested JSON keys, joined with dots

	Data/Texts/account/login_texts.json  ->  { "error": { "invalid_credentials": "..." } }
	key: account.login.error.invalid_credentials

So a file never repeats its own prefix. Moving a file to another folder changes its keys.
Placeholders use `{name}` and are filled from the params dictionary.
Every `.json` file under this folder is loaded automatically at startup (recursively).

## Conventions

- Files are named `<topic>_texts.json`, grouped in a folder per area.
- Use nested objects for sub-groups: `validation`, `error`, `button`, `label`, ...
- Server errors arrive as keys (e.g. `account.login.error.invalid_credentials`) plus params. The server
  never sends player-facing prose, so every key it can send must exist here.
- `account/dev_texts.json` belongs to the TEMP-DEV auto-login. Delete it along with that feature.

## Planned layout as the game grows

	account/   login, register, password reset, ...
	network/   connection and server status
	ui/        shared widgets, menus, tooltips
	game/      world, zones, travel
	chat/      channels, system messages, slash-command help
	combat/    damage and status messages
	items/     names, descriptions, tooltips
	npcs/      names and dialogue
	quests/    titles, objectives, journal entries

## Exporting

Add `*.json` (or `Data/Texts/*`) to the export preset's "Filters to export non-resource files",
otherwise the texts will be missing from exported builds.
