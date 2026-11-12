# Working on Baltic Drive

* Owner: Tom (BadSad). The assistant is called Sage. Tom does not use git from the command line; commit to `main` and
  push (`github.com/badsadcode/BalticDrive`). Never rewrite pushed history.
* Reference projects, read-only, cloned beside this folder: `../DeadMiles` (branch `claude/youthful-noether-4wxif0`,
  its HANDOFF.md) and `../Thornwake_Rewrite`. Copy ideas and code in; never change them.
* Log every decision with its reason in `docs/DECISIONS.md` (next D number). State of play: `docs/HANDOFF.md`.
* Godot 4.7.2 .NET is not on PATH: `F:\Codex\Project9a\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
  Build C# with `dotnet build BalticDrive.csproj`; check in the real game with the self-tests (README → Tests) and look at
  the screenshots before saying something works. Check shaders compile (look for `SHADER ERROR` in the log).
* Large data never goes into git: `data/` (sources, elevation, asset drops — Tom drops zips in `data/inbox`), `Assets/Synty/` (licensed, never public) and `map/`
  (built packs) are ignored. Only assets actually used are copied into `Assets/`, with their licence.
* Map data © OpenStreetMap contributors (ODbL): the credit must stay visible in the game.
* Ask Tom before significant artistic decisions (looks, palettes); technical fixes need no pause.
