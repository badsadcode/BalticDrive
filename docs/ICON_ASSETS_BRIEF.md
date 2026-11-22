# Item icons and the crafting notebook: brief for Codex

Tom asks Codex (image generation) for the inventory's item icons and the art of the crafting notebook (D117: the
inventory is "the rucksack", a grid of item tiles; crafting is "zeszyt w kratkę", a survivor's squared school
notebook). Paste everything under "Prompt" to Codex. When the zip comes back, drop it in `data/inbox`; Sage wires it
in. The file names are the game's item ids, so new items later just need new icons with their ids.

---

## Prompt

You are making the **item icons** and the **crafting notebook art** for **Baltic Drive**, a survival driving game set in
a quiet, overgrown, post-apocalyptic Poland: post-communist blocks of flats, wooden villages, rusted cars, Polish
kitchens and garages, nature taking things back. Melancholic but warm, not gory. The game already has a UI set
(scuffed dark-grey enamel, painted sheet metal, faded paper labels, muted amber accents); these must sit well on it.

### Style of the item icons
* One object per icon, **three-quarter view**, centred, filling about 80 % of the square.
* **Hand-painted, slightly stylised, low-detail**, like a painted inventory icon: clear silhouette first, readable at
  48 px. Soft painterly shading, light from the top left, a thin dark outline so it reads on any background.
* Everyday **Polish / Central European** objects from the 1990s–2010s: enamel pots, glass jars with metal lids, paper
  bags, plain tins. Worn, used, a bit dented, never brand new.
* **No text, no logos, no real brands** anywhere (labels are blank or have abstract stripes). No drop shadow outside
  the object (the game adds its own).
* One consistent style, palette and light across the whole set: they must look like one family.

### Technical rules (important)
* PNG, 32-bit with **transparent background**, sRGB, **256×256** (the game scales them down).
* Name files exactly as listed (lower case, underscores), in the folders listed.
* Zip everything as **`baltic_drive_icons_v1.zip`**, with a **`manifest.json`** at the root listing every file:
  `{"file": "...", "size": [w, h], "nine_slice": [l, t, r, b] or null, "notes": "..."}`.

### What to make

**1. `items/`: one icon per item (78 icons).** The file name is the item's id; the line says what to draw.

*food* (25):
* `items/beans.png` — tin of beans: a plain unlabelled tin can with a paper wrap
* `items/sprats.png` — tin of sprats: a flat oval tin with a ring pull
* `items/luncheon_meat.png` — tin of luncheon meat: a squat rectangular tin with a key
* `items/pate.png` — tin of pâté: a tiny round tin
* `items/crackers.png` — packet of crackers: packet of crackers
* `items/wafer.png` — chocolate wafer: chocolate wafer
* `items/kabanos.png` — pack of kabanos: a bundle of thin dried sausages
* `items/pickles.png` — jar of pickles: a glass jar of gherkins with a metal lid
* `items/jam.png` — jar of jam: a jar of dark red jam, cloth over the lid
* `items/rusks.png` — bag of rusks: a paper bag of rusks (sucharki)
* `items/instant_soup.png` — instant soup: a small foil sachet
* `items/rice.png` — bag of rice: a paper bag of rice
* `items/potatoes.png` — bag of potatoes: a mesh sack of potatoes
* `items/onion.png` — onion: one golden onion
* `items/cooked_rice.png` — pot of cooked rice: a dented pot with steaming rice
* `items/hot_soup.png` — hot soup: a steaming enamel bowl
* `items/kabanos_rusks.png` — kabanos on rusks: kabanos laid on two rusks
* `items/kartacze.png` — plate of kartacze: two large potato dumplings on a plate with a bit of fried onion
* `items/placki.png` — potato pancake: a stack of potato pancakes
* `items/sauerkraut.png` — jar of sauerkraut: a big glass jar of pale sauerkraut
* `items/cabbage.png` — head of cabbage: a green head of cabbage
* `items/dried_mushrooms.png` — bag of dried mushrooms: dried mushroom slices on a string
* `items/prunes.png` — bag of prunes: a small paper bag of prunes
* `items/kielbasa.png` — kiełbasa: a horseshoe of smoked sausage
* `items/bigos.png` — pot of bigos: a dented enamel pot of dark stewed cabbage and meat

*drink* (8):
* `items/water.png` — bottle of water: a 1.5 l plastic bottle of water
* `items/mineral_water.png` — small bottle of mineral water: a small glass bottle of mineral water
* `items/juice.png` — carton of juice: a carton of juice
* `items/cola.png` — can of cola: a plain red drinks can, no brand
* `items/compote.png` — jar of compote: a tall jar of fruit compote
* `items/vodka.png` — bottle of vodka: a clear bottle, plain label, no brand
* `items/hot_tea.png` — mug of tea: a glass of tea in a metal holder (koszyczek)
* `items/dirty_water.png` — bottle of river water: a bottle of cloudy greenish water

*medical* (6):
* `items/medkit.png` — first aid kit: a small first aid pouch
* `items/bandage.png` — bandage: a rolled white bandage
* `items/painkillers.png` — box of painkillers: a small blister-pack box
* `items/disinfectant.png` — bottle of disinfectant: a small brown glass bottle
* `items/plasters.png` — box of plasters: a tin of plasters
* `items/makeshift_bandage.png` — makeshift bandage: a strip of torn fabric, knotted

*tool* (12):
* `items/screwdriver.png` — screwdriver: a screwdriver
* `items/hammer.png` — hammer: a claw hammer
* `items/kitchen_knife.png` — kitchen knife: a kitchen knife
* `items/scissors.png` — pair of scissors: a pair of scissors
* `items/can_opener.png` — can opener: an old manual can opener
* `items/torch.png` — torch: a flashlight
* `items/lighter.png` — lighter: a disposable lighter
* `items/matches.png` — box of matches: a small box of matches
* `items/saucepan.png` — saucepan: a dented enamel saucepan with a handle
* `items/molotov.png` — molotov cocktail: a bottle with a rag in its neck
* `items/axe.png` — axe: a short wood axe
* `items/camping_stove.png` — camping gas stove: a small camping gas stove

*material* (13):
* `items/rag.png` — rag: a torn rag
* `items/duct_tape.png` — roll of duct tape: a roll of grey duct tape
* `items/nails.png` — box of nails: a small cardboard box of nails
* `items/batteries.png` — pair of batteries: two round batteries
* `items/candle.png` — candle: a white candle
* `items/rope.png` — length of rope: a coil of rope
* `items/sheet.png` — bed sheet: a folded bed sheet
* `items/towel.png` — towel: a folded towel
* `items/thread.png` — spool of thread: a spool of thread with a needle
* `items/empty_bottle.png` — empty bottle: an empty plastic bottle
* `items/tea.png` — box of tea: a small box of tea
* `items/firewood.png` — armful of firewood: a few split logs
* `items/gas_canister.png` — gas canister: a small screw-on gas cartridge

*fuel* (1):
* `items/petrol_can.png` — can of petrol: a red jerrycan

*book* (3):
* `items/book_kuchnia_podlaska.png` — Kuchnia podlaska (a cookbook): a cookbook with a folk-pattern cover (no readable title)
* `items/book_pierwsza_pomoc.png` — Pierwsza pomoc (a first-aid booklet): a thin booklet with a red cross
* `items/book_poradnik_przetrwania.png` — Poradnik przetrwania (a survival handbook): a worn khaki field handbook

*junk* (10):
* `items/newspaper.png` — old newspaper: a folded old newspaper (no readable text)
* `items/book.png` — book: a worn hardback book
* `items/toilet_paper.png` — roll of toilet paper: a roll of grey toilet paper
* `items/soap.png` — bar of soap: a bar of soap
* `items/toothbrush.png` — toothbrush: a toothbrush
* `items/coffee_mug.png` — coffee mug: an enamel mug
* `items/photo_album.png` — photo album: a photo album with a few loose photos
* `items/radio.png` — transistor radio: a small transistor radio with an antenna
* `items/coins.png` — handful of coins: a few coins
* `items/empty_petrol_can.png` — empty petrol can: an empty red jerrycan, dented

**2. `categories/`: rucksack tab icons**, 128×128, simple **monochrome white** pictograms on transparent (the game
tints them): `food.png` (a tin and a spoon), `drink.png` (a bottle), `medical.png` (a cross on a pouch), `tool.png`
(a wrench), `material.png` (a roll of tape and a rag), `fuel.png` (a jerrycan), `book.png` (a book), `junk.png` (a
cardboard box), `all.png` (a rucksack).

**3. `freshness/`: small badges**, 64×64, monochrome white on transparent (tinted by the game): `fresh.png` (a leaf),
`stale.png` (a wilted leaf), `rotten.png` (a fly).

**4. `notebook/`: the crafting notebook ("zeszyt w kratkę")**, a Polish school notebook with squared paper:
* `paper_squared.png`: 512×512, **seamless tileable** off-white squared paper (pale blue-grey 5 mm grid, about 32 px
  per square at this size), slightly yellowed, very subtle stains. Must tile with no visible seam.
* `page_left.png` and `page_right.png`: 1024×1400, the two pages of an open notebook seen from above: squared paper,
  a thin **red margin line** near the outer edge, worn corners, the left page's inner edge darkening into the spine;
  nine-slice (keep detail at the edges, plain squared middle). List margins in the manifest.
* `spine.png`: 96×1400, the middle of the open notebook with **two staples** (zszywki), nine-slice vertically.
* `cover_edge.png`: 1024×64, the edge of a worn soft cover peeking out under the pages (faded blue or green),
  nine-slice horizontally.
* `tab_*.png`: 160×72, coloured paper tab stickers stuck to the page edge for recipe categories (one each, plain, no
  text; the game writes the word): `tab_cooking.png` (yellow), `tab_medicine.png` (red), `tab_materials.png` (grey),
  `tab_fire.png` (orange), `tab_bigos.png` (green), `tab_trouble.png` (black).
* `pencil_tick.png`: 64×64, a hand-drawn pencil tick ✓; `pencil_cross.png`: 64×64, a pencil cross ✗;
  `pencil_question.png`: 64×64, a pencil question mark.
* `pencil_strike.png`: 512×24, a hand-drawn pencil line for crossing out a word; nine-slice horizontally.
* `pencil_underline.png`: 512×24, a wobbly pencil underline; nine-slice horizontally.
* `pencil_circle.png`: 256×96, a hand-drawn pencil ellipse around a word (for "you can make this"); nine-slice.
* `sticky_note.png`: 320×320, a small square sticky note, slightly curled corner (for a recipe's flavour line);
  nine-slice.
* `stamp_known.png`: 192×192, a faded round ink stamp with no text, just a mark (for "learnt from a book").
* `coffee_ring.png`: 256×256, a faint coffee-cup ring stain (decoration).
