# UI art for Baltic Drive: brief for Codex

Tom asks Codex (image generation) for the game's interface pieces. Paste everything under "Prompt" to Codex. When the
zip comes back, drop it in `data/inbox`; Sage wires it in (Godot `StyleBoxTexture` / `NinePatchRect` with the margins
from the manifest), and anything not used stays out of `Assets/` (CLAUDE.md).

---

## Prompt

You are making the user-interface art for **Baltic Drive**, a survival driving game set in a quiet, overgrown,
post-apocalyptic Poland (post-communist blocks of flats, wooden villages, rusted cars, nature taking things back:
melancholic but beautiful, not gory). The interface should feel like **worn, practical objects from that world**:
scuffed dark-grey enamel and painted sheet metal, faded paper labels, a little rust at the edges, stencilled lettering
style, warm off-white and muted amber accents. Readable first, atmospheric second. **No text baked into any image**
(the game writes all text itself, in any language). No logos, no real brands.

### Technical rules (important)
* PNG, 32-bit with transparency, sRGB. Drawn at **2× size** (the game scales down; sizes below are the 2× size).
* **Nine-slice ready**: every frame, panel, bar and button must stretch without distortion. Keep all decoration in
  the corners and edges; the middle must be plain (flat or a subtle even texture) so it can stretch. Each nine-slice
  image lists its margins (left, top, right, bottom, in pixels) in the manifest.
* Flat lighting from the top; no drop shadows outside the image bounds (the game adds them).
* Consistent palette and wear level across all pieces, as one set.
* Name files exactly as listed (lower case, underscores). Put them in folders as listed.
* Zip everything as **`baltic_drive_ui_v1.zip`**, with a **`manifest.json`** at the root listing every file:
  `{"file": "...", "size": [w, h], "nine_slice": [l, t, r, b] or null, "notes": "..."}`.
* Where a piece has states, make one file per state: `_normal`, `_hover`, `_pressed`, `_disabled`.

### What to make

**1. `bars/` — status bars (health, hunger, thirst, and more later)**
* `bar_frame.png` — 512×48, the empty housing of a gauge bar (a recessed slot in a metal strip); nine-slice.
* `bar_fill.png` — 512×36, a plain light fill (white-ish, with a faint worn texture) that the game tints per stat
  (red health, amber food, blue water); nine-slice.
* `bar_fill_glow.png` — 512×36, the same fill with a soft inner glow, for a bar that's low and pulsing.
* `bar_icons/` — 64×64 each, simple monochrome white pictograms on transparent background (the game tints them):
  `icon_health.png` (a heart or cross), `icon_food.png` (a tin can), `icon_water.png` (a drop or bottle),
  `icon_weight.png` (a backpack), `icon_fuel.png` (a jerrycan), `icon_temperature.png` (a thermometer),
  `icon_fatigue.png` (a crescent moon).

**2. `panels/` — windows and containers**
* `window_frame.png` — 512×512, a large window (inventory, workshop, map): a dark painted-metal panel with riveted
  edges; nine-slice with a plain middle.
* `window_title_bar.png` — 512×64, the strip at the top of a window for its title; nine-slice.
* `container_frame.png` — 512×512, a lighter variant for "what's inside" (a cupboard, a car's trunk, a crate), like a
  worn wooden or cardboard crate edge; nine-slice.
* `slot_empty.png` — 96×96, one inventory slot (a shallow recessed square); nine-slice.
* `slot_hover.png`, `slot_selected.png` — 96×96, the same slot highlighted and selected.
* `row_normal.png`, `row_hover.png`, `row_selected.png` — 512×56, one list row (an item in a list); nine-slice.
* `tooltip.png` — 256×128, a small paper-label tooltip; nine-slice.
* `note_toast.png` — 512×96, a message strip that appears at the bottom of the screen ("It won't start"), like a
  strip of masking tape or a paper note; nine-slice.
* `divider.png` — 512×8, a thin horizontal separator line.

**3. `buttons/` — empty buttons (the game writes the label)**
* `button_*` — 256×72, a rectangular button with room for one word or two, states `_normal`, `_hover`, `_pressed`,
  `_disabled`; nine-slice.
* `button_small_*` — 128×56, the same, small (for "Eat", "Use"); four states; nine-slice.
* `button_icon_*` — 72×72, a square button for an icon (close, settings); four states.
* `icon_close.png`, `icon_settings.png`, `icon_back.png`, `icon_take_all.png` — 48×48 white pictograms.

**4. `controls/` — for the settings (sandbox) menu**
* `slider_track.png` — 512×24, a slider's empty groove; nine-slice. `slider_fill.png` — 512×24, the filled part;
  nine-slice. `slider_knob_normal.png`, `slider_knob_hover.png` — 40×48, the handle.
* `checkbox_off.png`, `checkbox_on.png` — 48×48 (a hand-ticked box).
* `dropdown_*` — 384×64 with an arrow on the right, states `_normal`, `_hover`; nine-slice.
* `tab_active.png`, `tab_inactive.png` — 256×64, a tab; nine-slice.
* `scrollbar_track.png` — 24×256, `scrollbar_grabber.png` — 24×96; nine-slice.

**5. `hud/` — on-screen while playing**
* `minimap_frame.png` — 512×512, a round frame for the circular minimap (like a car's dashboard instrument bezel),
  transparent centre, with a small north tick at the top.
* `compass_strip.png` — 1024×48, a horizontal frame for a compass and road strip at the top of the screen; nine-slice.
* `prompt_key.png` — 64×64, a keycap shape for showing "F", "E", "Tab" (the game writes the letter on it); nine-slice.
* `crosshair_dot.png` — 32×32, a small soft dot. `crosshair_cross.png` — 64×64, a thin cross.
* `damage_vignette.png` — 1024×1024, a soft dark-red edge vignette, transparent middle.
* `speedometer_frame.png` — 384×384, a round gauge bezel with an empty face (the game draws the needle and numbers).

**6. `cursor/`**
* `cursor_pointer.png` — 48×48, `cursor_grab.png` — 48×48 (dragging an item), hotspot at the top-left tip.

**7. `screens/`**
* `death_overlay.png` — 1920×1080, a dark, slightly desaturating overlay with a soft vignette, transparent-ish middle.
* `loading_frame.png` — 1024×128, a frame for a loading bar; nine-slice.

Keep everything as one coherent family. If you have to choose, make fewer pieces that match rather than many that
don't. Include `manifest.json` and nothing else at the root besides the folders.
