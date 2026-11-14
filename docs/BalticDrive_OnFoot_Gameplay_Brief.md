# Baltic Drive — On-Foot Gameplay Brief

*A direction for Sage, not a giant implementation checklist.*

## The idea

Baltic Drive already has an enormous real-world-derived map of Warsaw and Mazowsze, roads, terrain, buildings, vegetation, and drivable vehicles. Let's make it enjoyable **on foot**, too, with a **small survival-and-combat sandbox**. The emphasis is on exploring familiar places, finding supplies, and having genuinely fun firefights. **Keep it contained.** This is an additional way to play Baltic Drive, not an entirely new game.

## Player and camera

- Support **both first-person and third-person** movement, with an easy toggle between them.
- **Aiming always moves into first-person aiming**, regardless of the currently selected camera. Use proper iron sights, sights, or scope views as appropriate. On releasing aim, return to the player's selected first- or third-person view.
- Responsive, polished movement: walk, sprint, crouch, jump, sensible interactions; no overly complicated movement or tactical simulation.
- Make getting in and out of the existing drivable cars feel natural. Cars remain an important part of the game.

## Gunplay: the part that must feel great

**Reference feel:** *Ghost Recon Wildlands* and *Breakpoint*, with satisfying weapon handling, animations, impacts, and readable encounters. More grounded than Quake; much more accessible than ARMA. Fun first, not a mil-sim.

- Weapons have different identities: an old **Mosin-Nagant-style bolt-action rifle** (powerful report, decisive kick, satisfying bolt cycle), a **handgun** (quick and practical), and an **AK-style assault rifle** (controllable bursts, distinct automatic recoil). Other weapons can be rare finds later.
- Responsive shooting, appealing recoil and recovery, clean aim transitions, readable muzzle effects, ejected casings, material-specific bullet impacts, and satisfying hit reactions.
- Convincing but approachable ballistics: some travel time and drop at range; no complicated wind tables or hardcore maintenance systems.
- Avoid bullet-sponge enemies, unrealistically perfect enemy aim, and constant combat. A few meaningful encounters beat hundreds of bots.
- **Sound matters enormously**: powerful shots, mechanical clacks, distant reports, and different acoustic character between apartment courtyards, streets, and forests. For now, **NoorSounds bleeps and bloops are fine as placeholders**. Keep weapon audio easily replaceable/layerable later (shot, mechanism, tail/echo, impact).

## Small survival loop

- Simple **health, hunger, and thirst**, with clear effects but no tedious micromanagement.
- Explore on foot, discover outdoor loot caches, find food/water/ammunition/medical items, avoid or confront bandits, and use vehicles when useful.
- OSM points of interest help decide **what might be found nearby**: food outside grocery shops, supplies at petrol stations, medical items near pharmacies, etc. Caches spawn **outside**: **no building interiors required**.
- Human **bandits / hostile survivors**, not zombies. Simple believable behaviour: patrol or guard, notice the player, shoot from cover when possible, react to hits, and investigate loud gunfire. No sophisticated faction simulation needed.
- Potential lightweight structure: a starting location in Warsaw and an optional **"get out of town"** objective. Or simply free exploration. Neighbourhoods can get different danger/loot tendencies based on real district/street layout or sensible procedural grouping, without building a territory simulator.

## World integration

- Keep the real-world road network, buildings and geography as the star. Warsaw's courtyards, long streets, suburbs, villages, and forests should naturally produce different exploration and firefight situations.
- Keep encounters and loot sparse, local, and generated around the player. The map is huge; **do not simulate enemies or caches across all Mazowsze at once**.
- Don't require BadBuilding interiors, new procedural generators, or complicated new systems to make this mode playable.

## Boundaries (important!)

**Not the plan:** elaborate crafting, base building, complex economy, quest chains, huge weapon catalogue, realistic ARMA mechanics, interiors everywhere, zombies, helicopters, explosions as a feature mandate, giant bathrobe Godzilla, or pink rabbits from Baghdad. Those last ones were jokes. :P

**The goal:** a modest, fun survival layer on the existing driving game, with **excellent-feeling gunplay**, easy switching between first- and third-person, **first-person aiming**, simple outdoor loot, a handful of bandits, and a reason to explore Warsaw and Mazowsze on foot.

**Sage:** treat this as a creative direction. Use good judgment about implementation and what already exists; don't turn this brief into a massive roadmap. Polish the enjoyable core before expanding it.
