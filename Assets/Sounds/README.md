# Sounds

Drop a recording here named after a sound (`.ogg` or `.wav`) and the game plays it instead of the generated
placeholder (D53, `Game/Audio/SoundBank.cs`). Names:

| Name | What |
|---|---|
| `shot_mosin`, `shot_ak`, `shot_pistol` | a shot near you (crack and thump) |
| `shot_far_mosin`, `shot_far_ak`, `shot_far_pistol` | the same shot from far away (a low boom, no crack) |
| `echo` | a shot's slap off a wall (played from each wall, late by the distance) |
| `roll` | the long roll of a shot over open ground |
| `hit_hard`, `hit_soft` | a bullet hitting something |
| `city`, `wind`, `tyres` | loops |
| `bird0` … `bird5` | short bird phrases |

Only sounds you are allowed to publish go in git (the repository is public).

## Credits

| File | Source |
|---|---|
| `shot_mosin.wav` | Tom (Bad Ideas Works), made with Suno; trimmed to start at the shot |
| `shot_far_mosin.wav` | derived from `shot_mosin.wav`: mono, low-passed at ~500 Hz, longer fade |
| `shot_ak.wav`, `shot_pistol.wav` | derived from `shot_mosin.wav` until real ones exist: pitched up (12 %, 35 %), thinner low end, cut shorter (`tools/derive_shots.py`) |
| `shot_far_ak.wav`, `shot_far_pistol.wav` | derived from those, as for the Mosin |

`python tools/derive_shots.py` rebuilds the derived files from `shot_mosin.wav`. To use a real recording of a gun,
put it in place of the derived file (and remove that gun from the script, or it will be overwritten).

