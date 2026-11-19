# Baltic Drive — campaign concept

*Tom's idea (20 Nov 2026), written up by Sage. A concept, not a plan: nothing here is scheduled, and every open
question at the end is Tom's to answer. Noor: this is yours to build on.*

## The one-line pitch

**The world ended. Your friends are scattered across Europe. You have a car, a map and a full tank — for now.**

You wake up in post-apocalyptic Warsaw. You have to survive the city, get out of it, live off the land and the
ruins, and then drive — through Poland, around the Baltic Sea and across the North Sea — to find the people you care
about and bring them home, or at least bring them along.

## Why this story

* **It gives the drive a reason.** A map of Europe is beautiful; a person waiting at the end of it is a reason to
  cross it. Every region gets someone to find.
* **It's personal.** Some of the people you look for are inspired by Tom's real friends abroad (in Estonia,
  Denmark, Scotland and Wales). The rest are invented, so that every country on the route has someone to rescue.
  How much of the real friends goes into the game is decided with Tom — and with them (see *Open questions*).
* **It's what the game is already becoming.** Most of the systems the story needs exist (see *What we already
  have*).

## Tone

Beautifully post-apocalyptic. Not grey ruins and endless gloom: nature taking things back, quiet villages with
open shutters, storks on dead power poles, sunsets over empty motorways. Dangerous because of **people** — bandits,
the desperate, the organised — more than because of monsters. Moments of warmth: a found friend in the passenger
seat, a safehouse with a working stove, regional food as treasure (a whole sękacz!). Humour where it fits; Bad Ideas
Works is in the credits after all.

## The journey, in acts

### Act 1 — Warsaw: get out
* You start in the city with almost nothing. Warsaw is the hardest place: the most bandits, the fewest ways out,
  bridges held by gangs, the guarded rich districts ("Warszawska izba lordów").
* You learn the loop: scavenge, fix the car from wrecks, find a safehouse, follow **leads**.
* The first lead about a friend: a note, a radio message, a neighbour who saw them leave — they're alive, and far
  away.
* The act ends when you cross the city limits for the first time — Kampinos forest, or the open fields to the east —
  with somewhere to go.

### Act 2 — Poland
* Mazowsze, then region by region (the route in the region notes: north first, toward the sea). Villages, small
  towns, farm country; fuel and parts get harder to find, roads emptier.
* Shelters outside the city: farmhouses, fire stations, schools turned into camps. Some friendly, some not.
* Fictional people to find in Polish towns, each with a lead trail. Podlasie gets special care (Tom's holiday
  region): wooden villages, Białowieża, crime stories worthy of the local news.
* Gdańsk as a mini-boss; **Wrocław as the final boss of Poland**.

### Act 3 — The Baltic lap
* **Lithuania, Latvia, Estonia** along the Via Baltica. A friend in Estonia.
* The ferry from Tallinn to Helsinki — getting a car onto a ferry in a world that ended is a quest in itself.
* **Finland, Sweden, Norway**: lakes, empty forest roads, then fjords, tunnels and mountains.
* **Denmark** on the way back south. A friend in Denmark.

### Act 4 — Across the North Sea
* A ferry (or something improvised) to the **United Kingdom**: left-hand traffic, miles, roundabouts the other way.
* **Scotland**: Edinburgh and around — a friend there. The Highlands: single-track roads, lochs, very empty.
* **Wales**: a Welsh friend.
* The ending — home, wherever the crew decides home is now.

## The crew

* Each friend you find **joins the car**. The car fills up over the journey; by the end it's a full crew (and maybe
  a second car, from a fixed wreck).
* Each one brings something: one fixes engines faster, one is a good shot, one reads maps and finds shortcuts, one
  knows the local language. Losing someone should hurt.
* Banter on long drives — about the road, the places, each other. (Dialogue is a big job; start small.)

## Systems: what we already have

| The story needs | Baltic Drive already has |
|---|---|
| A real, drivable Europe | Mazowsze from OSM and GUGiK elevation; BadBuilding for buildings; region-by-region growth (no blank spots) |
| Danger from people | Bandits with pathfinding, guarded rich districts, ragdolls, the breath-hold shot |
| Surviving on foot | On-foot mode, lean, weapons, the death package (your pack and spare ammo) |
| Shelter | Safehouses at people's homes (hold K at a wall), random respawn homes 1.5–3 km away |
| A car to keep alive | Car parts (D67), the workshop view (hold F), wrecks to strip for parts (D69), bullet holes |
| Reasons to roam | Leads (D54): rumours that point at places |
| Atmosphere | Weather-driven mood and colour grading, grass, far city skyline, day and night |

## Systems: what's new

* **Story leads**: a chain of leads per person — clues that point to the next place (their flat, their work, a
  shelter, a border crossing) — on top of the random ones.
* **Crew**: companions in the car and on foot; their skills; their safety.
* **Borders and crossings**: checkpoints, closed bridges, ferries — the natural bosses between regions.
* **Fuel and range**: so a 3,000 km journey is a journey.
* **Regional flavour**: food as loot, regional road signs and buildings (BadBuilding region presets), languages.
* **Left-hand traffic** for the UK (one switch per region, but it touches the road builder and traffic rules).
* **Save game and progress** across regions.
* **Maybe**: zombies (undecided — see below), multiplayer (not for a long time).

## Open questions (Tom's)

1. **The real friends.** Names, places, personalities — or characters only *inspired* by them? Do they want to be in
   the game? (Ask them first; nothing personal goes into this public repository without their OK.)
2. **Zombies or not?** Sage's vote: no — people are scarier, and quieter fits the tone. Tom's call.
3. **Who are you?** A named character, or yourself?
4. **How did the world end?** Explained, hinted, or never said?
5. **The ending.** Bring everyone home to Warsaw, settle somewhere new, or keep driving?
6. **Order of the lap.** Clockwise (Baltics first, as the region notes say) or open: drive wherever the leads take
   you?
7. **Permadeath of companions?** Tense but heartbreaking.

---
*See also: `docs/HANDOFF.md` (state of play), `docs/DECISIONS.md` (the systems listed above), and BadBuilding's
`docs/COOKBOOK_REVIEW.md` (the buildings each region will need).*
