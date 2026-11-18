# Car workshop — design (Tom and Sage, 18 Nov 2026)

Tom: "Project Zomboid complexity for cars", repaired like the car workshop simulator games. His picks: a **workshop
view** (not bolt by bolt, not a menu), parts from **wrecked cars**, and **clear, readable effects**.

## Step 1 — parts and damage (done, D67)
Every car has parts with a condition 0–100: engine, battery, four tyres, front and rear brakes, front and rear
suspension, windshield, two side windows, two doors, hood, trunk, driver's seat (`Vehicle/CarParts.cs`).
- **Damage:** bullets wear the part they hit (glass and tyres fast, panels slowly; through a broken hood into the
  engine or battery); crashes (a velocity change over 5 m/s) wear the end that hit (front: hood, engine, front
  suspension, battery, windshield on big ones; rear: trunk; sides: door, window, tyre); hard landings wear the
  suspension.
- **Effects:** engine power 25–100 % (coughs and smokes below 35 %, dead at 0); a battery under 8 % won't start the
  engine and dims the lights; worn tyres grip less, a flat runs on its rim and pulls to its side; brakes stop
  25–100 %; worn suspension is soft, low and bouncy; broken windows and doors let more of a bullet reach the driver.
- **HUD:** parts under 70 % listed while driving; a note when something breaks.

## Step 2 — the workshop view (next)
- Stand by your car (or a wreck), press **G**... (key to choose with Tom): the camera orbits the car, the mouse
  turns it; the car's parts glow by condition (green → red) and show their name and percentage on hover.
- **Open** the hood, the trunk, the doors; **jack up** a corner to take a wheel off.
- **Remove** a part (a short timed action, longer for big ones: a tyre 5 s, an engine 30 s), and it goes into the
  boot or your hands; **fit** a part from your inventory into an empty slot.
- Parts fit by kind (any tyre fits any corner; engines and batteries of a class). Later: car models with their own
  part sizes.
- Tools later if wanted (Tom ticked only wrecks): a jack for wheels, a wrench for the rest.

## Step 3 — wrecks
- Abandoned cars along roads, in car parks and yards (OSM parking areas already in the map), generated around the
  player like the crates; each with random part conditions, some parts missing (stripped by someone before you).
- Strip them in the workshop view; some are drivable once fixed.
- The boot as storage for parts and loot.

## Open questions for Tom
- Which key opens the workshop view.
- Repairing a part in place (welding, patching a tyre) or only swapping parts?
- Should your car be lost/stolen if you leave it, or stay where you parked?
