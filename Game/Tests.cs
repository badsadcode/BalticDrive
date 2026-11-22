using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.World;
using Godot;

namespace BalticDrive;

/// <summary>
/// Scripted checks that drive the real game (like Dead Miles' DebugHarness). After <c>--</c>:
/// <list type="bullet">
/// <item><c>--views=x:z:above:yawDeg:pitchDeg;...</c> — for each view: go there, wait until everything has streamed in,
/// save a screenshot to <c>--shots=dir</c>.</item>
/// <item><c>--autotest=fly</c> — fly 6 km in a straight line at <c>--speed=</c> m/s (default 40, ~145 km/h) from the
/// start and report frame times and holes (frames where the ground under the camera was missing).</item>
/// <item><c>--autotest=walk</c> — out of the car (E), walk 5 s, sprint 3 s, crouch 2 s, first person, walk back
/// to the car and get in, with screenshots on the way (D38).</item>
/// <item>Self-tests leave the mouse free; <c>--capture-mouse</c> lets them take it (the shoot test then also checks
/// that a right click keeps it captured).</item>
/// <item><c>--autotest=shoot</c> — out of the car, then each gun in turn (D40): hip view, aim down the sights, fire
/// (a burst for the AK), reload; screenshots of each and the bullets' hits in the log.</item>
/// <item><c>--autotest=range</c> — out of the car, set up the shooting range (D42), shoot each target with the AK
/// (aimed straight at it, the 100 m plate with the Mosin), screenshots before and after, and what each hit said.</item>
/// <item><c>--autotest=loot</c> — out of the car, wait for supply crates by the nearby shops (D46), walk up to the
/// nearest, open it; then hunger, thirst and a medkit: are carried food and water used, does the medkit heal.</item>
/// <item><c>--autotest=bandits</c> — out of the car, a group of bandits 45 m ahead (D47): stand and watch them notice
/// you and fight (their states and your health logged), then shoot back with the AK, then search a body.</item>
/// <item><c>--autotest=banditlook</c> — one bandit 6 m ahead, photographed relaxed and then aiming at you (how they
/// hold their guns).</item>
/// <item><c>--autotest=tpgun</c> — third person with each gun (D49): standing, walking, sprinting, firing, seen
/// from the side.</item>
/// <item><c>--autotest=death</c> (D55: the fall, waking far away, your things, a safehouse) — — crates on the radar (D50), then going down: the screen goes dark, you come round
/// by the car with the pack gone.</item>
/// <item><c>--autotest=crouch</c> — third person from the side: standing, crouched still, crouch-walking (D52).</item>
/// <item><c>--autotest=carbandits</c> — sitting in the car, a group of bandits 30 m away (D52): do they come for the
/// car and shoot, and does it hurt the driver.</item>
/// <item><c>--autotest=sound</c> — records the game's own sound to <c>sound.wav</c> (D53): a pistol shot by the
/// buildings, one out in the open, then a drive (engine revs, gears); the places the acoustics found, and when each
/// sound should land, go to the log.</item>
/// <item>Test runs play muted (the master bus at -80 dB; a recording still hears it), unless <c>--sound</c>.</item>
/// <item><c>--autotest=leads</c> — a lead (D54): one is found 300–600 m away, shown on the map; then you're put
/// 150 m from it: the stash is set down with its guards; the guards are shot, the stash opened (a note may lead on).</item>
/// <item><c>--autotest=lean</c> — leaning (D55): first person upright, left, right; third person left and right
/// with the AK out; logs how far the eye moved and the room a wall left.</item>
/// <item><c>--autotest=ragdoll</c> — ragdolls (D56): a bandit shot from the side falls (photographed as it goes), the
/// body checked for flying off or sinking, the world origin moved under it and back; then the walker is shot and the
/// fall seen through its eyes.</item>
/// <item><c>--autotest=roadscan [--radius=3000]</c> — looks through the roads around the start for bends tighter than
/// the road is wide (the inner edge folds over itself: Tom's wobbly service roads), logs the worst with a
/// <c>--views=</c> line to photograph them from above, and quits.</item>
/// <item><c>--autotest=respawn</c> — the "fly far, press F" check: from the car, switch to the free camera, fly
/// <c>--distance=</c> m (default 3000) at <c>--speed=</c>, press F (drive here), wait, and repeat <c>--rounds=</c> times
/// (default 6) in different directions.</item>
/// </list>
/// The game quits when done; results go to the log as lines starting with <c>[Test]</c>.
/// </summary>
public sealed class Tests
{
    private readonly Main _main;
    private readonly string _shots;
    private readonly List<string[]> _views = new();
    private int _view = -1;
    private double _settledFor, _elapsed;
    private int _frames;
    private bool _fly;
    private double _flown, _speed;
    private readonly List<double> _frameMs = new();
    private int _holes;
    private bool _flyStarted;
    private bool _drive;
    private double _driveTime, _maxSpeed, _offRoad;
    private bool _driveStarted;
    // --autotest=tip (D97): lane changes and a handbrake turn at speed, the worst roll and the flips
    private bool _tip, _tipStarted;
    // --autotest=takecar (D99): get out, take a wreck that runs, drive it, come back to the old car
    private bool _take, _craft;
    private double _craftT = -1, _genT = -1, _heatT = -1, _booksT = -1, _skillsT = -1, _bigosT = -1;
    private bool _bigos, _fresh, _ui, _wells;
    private double _buildT = -1, _buildStepAt;
    private bool _build;
    private int _buildStep, _buildHeading;
    private string? _buildBackup;

    /// <summary>--autotest=build (D128): the four things you build, one after another: materials in the pack, the build
    /// recipe picked (the ghost appears), turned round until it fits (a red ghost first, aimed at the car), built with F
    /// (the hammering, then the thing); the barricade boards cut at the workbench (only there); the crate opened and
    /// something put in; half an hour of rain in the collector and a bottle filled; the barricade shot to pieces. The
    /// player's containers file is put back (a copy on disk if the test dies), nothing else is saved.</summary>
    private bool BuildTest(double delta)
    {
        string file = ProjectSettings.GlobalizePath("user://containers.json");
        var built = _main.Built;
        if (_buildT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null || built == null) return true;
            _buildT = 0;
            string keep = file + ".selftest";
            if (System.IO.File.Exists(keep)) { System.IO.File.Copy(keep, file, true); Log.Info("Test", "build: the containers file from a test that died put back"); }
            _buildBackup = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null;
            if (_buildBackup != null) System.IO.File.WriteAllText(keep, _buildBackup);
            built.Frozen = true;
            Items.Skills.Add("woodcraft", 400);   // a practised hand: shorter waits in the test
            Log.Info("Test", $"build: woodcraft level {Items.Skills.Level("woodcraft")}, times x{Items.Skills.TimeFactor("woodcraft"):0.00}; {built.All.Count} built before");
            _main.ToggleOnFoot();
            return true;
        }
        _buildT += delta;
        var w = _main.Walker!;
        var pack = w.Body.Pack;
        var placer = _main.Placer!;
        var world = _main.World!;
        Items.Recipe R(string id) => Items.Recipes.All.First(r => r.Id == id);
        string[] order = { "build_workbench", "build_barricade", "build_crate", "build_rain_collector" };
        void Look(float yaw)
        {
            var at = w.GlobalPosition + new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw)) * 2.7f;
            var (x, _, z) = WorldOrigin.ToWorld(at);
            w.Face(at with { Y = world.Height(x, z) + 0.2f });
        }
        bool Wait(double s) => _buildT - _buildStepAt < s;
        void Next() { _buildStep++; _buildStepAt = _buildT; }
        int piece = _buildStep / 10, sub = _buildStep % 10;
        if (piece < order.Length)
        {
            var r = R(order[piece]);
            switch (sub)
            {
                case 0:
                    if (Wait(1)) return true;
                    w.SetView(true);
                    pack.Stacks.Clear();
                    pack.Add("hammer", 1); pack.Add("nails", 2);
                    if (r.Id == "build_workbench") pack.Add("plank", 4);
                    if (r.Id == "build_crate") pack.Add("plank", 4);
                    if (r.Id == "build_rain_collector") { pack.Add("plastic_barrel", 1); pack.Add("sheet", 1); pack.Add("rope", 1); pack.Add("plank", 2); }
                    if (r.Id == "build_barricade")
                    {
                        // the boards first: only at the workbench
                        pack.Add("plank", 3);
                        var boards = R("barricade_boards");
                        bool at = Items.Recipes.BenchHere?.Invoke() == true;
                        bool made = Items.Recipes.Make(boards, pack, out var said);
                        Log.Info("Test", $"build: cutting barricade boards by the workbench ({at}): {made}, {said}");
                    }
                    var why = Items.Recipes.Build?.Invoke(r);
                    Log.Info("Test", $"build: {r.Id}: ghost {(why == null ? "up" : why)}; pack {Items.Inventory.Say(pack.Stacks)}");
                    _buildHeading = 0;
                    if (piece == 0) { Look(MathF.Atan2(-(_main.Car!.GlobalPosition - w.GlobalPosition).X, -(_main.Car.GlobalPosition - w.GlobalPosition).Z)); }   // at the car first: red
                    Next();
                    return true;
                case 1:
                    if (Wait(0.6)) return true;
                    if (piece == 0 && _buildHeading == 0 && !placer.Fits) { Log.Info("Test", $"build: aimed at the car: fits {placer.Fits}, \"{placer.Why}\""); Shot("build_red"); }
                    if (placer.Fits)
                    {
                        Log.Info("Test", $"build: {r.Id}: fits at heading {_buildHeading}; \"{_main.PromptText}\"");
                        if (piece == 0) Shot("build_ghost");
                        Next();
                    }
                    else if (_buildHeading >= 16) { Log.Info("Test", $"build: {r.Id}: nowhere fits ({placer.Why})"); placer.Cancel(); _buildStep = (piece + 1) * 10; _buildStepAt = _buildT; }
                    else { Look(w.Yaw + MathF.PI / 4 * (_buildHeading % 2 == 0 ? 1 : 1)); _buildHeading++; _buildStepAt = _buildT; }
                    return true;
                case 2:
                    if (Wait(0.3)) return true;
                    PressF();
                    Next();
                    return true;
                case 3:
                    if (Wait(0.3)) return true;
                    if (placer.Job != null)
                    {
                        if (piece == 0 && _buildT - _buildStepAt > 2.5 && _buildT - _buildStepAt < 2.6) Shot("build_building");
                        return true;
                    }
                    Log.Info("Test", $"build: {r.Id}: done: {built.All.Count} built; pack {Items.Inventory.Say(pack.Stacks)}");
                    _buildStep = (piece + 1) * 10; _buildStepAt = _buildT;
                    return true;
            }
            return true;
        }
        // all four up: use them
        int u = _buildStep - order.Length * 10;
        if (Wait(1)) return true;
        switch (u)
        {
            case 0:
            {
                // the overview: step back and look at them all
                var mid = built.All.Where(p => p.Node != null).Select(p => p.Node!.GlobalPosition).Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(1, built.All.Count);
                var away = (w.GlobalPosition - mid) with { Y = 0 };
                var car = _main.Car!;
                car.Warp(new Transform3D(car.GlobalBasis, car.GlobalPosition + (car.GlobalPosition - mid).Normalized() * 25f + Vector3.Up * 0.5f));   // out of the picture
                w.GlobalPosition = mid + (away.LengthSquared() < 0.01f ? Vector3.Back : away.Normalized()) * 5f + Vector3.Up * 0.5f;
                w.Face(mid + Vector3.Up * 0.5f);
                Log.Info("Test", $"build: built: {string.Join(", ", built.All.Select(p => $"{p.Def} at {p.X:0.0},{p.Z:0.0}"))}");
                Next(); return true;
            }
            case 1: Shot("build_all"); Next(); return true;
            case 2:
            {
                // the crate: open it with F, put a tin of beans in
                var crate = built.All.First(p => p.Def == "crate");
                w.GlobalPosition = crate.Node!.GlobalPosition + crate.Node.GlobalBasis.Z * 0.9f + Vector3.Up * 0.3f;
                w.Face(crate.Node.GlobalPosition + Vector3.Up * 0.3f);
                pack.Add("beans", 2);
                Next(); return true;
            }
            case 3: Log.Info("Test", $"build: by the crate: \"{_main.PromptText}\""); PressF(); Next(); return true;
            case 4:
            {
                var inv = _main.InventoryWindow!;
                var crate = built.All.First(p => p.Def == "crate");
                int moved = pack.MoveTo(built.Crate(crate), "beans", 2); inv.Refresh();
                Log.Info("Test", $"build: crate open {inv.IsOpen}; beans moved {moved}: crate {Items.Inventory.Say(built.Crate(crate).Stacks)}");
                Shot("build_crate"); Next(); return true;
            }
            case 5:
            {
                _main.InventoryWindow!.Close();
                var col = built.All.First(p => p.Def == "rain_collector");
                Core.WorldClock.Skip(1800);
                w.GlobalPosition = col.Node!.GlobalPosition + col.Node.GlobalBasis.Z * 1.2f + Vector3.Up * 0.3f;
                w.Face(col.Node.GlobalPosition + Vector3.Up * 0.8f);
                pack.Add("empty_bottle", 2);
                Next(); return true;
            }
            case 6: Log.Info("Test", $"build: half an hour later by the collector: {Build.Built.Bottles(built.All.First(p => p.Def == "rain_collector"))} bottles; \"{_main.PromptText}\""); Shot("build_collector"); PressF(); Next(); return true;
            case 7:
            {
                Log.Info("Test", $"build: pack: water {pack.Count("water")}, empty {pack.Count("empty_bottle")}; collector {Build.Built.Bottles(built.All.First(p => p.Def == "rain_collector"))}");
                // the barricade: shot until it gives way
                var bar = built.All.First(p => p.Def == "barricade");
                int hits = 0;
                while (built.All.Contains(bar) && hits < 50) { bar.Node!.GetChildren().OfType<Build.BarricadeBody>().First().Hit(default); hits++; }
                Log.Info("Test", $"build: the barricade gave way after {hits} hits; {built.All.Count} left");
                Next(); return true;
            }
            case 8:
                if (_buildBackup != null) System.IO.File.WriteAllText(file, _buildBackup); else if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                if (System.IO.File.Exists(file + ".selftest")) System.IO.File.Delete(file + ".selftest");
                w.SetView(false);
                Log.Info("Test", "build: done (containers file put back)");
                return false;
        }
        return true;
    }

    private double _fridgeT = -1;
    private bool _fridge;
    private Power.Generators.Gen? _fridgeGen;
    private Vector3 _fridgeHome;
    private readonly Items.Inventory _fridgeIn = new(), _fridgeOut = new();

    /// <summary>--autotest=fridge (D127): a generator started with a full tank beside a "fridge" (an inventory with
    /// kabanos in it, another kabanos left out); you walk 800 m away, an hour of play passes, you come back: the generator
    /// burned its tank while you were gone and stopped when it ran dry, and the fridge's kabanos aged for the hour less two
    /// thirds of the 24 minutes it ran. Nothing is saved.</summary>
    private bool FridgeTest(double delta)
    {
        var gens = _main.Generators;
        if (_fridgeT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null || gens == null) return true;
            _fridgeT = 0;
            gens.Frozen = true; Power.Fridges.Frozen = true;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _fridgeT;
        _fridgeT += delta;
        bool At(double t) => before < t && _fridgeT >= t;
        var w = _main.Walker!;
        if (At(1))
        {
            _fridgeHome = w.GlobalPosition;
            _fridgeGen = gens!.PlaceHere(w.GlobalPosition + new Vector3(3, 0, 0), 0, Power.Generators.Tank);
            gens.Toggle(_fridgeGen, out _);
            double now = Core.WorldClock.Seconds;
            _fridgeIn.Add("kabanos", 1, now); _fridgeOut.Add("kabanos", 1, now);
            var (x, _, z) = WorldOrigin.ToWorld(w.GlobalPosition);
            Power.Fridges.Chill(Main.SaveWorld, "fridge_selftest", _fridgeIn, gens.RunsNear(x, z, Power.Fridges.Reach));
            Log.Info("Test", $"fridge: generator on with {_fridgeGen.Fuel:0.0} L; a kabanos in the fridge and one out; day {Core.WorldClock.Days:0.00}");
        }
        if (At(2)) w.GlobalPosition = _fridgeHome + new Vector3(800, 0, 0);
        if (At(6))
        {
            Log.Info("Test", $"fridge: 800 m away; the generator still built: {gens!.Live.Contains(_fridgeGen!)}; an hour passes");
            Core.WorldClock.Skip(3600);
        }
        if (At(7)) w.GlobalPosition = _fridgeHome + Vector3.Up * 0.3f;
        if (At(11))
        {
            var g = _fridgeGen!;
            Log.Info("Test", $"fridge: back: generator on {g.On}, {g.Fuel:0.0} L; it ran {(g.OffAt - g.OnSince) / 60:0.0} min");
            var (x, _, z) = WorldOrigin.ToWorld(_fridgeHome);
            double cold = Power.Fridges.Chill(Main.SaveWorld, "fridge_selftest", _fridgeIn, gens!.RunsNear(x, z, Power.Fridges.Reach));
            var a = _fridgeIn.Stacks[0]; var b = _fridgeOut.Stacks[0];
            Log.Info("Test", $"fridge: cold {cold / 60:0.0} min; kabanos in the fridge {Items.Freshness.AgeDays(a.Made):0.00} days old, left out {Items.Freshness.AgeDays(b.Made):0.00} days");
            var (fx, _, fz) = WorldOrigin.ToWorld(_fridgeHome + new Vector3(500, 0, 0));
            Log.Info("Test", $"fridge: one 500 m away from it would get: {gens.RunsNear(fx, fz, Power.Fridges.Reach).Count()} runs");
        }
        if (At(12)) { Log.Info("Test", "fridge: done"); return false; }
        return true;
    }

    private double _bootT = -1;
    private bool _boot;
    private string? _bootBackup;

    /// <summary>--autotest=boot (D125): behind the car, F opens its boot beside the pack; things moved in (firewood, a
    /// can, water); more than it holds refused; closed, the load's weight goes on the car; in the car, stopped, Tab opens
    /// it too; the boots of the found cars around logged. The player's containers file is put back afterwards.</summary>
    private bool BootTest(double delta)
    {
        string file = ProjectSettings.GlobalizePath("user://containers.json");
        if (_bootT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _bootT = 0;
            // a copy on disk first: if the game dies mid-test, the next run puts it back (D125)
            string keep = file + ".selftest";
            if (System.IO.File.Exists(keep)) { System.IO.File.Copy(keep, file, true); Log.Info("Test", "boot: the containers file from a test that died put back"); }
            _bootBackup = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null;
            if (_bootBackup != null) System.IO.File.WriteAllText(keep, _bootBackup);
            Items.Containers.Forget(Main.SaveWorld);
            Log.Info("Test", $"boot: {_main.Car.Spec.Id}: holds {_main.Car.Spec.Boot:0} kg; mass {_main.Car.Mass:0} kg");
            _main.ToggleOnFoot();
            return true;
        }
        double before = _bootT;
        _bootT += delta;
        bool At(double t) => before < t && _bootT >= t;
        var w = _main.Walker!;
        var pack = w.Body.Pack;
        var car = _main.Car!;
        var inv = _main.InventoryWindow!;
        if (At(1))
        {
            var b = car.ToGlobal(car.BootAnchor);
            var out_ = (b - car.GlobalPosition) with { Y = 0 };
            w.GlobalPosition = b + out_.Normalized() * 0.75f - Vector3.Up * (b.Y - car.GlobalPosition.Y);
            w.SetView(true);
            w.Face(b);
            pack.Stacks.Clear();
            pack.Add("water", 3); pack.Add("firewood", 2); pack.Add("beans", 2); pack.Add("petrol_can", 1);
        }
        if (At(2)) { Log.Info("Test", $"boot: behind the car: \"{_main.PromptText}\""); PressF(); }
        if (At(2.5)) Log.Info("Test", $"boot: window open {inv.IsOpen}, with a container {inv.HasContainer}");
        if (At(3)) Shot("boot_open");
        if (At(3.5))
        {
            var boot = _main.Boot(car);
            int f = pack.MoveTo(boot, "firewood", 2), c = pack.MoveTo(boot, "petrol_can", 1), wa = pack.MoveTo(boot, "water", 2);
            inv.Refresh();
            Log.Info("Test", $"boot: moved firewood {f}, can {c}, water {wa}: boot {boot.Weight:0.0} of {boot.MaxWeight:0} kg, pack {pack.Weight:0.0} kg");
            int fit = boot.Add("firewood", 100);
            Log.Info("Test", $"boot: 100 armfuls of firewood offered, {fit} fit: {boot.Weight:0.0} kg");
            boot.Remove("firewood", fit);
            inv.Refresh();
        }
        if (At(4.5)) Shot("boot_loaded");
        if (At(5)) { inv.Close(); Log.Info("Test", $"boot: closed: car mass {car.Mass:0} kg (spec {car.Spec.Mass:0})"); }
        if (At(5.5)) { _main.ToggleOnFoot(); }
        if (At(7.5))
        {
            Log.Info("Test", $"boot: in the car {!_main.OnFoot}, {car.Speed * 3.6f:0} km/h; Tab");
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = false });
        }
        if (At(8)) { Log.Info("Test", $"boot: from the seat: window open {inv.IsOpen}, with a container {inv.HasContainer}"); Shot("boot_seat"); }
        if (At(8.5))
        {
            inv.Close();
            var found = _main.Wrecks?.Live.Where(c => c != car).Take(8).ToList() ?? new();
            foreach (var fc in found)
            {
                var fb = _main.Boot(fc);
                Log.Info("Test", $"boot: {fc.Name} ({fc.Spec.Id}, {fb.MaxWeight:0} kg): {(fb.IsEmpty ? "empty" : Items.Inventory.Say(fb.Stacks))}");
            }
        }
        if (At(9))
        {
            if (_bootBackup != null) System.IO.File.WriteAllText(file, _bootBackup); else if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
            if (System.IO.File.Exists(file + ".selftest")) System.IO.File.Delete(file + ".selftest");
            Log.Info("Test", "boot: done (containers file put back)");
            return false;
        }
        return true;
    }

    private double _pumpT = -1;
    private bool _pumps;
    private (double X, double Z) _pumpPoi;
    private Power.Stations.Pump? _pump;
    private Power.Generators.Gen? _pumpGen;

    /// <summary>--autotest=pumps (D124): to the nearest petrol station; its pumps dead without power; a running
    /// generator beside them; the car brought up and filled (F, then F to stop); an empty can filled; the generator off
    /// again. Screenshots of each. Nothing is saved (the stations' and generators' files are left alone).</summary>
    private bool PumpsTest(double delta)
    {
        if (_pumpT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null || _main.Stations == null) return true;
            _pumpT = 0;
            _main.Stations.Frozen = true;
            if (_main.Generators != null) _main.Generators.Frozen = true;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _pumpT;
        _pumpT += delta;
        bool At(double t) => before < t && _pumpT >= t;
        var w = _main.Walker!;
        var world = _main.World!;
        var pack = w.Body.Pack;
        var st = _main.Stations!;
        var car = _main.Car!;
        void Stand(Vector3 at, Vector3 look)
        {
            w.GlobalPosition = at with { Y = at.Y + 0.2f };
            w.SetView(true);
            w.Face(look);
        }
        if (At(1))
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            double bd = double.MaxValue;
            int t0x = (int)Math.Floor((px - 4000) / Map.MapTile.Size), t1x = (int)Math.Floor((px + 4000) / Map.MapTile.Size);
            int t0z = (int)Math.Floor((pz - 4000) / Map.MapTile.Size), t1z = (int)Math.Floor((pz + 4000) / Map.MapTile.Size);
            int all = 0;
            for (int tx = t0x; tx <= t1x; tx++)
                for (int tz = t0z; tz <= t1z; tz++)
                {
                    if (!world.Data.HasTile(tx, tz)) continue;
                    foreach (var p in world.Data.Tile(tx, tz).Pois.Where(p => p.Kind == Map.MapPoiKind.Fuel))
                    {
                        all++;
                        double x = tx * (double)Map.MapTile.Size + p.X, z = tz * (double)Map.MapTile.Size + p.Z, d = (x - px) * (x - px) + (z - pz) * (z - pz);
                        if (d < bd) { bd = d; _pumpPoi = (x, z); }
                    }
                }
            Log.Info("Test", $"pumps: {all} petrol stations within 4 km; the nearest {Math.Sqrt(bd):0} m away");
            Stand(WorldOrigin.ToLocal(_pumpPoi.X, world.Height(_pumpPoi.X, _pumpPoi.Z), _pumpPoi.Z), WorldOrigin.ToLocal(_pumpPoi.X + 10, world.Height(_pumpPoi.X, _pumpPoi.Z) + 1, _pumpPoi.Z));
        }
        if (At(5))
        {
            var s = st.Live.OrderBy(s => (s.X - _pumpPoi.X) * (s.X - _pumpPoi.X) + (s.Z - _pumpPoi.Z) * (s.Z - _pumpPoi.Z)).FirstOrDefault();
            if (s == null || s.Pumps.Count == 0 || s.Pumps[0].Node == null) { Log.Info("Test", "pumps: no station built"); return false; }
            Log.Info("Test", $"pumps: {(s.Name.Length > 0 ? s.Name : s.Id)}: {s.Pumps.Count} pumps, canopy {s.Canopy}, {s.Litres:0} L below, powered {s.Powered}; generators within 30 m: {_main.Generators?.Live.Count(g => (g.X - s.X) * (g.X - s.X) + (g.Z - s.Z) * (g.Z - s.Z) < 900) ?? 0}");
            if (s.Litres < 100) { s.Litres = 800; Log.Info("Test", "pumps: (dry; 800 L put in for the test)"); }
            _pump = s.Pumps[0];
            var n = _pump.Node!;
            Stand(n.GlobalPosition + (n.GlobalBasis.X + n.GlobalBasis.Z) * 9f + Vector3.Up * 0.5f, n.GlobalPosition + Vector3.Up * 1f);   // the forecourt
        }
        if (At(5.8)) Shot("pumps_forecourt");
        if (At(5.9) && _pump?.Node is { } pb) Stand(pb.GlobalPosition + pb.GlobalBasis.X * 1.4f - pb.GlobalBasis.Z * 0.9f, pb.GlobalPosition + Vector3.Up * 1.2f);   // beside it, its screen side
        if (At(6.3) && _pump != null) { Log.Info("Test", $"pumps: at the pump: \"{_main.PromptText}\""); Shot("pumps_station"); }
        if (At(6.5) && _pump?.Node is { } pn)
        {
            // a generator beside the island, started
            var gens = _main.Generators!;
            _pumpGen = gens.PlaceHere(pn.GlobalPosition + pn.GlobalBasis.Z * 3.4f + pn.GlobalBasis.X * 2.2f, _pump.Yaw, 4f);
            gens.Toggle(_pumpGen, out var said);
            Log.Info("Test", $"pumps: generator placed and started: {said}");
        }
        if (At(8) && _pump?.Node is { } pn2)
        {
            Log.Info("Test", $"pumps: powered {_pump.Station.Powered}; \"{_main.PromptText}\"");
            // the car brought up on the island's other side, along it
            car.Fuel = 5;
            car.Warp(new Transform3D(new Basis(Vector3.Up, _pump.Yaw), pn2.GlobalPosition - pn2.GlobalBasis.X * 2.9f + Vector3.Up * 0.3f));
        }
        if (At(9.5)) { Log.Info("Test", $"pumps: car {car.GlobalPosition.DistanceTo(_pump!.Node!.GlobalPosition):0.0} m from the pump; \"{_main.PromptText}\""); Shot("pumps_powered"); }
        if (At(10)) PressF();
        if (At(10.3)) Log.Info("Test", $"pumps: filling: {st.Filling != null}");
        if (At(16)) { Log.Info("Test", $"pumps: after 6 s: {car.Fuel:0.0} L in the car, {_pump!.Station.Litres:0} L below; \"{_main.PromptText}\""); Shot("pumps_filling"); }
        if (At(16.5)) PressF();
        if (At(17))
        {
            Log.Info("Test", $"pumps: stopped: filling {st.Filling != null}, {car.Fuel:0.0} L in the car");
            var n = _pump!.Node!;
            car.Warp(new Transform3D(car.GlobalBasis, n.GlobalPosition - n.GlobalBasis.X * 30f + Vector3.Up * 0.5f));
            pack.Stacks.Clear(); pack.Add("empty_petrol_can", 2);
        }
        if (At(18)) { Log.Info("Test", $"pumps: no car, two empty cans: \"{_main.PromptText}\""); PressF(); }
        if (At(18.5)) Log.Info("Test", $"pumps: pack: petrol {pack.Count("petrol_can")}, empty {pack.Count("empty_petrol_can")}; {_pump!.Station.Litres:0} L below");
        if (At(19)) { _main.Generators!.Toggle(_pumpGen!, out var off); Log.Info("Test", $"pumps: generator: {off}"); }
        if (At(20)) { Log.Info("Test", $"pumps: powered {_pump!.Station.Powered}; \"{_main.PromptText}\""); Shot("pumps_off"); }
        if (At(20.5)) { w.SetView(false); Log.Info("Test", "pumps: done"); return false; }
        return true;
    }

    private double _fuelT = -1, _fuelX0, _fuelZ0;
    private float _fuel0;
    private bool _fuel;
    private Transform3D _fuelStart;

    /// <summary>--autotest=fuel (D123): the car burns petrol driving (litres per 100 km logged), runs dry and stops
    /// pulling; then on foot in the workshop a siphon hose and an empty can take 5 litres out, the can goes back in, and a
    /// can poured from the pack beside the car; the petrol in the wrecks around is logged. Screenshots of each.</summary>
    private bool FuelTest(double delta)
    {
        var car = _main.Car;
        if (_fuelT < 0)
        {
            if (_settledFor < 2.0 || car == null) return true;
            _fuelT = 0;
            car.Fuel = 20; _fuel0 = car.Fuel; _fuelStart = car.GlobalTransform;
            (_fuelX0, _, _fuelZ0) = WorldOrigin.ToWorld(car.GlobalPosition);
            Log.Info("Test", $"fuel: {car.Spec.Id}: tank {car.Tank:0} L, {car.Spec.Consumption:0.0} L/100 km; {car.Fuel:0.0} L in");
            car.Autopilot = _ => (0.55f, 0f, 0f);
            return true;
        }
        double before = _fuelT;
        _fuelT += delta;
        bool At(double t) => before < t && _fuelT >= t;
        var w = _main.Walker!;
        var pack = w.Body.Pack;
        var ws = _main.Workshop;
        if (At(11.5)) Shot("fuel_driving");
        if (At(12))
        {
            var (x, _, z) = WorldOrigin.ToWorld(car!.GlobalPosition);
            double km = Math.Sqrt((x - _fuelX0) * (x - _fuelX0) + (z - _fuelZ0) * (z - _fuelZ0)) / 1000;
            float used = _fuel0 - car.Fuel;
            Log.Info("Test", $"fuel: 12 s at 55 % throttle: {km * 1000:0} m, {used:0.000} L used, {(km > 0.01 ? used / km * 100 : 0):0.0} L/100 km, {car.Speed * 3.6f:0} km/h now");
            car.Warp(_fuelStart);   // back to the start, standing
            car.Fuel = 0.005f;   // a splash, about 60 m: floored, gone in a few seconds
            car.Autopilot = _ => (1f, 0f, 0f);
        }
        if (At(22)) { Log.Info("Test", $"fuel: floored from a standstill on the last 0.005 L: dry {car!.Dry}, {car.Speed * 3.6f:0} km/h after 10 s"); Shot("fuel_dry"); }
        if (At(22.2)) car!.Autopilot = c => (0f, c.Speed > 0.5f ? 1f : 0f, 0f);   // brake, but don't reverse
        if (At(25))
        {
            car!.Autopilot = null;
            Log.Info("Test", $"fuel: stopped, {car.Speed * 3.6f:0} km/h; floored again: won't go? \"{Main.WontGo(car) ?? "-"}\"");
            _main.ToggleOnFoot();
        }
        if (At(26))
        {
            pack.Stacks.Clear(); pack.Add("siphon_hose", 1); pack.Add("empty_petrol_can", 1);
            car!.Fuel = 18;
            _main.OpenWorkshop();
        }
        if (At(28)) { Log.Info("Test", $"fuel: workshop: {car!.Fuel:0.0} L, can siphon {ws!.CanSiphon}, can pour {ws.CanPour}"); Shot("fuel_workshop"); }
        if (At(28.3)) Log.Info("Test", $"fuel: siphon started: {ws!.StartSiphon()}");
        if (At(32)) Shot("fuel_siphoning");
        if (At(40))
        {
            Log.Info("Test", $"fuel: after siphoning: {car!.Fuel:0.0} L in the tank; pack: petrol {pack.Count("petrol_can")}, empty {pack.Count("empty_petrol_can")}");
            Shot("fuel_siphoned");
            Log.Info("Test", $"fuel: pour started: {ws!.StartPour()}");
        }
        if (At(47))
        {
            Log.Info("Test", $"fuel: after pouring: {car!.Fuel:0.0} L; pack: petrol {pack.Count("petrol_can")}, empty {pack.Count("empty_petrol_can")}");
            ws!.End();
            car.Fuel = 0;
            w.GlobalPosition = car.GlobalPosition + car.GlobalBasis.X * 2.2f + Vector3.Up * 0.3f;   // beside the door
            pack.Add("petrol_can", 1);
            w.Body.UseItem("petrol_can");
            Log.Info("Test", $"fuel: a can from the pack beside the dry car: {car.Fuel:0.0} L; won't go? \"{Main.WontGo(car) ?? "-"}\"; pack: petrol {pack.Count("petrol_can")}, empty {pack.Count("empty_petrol_can")}");
            var found = _main.Wrecks?.Live.Where(c => c != car).ToList() ?? new();
            Log.Info("Test", $"fuel: {found.Count} found cars around: " + string.Join("; ", found.Take(12).Select(c => $"{c.Spec.Id} {c.Fuel:0.0}/{c.Tank:0} L")));
        }
        if (At(48)) { Log.Info("Test", "fuel: done"); return false; }
        return true;
    }

    private double _wellsT = -1;
    private List<(byte Kind, double X, double Z)>? _wellSpots;
    private (double X, double Z) _wellStart;

    /// <summary>--autotest=wells (D121; a map built with wells, e.g. --map=podlaskie_wells --place=Trześcianka): the
    /// wells, hand pumps and springs within 3 km counted; the nearest of each photographed up close; a bottle filled at
    /// the well (clean water).</summary>
    private bool WellsTest(double delta)
    {
        if (_wellsT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _wellsT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _wellsT;
        _wellsT += delta;
        bool At(double t) => before < t && _wellsT >= t;
        var w = _main.Walker!;
        var world = _main.World!;
        var pack = w.Body.Pack;
        void StandBy((byte Kind, double X, double Z) s)
        {
            double x = s.X - 2.6, z = s.Z + 1.2;
            w.GlobalPosition = WorldOrigin.ToLocal(x, world.Height(x, z) + 0.1, z);
            w.SetView(true);
            w.Face(WorldOrigin.ToLocal(s.X, world.Height(s.X, s.Z) + 0.6, s.Z));
        }
        if (At(1))
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            _wellStart = (px, pz);
            var ctx = new Map.MapContext(world);
            ctx.Reset(px - 3000, pz - 3000, 0, 0, 6000, 6000, withPoints: true);
            _wellSpots = ctx.Points.Where(p => p.Kind is Map.MapPointKind.Well or Map.MapPointKind.Pump or Map.MapPointKind.Spring)
                .Select(p => (p.Kind, ctx.Ox + p.X, ctx.Oz + p.Z)).OrderBy(p => (p.Item2 - px) * (p.Item2 - px) + (p.Item3 - pz) * (p.Item3 - pz)).ToList();
            Log.Info("Test", $"wells: within 3 km: {_wellSpots.Count(p => p.Kind == Map.MapPointKind.Well)} wells, {_wellSpots.Count(p => p.Kind == Map.MapPointKind.Pump)} hand pumps, {_wellSpots.Count(p => p.Kind == Map.MapPointKind.Spring)} springs");
            if (_wellSpots.FirstOrDefault(p => p.Kind == Map.MapPointKind.Well) is { Kind: Map.MapPointKind.Well } wl) StandBy(wl);
        }
        if (At(4)) Shot("wells_well");
        if (At(4.3))
        {
            if (_wellSpots!.FirstOrDefault(p => p.Kind == Map.MapPointKind.Well) is { Kind: Map.MapPointKind.Well } wl)
            {
                double x = wl.X - 1.0, z = wl.Z;
                w.GlobalPosition = WorldOrigin.ToLocal(x, world.Height(x, z) + 0.1, z);
                w.Face(WorldOrigin.ToLocal(wl.X, world.Height(wl.X, wl.Z) + 0.6, wl.Z));
                pack.Stacks.Clear(); pack.Add("empty_bottle", 2);
            }
        }
        if (At(4.9) && _wellSpots!.Any(p => p.Kind == Map.MapPointKind.Well)) PressF();   // only with a well there: else F would get you in the car
        if (At(5.4))
        {
            Log.Info("Test", $"wells: at the well: water {pack.Count("water")}, empty {pack.Count("empty_bottle")}");
            if (_wellSpots!.FirstOrDefault(p => p.Kind == Map.MapPointKind.Pump) is { Kind: Map.MapPointKind.Pump } pm) StandBy(pm);
        }
        if (At(8)) Shot("wells_pump");
        if (At(8.3) && _wellSpots!.FirstOrDefault(p => p.Kind == Map.MapPointKind.Spring) is { Kind: Map.MapPointKind.Spring } sp) StandBy(sp);
        if (At(11)) Shot("wells_spring");
        if (At(11.3))
        {
            // the village's own (D122): walk back to the start and look around (on foot)
            if (!_main.OnFoot) _main.ToggleOnFoot();
            var (sx, _, sz) = WorldOrigin.ToWorld(_genFrom == Vector3.Zero ? w.GlobalPosition : _genFrom);
            w.GlobalPosition = WorldOrigin.ToLocal(_wellStart.X, world.Height(_wellStart.X, _wellStart.Z) + 0.3, _wellStart.Z);
        }
        if (At(15))
        {
            var all = _main.VillageWells!.Live.ToList();
            Log.Info("Test", $"wells: village wells within 160 m of the start: {all.Count(v => !v.Pump)} wells, {all.Count(v => v.Pump)} hand pumps");
            if (all.OrderBy(v => (WorldOrigin.ToLocal(v.X, 0, v.Z) - w.GlobalPosition with { Y = 0 }).Length()).FirstOrDefault() is { } vw)
            {
                StandBy((vw.Pump ? Map.MapPointKind.Pump : Map.MapPointKind.Well, vw.X, vw.Z));
                _wellStart = (vw.X, vw.Z);
            }
        }
        if (At(17.5)) Shot("wells_village");
        if (At(17.8))
        {
            double x = _wellStart.X - 1.0, z = _wellStart.Z;
            w.GlobalPosition = WorldOrigin.ToLocal(x, world.Height(x, z) + 0.1, z);
            pack.Stacks.Clear(); pack.Add("empty_bottle", 1);
        }
        if (At(18.3))
        {
            var near = _main.VillageWells!.Near_(w.GlobalPosition, Power.Wells.Reach);
            var vwp = near?.Node?.GlobalPosition ?? Vector3.Zero;
            Log.Info("Test", $"wells: by the village well: in reach {near != null}, {(vwp - w.GlobalPosition with { Y = vwp.Y }).Length():0.0} m away; the prompt: \"{_main.PromptText}\"");
        }
        if (At(18.4)) PressF();
        if (At(19))
        {
            Log.Info("Test", $"wells: at the village well: water {pack.Count("water")}, empty {pack.Count("empty_bottle")}");
            w.SetView(false);
            return false;
        }
        return true;
    }
    private double _uiT = -1;

    /// <summary>--autotest=ui (D117): the rucksack and the notebook photographed: Tab with a varied pack (fresh and
    /// stale food, a pot of bigos, a book, tools); the card of the pot picked; a cupboard opened beside the rucksack.</summary>
    private bool UiTest(double delta)
    {
        if (_uiT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _uiT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _uiT;
        _uiT += delta;
        bool At(double t) => before < t && _uiT >= t;
        var pack = _main.Walker!.Body.Pack;
        var inv = _main.InventoryWindow!;
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("kielbasa", 1), ("onion", 2), ("potatoes", 1), ("water", 2), ("bandage", 3), ("kitchen_knife", 1), ("matches", 1),
                         ("camping_stove", 1), ("gas_canister", 2), ("book_kuchnia_podlaska", 1), ("rag", 2), ("beans", 1) })
                pack.Add(id, n);
            pack.Add("kielbasa", 1, Core.WorldClock.Seconds - 5 * Core.WorldClock.Day);   // a stale one
            pack.Add("bigos:KMG:5:2", 1);
            _main.OpenInventory(null);
        }
        if (At(2)) Shot("ui_tab");
        if (At(2.3)) inv.SelectMine("bigos:KMG:5:2", Items.Freshness.Fresh);
        if (At(2.8)) Shot("ui_card");
        if (At(3.1))
        {
            inv.Close();
            var box = new Items.Inventory();
            foreach (var (id, n) in new[] { ("sauerkraut", 1), ("prunes", 2), ("tea", 1), ("saucepan", 1), ("jam", 1) }) box.Add(id, n);
            inv.Open(pack, box, "the kitchen cupboards", OnFoot.Survival.Usable, (id, st) => { }, () => { });
        }
        if (At(4)) Shot("ui_container");
        if (At(4.4)) { inv.Close(); return false; }
        return true;
    }
    private double _freshT = -1;

    /// <summary>--autotest=fresh (D115; the play clock in its scratch file): kiełbasa fresh, then stale after 5 days
    /// (eaten: 60 %), a fresh one beside it (two rows, photographed), then the old one rotten: recipes don't count it,
    /// hunger eats the beans instead, eating it by hand hurts; ages kept through a box; fridge loot of mixed ages; a
    /// stale pot of bigos reheated fresh. True while running.</summary>
    private bool FreshTest(double delta)
    {
        if (_freshT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _freshT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _freshT;
        _freshT += delta;
        bool At(double t) => before < t && _freshT >= t;
        var body = _main.Walker!.Body;
        var pack = body.Pack;
        const double Day = Core.WorldClock.Day;
        string Ages() => string.Join(", ", pack.Stacks.Where(s => s.Made >= 0).Select(s => $"{s.Id} x{s.Count} {Items.Freshness.Word(Items.Freshness.Stage(s)) switch { "" => "fresh", var w => w }} ({Items.Freshness.AgeDays(s.Made):0.0} d)"));
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("kielbasa", 2), ("beans", 1), ("camping_stove", 1), ("gas_canister", 3), ("sauerkraut", 1), ("water", 1), ("saucepan", 1) })
                pack.Add(id, n);
            Log.Info("Test", $"fresh: clock {Core.WorldClock.Days:0.00} d; {Ages()}");
            Core.WorldClock.Skip(5 * Day);
            body.Hunger = 30;
            body.UseItem("kielbasa");
            Log.Info("Test", $"fresh: +5 d: {Ages()}; ate one: hunger 30 -> {body.Hunger:0} (30 x 0.6 = 18)");
            pack.Add("kielbasa", 1);
            Log.Info("Test", $"fresh: a fresh one beside it: {Ages()}");
            Items.Recipes.Make(Items.Recipes.All.First(r => r.Id == "bigos_start"), pack, out var said);
            Log.Info("Test", $"fresh: {said}");
            _main.OpenInventory(null);
        }
        if (At(2)) Shot("fresh_rows");
        if (At(2.5))
        {
            _main.InventoryWindow!.Close();
            Core.WorldClock.Skip(4.5 * Day);
            Log.Info("Test", $"fresh: +4.5 d: {Ages()}; meat a recipe can use: {pack.CountMatching("tag:meat")} of {pack.Count("kielbasa")}");
            var box = new Items.Inventory();
            pack.MoveTo(box, "kielbasa", 9);
            Log.Info("Test", $"fresh: in a box: {string.Join(", ", box.Stacks.Select(s => $"{Items.Freshness.Word(Items.Freshness.Stage(s))}/{Items.Freshness.AgeDays(s.Made):0.0}"))}");
            box.MoveTo(pack, "kielbasa", 9);
            body.Hunger = 20;
            pack.Remove("beans", 9); pack.Add("beans", 1);
            body._Process(0.01);   // hungry: what does it eat by itself
            Log.Info("Test", $"fresh: hungry, by itself: beans left {pack.Count("beans")}, kiełbasa left {pack.Count("kielbasa")}");
            float h0 = body.Health;
            body.UseItem("kielbasa", Items.Freshness.Rotten);
            Log.Info("Test", $"fresh: rotten eaten by hand: health {h0:0} -> {body.Health:0}");
            // fridges, 312 days in
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < 40; i++)
                foreach (var s in Items.LootTables.Roll("fridge", "kitchen", $"fresh-test-{i}").Where(s => Items.Freshness.Perishable(Items.ItemDb.Get(s.Id))))
                {
                    string w = Items.Freshness.Word(Items.Freshness.Stage(s)); if (w == "") w = "fresh";
                    counts[w] = counts.GetValueOrDefault(w) + s.Count;
                }
            Log.Info("Test", $"fresh: 40 fridges' food: {string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}"))}");
            Core.WorldClock.Skip(3 * Day);
            Log.Info("Test", $"fresh: the bigos after 7.5 d: {Ages().Split(", ").FirstOrDefault(x => x.StartsWith("bigos")) ?? "(none)"}");
            Items.Recipes.Make(Items.Recipes.All.First(r => r.Id == "bigos_reheat"), pack, out var said2);
            Log.Info("Test", $"fresh: {said2}; now {Ages().Split(", ").FirstOrDefault(x => x.StartsWith("bigos")) ?? "(none)"}");
            return false;
        }
        return true;
    }

    /// <summary>--autotest=bigos (D114): adding before there's a pot is refused; a bigos started (sauerkraut, water,
    /// the saucepan); meat, mushrooms, prunes and onion added, meat twice refused; reheated three times, a fourth refused;
    /// the pot photographed in the inventory; a pot moved into a box and back; eaten serving by serving until the
    /// saucepan comes back. True while running.</summary>
    private bool BigosTest(double delta)
    {
        if (_bigosT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _bigosT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _bigosT;
        _bigosT += delta;
        bool At(double t) => before < t && _bigosT >= t;
        var body = _main.Walker!.Body;
        var pack = body.Pack;
        Items.Recipe R(string id) => Items.Recipes.All.First(x => x.Id == id);
        string Pot() => pack.Stacks.Select(s => s.Id).FirstOrDefault(Items.Evolved.Is) ?? "(no pot)";
        void Make(string id)
        {
            bool ok = Items.Recipes.Make(R(id), pack, out var said);
            Log.Info("Test", $"bigos: {id}: {(ok ? "made" : "not made")}: {said}  [{Pot()}]");
        }
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("sauerkraut", 1), ("water", 1), ("saucepan", 1), ("kielbasa", 2), ("dried_mushrooms", 1), ("prunes", 1),
                         ("onion", 1), ("kitchen_knife", 1), ("camping_stove", 1), ("gas_canister", 4) })
                pack.Add(id, n);
            Make("bigos_meat");
            Make("bigos_start");
            var d = Items.ItemDb.Get(Pot());
            Log.Info("Test", $"bigos: started: {d?.Name}, {d?.Food} food a serving, {d?.Weight:0.00} kg; saucepan left in the pack: {pack.Count("saucepan")}");
            foreach (var id in new[] { "bigos_meat", "bigos_mushrooms", "bigos_prunes", "bigos_onion", "bigos_meat" }) Make(id);
            for (int i = 0; i < 4; i++) Make("bigos_reheat");
            d = Items.ItemDb.Get(Pot());
            Log.Info("Test", $"bigos: now: {d?.Name}, {d?.Food} food a serving");
            _main.OpenInventory(null);
        }
        if (At(2)) Shot("bigos_pot");
        if (At(2.5))
        {
            _main.InventoryWindow!.Close();
            var box = new Items.Inventory();
            string pot = Pot();
            int moved = pack.MoveTo(box, pot, 1);
            Log.Info("Test", $"bigos: into a box: {moved}, the box holds {Items.Inventory.Say(box.Stacks)}");
            box.MoveTo(pack, pot, 1);
            Log.Info("Test", $"bigos: back: {Pot() == pot}");
            for (int i = 0; i < 6; i++)
            {
                body.Hunger = 20;
                string id = Pot();
                body.UseItem(id);
                Log.Info("Test", $"bigos: serving {i + 1}: hunger 20 -> {body.Hunger:0}; left: {Pot()}");
            }
            Log.Info("Test", $"bigos: saucepan back: {pack.Count("saucepan")}; pack {Items.Inventory.Say(pack.Stacks)}");
            return false;
        }
        return true;
    }
    private bool _skills;
    private float _skH0;

    /// <summary>--autotest=skills (D113; XP in the scratch file): all at 0; rice boiled five times (on a camping stove)
    /// takes Cooking to level 1 and the time from 30 to 27 s (the level-up note photographed with the inventory's
    /// skills line); a bandage at First aid 3 heals 15 x 1.3; woodcraft, mechanics and shooting factors at levels 2, 1
    /// and 4; the XP read back from the file. True while running.</summary>
    private bool SkillsTest(double delta)
    {
        if (_skillsT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _skillsT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _skillsT;
        _skillsT += delta;
        bool At(double t) => before < t && _skillsT >= t;
        var body = _main.Walker!.Body;
        var pack = body.Pack;
        Items.Recipe R(string id) => Items.Recipes.All.First(x => x.Id == id);
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("saucepan", 1), ("camping_stove", 1), ("gas_canister", 6), ("bandage", 2) })
                pack.Add(id, n);
            Log.Info("Test", $"skills: start: {Items.Skills.Line()}; boil rice takes {Items.Recipes.SecondsFor(R("boil_rice")):0.0} s");
            for (int i = 0; i < 5; i++)
            {
                pack.Add("rice", 1); pack.Add("water", 1);
                if (pack.CountMatching("tag:fuel_gas") == 0) pack.Add("gas_canister", 2);
                bool ok = Items.Recipes.Make(R("boil_rice"), pack, out var said);
                pack.Remove("cooked_rice", 9);
                if (!ok) Log.Info("Test", $"skills: rice {i + 1}: {said}");
            }
            Log.Info("Test", $"skills: after 5 rice: cooking {Items.Skills.Level("cooking")} ({Items.Skills.Xp("cooking"):0} XP); boil rice takes {Items.Recipes.SecondsFor(R("boil_rice")):0.0} s");
            _main.OpenInventory(null);
        }
        if (At(2)) Shot("skills_levelup");
        if (At(2.5))
        {
            _main.InventoryWindow!.Close();
            Items.Skills.Add("first_aid", 220);
            body.Health = 50;
            _skH0 = body.Health;
            body.UseItem("bandage");
        }
        if (At(5))
        {
            Log.Info("Test", $"skills: first aid {Items.Skills.Level("first_aid")}: a bandage healed {body.Health - _skH0:0.0} (15 x {1 + Items.Skills.Bonus("first_aid"):0.00} = {15 * (1 + Items.Skills.Bonus("first_aid")):0.0})");
            Items.Skills.Add("woodcraft", 120);
            Items.Skills.Add("mechanics", 50);
            Items.Skills.Add("shooting", 360);
            Log.Info("Test", $"skills: woodcraft {Items.Skills.Level("woodcraft")}: chop {Power.Woodland.ChopSeconds * Items.Skills.TimeFactor("woodcraft"):0.00} s; mechanics {Items.Skills.Level("mechanics")}: engine job {Vehicle.Workshop.JobTime(Vehicle.PartKind.Engine) * Items.Skills.TimeFactor("mechanics"):0.0} s; shooting {Items.Skills.Level("shooting")}: sway and spread x {1 - Items.Skills.Bonus("shooting"):0.00}");
            Items.Skills.Reset();
            Log.Info("Test", $"skills: from the file: {Items.Skills.Line()}");
            return false;
        }
        return true;
    }
    private bool _books;
    private string? _knowBackup;
    private static string KnowFile => ProjectSettings.GlobalizePath("user://knowledge.json");

    private void RestoreKnowledge()
    {
        if (_knowBackup != null) System.IO.File.WriteAllText(KnowFile, _knowBackup);
        else if (System.IO.File.Exists(KnowFile)) System.IO.File.Delete(KnowFile);
        Items.Knowledge.Reset();
    }

    /// <summary>--autotest=books (D112): knowing nothing, kartacze are refused ("learn it from Kuchnia podlaska"); the
    /// crafting list photographed with its "not learnt yet" part; the cookbook read in 15 s (photographed half-way);
    /// kartacze and placki made; knowledge read back from the file; a second reading says there's nothing new.
    /// user://knowledge.json is backed up and restored. True while running.</summary>
    private bool BooksTest(double delta)
    {
        if (_booksT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _booksT = 0;
            _knowBackup = System.IO.File.Exists(KnowFile) ? System.IO.File.ReadAllText(KnowFile) : null;
            if (System.IO.File.Exists(KnowFile)) System.IO.File.Delete(KnowFile);
            Items.Knowledge.Reset();
            _main.ToggleOnFoot();
            return true;
        }
        double before = _booksT;
        _booksT += delta;
        bool At(double t) => before < t && _booksT >= t;
        var pack = _main.Walker!.Body.Pack;
        var inv = _main.InventoryWindow!;
        Items.Recipe R(string id) => Items.Recipes.All.First(x => x.Id == id);
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("book_kuchnia_podlaska", 1), ("potatoes", 3), ("onion", 2), ("luncheon_meat", 1), ("saucepan", 1),
                         ("kitchen_knife", 1), ("camping_stove", 1), ("gas_canister", 4) })
                pack.Add(id, n);
            bool ok = Items.Recipes.Make(R("kartacze"), pack, out var said);
            Log.Info("Test", $"books: kartacze before reading: {(ok ? "made" : "not made")}: {said}");
            _main.OpenInventory(null);
        }
        if (At(1.6)) inv.ScrollCraftingToEnd();
        if (At(2)) Shot("books_list");
        if (At(2.5)) { inv.Use("book_kuchnia_podlaska"); Log.Info("Test", $"books: reading: {inv.Making?.Name}, {inv.Making?.Seconds} s"); }
        if (At(10)) Shot("books_reading");
        if (At(18.5))
        {
            Log.Info("Test", $"books: after reading: busy {inv.Busy}; kartacze known {Items.Knowledge.Known(R("kartacze"))}, placki known {Items.Knowledge.Known(R("placki"))}, molotov known {Items.Knowledge.Known(R("molotov"))}");
            Items.Knowledge.Reset();   // read it back from the file
            Log.Info("Test", $"books: from the file: kartacze known {Items.Knowledge.Known(R("kartacze"))}");
            foreach (var id in new[] { "placki", "kartacze" })
            {
                bool ok = Items.Recipes.Make(R(id), pack, out var said);
                Log.Info("Test", $"books: {id}: {(ok ? "made" : "not made")}: {said}");
            }
            inv.Use("book_kuchnia_podlaska");
            Log.Info("Test", $"books: read again: busy {inv.Busy}");
            inv.Refresh();
        }
        if (At(19.3)) Shot("books_after");
        if (At(19.8)) { inv.Close(); RestoreKnowledge(); return false; }
        return true;
    }
    private bool _heat;
    private string? _woodBackup;
    private Power.Campfires.Fire? _fire;
    private Vector3 _fireSpot;

    private void PressF()
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F, Pressed = false });
    }

    /// <summary>A dry spot (world x, z) with something (a tree, water) about a metre ahead along +dir, nearest first,
    /// within <paramref name="r"/> m; null if none.</summary>
    private (double X, double Z, double Tx, double Tz)? FindAhead(double px, double pz, float r, Func<double, double, (double, double)?> target)
    {
        var w = _main.World!;
        for (float ring = 0; ring <= r; ring += 6)
            for (int k = 0; k < Math.Max(1, (int)(ring * 1.2f)); k++)
            {
                double a = k / Math.Max(1.0, ring * 1.2) * Math.Tau;
                double x = px + Math.Cos(a) * ring, z = pz + Math.Sin(a) * ring;
                if (target(x, z) is not { } t) continue;
                return (x, z, t.Item1, t.Item2);
            }
        return null;
    }

    /// <summary>--autotest=heat (D111), best with --place=Białowieża (trees and ponds): cooking refused without heat; an
    /// axe chops a tree (photographed) into firewood; a campfire built and rice boiled over it (photographed by day and by
    /// night); a bottle filled at the water; river water drunk raw (it hurts) and boiled on a camping stove; the fire
    /// burns out and is relit. user://trees_used.json is backed up and restored. True while running.</summary>
    private bool HeatTest(double delta)
    {
        string tf = ProjectSettings.GlobalizePath("user://trees_used.json");
        if (_heatT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _heatT = 0;
            _woodBackup = System.IO.File.Exists(tf) ? System.IO.File.ReadAllText(tf) : null;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _heatT;
        _heatT += delta;
        bool At(double t) => before < t && _heatT >= t;
        var w = _main.Walker!;
        var body = w.Body;
        var pack = body.Pack;
        var world = _main.World!;
        Items.Recipe R(string id) => Items.Recipes.All.First(x => x.Id == id);
        void Make(string id)
        {
            bool ok = Items.Recipes.Make(R(id), pack, out var said);
            Log.Info("Test", $"heat: {id}: {(ok ? "made" : "not made")}: {said}");
        }
        void StandAt(double x, double z, double tx, double tz)
        {
            w.GlobalPosition = WorldOrigin.ToLocal(x, world.Height(x, z) + 0.1, z);
            w.Face(WorldOrigin.ToLocal(tx, world.Height(tx, tz) + 1.2, tz));
        }
        if (At(1))
        {
            pack.Stacks.Clear();
            foreach (var (id, n) in new[] { ("newspaper", 2), ("matches", 1), ("axe", 1), ("saucepan", 1), ("rice", 2), ("empty_bottle", 2), ("tea", 1), ("coffee_mug", 1), ("water", 2) })
                pack.Add(id, n);
            body.Health = 90;
            Power.Woodland.Reset();
            if (System.IO.File.Exists(tf)) System.IO.File.Delete(tf);
            Make("boil_rice");
        }
        if (At(1.5))
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            var spot = FindAhead(px, pz, 200, (x, z) =>
            {
                if (!float.IsNaN(world.Water(x, z)) || world.InBuilding(x, z, 0.5f)) return null;
                // a tree 1.2-1.5 m away, east of here
                return Power.Woodland.Nearest(world, x + 1.3, z, 0.4f) is { } t ? (t.X, t.Z) : null;
            });
            if (spot is { } s)
            {
                StandAt(s.X, s.Z, s.Tx, s.Tz);
                Log.Info("Test", $"heat: a tree at {s.Tx:0.0},{s.Tz:0.0}, standing at {s.X:0.0},{s.Z:0.0}");
            }
            else Log.Info("Test", "heat: no tree within 200 m");
        }
        if (At(2.2)) PressF();
        if (At(5)) Shot("heat_chop");
        if (At(9))
        {
            Log.Info("Test", $"heat: chopped: firewood {pack.Count("firewood")}; again: used {Power.Woodland.Used(Main.SaveWorld, "x") == false}");
            PressF();   // the same tree again: nothing more to give
        }
        if (At(9.5))
        {
            if (pack.Count("firewood") < 3) pack.Add("firewood", 3 - pack.Count("firewood"));
            // a fire in the open: step back from the tree first
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            w.GlobalPosition -= fwd * 2.5f;
            Make("campfire");
            _fire = _main.Fires!.All.LastOrDefault();
            _fireSpot = _fire?.Node.GlobalPosition ?? Vector3.Zero;
            Log.Info("Test", $"heat: fire: {_fire != null}, heat here: {Items.Recipes.HeatHere?.Invoke(pack)}");
            Make("boil_rice");
            Make("herbata");
        }
        if (At(10.4))
        {
            // a look at it: first person, two and a half metres off
            var (fx, _, fz) = WorldOrigin.ToWorld(_fireSpot);
            var (wx2, _, wz2) = WorldOrigin.ToWorld(w.GlobalPosition);
            double dx = wx2 - fx, dz = wz2 - fz, dl = Math.Max(0.01, Math.Sqrt(dx * dx + dz * dz));
            StandAt(fx + dx / dl * 2.6, fz + dz / dl * 2.6, fx, fz);
            w.SetView(true);
            w.Face(_fireSpot + Vector3.Up * 0.2f);
        }
        if (At(11)) Shot("heat_campfire");
        if (At(11.2)) w.SetView(false);
        if (At(11.5))
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            var spot = FindAhead(px, pz, 600, (x, z) =>
            {
                if (!float.IsNaN(world.Water(x, z)) || world.InBuilding(x, z, 0.5f)) return null;
                return !float.IsNaN(world.Water(x + 1.2, z)) ? (x + 1.2, z) : null;
            });
            if (spot is { } s) { StandAt(s.X, s.Z, s.Tx + 3, s.Tz); Log.Info("Test", $"heat: water at {s.Tx:0.0},{s.Tz:0.0}"); }
            else Log.Info("Test", "heat: no water within 600 m");
        }
        if (At(12.2)) PressF();
        if (At(12.6))
        {
            Log.Info("Test", $"heat: bottles: river water {pack.Count("dirty_water")}, empty {pack.Count("empty_bottle")}");
            Shot("heat_water");
            float h0 = body.Health;
            pack.Add("dirty_water", 1);
            body.UseItem("dirty_water");
            Log.Info("Test", $"heat: drank river water raw: health {h0:0} -> {body.Health:0}");
            Make("boil_water");   // no fire here, no stove yet
            pack.Add("camping_stove", 1); pack.Add("gas_canister", 1);
            Make("boil_water");
            Log.Info("Test", $"heat: pack {Items.Inventory.Say(pack.Stacks)}");
        }
        if (At(13.5))
        {
            // back to the fire, at night
            var (fx, _, fz) = WorldOrigin.ToWorld(_fireSpot);
            StandAt(fx - 3.2, fz + 1.5, fx, fz);
            w.SetView(true);
            w.Face(_fireSpot + Vector3.Up * 0.3f);
            _main.Atmosphere.Hour = 22.3;
        }
        if (At(15.5)) Shot("heat_campfire_night");
        if (At(15.7)) w.SetView(false);
        if (At(16))
        {
            _main.Atmosphere.Hour = 12;
            if (_fire != null) _fire.Fuel = 0.5f;
        }
        if (At(17.5))
        {
            Log.Info("Test", $"heat: after the wood ran out: burning {_fire?.Burning}");
            pack.Remove("rice", 9); pack.Remove("cooked_rice", 9);   // room for an armful
            if (pack.Count("firewood") == 0) pack.Add("firewood", 1);
            var (fx, _, fz) = WorldOrigin.ToWorld(_fireSpot);
            StandAt(fx - 1.2, fz, fx, fz);
        }
        if (At(18)) PressF();
        if (At(18.5))
        {
            Log.Info("Test", $"heat: relit: burning {_fire?.Burning}, {(_fire != null ? Power.Campfires.Left(_fire) : "")} of wood; firewood left {pack.Count("firewood")}");
            if (_woodBackup != null) System.IO.File.WriteAllText(tf, _woodBackup); else if (System.IO.File.Exists(tf)) System.IO.File.Delete(tf);
            Power.Woodland.Reset();
            return false;
        }
        return true;
    }
    private bool _gen;
    private string? _genBackup;
    private Power.Generators.Gen? _g;
    private OnFoot.Bandit? _genBandit;
    private Vector3 _genFrom;
    private float _genFuel0;

    /// <summary>--autotest=generator (D109): the generators found near the start; one put down in front of you, empty:
    /// it won't start; a can of petrol poured in from the pack; started (photographed, and at night with its lamp); a
    /// bandit 80 m off comes to the noise; fuel burns; switched off, carried 3 s (speed) and put down. user://generators.json
    /// is backed up and restored. True while running.</summary>
    private bool GeneratorTest(double delta)
    {
        string gf = ProjectSettings.GlobalizePath("user://generators.json");
        if (_genT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _genT = 0;
            _genBackup = System.IO.File.Exists(gf) ? System.IO.File.ReadAllText(gf) : null;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _genT;
        _genT += delta;
        bool At(double t) => before < t && _genT >= t;
        var w = _main.Walker!;
        var gens = _main.Generators!;
        var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
        if (At(4))
        {
            var found = gens.Live.ToList();
            Log.Info("Test", $"generator: {found.Count} found within {Power.Generators.Near:0} m: " + string.Join("; ", found.Take(6).Select(g => $"{g.Id} {g.Fuel:0.0} L at {g.X:0},{g.Z:0}")));
            // away from the car first, then one on the verge ahead and to the left, turned to show its panel
            var (sx, _, sz) = WorldOrigin.ToWorld(w.GlobalPosition + fwd * 7f);
            w.GlobalPosition = WorldOrigin.ToLocal(sx, _main.World!.Height(sx, sz) + 0.2, sz);
            var side0 = fwd.Cross(Vector3.Up);
            _g = gens.PlaceHere(w.GlobalPosition + fwd * 3.5f - side0 * 1.4f, w.Yaw + 0.6f, 0, can: true);
        }
        if (At(4.5)) w.SetView(true);
        if (At(4.7)) Shot("generator_can");
        if (At(4.8)) w.SetView(false);
        if (At(5))
        {
            // walk up to it: the prompt
            w.GlobalPosition = _g!.Node!.GlobalPosition - fwd * 1.4f + fwd.Cross(Vector3.Up) * 0.7f + Vector3.Up * 0.2f;
            var canGen = gens.CanInReach(_g.CanNode!.GlobalPosition + Vector3.Up * 0.3f);
            bool took = canGen != null && gens.TakeCan(canGen, w.Body.Pack, out _);
            Log.Info("Test", $"generator: the can beside it: in reach {canGen == _g}, taken {took}, pack petrol {w.Body.Pack.Count("petrol_can")}, can left {_g.Can}");
        }
        if (At(5.6))
        {
            bool started = gens.Toggle(_g!, out var said);
            Log.Info("Test", $"generator: empty, start: {started} ({said}); in reach: {gens.InReach(w.GlobalPosition) == _g}; prompt: {Power.Generators.Prompt(_g!, w.Body.Pack.Count("petrol_can"))}");
            w.Body.UseItem("petrol_can");
            Log.Info("Test", $"generator: poured, fuel {_g!.Fuel:0.0} L; pack: {Items.Inventory.Say(w.Body.Pack.Stacks.Where(s => s.Id.Contains("petrol")))}");
            started = gens.Toggle(_g!, out said);
            _genFuel0 = _g!.Fuel;
            Log.Info("Test", $"generator: start: {started} ({said}); running node: {_g.Fx != null}");
            // a bandit 80 m off, round the corner of your view
            var side = fwd.Cross(Vector3.Up);
            var (bx, _, bz) = WorldOrigin.ToWorld(_g.Node!.GlobalPosition + side * 80f);
            _genBandit = _main.Bandits?.SpawnOne(WorldOrigin.ToLocal(bx, _main.World!.Height(bx, bz) + 0.3, bz));
            _genFrom = _genBandit?.GlobalPosition ?? Vector3.Zero;
        }
        if (At(7)) Shot("generator_running");
        if (At(14))
        {
            bool alive = _genBandit != null && GodotObject.IsInstanceValid(_genBandit);
            float d0 = _genFrom.DistanceTo(_g!.Node!.GlobalPosition), d1 = alive ? _genBandit!.GlobalPosition.DistanceTo(_g.Node.GlobalPosition) : -1;
            if (!alive) _genBandit = null;
            Log.Info("Test", $"generator: the bandit: {_genBandit?.State}, {d0:0} m -> {d1:0} m from it; loud: {gens.Loud}; bandit boost {_main.Bandits?.NoiseBoost}");
            Log.Info("Test", $"generator: fuel {_genFuel0:0.000} -> {_g.Fuel:0.000} L in ~8.4 s (expected {8.4f * Power.Generators.BurnPerSecond:0.000})");
            if (_genBandit != null) { _main.Bandits?.Remove(_genBandit); _genBandit.QueueFree(); _genBandit = null; }
            _main.Atmosphere.Hour = 22.5;
        }
        if (At(16)) Shot("generator_night");
        if (At(16.5))
        {
            _main.Atmosphere.Hour = 12;
            gens.Toggle(_g!, out var said);
            gens.PickUp(_g!);
            w.Burden = 1;
            _genFrom = w.GlobalPosition;
            w.Autopilot = _ => (new Vector2(0, 1), true);   // trying to sprint
            Log.Info("Test", $"generator: off ({said}), carried: {gens.Carried == _g}");
        }
        if (At(17.6)) w.SetView(true);
        if (At(18)) Shot("generator_carry");
        if (At(18.2)) w.SetView(false);
        if (At(19.5))
        {
            w.Autopilot = _ => (Vector2.Zero, false);
            float moved = (w.GlobalPosition - _genFrom).Length();
            gens.PutDown(w.GlobalPosition, w.Yaw);
            w.Burden = 0;
            Log.Info("Test", $"generator: carried {moved:0.0} m in 3 s ({moved / 3f:0.0} m/s, a run is {OnFoot.Walker.RunSpeed}); put down at {_g!.X:0.0},{_g.Z:0.0}, carried now: {gens.Carried != null}");
        }
        if (At(20))
        {
            w.Autopilot = null;
            gens.Frozen = true;   // or it saves the test's generators again on the way out
            if (_genBackup != null) System.IO.File.WriteAllText(gf, _genBackup); else if (System.IO.File.Exists(gf)) System.IO.File.Delete(gf);
            return false;
        }
        return true;
    }
    private double _takeT = -1;
    private Vehicle.Car? _takeOld, _takeNew;
    private Vector3 _takeFrom;
    private int _tipStep, _tipFlips, _tipRound;
    private double _tipAt, _tipT;
    private float _tipMaxRoll, _tipRoundRoll;
    private readonly List<string> _tipReport = new();
    private double _driveLimit = 20;
    private int _routeHint;
    private float _stuckTime, _reverseLeft, _reverseSteer;   // drive test: a car pinned against a kerb backs off and retries
    private int _reversals;
    private double _driven;
    private bool _map;
    private bool _respawn;
    private bool _walk, _shoot, _rangeTest, _loot, _banditTest;
    private double _banditTime = -1;
    private OnFoot.Bandit? _banditTarget;
    private int _banditShot;
    private double _lootTime = -1;
    private OnFoot.CacheField.Cache? _lootCache;
    private double _rangeTime = -1;
    private int _rangeTarget;
    private double _shootTime = -1;
    private int _shootShot;
    private Input.MouseModeEnum _mouseBefore;
    private double _walkTime = -1;
    private int _walkShot;
    private Vector3 _walkFrom;
    private int _round, _rounds;
    private double _roundFlown, _roundWait = -1, _distance;
    private double _mapTime = -1;

    public Tests(Main main)
    {
        _main = main;
        _shots = Main.Args.GetValueOrDefault("shots", ProjectSettings.GlobalizePath("user://shots"));
        Directory.CreateDirectory(_shots);
        if (Main.Args.TryGetValue("views", out var v))
            foreach (var s in v.Split(';', StringSplitOptions.RemoveEmptyEntries)) _views.Add(s.Split(':'));
        _fly = Main.Args.GetValueOrDefault("autotest", "").Contains("fly");
        _map = Main.Args.GetValueOrDefault("autotest", "").Contains("map");
        _drive = Main.Args.GetValueOrDefault("autotest", "").Contains("drive");
        _tip = Main.Args.GetValueOrDefault("autotest", "").Contains("tip");
        _take = Main.Args.GetValueOrDefault("autotest", "").Contains("takecar");
        _craft = Main.Args.GetValueOrDefault("autotest", "").Contains("craft");
        if (Main.Args.ContainsKey("autotest"))
        {
            // self-tests practise too (D113): their XP goes to a scratch file, never the player's
            Items.Skills.FilePath = "user://skills_selftest.json";
            Core.WorldClock.FilePath = "user://clock_selftest.json";   // and their play time (D115)
            string cf = ProjectSettings.GlobalizePath(Core.WorldClock.FilePath);
            if (System.IO.File.Exists(cf)) System.IO.File.Delete(cf);
            Core.WorldClock.Reset();
            string sf = ProjectSettings.GlobalizePath(Items.Skills.FilePath);
            if (System.IO.File.Exists(sf)) System.IO.File.Delete(sf);
            Items.Skills.Reset();
        }
        _gen = Main.Args.GetValueOrDefault("autotest", "").Contains("generator");
        _heat = Main.Args.GetValueOrDefault("autotest", "").Contains("heat");
        _books = Main.Args.GetValueOrDefault("autotest", "").Contains("books");
        _skills = Main.Args.GetValueOrDefault("autotest", "").Contains("skills");
        _bigos = Main.Args.GetValueOrDefault("autotest", "").Contains("bigos");
        _fresh = Main.Args.GetValueOrDefault("autotest", "").Contains("fresh");
        _ui = Main.Args.GetValueOrDefault("autotest", "").Contains("ui") && Main.Args.GetValueOrDefault("autotest", "") != "build";   // "build" has "ui" in it (D128)
        _wells = Main.Args.GetValueOrDefault("autotest", "").Contains("wells");
        _fuel = Main.Args.GetValueOrDefault("autotest", "").Contains("fuel");
        _pumps = Main.Args.GetValueOrDefault("autotest", "").Contains("pumps");
        _boot = Main.Args.GetValueOrDefault("autotest", "").Contains("boot");
        _fridge = Main.Args.GetValueOrDefault("autotest", "").Contains("fridge");
        _build = Main.Args.GetValueOrDefault("autotest", "") == "build";
        _respawn = Main.Args.GetValueOrDefault("autotest", "").Contains("respawn");
        _walk = Main.Args.GetValueOrDefault("autotest", "").Contains("walk");
        _shoot = Main.Args.GetValueOrDefault("autotest", "").Contains("shoot");
        _rangeTest = Main.Args.GetValueOrDefault("autotest", "").Contains("range");
        _loot = Main.Args.GetValueOrDefault("autotest", "").Contains("loot");
        _banditTest = Main.Args.GetValueOrDefault("autotest", "").Split(',').Contains("bandits");
        _banditLook = Main.Args.GetValueOrDefault("autotest", "").Contains("banditlook");
        _tpGun = Main.Args.GetValueOrDefault("autotest", "").Contains("tpgun");
        _death = Main.Args.GetValueOrDefault("autotest", "").Contains("death");
        _crouchTest = Main.Args.GetValueOrDefault("autotest", "").Contains("crouch");
        _carBandits = Main.Args.GetValueOrDefault("autotest", "").Contains("carbandits");
        _soundTest = Main.Args.GetValueOrDefault("autotest", "").Contains("sound");
        _leadTest = Main.Args.GetValueOrDefault("autotest", "").Contains("leads");
        _leanTest = Main.Args.GetValueOrDefault("autotest", "").Contains("lean");
        _ragdollTest = Main.Args.GetValueOrDefault("autotest", "").Contains("ragdoll");
        // muted: Tom works while tests run
        if (!Main.Args.ContainsKey("sound")) AudioServer.SetBusVolumeDb(0, -80);
        _rounds = int.Parse(Main.Args.GetValueOrDefault("rounds", "6"), CultureInfo.InvariantCulture);
        _distance = double.Parse(Main.Args.GetValueOrDefault("distance", "3000"), CultureInfo.InvariantCulture);
        _speed = double.Parse(Main.Args.GetValueOrDefault("speed", "40"), CultureInfo.InvariantCulture);
    }

    private int _shootFired;
    private readonly HashSet<string> _shootFlashTaken = new();

    /// <summary>The shooting test (D40): 8 s per gun.</summary>
    private void Shoot(double delta)
    {
        if (_shootTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "shoot: could not get out of the car"); _shoot = false; return; }
            _main.Walker!.SetView(true);
            _shootTime = 0;
            return;
        }
        var g = _main.Walker!.Guns;
        double before = _shootTime;
        _shootTime += delta;
        string[] ids = { "mosin", "ak", "pistol" };
        int gun = (int)(_shootTime / 8);
        if (gun >= ids.Length)
        {
            Log.Info("Test", $"shoot: done, {g.Fired} fired, {g.Hits} hit");
            g.TestAim = g.TestTrigger = false;
            _shoot = false;
            return;
        }
        double t = _shootTime - gun * 8, b = before - gun * 8;
        bool Now(double x) => b < x && t >= x;
        string id = ids[gun];
        if (g.Current?.Id != id && t < 0.5) { g.Equip(id); Log.Info("Test", $"shoot: {id} equipped"); }
        // the aim button must not let the mouse go (Tom: the free camera freed it on release)
        g.Arms.DebugOn = true;
        if (Main.CaptureMouse && gun == 0 && Now(0.2)) { Input.MouseMode = Input.MouseModeEnum.Captured; _mouseBefore = Input.MouseMode; }
        if (Main.CaptureMouse && gun == 0 && Now(0.3)) Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        if (Main.CaptureMouse && gun == 0 && Now(0.5)) Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        if (Main.CaptureMouse && gun == 0 && Now(0.7)) Log.Info("Test", $"shoot: mouse {_mouseBefore} before a right click, {Input.MouseMode} after it");
        if (Now(0.8)) Shot($"shoot_{id}_hip");
        // level the view on the street 15 m ahead before each gun: recoil from the last one would carry over
        if (Now(0.9)) { var w = _main.Walker!; w.Face(w.GlobalPosition + new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw)) * 15f); }
        if (Now(1.0)) g.TestAim = true;
        if (Now(1.8)) Shot($"shoot_{id}_aim");
        if (Now(2.0)) g.TestTrigger = true;
        if (Now(id == "ak" ? 2.5 : 2.05)) g.TestTrigger = false;
        if (Now(2.02)) Shot($"shoot_{id}_fire");
        // the first frame after a shot really left (D66: the muzzle light)
        if (g.Fired > _shootFired && !_shootFlashTaken.Contains(id)) { _shootFlashTaken.Add(id); Shot($"shoot_{id}_flash"); }
        _shootFired = g.Fired;
        if (id == "mosin" && (Now(2.55) || Now(2.75) || Now(2.95))) { Shot($"shoot_mosin_bolt_{(Now(2.55) ? 1 : Now(2.75) ? 2 : 3)}"); Log.Info("Test", "arm bolt " + g.Arms.DebugRight); }   // the bolt cycle, three moments
        if (Now(1.8)) Log.Info("Test", $"arm {id} aim " + g.Arms.DebugRight);
        if (Now(3.2)) { Log.Info("Test", $"shoot: {id}: {g.Status}; fired {g.Fired}, hits {g.Hits}, last: {g.LastHit}"); g.TestAim = false; }
        if (Now(3.6)) g.StartReload();
        if (Now(3.6 + (g.Current?.ReloadTime ?? 2) * 0.5)) Shot($"shoot_{id}_reload");
        if (Now(7.6)) Log.Info("Test", $"shoot: {id} after reload: {g.Status}");
    }

    /// <summary>The range test (D42): 2.5 s per target: face it, aim, fire one shot, look.</summary>
    private void RangeTest(double delta)
    {
        if (_rangeTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "range: could not get out of the car"); _rangeTest = false; return; }
            _main.Walker!.SetView(true);
            _rangeTime = 0;
            return;
        }
        var w = _main.Walker!;
        var g = w.Guns;
        double before = _rangeTime;
        _rangeTime += delta;
        bool Now(double t) => before < t && _rangeTime >= t;
        if (Now(0.5))
        {
            var r = _main.SetUpRange();
            Log.Info("Test", $"range: {r?.Targets.Count ?? 0} targets set up");
            g.Equip("ak");
        }
        if (Now(2.0))
        {
            // the whole range, from a step back and a little up
            w.Face(_main.Range!.Targets[^1].Node.GlobalPosition + Vector3.Up * 1.2f);
            Shot("range_overview");
        }
        var targets = _main.Range?.Targets;
        if (targets == null || _rangeTime < 3) return;
        int i = (int)((_rangeTime - 3) / 2.5);
        double t = (_rangeTime - 3) - i * 2.5, b = (before - 3) - i * 2.5;
        bool At(double x) => b < x && t >= x;
        if (i >= targets.Count)
        {
            g.TestAim = g.TestTrigger = false;
            Shot("range_after");
            Log.Info("Test", $"range: done, {g.Fired} fired, {g.TargetHits} target hits");
            _rangeTest = false;
            return;
        }
        var (name, node, aim) = targets[i];
        if (At(0.01))
        {
            if (name.StartsWith("plate 100")) g.Equip("mosin");
            g.TestAim = true;
        }
        // keep looking at the target (it may have moved)
        if (t < 1.3 && GodotObject.IsInstanceValid(node)) w.Face(node.GlobalPosition + aim + Vector3.Up * (name.StartsWith("plate 100") ? 0.06f : 0f));
        // long shots: hold the breath first, as a player would (D65: the sights drift now)
        if (At(0.3) && name.StartsWith("plate")) g.TestBreath = true;
        if (At(1.0)) { g.TestTrigger = true; _rangeHitsBefore = g.TargetHits; }
        if (At(1.06)) { g.TestTrigger = false; g.TestBreath = false; }
        if (At(1.25) && (name.StartsWith("bottle 2") || name.StartsWith("plate 25") || name.StartsWith("paper"))) Shot($"range_{name.Replace(' ', '_')}");
        if (At(2.3)) Log.Info("Test", $"range: {name}: {(g.TargetHits > _rangeHitsBefore ? "hit, " + g.LastTarget : "missed")} ({g.LastHit})");
    }
    private int _rangeHitsBefore;

    /// <summary>Bends where a road's inner edge folds: the sideways shift of the edge at a corner (half width times
    /// tan of half the turn) is longer than the shorter piece beside it.</summary>
    private void RoadScan()
    {
        var w = _main.World!;
        var (fx, fz) = _main.Focus;
        double radius = Num(Main.Args.GetValueOrDefault("radius", "3000"));
        var found = new List<(double X, double Z, int Folds, string What)>();
        int roads = 0, bends = 0;
        int tx0 = (int)Math.Floor((fx - radius) / Map.MapTile.Size), tx1 = (int)Math.Floor((fx + radius) / Map.MapTile.Size);
        int tz0 = (int)Math.Floor((fz - radius) / Map.MapTile.Size), tz1 = (int)Math.Floor((fz + radius) / Map.MapTile.Size);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!w.Data.HasTile(tx, tz)) continue;
                var tile = w.Data.Tile(tx, tz);
                foreach (var r in tile.Roads)
                {
                    if (r.IsRail || r.IsTram || r.X.Length < 3) continue;
                    roads++;
                    float hw = r.HalfWidth + 0.6f;   // with the shoulder
                    int folds = 0; double worstX = 0, worstZ = 0; float worstTurn = 0;
                    for (int i = 1; i < r.X.Length - 1; i++)
                    {
                        var a = new Vector2(r.X[i] - r.X[i - 1], r.Z[i] - r.Z[i - 1]);
                        var b = new Vector2(r.X[i + 1] - r.X[i], r.Z[i + 1] - r.Z[i]);
                        float la = a.Length(), lb = b.Length();
                        if (la < 0.05f || lb < 0.05f) continue;
                        float turn = MathF.Acos(Mathf.Clamp(a.Dot(b) / (la * lb), -1, 1));
                        if (turn < 0.05f) continue;
                        bends++;
                        if (hw * MathF.Tan(MathF.Min(turn, 2.8f) / 2) > MathF.Min(la, lb))
                        {
                            folds++;
                            if (turn > worstTurn) { worstTurn = turn; worstX = tile.OriginX + r.X[i]; worstZ = tile.OriginZ + r.Z[i]; }
                        }
                    }
                    if (folds > 0 && (worstX - fx) * (worstX - fx) + (worstZ - fz) * (worstZ - fz) < radius * radius)
                        found.Add((worstX, worstZ, folds, $"class {r.Cls} '{r.Name}' width {r.HalfWidth * 2:0.0} m, worst turn {worstTurn * 180 / MathF.PI:0} deg"));
                }
            }
        // --find=industrial: industrial buildings of three sizes to look at (D63), seen from 45 m off at 25 m up
        if (Main.Args.GetValueOrDefault("find", "") == "industrial")
        {
            var picks = new List<string>();
            foreach (var (lo, hi) in new[] { (300f, 900f), (900f, 3000f), (3000f, 20000f), (300f, 900f), (900f, 3000f), (3000f, 20000f) })
            {
                bool done = false;
                for (int tx = tx0; tx <= tx1 && !done; tx++)
                    for (int tz = tz0; tz <= tz1 && !done; tz++)
                    {
                        if (!w.Data.HasTile(tx, tz)) continue;
                        var tile = w.Data.Tile(tx, tz);
                        foreach (var b in tile.Buildings)
                        {
                            if (b.Type != Map.BuildingKind.Industrial || b.Ghost || b.IsPart) continue;
                            float area = (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ);
                            if (area < lo || area > hi) continue;
                            string v = $"{tile.OriginX + b.Cx:0}:{tile.OriginZ + b.MaxZ + 45:0}:25:0:-22";
                            if (picks.Contains(v) || picks.Count(p2 => p2.StartsWith($"{tile.OriginX + b.Cx:0}")) > 0) continue;
                            if (Hash.Of((int)b.Cx, (int)b.Cz) % 7 != (uint)(picks.Count % 7)) continue;
                            picks.Add(v); done = true; break;
                        }
                    }
            }
            Log.Info("Test", "roadscan: industrial --views=\"" + string.Join(";", picks) + "\"");
        }
        // --find=tram: a few tram tracks to look at (D58)
        if (Main.Args.GetValueOrDefault("find", "") == "tram")
        {
            var trams = new List<string>();
            for (int tx = tx0; tx <= tx1 && trams.Count < 4; tx++)
                for (int tz = tz0; tz <= tz1 && trams.Count < 4; tz++)
                {
                    if (!w.Data.HasTile(tx, tz)) continue;
                    var tile = w.Data.Tile(tx, tz);
                    foreach (var r in tile.Roads)
                        if (r.IsTram && r.Layer == 0 && r.X.Length > 3 && trams.Count < 4)
                        {
                            int m = r.X.Length / 2;
                            double x = tile.OriginX + r.X[m], z = tile.OriginZ + r.Z[m];
                            float yaw = MathF.Atan2(-(r.X[m + 1] - r.X[m]), -(r.Z[m + 1] - r.Z[m])) * 180 / MathF.PI;
                            trams.Add($"{x + 6:0}:{z:0}:4:{yaw + 20:0}:-6");
                        }
                }
            Log.Info("Test", "roadscan: trams --views=\"" + string.Join(";", trams) + "\"");
        }
        Log.Info("Test", $"roadscan: {roads} roads, {bends} bends, {found.Count} roads with folding bends within {radius:0} m");
        foreach (var cls in found.GroupBy(f => f.What.Split(' ')[1]).OrderByDescending(g => g.Count()))
            Log.Info("Test", $"roadscan: class {cls.Key}: {cls.Count()} roads, {cls.Sum(f => f.Folds)} folding bends");
        var worst = found.OrderByDescending(f => f.Folds).Take(10).ToList();
        foreach (var f in worst) Log.Info("Test", $"roadscan: {f.X:0},{f.Z:0}: {f.Folds} folding bends, {f.What}");
        Log.Info("Test", "roadscan: --views=\"" + string.Join(";", worst.Take(6).Select(f => $"{f.X:0}:{f.Z:0}:35:0:-80")) + "\"");
    }

    private double _gbTime = -1;
    private (double X, double Z) _gbAt;
    private readonly List<double> _gpuOn = new(), _gpuOff = new();

    /// <summary>--autotest=grassbench (D59): the grassiest spot within 2 km of the start (meadow or park all round), at
    /// eye height; a slow full turn with grass on, then off, measuring the GPU's time per frame; screenshots.</summary>
    private void GrassBench(double delta)
    {
        var rid = _main.GetViewport().GetViewportRid();
        if (_gbTime < 0)
        {
            if (_settledFor < 1.5) return;
            var w = _main.World!;
            var (fx, fz) = _main.Focus;
            int best = -1;
            for (int i = -20; i <= 20; i++)
                for (int j = -20; j <= 20; j++)
                {
                    double x = fx + i * 100, z = fz + j * 100;
                    int score = 0;
                    for (int a = -2; a <= 2; a++)
                        for (int b = -2; b <= 2; b++)
                        {
                            byte l = w.LandAt(x + a * 15, z + b * 15);
                            if (l == Map.MapLand.Meadow || l == Map.MapLand.Park) score++;
                        }
                    if (score > best) { best = score; _gbAt = (x, z); }
                }
            Log.Info("Test", $"grassbench: grassiest spot {_gbAt.X:0},{_gbAt.Z:0} ({best}/25 samples meadow or park)");
            if (!_main.Flying) _main.ToggleFreeCamera();
            _main.Teleport(_gbAt.X, _gbAt.Z, 1.7f, 0, -8);
            RenderingServer.ViewportSetMeasureRenderTime(rid, true);
            _gbTime = 0; _settledFor = 0;
            return;
        }
        if (_settledFor < 2 && _gbTime == 0) return;
        double before = _gbTime;
        _gbTime += delta;
        bool Now(double t) => before < t && _gbTime >= t;
        var g = _main.Grass;
        // 0-10 s grass on, 10-12 settle, 12-22 off
        if (_gbTime < 10 || (_gbTime > 12 && _gbTime < 22))
        {
            double turn = (_gbTime % 12) / 10 * 360;
            _main.Camera.Yaw = Mathf.DegToRad((float)turn);
            (_gbTime < 10 ? _gpuOn : _gpuOff).Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        if (Now(1)) Shot("grass_on_a");
        if (Now(4)) Shot("grass_on_b");
        if (Now(10)) { Log.Info("Test", $"grassbench: {g.Instances} clumps in {g.Pending} pending"); g.On = false; }
        if (Now(13)) Shot("grass_off_a");
        if (Now(22))
        {
            double Med(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[c.Count / 2]; }
            double P95(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[(int)(c.Count * 0.95)]; }
            Log.Info("Test", $"grassbench: GPU ms per frame, grass on: median {Med(_gpuOn):F2}, p95 {P95(_gpuOn):F2}; off: median {Med(_gpuOff):F2}, p95 {P95(_gpuOff):F2}");
            Log.Info("Test", "done"); _main.GetTree().Quit(0);
        }
    }

    private double _fbTime = -1;
    private readonly List<double> _fbOn = new(), _fbOff = new();

    /// <summary>--autotest=farbench (D61): 90 m over Służewiec, a full turn with the far buildings shown and then
    /// hidden, GPU time per frame.</summary>
    private void FarBench(double delta)
    {
        var rid = _main.GetViewport().GetViewportRid();
        if (_fbTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.Flying) _main.ToggleFreeCamera();
            var at = Main.Args.GetValueOrDefault("at", "68700,645500").Split(',');
            _main.Teleport(Num(at[0]), Num(at[1]), 90, 0, -6);
            RenderingServer.ViewportSetMeasureRenderTime(rid, true);
            _fbTime = 0; _settledFor = 0;
            return;
        }
        if (_fbTime == 0 && _settledFor < 3) return;
        double before = _fbTime;
        _fbTime += delta;
        bool Now(double t) => before < t && _fbTime >= t;
        var far = _main.Content.Far!;
        if (_fbTime < 10 || (_fbTime > 12 && _fbTime < 22))
        {
            _main.Camera.Yaw = Mathf.DegToRad((float)((_fbTime % 12) / 10 * 360));
            (_fbTime < 10 ? _fbOn : _fbOff).Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        if (Now(10)) { Log.Info("Test", $"farbench: {far.Buildings} far buildings"); far.SetVisible(false); }
        if (Now(22))
        {
            double Med(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[c.Count / 2]; }
            double P95(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[(int)(c.Count * 0.95)]; }
            Log.Info("Test", $"farbench: GPU ms per frame, far buildings shown: median {Med(_fbOn):F2}, p95 {P95(_fbOn):F2}; hidden: median {Med(_fbOff):F2}, p95 {P95(_fbOff):F2}");
            Log.Info("Test", "done"); _main.GetTree().Quit(0);
        }
    }

    private double _bpTime = -1;
    private OnFoot.Bandit? _bp;
    private double _bpReached = -1;

    /// <summary>--autotest=banditpath [--nav=0] (D62): a bandit on the far side of a real building from you, told
    /// where you are; does it come round? Logged every 2 s; success when it gets within 15 m with a clear view.</summary>
    private void BanditPath(double delta)
    {
        if (_bpTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "banditpath: no walker"); _main.GetTree().Quit(1); return; }
            var w = _main.Walker!;
            var world = _main.World!;
            var (wx, _, wz) = WorldOrigin.ToWorld(w.GlobalPosition);
            // a building 25-60 m long within 250 m, with open ground both sides
            Map.MapBuilding? pick = null; double bx = 0, bz = 0;
            var tile = world.Data.TileAt(wx, wz);
            foreach (var b in tile.Buildings)
            {
                if (b.Ghost || b.IsPart) continue;
                float sx = b.MaxX - b.MinX, sz = b.MaxZ - b.MinZ;
                if (sx < 25 || sx > 60 || sz > 25) continue;
                double cx = tile.OriginX + b.Cx, cz = tile.OriginZ + b.Cz;
                if ((cx - wx) * (cx - wx) + (cz - wz) * (cz - wz) > 250 * 250) continue;
                // across the building north-south: both spots open
                double n = tile.OriginZ + b.MinZ - 6, s2 = tile.OriginZ + b.MaxZ + 6;
                if (world.InBuilding(cx, n, 1f) || world.InBuilding(cx, s2, 1f)) continue;
                pick = b; bx = cx; bz = cz; break;
            }
            if (pick == null) { Log.Error("Test", "banditpath: no suitable building"); _main.GetTree().Quit(1); return; }
            double nZ = tile.OriginZ + pick.MinZ - 6, sZ = tile.OriginZ + pick.MaxZ + 6;
            w.GlobalPosition = WorldOrigin.ToLocal(bx, world.Height(bx, sZ) + 0.5, sZ);
            var bandit = WorldOrigin.ToLocal(bx, world.Height(bx, nZ) + 0.3, nZ);
            _bp = _main.Bandits!.SpawnOne(bandit);
            _bp.Heard(w.GlobalPosition, 30);
            Log.Info("Test", $"banditpath: building {pick.MaxX - pick.MinX:0}x{pick.MaxZ - pick.MinZ:0} m at {bx:0},{bz:0}; you south, bandit north, {w.GlobalPosition.DistanceTo(bandit):0} m apart; routes {(OnFoot.BanditNav.Enabled ? "on" : "off")}");
            w.SetView(false);
            _bpTime = 0;
            return;
        }
        double before = _bpTime;
        _bpTime += delta;
        var wk = _main.Walker!;
        wk.Body.Health = 100;   // a test, not a fight
        if (_bp == null || !GodotObject.IsInstanceValid(_bp)) return;
        float d = _bp.GlobalPosition.DistanceTo(wk.GlobalPosition);
        if (_bpReached < 0 && d < 15 && _bp.State == OnFoot.Bandit.Mode.Engage) _bpReached = _bpTime;
        if ((int)(_bpTime / 2) != (int)(before / 2))
        {
            if (_bp.State != OnFoot.Bandit.Mode.Engage) _bp.Heard(wk.GlobalPosition, 30);   // keep it coming
            Log.Info("Test", $"banditpath: {_bpTime:0} s: {d:0.0} m, {_bp.State}, route legs {_bp.RouteLegs}");
        }
        if ((int)(_bpTime) == 8 && (int)before == 7) { wk.Face(_bp.GlobalPosition + Vector3.Up); Shot("banditpath_8s"); }
        if (_bpTime > 30 || (_bpReached > 0 && _bpTime > _bpReached + 2))
        {
            wk.Face(_bp.GlobalPosition + Vector3.Up);
            Shot("banditpath_end");
            Log.Info("Test", _bpReached > 0 ? $"banditpath: REACHED you in {_bpReached:0.0} s" : $"banditpath: did not reach you in 30 s (ended {d:0} m away)");
            Log.Info("Test", "done"); _main.GetTree().Quit(0);
        }
    }

    private double _brTime = -1;
    private readonly List<float> _brSway = new();
    private Vector3 _brMarkLocal;

    /// <summary>--autotest=breath (D65): Mosin aimed; sway measured normally, holding the breath, then catching it
    /// (largest drift of the eye from its mean over each 2 s); field of view held. Then bullets into the car, the car
    /// moved 10 m: do the holes go with it?</summary>
    private void BreathTest(double delta)
    {
        if (_brTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _main.GetTree().Quit(1); return; }
            _main.Walker!.Guns.Equip("mosin");
            _main.Walker.Guns.TestAim = true;
            _brTime = 0;
            return;
        }
        var w = _main.Walker!; var g = w.Guns;
        double before = _brTime;
        _brTime += delta;
        bool Now(double t) => before < t && _brTime >= t;
        float Spread() { if (_brSway.Count == 0) return 0; float m = _brSway.Average(); float r = _brSway.Max(x => MathF.Abs(x - m)); _brSway.Clear(); return r * 1000; }
        _brSway.Add(w.AimSway.X + w.AimSway.Y * 0.7f);
        if (Now(1)) _brSway.Clear();
        if (Now(3)) { Log.Info("Test", $"breath: aiming: sway {Spread():0.00} mrad, fov {w.FirstPersonCamera.Fov:0.0}"); g.TestBreath = true; }
        if (Now(3.6)) _brSway.Clear();
        if (Now(5.6)) { Log.Info("Test", $"breath: holding: sway {Spread():0.00} mrad, fov {w.FirstPersonCamera.Fov:0.0}, breath {g.Breath:0.00}, holding {g.Holding}"); Shot("breath_holding"); }
        if (Now(7.5)) Log.Info("Test", $"breath: after 4.5 s of holding: holding {g.Holding}, breath {g.Breath:0.00}, catching breath {g.Gasp:0.0} s");
        if (Now(7.6)) { g.TestBreath = false; _brSway.Clear(); }
        if (Now(9.6)) Log.Info("Test", $"breath: catching breath: sway {Spread():0.00} mrad, fov {w.FirstPersonCamera.Fov:0.0}, gasp left {g.Gasp:0.0} s");
        if (Now(14)) { Log.Info("Test", $"breath: recovered: gasp {g.Gasp:0.0} s, breath {g.Breath:0.00}"); g.TestAim = false; }
        // holes in the car
        var car = _main.Car!;
        if (Now(14.5))
        {
            var target = car.GlobalPosition + Vector3.Up * 0.8f;
            w.GlobalPosition = car.GlobalPosition + car.GlobalBasis.X * 6f + Vector3.Up * 0.3f;
            w.Face(target);
        }
        if (Now(15)) { g.TestAim = true; }
        if (Now(15.6)) g.TestTrigger = true;
        if (Now(15.7)) g.TestTrigger = false;
        if (Now(17))
        {
            var marks = car.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.Mesh is QuadMesh q && q.Size.X < 0.1f).ToList();
            Log.Info("Test", $"breath: bullet holes on the car: {marks.Count}");
            if (marks.Count > 0) _brMarkLocal = car.ToLocal(marks[0].GlobalPosition);
            car.GlobalPosition += car.GlobalBasis.Z * -10f;
        }
        if (Now(18))
        {
            var marks = car.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.Mesh is QuadMesh q && q.Size.X < 0.1f).ToList();
            if (marks.Count > 0) Log.Info("Test", $"breath: after moving the car 10 m the hole is {car.ToLocal(marks[0].GlobalPosition).DistanceTo(_brMarkLocal):0.00} m from where it was on the car");
            Log.Info("Test", "done"); _main.GetTree().Quit(0);
        }
    }

    private double _wrTime = -1;
    private Vehicle.Car? _wreck;

    /// <summary>--autotest=wrecks (D69): on foot, wait for wrecks round about, go to the nearest, strip a tyre from it
    /// in the workshop, then fit it on your own car (whose front-left tyre is taken off first).</summary>
    private void WrecksTest(double delta)
    {
        if (_wrTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _main.GetTree().Quit(1); return; }
            _wrTime = 0;
            return;
        }
        double before = _wrTime;
        _wrTime += delta;
        bool Now(double t) => before < t && _wrTime >= t;
        var w = _main.Walker!;
        if (Now(6))
        {
            var all = _main.Wrecks!.Live.ToList();
            Log.Info("Test", $"wrecks: {all.Count} near: " + string.Join("; ", all.Select(c => $"{c.Name} {c.GlobalPosition.DistanceTo(w.GlobalPosition):0} m, missing {c.Parts.All.Count(p => p.Missing)}")));
            _wreck = all.Where(c => c.Parts.All.Any(p => p.Kind == Vehicle.PartKind.Tyre && !p.Missing)).OrderBy(c => c.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            if (_wreck == null) { Log.Error("Test", "wrecks: none with a tyre"); _main.GetTree().Quit(1); return; }
            var side = _wreck.GlobalBasis.X;
            w.GlobalPosition = _wreck.GlobalPosition - side * 2.6f + Vector3.Up * 0.5f;
            w.SetView(false); w.CameraOrbit = 0.6f;
        }
        if (Now(7) && _wreck != null) w.Face(_wreck.GlobalPosition + Vector3.Up * 0.8f);
        if (Now(9)) { Shot("wreck_seen"); Log.Info("Test", $"wrecks: the nearest car to the walker is {_main.NearestCar()?.Name}"); }
        if (Now(9.5) && _wreck != null) _main.OpenWorkshop(_wreck);
        var ws = _main.Workshop;
        if (Now(11) && _wreck != null)
        {
            var tyre = _wreck.Parts.All.First(p => p.Kind == Vehicle.PartKind.Tyre && !p.Missing);
            ws!.Pick(tyre);
            Shot("wreck_workshop");
            Log.Info("Test", $"wrecks: stripping {tyre.Name} ({tyre.Condition:0} %): {ws.StartJob(tyre)}");
        }
        if (Now(17))
        {
            Log.Info("Test", $"wrecks: carrying {string.Join(", ", ws!.Carried.Select(c => $"{c.Name} {c.Condition:0} %"))}");
            ws.End();
            var car = _main.Car!;
            car.Parts.Tyre(0).Missing = true; car.PartsChanged();
            ws.Carried.RemoveAll(c => c.Name == "Spare tyre");
            w.GlobalPosition = car.GlobalPosition - car.GlobalBasis.X * 2.6f + Vector3.Up * 0.5f;
        }
        if (Now(18.5))
        {
            _main.OpenWorkshop(_main.Car);
            var t0 = _main.Car!.Parts.Tyre(0);
            var fit = ws!.Carried.FirstOrDefault(c => c.Kind == Vehicle.PartKind.Tyre);
            Log.Info("Test", $"wrecks: fitting {fit?.Name} on your car: {(fit != null && ws.StartJob(t0, fit))}");
        }
        if (Now(25)) { var t0 = _main.Car!.Parts.Tyre(0); Log.Info("Test", $"wrecks: your front-left tyre: missing {t0.Missing}, {t0.Condition:0} %"); ws!.Pick(t0); Shot("wreck_fitted"); }
        if (Now(26)) { ws!.End(); Log.Info("Test", "done"); _main.GetTree().Quit(0); }
    }

    private double _wsTime = -1;

    /// <summary>--autotest=workshop (D68): out of the car, the workshop opened; the front-left tyre taken off (its wheel
    /// gone, a flat in the parts' eyes), then the spare fitted; screenshots of each.</summary>
    private void WorkshopTest(double delta)
    {
        if (_wsTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _main.GetTree().Quit(1); return; }
            _wsTime = 0;
            return;
        }
        double before = _wsTime;
        _wsTime += delta;
        bool Now(double t) => before < t && _wsTime >= t;
        var car = _main.Car!;
        var ws = _main.Workshop;
        var tyre = car.Parts.Tyre(0);
        if (Now(1)) { _main.OpenWorkshop(); Log.Info("Test", $"workshop: open {_main.Workshop?.Open}, carrying {string.Join(", ", _main.Workshop!.Carried.Select(c => $"{c.Name} {c.Condition:0} %"))}"); }
        if (Now(3)) Shot("workshop_open");
        // ghosts (D68): one part per shot, each at a different condition
        if (_wsTime > 18.5 && _wsTime < 30)
        {
            var list = new (string Id, float Cond)[] { ("hood", 92), ("door.0", 55), ("windshield", 20), ("tyre.0", 70), ("engine", 8), ("window.0", 100) };
            int k = (int)((_wsTime - 18.5) / 1.8);
            if (k < list.Length)
            {
                var part = car.Parts[list[k].Id];
                if (part.Condition != list[k].Cond) { part.Condition = list[k].Cond; ws!.Pick(part); }
                if (Now(18.5 + k * 1.8 + 1.4)) Shot($"ghost_{list[k].Id}");
            }
        }
        if (Now(3.5)) { tyre.Condition = 62; Log.Info("Test", $"workshop: removing the front-left tyre: {ws!.StartJob(tyre)}"); }
        if (Now(5.5)) Shot("workshop_removing");
        if (Now(10)) Log.Info("Test", $"workshop: tyre missing {tyre.Missing}, flat in the parts {car.Parts.Flat(0)}, carrying {string.Join(", ", ws!.Carried.Select(c => $"{c.Name} {c.Condition:0} %"))}");
        if (Now(10.5)) Shot("workshop_removed");
        if (Now(11))
        {
            var spare = ws!.Carried.First(c => c.Name == "Spare tyre");
            Log.Info("Test", $"workshop: fitting the spare: {ws.StartJob(tyre, spare)}");
        }
        if (Now(17)) { Log.Info("Test", $"workshop: tyre missing {tyre.Missing}, condition {tyre.Condition:0} %, carrying {string.Join(", ", ws!.Carried.Select(c => $"{c.Name} {c.Condition:0} %"))}"); Shot("workshop_fitted"); }
        if (Now(31)) { ws!.End(); Log.Info("Test", $"workshop: closed, open {ws.Open}"); Log.Info("Test", "done"); _main.GetTree().Quit(0); }
    }

    private double _cpTime = -1;
    private int _cpStage;
    private double _cpT0, _cpX0, _cpZ0;
    private readonly List<string> _cpResults = new();

    /// <summary>--autotest=carparts (D67): runs on a straight stretch (the drive test's start); each a few seconds.
    /// 0-60 km/h with a good and a bad engine; braking from 60 with good and worn brakes; 6 s straight with a flat front
    /// tyre (how far it pulls); bullets at a tyre, the windshield and the hood. Checks that nothing wore by itself.</summary>
    private void CarPartsTest(double delta)
    {
        var car = _main.Car;
        if (car == null) return;
        if (_cpTime < 0) { if (_settledFor < 1.5) return; _cpTime = 0; _cpStage = 0; car.Recover(); _cpStart = car.GlobalTransform; return; }
        _cpTime += delta;
        var parts = car.Parts;
        float kmh = MathF.Abs(car.Speed) * 3.6f;
        var (cx, _, cz) = WorldOrigin.ToWorld(car.GlobalPosition);
        void Reset() { foreach (var p in parts.All) { p.Condition = 100; p.Missing = false; } }
        void Next(string result) { if (result.Length > 0) { _cpResults.Add(result); Log.Info("Test", "carparts: " + result); } _cpStage++; _cpT0 = _cpTime; car.Warp(_cpStart); car.Autopilot = null; }
        double t = _cpTime - _cpT0;
        switch (_cpStage)
        {
            case 0: case 1:   // acceleration, good then bad engine
                if (t < 0.05) { Reset(); if (_cpStage == 1) parts["engine"].Condition = 30; }
                car.Autopilot = _ => (1f, 0f, 0f);
                if (kmh >= 60 || t > 20) Next($"0-60 km/h with the engine at {parts["engine"].Condition:0} %: {t:0.0} s{(kmh < 60 ? " (didn't reach it)" : "")}");
                break;
            case 2: case 3:   // braking from 60, good then worn
                if (t < 0.05) { Reset(); if (_cpStage == 3) { parts["brakes.front"].Condition = 10; parts["brakes.rear"].Condition = 10; } _cpX0 = 0; }
                if (_cpX0 == 0) { car.Autopilot = _ => (1f, 0f, 0f); if (kmh >= 60) { _cpX0 = cx; _cpZ0 = cz; } }
                else { car.Autopilot = _ => (0f, 1f, 0f); if (kmh < 1) Next($"braking from 60 km/h with the brakes at {parts["brakes.front"].Condition:0} %: {Math.Sqrt((cx - _cpX0) * (cx - _cpX0) + (cz - _cpZ0) * (cz - _cpZ0)):0.0} m"); }
                if (t > 30) Next("braking: timed out");
                break;
            case 4: case 5:   // straight line, all good then a flat front-left
                if (t < 0.05) { Reset(); if (_cpStage == 5) parts.Tyre(0).Condition = 0; _cpX0 = cx; _cpZ0 = cz; _cpT0b = car.GlobalBasis; }
                car.Autopilot = _ => (t < 3 ? 0.6f : 0.3f, 0f, 0f);
                if (t > 6)
                {
                    var moved = new Vector3((float)(cx - _cpX0), 0, (float)(cz - _cpZ0));
                    float side = moved.Dot(_cpT0b.X), ahead = moved.Dot(-_cpT0b.Z);
                    Next($"6 s with no steering, {(parts.Flat(0) ? "flat front-left tyre" : "all tyres good")}: {ahead:0} m ahead, {side:+0.0;-0.0} m to the side");
                }
                break;
            case 6:   // bullets
                if (t < 0.05) { Reset(); car.Autopilot = _ => (0f, 1f, 0f); }
                if (t > 1 && t < 1.1)
                {
                    var b = car.GlobalBasis; var o = car.GlobalPosition;
                    string Fire(Vector3 at) => car.Hit(new OnFoot.BulletHit(at, Vector3.Up, -b.Z * 800, 0.008f, 30, 30)) ?? "-";
                    var said = new List<string>
                    {
                        Fire(car.ToGlobal(new Vector3(0f, 0.55f, -2.1f))),   // hood
                        Fire(o + b.Z * -0.9f + Vector3.Up * 1.6f),   // windshield
                        Fire(car.ToGlobal(new Vector3(-0.9f, 0.4f, -1.4f))),   // front-left wheel
                        Fire(car.ToGlobal(new Vector3(-0.9f, 0.4f, -1.4f))),
                    };
                    Next("bullets hit: " + string.Join(", ", said) + " → " + parts.Report());
                }
                break;
            case 7:
                Log.Info("Test", "carparts: done");
                _main.GetTree().Quit(0);
                break;
        }
        // nothing should wear on its own while driving the tests
        // (the flat-tyre run is left out: with nobody steering it pulls off the road into the roadside, a real crash)
        if (_cpStage < 6 && _cpStage != 5 && t > 0.1)
        {
            var worn = parts.All.Where(p => p.Condition < 100 && p.Condition != 30 && p.Condition != 10 && p.Condition != 0).ToList();
            if (worn.Count > 0 && !_cpWornLogged) { _cpWornLogged = true; Log.Info("Test", "carparts: WORE BY ITSELF: " + string.Join(", ", worn.Select(p => $"{p.Name} {p.Condition:0}"))); }
        }
    }
    private Basis _cpT0b;
    private Transform3D _cpStart;
    private bool _cpWornLogged;

    private double _knockTime = -1, _knockHitAt = -1;
    private OnFoot.Bandit? _knockA, _knockB;
    private bool _knockOut;

    /// <summary>--autotest=knockdown (D80): two bandits on the road 40 m ahead, one healthy and one nearly dead; the car
    /// drives into the first at ~45 km/h and brakes; it should fly, lie a few seconds and get up angry. Then the car
    /// backs up and drives into the second, which should stay down. Photographed from the car and then on foot.</summary>
    private void KnockdownTest(double delta)
    {
        if (_main.Car is not { } car || _main.Bandits is not { } field || _main.World is not { } map) return;
        Vector3 Ground(Vector3 at) { var (x, _, z) = WorldOrigin.ToWorld(at); return WorldOrigin.ToLocal(x, map.Height(x, z), z); }
        if (_knockTime < 0)
        {
            if (_settledFor < 2.5) return;
            _knockTime = 0;
            var fwd = (-car.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            var side = fwd.Cross(Vector3.Up);
            _knockA = field.SpawnOne(Ground(car.GlobalPosition + fwd * 24));
            Log.Info("Test", $"knockdown: a bandit 24 m ahead; hood {car.Parts["hood"].Health:0.00}");
            car.Autopilot = c =>
            {
                // phases: 0 at A, 1 stop and watch A, 2 at B, 3 stop
                if (_kPhase is 1 or 3) return MathF.Abs(c.Speed) > 0.5f ? (0f, 1f, 0f) : (0f, 0f, 0f);
                var target = _kPhase == 0 ? _knockA! : _knockB!;
                var to = target.GlobalPosition - c.GlobalPosition;
                var f = -c.GlobalTransform.Basis.Z;
                float steer = Mathf.Clamp(f.Cross(to.Normalized()).Y * 0.8f, -0.35f, 0.35f);
                return (MathF.Abs(c.Speed) < 12.5f ? 1f : 0f, 0f, steer);
            };
            return;
        }
        double before = _knockTime;
        _knockTime += delta;
        double t = _knockTime - _kPhaseAt, tb = before - _kPhaseAt;
        bool At(double x) => tb < x && t >= x;
        void Phase(int p) { _kPhase = p; _kPhaseAt = _knockTime; Log.Info("Test", $"knockdown: phase {p} at {_knockTime:0.0} s"); }
        switch (_kPhase)
        {
            case 0:
                if (_knockTime < 7 && (int)before != (int)_knockTime) Shot($"knock_run_{(int)_knockTime}");
                if (_knockA!.KnockCount > 0)
                {
                    Log.Info("Test", $"knockdown: A hit, state {_knockA.State}, health {_knockA.Health:0}; hood {car.Parts["hood"].Health:0.00}");
                    Phase(1);
                }
                break;
            case 1:
                if (At(0.15)) Shot("knock_0_15s");
                if (At(0.5)) Shot("knock_0_5s");
                if (At(1.2)) { Shot("knock_1_2s"); Log.Info("Test", $"knockdown: A at 1.2 s: body {_knockA!.BodyAt.DistanceTo(_knockA.GlobalPosition):0.0} m from the feet, state {_knockA.State}"); }
                if (t > 1.5 && !_knockOut && MathF.Abs(car.Speed) < 1f)
                {
                    _knockOut = _main.ToggleOnFoot();
                    if (_knockOut && _main.Walker is { } w0)
                    {
                        // watch from 5 m beside the body, across the road from the car
                        var body = _knockA!.BodyAt;
                        var away = (body - car.GlobalPosition) with { Y = 0 };
                        var spot = Ground(body + away.Normalized().Cross(Vector3.Up) * 5f + away.Normalized() * 2f);
                        w0.GlobalPosition = spot + Vector3.Up * 0.3f;
                        Log.Info("Test", $"knockdown: out of the car at {t:0.0} s, watching from 5 m");
                    }
                }
                if (_knockOut && _main.Walker is { } w)
                {
                    w.SetView(false);
                    w.Face(_knockA!.BodyAt);
                    foreach (var x in new[] { 3.0, 4.0, 5.0, 5.5, 6.0, 6.5, 7.5 })
                        if (At(x)) { Shot($"knock_{(int)(x * 10)}"); Log.Info("Test", $"knockdown: A at {x:0.0} s: {_knockA.State}, health {_knockA.Health:0}, {w.GlobalPosition.DistanceTo(_knockA.BodyAt):0.0} m from you"); }
                }
                if (At(9))
                {
                    Log.Info("Test", $"knockdown: A after 9 s: {_knockA!.State} (should be up), health {_knockA.Health:0}");
                    if (_knockOut && _main.ToggleOnFoot()) _knockOut = false;
                    var fwd = (-car.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                    _knockB = field.SpawnOne(Ground(car.GlobalPosition + fwd * 35));
                    _knockB.Health = 8;
                    Phase(2);
                }
                break;
            case 2:
                if (_knockB!.KnockCount > 0)
                {
                    Log.Info("Test", $"knockdown: B hit, state {_knockB.State} (should be Dead), killed {field.Killed}");
                    Phase(3);
                }
                if (t > 25) { Log.Error("Test", "knockdown: never reached B"); _main.GetTree().Quit(); }
                break;
            case 3:
                if (At(0.3)) Shot("knock_b_0_3s");
                if (At(1.0)) Shot("knock_b_1s");
                if (At(4))
                {
                    Log.Info("Test", $"knockdown: B after 4 s: {_knockB!.State}; A {_knockA!.State} {_knockA.Health:0}; hood {car.Parts["hood"].Health:0.00}, windshield {car.Parts["windshield"].Health:0.00}; done");
                    car.Autopilot = null;
                    _main.GetTree().Quit();
                }
                break;
        }
        if ((int)(before / 3) != (int)(_knockTime / 3))
            Log.Info("Test", $"knockdown: {_knockTime:0} s: {car.Speed * 3.6f:0} km/h, A {_knockA!.GlobalPosition.DistanceTo(car.GlobalPosition):0} m {_knockA.State}, B {(_knockB == null ? "-" : $"{_knockB.GlobalPosition.DistanceTo(car.GlobalPosition):0} m {_knockB.State}")}");
        if (_knockTime > 70) { Log.Error("Test", "knockdown: timed out"); _main.GetTree().Quit(); }
    }
    private int _kPhase;
    private double _kPhaseAt;

    private bool _ragdollTest;
    private double _ragTime = -1;
    private OnFoot.Bandit? _ragBandit;
    private (double X, double Y, double Z) _ragWorld;

    /// <summary>The ragdoll test (D56).</summary>
    private void RagdollTest(double delta)
    {
        if (_ragTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "ragdoll: could not get out of the car"); _ragdollTest = false; return; }
            _ragTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _ragTime;
        _ragTime += delta;
        bool Now(double t) => before < t && _ragTime >= t;
        string Pos(Vector3 v) { var (x, y, z) = WorldOrigin.ToWorld(v); return $"{x:0.0},{y:0.00},{z:0.0}"; }
        if (Now(0.5))
        {
            int n = _main.Bandits!.SpawnAhead(w.GlobalPosition, w.Yaw, 9);
            _ragBandit = _main.Bandits.All.LastOrDefault();
            Log.Info("Test", $"ragdoll: {n} bandits 9 m ahead");
        }
        if (Now(1.5) && _ragBandit != null)
        {
            Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps before the shot (4 ragdolls built, asleep)");
            // the bullet comes from where you stood; you watch from 7 m to the side
            var dir = (_ragBandit.GlobalPosition - w.GlobalPosition) with { Y = 0 };
            var side = dir.Normalized().Cross(Vector3.Up);
            w.GlobalPosition = _ragBandit.GlobalPosition + side * 7f + Vector3.Up * 0.3f;
            w.SetView(true); w.Face(_ragBandit.GlobalPosition + Vector3.Up * 0.6f);
            _ragBandit.Hit(new OnFoot.BulletHit(_ragBandit.GlobalPosition + Vector3.Up * 1.3f, -dir.Normalized(), dir.Normalized() * 800, 0.008f, 9, 500));
            Log.Info("Test", $"ragdoll: shot; feet at {Pos(_ragBandit.GlobalPosition)}, body at {Pos(_ragBandit.BodyAt)}");
        }
        if (_ragBandit != null)
        {
            if (Now(1.65)) Shot("ragdoll_0_15s");
            if (Now(1.9)) Shot("ragdoll_0_4s");
            if (Now(2.5)) Shot("ragdoll_1s");
            if (Now(5.0)) Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps with a ragdoll lying there");
            if (Now(5.5))
            {
                Shot("ragdoll_4s");
                float ground = _main.World!.Height(WorldOrigin.ToWorld(_ragBandit.BodyAt).X, WorldOrigin.ToWorld(_ragBandit.BodyAt).Z);
                Log.Info("Test", $"ragdoll: after 4 s the body is at {Pos(_ragBandit.BodyAt)}, {_ragBandit.BodyAt.DistanceTo(_ragBandit.GlobalPosition):0.00} m from the feet, {WorldOrigin.ToWorld(_ragBandit.BodyAt).Y - ground:0.00} m above the ground");
                _ragWorld = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                // the floating origin jumps 3 km away (the walker's own position will pull it back next frame)
                WorldOrigin.Follow(WorldOrigin.X + 3500, WorldOrigin.Z);
                var (x, y, z) = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                Log.Info("Test", $"ragdoll: origin moved: body now at {x:0.0},{y:0.00},{z:0.0} (was {_ragWorld.X:0.0},{_ragWorld.Y:0.00},{_ragWorld.Z:0.0})");
            }
            if (Now(6.5))
            {
                var (x, y, z) = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                Log.Info("Test", $"ragdoll: a second later, origin back: body at {x:0.0},{y:0.00},{z:0.0}, moved {Math.Sqrt((x - _ragWorld.X) * (x - _ragWorld.X) + (z - _ragWorld.Z) * (z - _ragWorld.Z)):0.00} m");
                Shot("ragdoll_after_shift");
            }
        }
        if (Now(7))
        {
            foreach (var b in _main.Bandits!.All) b.QueueFree();
            _main.Bandits.Clear();
            w.CameraOrbit = 0; w.SetView(true);
            w.LastShot = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw)) * -800;   // from ahead: falls backwards
            w.Body.Damage(500);
        }
        if (Now(7.2)) Shot("ragdoll_eyes_0_2s");
        if (Now(7.6)) Shot("ragdoll_eyes_0_6s");
        if (Now(8.9)) Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps while you lie there");
        if (Now(9.0)) { Shot("ragdoll_eyes_2s"); Log.Info("Test", $"ragdoll: you fell, body at {Pos(w.BodyAt)}, eye at {Pos(w.FirstPersonCamera.GlobalPosition)}"); }
        if (Now(9.5)) _main.Respawn();
        if (Now(14))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null)
            {
                w.GlobalPosition = pkg.GlobalPosition + new Vector3(2.2f, 0.5f, 1.2f);
                w.SetView(false);
            }
        }
        if (Now(18))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null) w.Face(pkg.GlobalPosition);
        }
        if (Now(18.4))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            var sk = pkg?.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
            if (sk != null)
            {
                float hips = (sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("Hips")).Origin).Y, head = (sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("Head")).Origin).Y;
                Log.Info("Test", $"ragdoll: the body left behind: hips {hips - pkg!.GlobalPosition.Y:0.00} m, head {head - pkg.GlobalPosition.Y:0.00} m above the package; you {w.GlobalPosition.DistanceTo(pkg.GlobalPosition):0.0} m away, {w.DebugPose()[..60]}");
            }
        }
        if (Now(18.5)) { Shot("ragdoll_left_behind"); Log.Info("Test", "ragdoll: done"); _ragdollTest = false; }
    }

    private bool _leanTest;
    private double _leanTime = -1;

    /// <summary>The lean test (D55).</summary>
    private void LeanTest(double delta)
    {
        if (_leanTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "lean: could not get out of the car"); _leanTest = false; return; }
            _main.Walker!.SetView(true);
            _main.Walker.Guns.Equip("ak");
            _leanTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _leanTime;
        _leanTime += delta;
        bool Now(double t) => before < t && _leanTime >= t;
        void Report(string what) => Log.Info("Test", $"lean: {what}: lean {w.Lean:0.00}, eye {w.LeanOffset:0.00} m to the side, camera at {w.FirstPersonCamera.GlobalPosition - w.GlobalPosition}");
        if (Now(1.0)) { Shot("lean_fp_upright"); Report("upright"); w.TestLean = -1; }
        if (Now(2.0)) { Shot("lean_fp_left"); Report("left"); w.TestLean = 1; }
        if (Now(3.0)) { Shot("lean_fp_right"); Report("right"); w.TestLean = 0; w.SetView(false); w.CameraOrbit = MathF.PI; }
        if (Now(4.0)) { Shot("lean_tp_front_upright"); w.TestLean = -1; }
        if (Now(5.0)) { Shot("lean_tp_front_left"); Report("third person, left"); w.TestLean = 1; }
        if (Now(6.0)) { Shot("lean_tp_front_right"); Report("third person, right"); w.CameraOrbit = 0; }
        if (Now(7.0)) { Shot("lean_tp_back_right"); w.TestLean = -1; }
        if (Now(8.0)) { Shot("lean_tp_back_left"); w.TestLean = null; Log.Info("Test", "lean: done"); _leanTest = false; }
    }

    private bool _leadTest;
    private double _leadTime = -1;

    /// <summary>The leads test (D54).</summary>
    private void LeadTest(double delta)
    {
        if (_leadTime < 0)
        {
            if (_settledFor < 1.5 || _main.Leads == null) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "leads: could not get out of the car"); _leadTest = false; return; }
            _leadTime = 0;
            return;
        }
        var w = _main.Walker!;
        var leads = _main.Leads!;
        double before = _leadTime;
        _leadTime += delta;
        bool Now(double t) => before < t && _leadTime >= t;
        if (Now(0.5)) leads.Look(w.GlobalPosition, "crate", 0.3, 0.6);
        var lead = leads.All.FirstOrDefault();
        if (Now(3))
        {
            if (lead == null) { Log.Error("Test", "leads: no lead found"); _leadTest = false; return; }
            Log.Info("Test", $"leads: lead {lead.Id}: {lead.Where} ({lead.Kind}), {lead.Gang}");
            _main.MapView!.Toggle(); _main.MapView.SetZoom(1);
        }
        if (Now(4.5)) Shot("leads_map");
        if (Now(5)) _main.MapView!.Toggle();
        if (Now(5.5) && lead != null)
        {
            // 150 m short of it, on the line from where you are
            var target = WorldOrigin.ToLocal(lead.X, 0, lead.Z);
            var from = w.GlobalPosition; from.Y = 0; target.Y = 0;
            var p = target + (from - target).Normalized() * 150f;
            var hit = w.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 300, p + Vector3.Down * 300, Bodies.Ground | Bodies.Roads));
            // no ground collision that far out yet: keep the height you're at (the town is flat) until it streams in
            w.GlobalPosition = (hit.Count > 0 ? (Vector3)hit["position"] : p with { Y = w.GlobalPosition.Y + 1f }) + Vector3.Up * 0.5f;
            w.Velocity = Vector3.Zero;
        }
        if (Now(9) && lead != null)
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            Log.Info("Test", $"leads: you are {Math.Sqrt((px - lead.X) * (px - lead.X) + (pz - lead.Z) * (pz - lead.Z)):0} m from the place");
            if (lead.Stash != null) { var (sx, sy, sz) = WorldOrigin.ToWorld(lead.Stash.GlobalPosition); Log.Info("Test", $"leads: place {lead.X:0},{lead.Z:0}; stash {sx:0},{sy:0},{sz:0} (local {lead.Stash.GlobalPosition}); you {px:0},{pz:0} (local {w.GlobalPosition})"); }
            var guards = _main.Bandits!.All.Where(b => b.Guarding == lead.Id).ToList();
            Log.Info("Test", $"leads: stage {lead.Stage}, stash {(lead.Stash != null ? $"at {lead.Stash.GlobalPosition.DistanceTo(w.GlobalPosition):0} m" : "none")}, {guards.Count} guards: "
                + string.Join(", ", guards.Select(b => $"{b.Gun.Id} {b.GlobalPosition.DistanceTo(lead.Stash?.GlobalPosition ?? b.GlobalPosition):0} m from it")));
            if (lead.Stash != null)
            {
                // 12 m off, third person, looking at the camp
                var s = lead.Stash.GlobalPosition;
                var away = w.GlobalPosition - s; away.Y = 0;
                w.GlobalPosition = s + away.Normalized() * 12f + Vector3.Up * 0.5f;
                w.SetView(false);
            }
        }
        if (Now(9.3) && lead?.Stash != null) w.Face(lead.Stash.GlobalPosition);
        if (Now(10)) Shot("leads_camp");
        if (Now(10.5) && lead != null)
        {
            foreach (var b in _main.Bandits!.All.Where(b => b.Guarding == lead.Id && !b.Dead).ToList())
                b.Hit(new OnFoot.BulletHit(b.GlobalPosition + Vector3.Up * 1.0f, Vector3.Up, Vector3.Forward * 800, 0.008f, 12, 500));
            Log.Info("Test", $"leads: guards shot, {lead.Guards} left");
        }
        if (Now(11) && lead?.Stash != null)
        {
            var s = lead.Stash.GlobalPosition;
            var away = w.GlobalPosition - s; away.Y = 0;
            w.GlobalPosition = s + away.Normalized() * 3.5f + Vector3.Up * 0.3f;
            Log.Info("Test", $"leads: stash look: {string.Join(", ", lead.Stash.GetChildren().Select(c => c.GetType().Name + (c is MeshInstance3D m && m.Mesh != null ? " " + m.Mesh.GetAabb().Size : "")))}");
        }
        if (Now(11.3) && lead?.Stash != null) { w.SetView(true); w.Face(lead.Stash.GlobalPosition); }   // first person: the camp can be cramped
        if (Now(12)) Shot("leads_stash");
        if (Now(12.5))
        {
            bool opened = lead?.Stash != null && _main.TryOpenCache(lead.Stash);
            Log.Info("Test", $"leads: stash opened {opened}, stage {lead?.Stage}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; {w.Guns.Status}");
        }
        if (Now(13.2)) Shot("leads_taken");
        if (Now(15))
        {
            Log.Info("Test", $"leads: {leads.All.Count} leads now: " + string.Join("; ", leads.All.Select(l => $"{l.Id} {l.Stage} {l.Where}")));
            _main.MapView!.Toggle(); _main.MapView.SetZoom(3);
        }
        if (Now(16)) { Shot("leads_map_after"); _main.MapView!.Toggle(); Log.Info("Test", "leads: done"); _leadTest = false; }
    }

    /// <summary>The survival test (D46).</summary>
    private void LootTest(double delta)
    {
        if (_lootTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "loot: could not get out of the car"); _loot = false; return; }
            _lootTime = 0;
            return;
        }
        var w = _main.Walker!;
        var b = w.Body;
        double before = _lootTime;
        _lootTime += delta;
        bool Now(double t) => before < t && _lootTime >= t;
        if (Now(5))
        {
            var all = _main.Caches?.Live.ToList() ?? new();
            Log.Info("Test", $"loot: {all.Count} crates within reach of the streamer: " + string.Join("; ", all.Take(8).Select(c => $"{c.Kind} '{c.Place}' {c.GlobalPosition.DistanceTo(w.GlobalPosition):0} m")));
            _lootCache = all.OrderBy(c => c.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            if (_lootCache == null) { Log.Error("Test", "loot: no crate anywhere near"); _loot = false; return; }
            // walk up to it (a step away, facing it), third person to see it
            var to = _lootCache.GlobalPosition - w.GlobalPosition; to.Y = 0;
            w.GlobalPosition = _lootCache.GlobalPosition - to.Normalized() * 1.8f + Vector3.Up * 0.3f;
            w.SetView(false);
        }
        if (Now(5.3) && _lootCache != null) w.Face(_lootCache.GlobalPosition);
        if (Now(6.5)) Shot("loot_crate");
        if (Now(7))
        {
            var start = (b.Food, b.Water, b.Medkits);
            bool opened = _main.TryOpenCache();
            Log.Info("Test", $"loot: opened {opened} ({_lootCache?.Kind} '{_lootCache?.Place}'): food {start.Food}→{b.Food}, water {start.Water}→{b.Water}, medkits {start.Medkits}→{b.Medkits}");
        }
        if (Now(7.4)) Shot("loot_opened");
        if (Now(8))
        {
            b.Food = Math.Max(b.Food, 1); b.Water = Math.Max(b.Water, 1); b.Medkits = Math.Max(b.Medkits, 1);
            b.Hunger = 30; b.Thirst = 30;
        }
        if (Now(8.5)) Log.Info("Test", $"loot: hungry and thirsty → hunger {b.Hunger:0}, thirst {b.Thirst:0}, food {b.Food}, water {b.Water} (used by themselves)");
        if (Now(9)) { b.Damage(55); b.UseMedkit(); Log.Info("Test", $"loot: hurt to {b.Health:0}, medkit used ({b.Medkits} left)"); }
        if (Now(15))
        {
            Log.Info("Test", $"loot: health after the medkit {b.Health:0}");
            Shot("loot_hud");
            Log.Info("Test", "loot: done");
            _loot = false;
        }
    }

    private bool _banditLook, _tpGun, _death, _crouchTest, _carBandits, _soundTest;
    private double _soundTime = -1;
    private AudioEffectRecord? _rec;
    private double _carBanditTime = -1;
    private double _crouchTime = -1;
    private double _deathTime = -1;
    private double _tpTime = -1;
    private double _lookTime = -1;

    /// <summary>The game's own sound, recorded (D53).</summary>
    private void SoundTest(double delta)
    {
        if (_soundTime < 0)
        {
            if (_settledFor < 2.0) return;
            _rec = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
            AudioServer.AddBusEffect(0, _rec, 0);
            _rec.SetRecordingActive(true);
            _soundTime = 0;
            return;
        }
        var c = _main.Car!;
        double before = _soundTime;
        _soundTime += delta;
        bool Now(double t) => before < t && _soundTime >= t;
        var ac = Audio.Acoustics.Instance;
        if (Now(1)) Log.Info("Test", $"sound: in the car: place {ac?.Place}, enclosure {ac?.Enclosure:F2}, walls {ac?.Walls.Count}");
        if (Now(2)) { _main.ToggleOnFoot(); _main.Walker!.Guns.Equip(Main.Args.GetValueOrDefault("gun", "pistol")); }
        if (Now(3.5))
        {
            Log.Info("Test", $"sound: on foot: place {ac?.Place}, enclosure {ac?.Enclosure:F2}; shot at {_soundTime:F2} s; echoes due at " +
                string.Join(", ", ac!.Walls.OrderBy(w => w.Distance).Where((_, i) => i % 3 == 0).Take(5).Select(w => $"{_soundTime + 2 * w.Distance / 343:F2}")));
            _main.Walker!.Guns.TestTrigger = true;
        }
        if (Now(3.6)) _main.Walker!.Guns.TestTrigger = false;
        if (Now(6))
        {
            // out into the open: the free-camera spot over the river meadows would do; here, 60 m up for a crude "open"
            _main.ToggleOnFoot();
            c.Autopilot = car => (_soundTime < 16 ? 1f : 0f, _soundTime >= 16 ? 1f : 0f, 0f);
        }
        foreach (var t in new[] { 7.0, 9.0, 11.0, 13.0, 15.0, 17.0 })
            if (Now(t)) Log.Info("Test", $"sound: driving {MathF.Abs(c.Speed) * 3.6f:F0} km/h, throttle {c.Throttle:F1}, rpm {c.GetNodeOrNull<Audio.EngineSound>("EngineSound")?.Rpm:F0}, gear {c.GetNodeOrNull<Audio.EngineSound>("EngineSound")?.Gear}, place {ac?.Place}");
        if (Now(18))
        {
            _rec!.SetRecordingActive(false);
            var wav = _rec.GetRecording();
            string path = Path.Combine(_shots, "sound.wav");
            wav?.SaveToWav(path);
            Log.Info("Test", $"sound: recorded {wav?.GetLength():F1} s to {path}");
            c.Autopilot = null;
            _soundTest = false;
        }
    }

    /// <summary>Bandits and the car (D52).</summary>
    private void CarBanditTest(double delta)
    {
        if (_carBanditTime < 0)
        {
            if (_settledFor < 2.5 || _main.Bandits == null) return;
            _carBanditTime = 0;
            var c = _main.Car!;
            int n = _main.Bandits.SpawnAhead(c.GlobalPosition, c.Rotation.Y, 30);
            Log.Info("Test", $"carbandits: {n} spawned 30 m from the car; driving {c.Driving}; your health {_main.Walker?.Body.Health:0}");
            return;
        }
        double before = _carBanditTime;
        _carBanditTime += delta;
        bool Now(double t) => before < t && _carBanditTime >= t;
        var f = _main.Bandits!;
        string States() => string.Join(", ", f.All.Select(b => $"{b.Gun.Id} {b.State}"));
        foreach (var t in new[] { 3.0, 6.0, 10.0 })
            if (Now(t)) Log.Info("Test", $"carbandits: {t:0} s: {States()}; your health {_main.Walker?.Body.Health:0}");
        if (Now(6)) Shot("carbandits");
        if (Now(10.5)) { Log.Info("Test", "carbandits: done"); _carBandits = false; }
    }

    /// <summary>Crouching in third person (D52).</summary>
    private void CrouchTest(double delta)
    {
        if (_crouchTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _crouchTest = false; return; }
            _crouchTime = 0;
            var wk = _main.Walker!;
            wk.SetView(false);
            wk.GlobalPosition += new Vector3(-MathF.Sin(wk.Yaw), 0, -MathF.Cos(wk.Yaw)) * 8 + Vector3.Up * 0.3f;
            wk.Autopilot = w => (_crouchTime > 4 && _crouchTime < 6 ? new Vector2(0, 1) : Vector2.Zero, false);
            return;
        }
        var w = _main.Walker!;
        double before = _crouchTime;
        _crouchTime += delta;
        bool Now(double t) => before < t && _crouchTime >= t;
        w.CameraOrbit = 1.4f;
        if (Now(1)) { Shot("crouch_stand"); w.Crouch(true); }
        if (Now(3)) { Shot("crouch_still"); Log.Info("Test", $"crouch: crouching {w.Crouching}, {w.DebugPose()}"); }
        if (Now(5.5)) { Shot("crouch_walk"); Log.Info("Test", $"crouch: walking {w.Speed:F1} m/s"); }
        if (Now(6.5)) { w.Crouch(false); w.Autopilot = null; Log.Info("Test", "crouch: done"); _crouchTest = false; }
    }

    private OnFoot.Doorstep? _deathHome;
    private Vector3 _fellAt;

    /// <summary>Dying and waking (D55): the fall seen from the eyes, waking 1.5-3 km away, your things on the map,
    /// going back for them; then a safehouse claimed at a home's wall, dying again and waking there.</summary>
    private void DeathTest(double delta)
    {
        if (_deathTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _death = false; return; }
            // the player's own safehouse is kept and put back at the end
            string sf = ProjectSettings.GlobalizePath("user://safehouses.json");
            _deathSafeBackup = System.IO.File.Exists(sf) ? System.IO.File.ReadAllText(sf) : null;
            OnFoot.Safehouse.Clear(Main.SaveWorld);
            _deathTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _deathTime;
        _deathTime += delta;
        bool Now(double t) => before < t && _deathTime >= t;
        string Where(Vector3 local) { var (x, _, z) = WorldOrigin.ToWorld(local); return $"{x:0},{z:0}"; }
        if (Now(3))
        {
            // a full pack and some spare rounds, then a bullet from ahead-left
            w.Body.Food = 3; w.Body.Water = 2; w.Body.Medkits = 1;
            w.Guns.AddAmmo("ak", 40);
            w.SetView(false);
            Log.Info("Test", $"death: before: food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}, at {Where(w.GlobalPosition)}");
            w.LastShot = new Vector3(-MathF.Sin(w.Yaw) + 0.5f, 0, -MathF.Cos(w.Yaw)) * -800;
            w.Body.Damage(500);
            _fellAt = w.GlobalPosition;
        }
        if (Now(3.2)) Shot("death_falling");
        if (Now(4.0)) Shot("death_down");
        if (Now(4.5))
        {
            Log.Info("Test", $"death: down {_main.PlayerDown}, dead {w.Dead}");
            _main.Respawn();
            Log.Info("Test", $"death: woke at {Where(w.GlobalPosition)}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; health {w.Body.Health:0}");
        }
        if (Now(12)) Shot("death_awake");
        if (Now(12.5)) { _main.MapView!.Toggle(); _main.MapView.SetZoom(3); }
        if (Now(13.5)) { Shot("death_map"); _main.MapView!.Toggle(); }
        if (Now(14))
        {
            // back to the body
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg == null) { Log.Error("Test", "death: no package"); _death = false; return; }
            Log.Info("Test", $"death: package at {Where(pkg.GlobalPosition)}, {pkg.GlobalPosition.DistanceTo(w.GlobalPosition):0} m away");
            var away = new Vector3(1, 0, 0.4f).Normalized();
            w.GlobalPosition = pkg.GlobalPosition + away * 2.6f + Vector3.Up * 0.5f;
        }
        if (Now(20))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null) { w.Face(pkg.GlobalPosition + Vector3.Up * 0.3f); Shot("death_body"); }
        }
        if (Now(20.5))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            bool took = pkg != null && _main.TryOpenCache(pkg);
            Log.Info("Test", $"death: took your things {took}: food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}");
        }
        if (Now(21))
        {
            // a home nearby: stand at its wall and hold K
            var (x, _, z) = WorldOrigin.ToWorld(w.GlobalPosition);
            _deathHome = OnFoot.Homes.Pick(_main.World!, x, z, new Random(5), 20, 250);
            if (_deathHome is not { } h) { Log.Error("Test", "death: no home near"); _death = false; return; }
            w.GlobalPosition = WorldOrigin.ToLocal(h.X, _main.World!.Height(h.X, h.Z) + 0.5, h.Z);
            w.Face(w.GlobalPosition + new Vector3(MathF.Sin(h.Yaw), 0, MathF.Cos(h.Yaw)) * 3 + Vector3.Up * 1.5f);   // the wall
            Log.Info("Test", $"death: at a home: {h.Address} ({h.X:0},{h.Z:0})");
        }
        if (Now(25)) { Shot("death_claim_prompt"); _main.TestClaim = true; }
        if (Now(26)) Shot("death_claiming");
        if (Now(27.6)) { _main.TestClaim = false; Log.Info("Test", $"death: safehouse {(_main.SafehouseAt is { } sh ? $"{sh.Address} at {sh.X:0},{sh.Z:0}" : "none")}"); }
        if (Now(28)) { w.LastShot = Vector3.Zero; w.Body.Damage(500); }
        if (Now(30)) _main.Respawn();
        if (Now(34))
        {
            var (x, _, z) = WorldOrigin.ToWorld(w.GlobalPosition);
            var sh = _main.SafehouseAt;
            Log.Info("Test", $"death: woke {(sh is { } h2 ? Math.Sqrt((x - h2.X) * (x - h2.X) + (z - h2.Z) * (z - h2.Z)) : -1):0.0} m from the safehouse door");
            Shot("death_safehouse");
            string sf = ProjectSettings.GlobalizePath("user://safehouses.json");
            if (_deathSafeBackup != null) System.IO.File.WriteAllText(sf, _deathSafeBackup); else OnFoot.Safehouse.Clear(Main.SaveWorld);
            Log.Info("Test", "death: done (the player's safehouse file restored)");
            _death = false;
        }
    }

    /// <summary>The gun in the walker's hands in third person (D49).</summary>
    private void TpGunTest(double delta)
    {
        if (_tpTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _tpGun = false; return; }
            _tpTime = 0;
            var wk = _main.Walker!;
            wk.SetView(false);
            wk.Autopilot = w =>
            {
                double t = _tpTime % 6;
                return t < 1.5 ? (Vector2.Zero, false) : t < 3 ? (new Vector2(0, 0.45f), false) : t < 4.5 ? (new Vector2(0, 1), true) : (Vector2.Zero, false);
            };
            return;
        }
        var w = _main.Walker!;
        var g = w.Guns;
        double before = _tpTime;
        _tpTime += delta;
        bool Now(double t) => before < t && _tpTime >= t;
        string[] guns = { "ak", "pistol", "mosin" };
        int i = (int)(_tpTime / 6);
        if (i >= guns.Length) { w.Autopilot = null; g.TestTrigger = false; Log.Info("Test", "tpgun: done"); _tpGun = false; return; }
        double l = _tpTime - i * 6, b = before - i * 6;
        bool At(double x) => b < x && l >= x;
        if (g.Current?.Id != guns[i] && l < 0.5) g.Equip(guns[i]);
        w.CameraOrbit = 1.35f;   // from the side
        if (At(1.0)) Shot($"tpgun_{guns[i]}_stand");
        if (At(2.6)) Shot($"tpgun_{guns[i]}_walk");
        if (At(4.2)) Shot($"tpgun_{guns[i]}_sprint");
        if (At(4.8)) g.TestTrigger = true;
        if (At(5.0)) { Shot($"tpgun_{guns[i]}_fire"); g.TestTrigger = false; }
        if (At(5.5)) Log.Info("Test", $"tpgun: {guns[i]}: {g.Status}, first person {w.FirstPerson}");
    }

    /// <summary>A close look at one bandit (D47): the gun low, then shouldered at the player.</summary>
    private void BanditLook(double delta)
    {
        if (_lookTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _banditLook = false; return; }
            _lookTime = 0;
            return;
        }
        var w = _main.Walker!;
        var f = _main.Bandits!;
        double before = _lookTime;
        _lookTime += delta;
        bool Now(double t) => before < t && _lookTime >= t;
        if (Now(0.3))
        {
            // clear of the car: 8 m up the street
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            w.GlobalPosition += fwd * 8 + Vector3.Up * 0.3f;
        }
        if (Now(0.6))
        {
            w.SetView(false); w.Crouch(true);
            Log.Info("Test", $"banditlook: {f.SpawnAhead(w.GlobalPosition, w.Yaw, 6)} spawned");
            while (f.All.Count > 1) { var x = f.All[^1]; x.QueueFree(); f.Remove(x); }
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            if (f.All.Count > 0) f.All[0].Place(w.GlobalPosition + fwd * 5, w.GlobalPosition + fwd * 5);
        }
        if (_lookTime > 0.7 && f.All.Count > 0) { w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.0f); w.CameraOrbit = 0.5f; }
        if (Now(1.0)) { Shot("banditlook_relaxed"); Log.Info("Test", $"banditlook: {f.All[0].State}, {f.All[0].Gun.Id}"); }
        if (Now(1.2)) w.Crouch(false);
        if (Now(4.0)) { Shot("banditlook_aiming"); Log.Info("Test", $"banditlook: {f.All[0].State}; you {w.Body.Health:0} hp; {f.All[0].Heights()}"); }
        if (Now(4.5)) { w.CameraOrbit = 2.6f; }
        if (Now(5.0)) { Shot("banditlook_side"); Log.Info("Test", "banditlook: done"); _banditLook = false; }
    }

    /// <summary>The bandit test (D47).</summary>
    private void BanditTest(double delta)
    {
        if (_banditTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "bandits: could not get out of the car"); _banditTest = false; return; }
            _banditTime = 0;
            return;
        }
        var w = _main.Walker!;
        var f = _main.Bandits!;
        double before = _banditTime;
        _banditTime += delta;
        bool Now(double t) => before < t && _banditTime >= t;
        string States() => string.Join(", ", f.All.Select(b => $"{b.Gun.Id} {b.State} {b.Health:0} hp {b.GlobalPosition.DistanceTo(w.GlobalPosition):0} m"));
        if (Now(1))
        {
            w.SetView(false);
            int n = f.SpawnAhead(w.GlobalPosition, w.Yaw, 25);
            Log.Info("Test", $"bandits: {n} spawned ahead: {States()}");
        }
        if (Now(1.5) && f.All.Count > 0) w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.2f);
        if (Now(2)) Shot("bandits_spawned");
        // a close look in first person, aimed at one of them
        if (Now(3.5) && f.All.Count > 0) { w.SetView(true); if (w.Guns.Current?.Id != "ak") w.Guns.Equip("ak"); w.Guns.TestAim = true; }
        if (_banditTime is > 3.6 and < 4.6 && f.All.Count > 0) w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.2f);
        if (Now(4.5)) Shot("bandits_sights");
        foreach (var t in new[] { 3.0, 5.0, 7.0, 12.0, 18.0 })
            if (Now(t)) Log.Info("Test", $"bandits: {t:0} s: {States()}; you {w.Body.Health:0} hp");
        // fight back: the AK, aimed at each one still standing in turn
        if (_banditTime > 4.6 && _banditTime < 30)
        {
            if (_banditTarget == null || _banditTarget.Dead || !GodotObject.IsInstanceValid(_banditTarget))
                _banditTarget = f.All.Where(b => !b.Dead).OrderBy(b => b.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            var tb = _banditTarget;
            if (tb == null) { w.Guns.TestTrigger = false; if (_banditShot == 0) { _banditShot = 1; _banditTime = 30; } }
            else
            {
                w.Face(tb.GlobalPosition + Vector3.Up * (tb.State == OnFoot.Bandit.Mode.Engage ? 1.1f : 1.25f));
                // short bursts
                double ph = (_banditTime * 2.5) % 1.0;
                w.Guns.TestTrigger = ph < 0.3;
                if (w.Guns.Current != null && w.Guns.Status.Contains(" 0 /")) w.Guns.StartReload();
            }
        }
        if (Now(12)) Shot("bandits_fight");
        if (Now(30.2))
        {
            w.Guns.TestTrigger = false; w.Guns.TestAim = false;
            Log.Info("Test", $"bandits: after the fight: {States()}; killed {f.Killed}; you {w.Body.Health:0} hp; your shots {w.Guns.Fired}, hits on bandits {w.Guns.TargetHits} (last: {w.Guns.LastTarget})");
            var body = f.All.FirstOrDefault(b => b.Dead);
            if (body != null)
            {
                w.SetView(false);
                var to = body.GlobalPosition - w.GlobalPosition; to.Y = 0;
                w.GlobalPosition = body.GlobalPosition - to.Normalized() * 1.5f + Vector3.Up * 0.3f;
            }
        }
        if (Now(30.6)) { var body = f.All.FirstOrDefault(b => b.Dead); if (body != null) w.Face(body.GlobalPosition); }
        if (Now(31.5)) Shot("bandits_body");
        if (Now(32)) { bool opened = _main.TryOpenCache(); Log.Info("Test", $"bandits: searched a body: {opened}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; {w.Guns.Status}"); }
        if (Now(32.5)) { Shot("bandits_searched"); Log.Info("Test", "bandits: done"); _banditTest = false; }
    }

    private bool Settled => _main.Terrain.Settled && _main.Content.Pending == 0 && _main.Terrain.PendingCollision == 0
        && (_main.Content.Far?.Pending ?? 0) == 0;

    public void Process(double delta)
    {
        _elapsed += delta;
        _frames++;
        if (_elapsed > 600) { Log.Error("Test", "timed out"); _main.GetTree().Quit(2); return; }
        _settledFor = Settled ? _settledFor + delta : 0;

        if (Main.Args.GetValueOrDefault("autotest", "") == "grassbench") { GrassBench(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "farbench") { FarBench(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "banditpath") { BanditPath(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "breath") { BreathTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "carparts") { CarPartsTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "workshop") { WorkshopTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "wrecks") { WrecksTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "knockdown") { KnockdownTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "interior") { InteriorTest(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "roadscan") { if (_settledFor > 1) { RoadScan(); _main.GetTree().Quit(); } return; }
        if (_view < _views.Count && _views.Count > 0)
        {
            // --sandbox-menu: the settings menu open over the views, to photograph it (D103)
            if (Main.Args.ContainsKey("sandbox-menu") && !(_main.SandboxMenu?.IsOpen ?? true)) _main.OpenSandboxMenu();
            if (_view >= 0 && _settledFor < 1.5) return;
            if (_view >= 0) { Log.Info("Test", $"view {_view}: far buildings {_main.Content.Far?.Buildings ?? 0}"); Shot($"view{_view:00}"); if (Main.Args.ContainsKey("dump")) Log.Info("Test", "dump: " + DebugDump.Take(_main)); }
            _view++;
            if (_view < _views.Count)
            {
                var p = _views[_view];
                double x = Num(p[0]), z = Num(p[1]);
                _main.Teleport(x, z, (float)Num(p.Length > 2 ? p[2] : "40"), (float)Num(p.Length > 3 ? p[3] : "0"), (float)Num(p.Length > 4 ? p[4] : "-15"));
                _settledFor = 0;
                Log.Info("Test", $"view {_view}: {string.Join(":", p)}");
            }
            return;
        }

        if (_fly)
        {
            if (!_flyStarted)
            {
                if (_settledFor < 1.5) return;
                _flyStarted = true;
                Shot("fly_start");
                Log.Info("Test", $"fly: start, {_speed} m/s");
            }
            _frameMs.Add(delta * 1000);
            if (Main.Args.ContainsKey("profile") && (int)(_flown / 200) != (int)((_flown - _speed * delta) / 200))
            {
                var vp = _main.GetViewport().GetViewportRid();
                RenderingServer.ViewportSetMeasureRenderTime(vp, true);
                Log.Info("Test", $"fly profile {_flown:0} m: frame {delta * 1000:F1} ms, process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} ms, GPU {RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp):F1} ms, CPU render {RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp):F1} ms, draws {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}, prims {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:F1} M, far {_main.Content.Far?.Buildings ?? 0} pending {_main.Content.Far?.Pending ?? 0}");
            }
            var cam = _main.Camera;
            var step = -cam.Basis.Z with { Y = 0 };
            cam.Position += step.Normalized() * (float)(_speed * delta);
            cam.Position = cam.Position with { Y = cam.GroundHeight + 40f };
            _flown += _speed * delta;
            var (fx, fz) = _main.Focus;
            if (!_main.Terrain.HasGround(fx, fz)) _holes++;
            if (_flown >= 6000)
            {
                Shot("fly_end");
                _frameMs.Sort();
                double p50 = _frameMs[_frameMs.Count / 2], p99 = _frameMs[(int)(_frameMs.Count * 0.99)], max = _frameMs[^1];
                int over33 = _frameMs.FindAll(m => m > 33.4).Count;
                Log.Info("Test", $"fly: {_frameMs.Count} frames, median {p50:F1} ms, p99 {p99:F1} ms, worst {max:F1} ms, {over33} frames over 33 ms, {_holes} frames without collision ground");
                _fly = false;
            }
            else return;
        }
        if (_walk && _main.Car is { } wcar)
        {
            if (_walkTime < 0)
            {
                if (_settledFor < 1.5) return;
                if (!_main.ToggleOnFoot()) { Log.Error("Test", "walk: could not get out of the car"); _walk = false; return; }
                _walkTime = 0;
                _walkFrom = _main.Walker!.GlobalPosition;
                Log.Info("Test", "walk: out of the car");
                _main.Walker.Autopilot = w =>
                {
                    double t = _walkTime;
                    if (t < 5) return (new Vector2(0, 1), false);          // walk... (run speed)
                    if (t < 8) return (new Vector2(0, 1), true);           // sprint
                    if (t < 10) return (new Vector2(0.6f, 0.8f), false);   // a curve
                    return (new Vector2(0, 0), false);
                };
                return;
            }
            _walkTime += delta;
            var wk = _main.Walker!;
            double[] shots = { 0.6, 2.5, 6.5, 9.5, 11.0 };
            wk.CameraOrbit = _walkTime is > 1.5 and < 7.5 ? 1.5f : 0f;   // side view for the run and the sprint shots
            if (_walkShot < shots.Length && _walkTime >= shots[_walkShot])
            {
                Shot($"walk_{_walkShot}");
                Log.Info("Test", $"walk: {_walkTime:F1} s, {wk.Speed * 3.6f:F1} km/h, {wk.GlobalPosition.DistanceTo(_walkFrom):F1} m from where it got out, on floor {wk.IsOnFloor()}");
                _walkShot++;
                if (_walkShot == 1) Log.Info("Test", "walk pose: " + wk.DebugPose());
                if (_walkShot == 4)
                {
                    // the real keys, through Godot's input (V broke once without anyone noticing)
                    bool before = wk.FirstPerson;
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.V, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.V, Pressed = false });
                    Input.FlushBufferedEvents();
                    Log.Info("Test", $"walk: V switches the view: {(wk.FirstPerson != before ? "yes" : "NO")} (first person {before} → {wk.FirstPerson})");
                    wk.Body.Medkits = 1; wk.Body.Damage(30);
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.H, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.H, Pressed = false });
                    Input.FlushBufferedEvents();
                    Log.Info("Test", $"walk: H uses a medkit: {(wk.Body.Medkits == 0 ? "yes" : "NO")}");
                }
            }
            if (_walkTime < 12) return;
            // back to the car the quick way, then in
            wk.Autopilot = null;
            wk.GlobalPosition = wcar.GlobalPosition - wcar.GlobalTransform.Basis.X * 1.8f;
            Log.Info("Test", _main.ToggleOnFoot() ? "walk: back in the car" : "walk: could not get back in");
            Log.Info("Test", $"walk: done, driving {wcar.Driving}");
            _walk = false;
        }
        if (_shoot && _main.Car != null) { Shoot(delta); return; }
        if (_rangeTest && _main.Car != null) { RangeTest(delta); return; }
        if (_loot && _main.Car != null) { LootTest(delta); return; }
        if (_banditTest && _main.Car != null) { BanditTest(delta); return; }
        if (_banditLook && _main.Car != null) { BanditLook(delta); return; }
        if (_tpGun && _main.Car != null) { TpGunTest(delta); return; }
        if (_death && _main.Car != null) { DeathTest(delta); return; }
        if (_crouchTest && _main.Car != null) { CrouchTest(delta); return; }
        if (_carBandits && _main.Car != null) { CarBanditTest(delta); return; }
        if (_soundTest && _main.Car != null) { SoundTest(delta); return; }
        if (_leadTest && _main.Car != null) { LeadTest(delta); return; }
        if (_leanTest && _main.Car != null) { LeanTest(delta); return; }
        if (_ragdollTest && _main.Car != null) { RagdollTest(delta); return; }
        if (_respawn)
        {
            if (_main.Car == null)
            {
                if (_settledFor < 1.5) return;
                var (sx, sz) = _main.Focus;
                _main.SpawnCar(sx, sz);
                Log.Info("Test", "respawn: car placed");
                return;
            }
            if (!_main.Flying)
            {
                if (_roundWait >= 0 && (_roundWait += delta) < 4) return;   // drive here, then sit for a moment
                if (_round >= _rounds) { _respawn = false; Log.Info("Test", $"respawn: {_rounds} rounds, no crash"); }
                else
                {
                    _main.ToggleFreeCamera();
                    var cam = _main.Camera;
                    cam.Yaw = _round * 1.1f; cam.Rotation = cam.Rotation with { Y = cam.Yaw };
                    _roundFlown = 0; _roundWait = -1;
                    Log.Info("Test", $"respawn: round {_round + 1}, free camera, flying {_distance:F0} m");
                    return;
                }
            }
            else
            {
                var cam = _main.Camera;
                var step = -cam.Basis.Z with { Y = 0 };
                double move = Math.Min(_speed * delta, _distance - _roundFlown);
                cam.Position += step.Normalized() * (float)move;
                cam.Position = cam.Position with { Y = cam.GroundHeight + 40f };
                _roundFlown += move;
                if (_roundFlown < _distance) return;
                var (fx, fz) = _main.Focus;
                Log.Info("Test", $"respawn: round {_round + 1}, pressing F at {fx:F0},{fz:F0}");
                _main.ToggleFreeCamera();
                _round++;
                _roundWait = 0;
                return;
            }
        }
        if (_map && _main.MapView is { } view && _main.Nav is { } nav)
        {
            // --autotest=map [--dest=x,z]: open the map, route to the destination, screenshot at two zooms.
            if (_mapTime < 0)
            {
                if (_settledFor < 1.5) return;
                _mapTime = 0;
                var (fx, fz) = _main.Focus;
                if (Main.Args.TryGetValue("dest", out var d))
                {
                    var p = d.Split(',');
                    nav.SetDestination(new Vector2((float)Num(p[0]), (float)Num(p[1])), new Vector2((float)fx, (float)fz));
                }
                view.Toggle();
                view.SetZoom(0);   // closest: vector roads, buildings, every street name
            }
            _mapTime += delta;
            if (nav.Routing && _mapTime < 60) return;
            if (_mapTime < 1.5) return;
            if (_mapTime < 100) { Shot("map_near"); view.SetZoom(2); _mapTime = 100; return; }
            if (_mapTime < 101.5) return;
            Shot("map_far");
            Log.Info("Test", $"map: route {(nav.Route != null ? $"{nav.Route.Length / 1000f:F1} km" : "none")}");
            view.Toggle();
            _map = false;
        }
        if (_craft)
        {
            if (CraftTest(delta)) return;
            _craft = false;
        }
        if (_gen)
        {
            if (GeneratorTest(delta)) return;
            _gen = false;
        }
        if (_heat)
        {
            if (HeatTest(delta)) return;
            _heat = false;
        }
        if (_books)
        {
            if (BooksTest(delta)) return;
            _books = false;
        }
        if (_skills)
        {
            if (SkillsTest(delta)) return;
            _skills = false;
        }
        if (_bigos)
        {
            if (BigosTest(delta)) return;
            _bigos = false;
        }
        if (_fresh)
        {
            if (FreshTest(delta)) return;
            _fresh = false;
        }
        if (_ui)
        {
            if (UiTest(delta)) return;
            _ui = false;
        }
        if (_wells)
        {
            if (WellsTest(delta)) return;
            _wells = false;
        }
        if (_build)
        {
            if (BuildTest(delta)) return;
            _build = false;
            Log.Info("Test", "done"); _main.GetTree().Quit(0); return;
        }
        if (_fridge)
        {
            if (FridgeTest(delta)) return;
            _fridge = false;
            Log.Info("Test", "done"); _main.GetTree().Quit(0); return;
        }
        if (_boot)
        {
            if (BootTest(delta)) return;
            _boot = false;
            Log.Info("Test", "done"); _main.GetTree().Quit(0); return;
        }
        if (_pumps)
        {
            if (PumpsTest(delta)) return;
            _pumps = false;
            Log.Info("Test", "done"); _main.GetTree().Quit(0); return;
        }
        if (_fuel)
        {
            if (FuelTest(delta)) return;
            _fuel = false;
            Log.Info("Test", "done"); _main.GetTree().Quit(0); return;
        }
        if (_take)
        {
            if (TakeCarTest(delta)) return;
            _take = false;
        }
        if (_tip && _main.Car is { } tcar)
        {
            if (TipTest(tcar, delta)) return;
            _tip = false;
        }
        if (_drive && _main.Car is { } car && _main.World is { } map)
        {
            if (!_driveStarted)
            {
                if (_settledFor < 1.5) return;
                _driveStarted = true;
                Shot("drive_start");
                // With --dest: follow the GPS route (steer at the route point ~20 m ahead, slow for bends).
                // Without: keep to the nearest road, steering at the carriageway point 18 m ahead.
                if (Main.Args.TryGetValue("dest", out var dest) && _main.Nav is { } nav0)
                {
                    var p = dest.Split(',');
                    var (fx, fz) = _main.Focus;
                    nav0.SetDestination(new Vector2((float)Num(p[0]), (float)Num(p[1])), new Vector2((float)fx, (float)fz));
                    _driveLimit = double.Parse(Main.Args.GetValueOrDefault("seconds", "180"), CultureInfo.InvariantCulture);
                }
                car.Autopilot = c =>
                {
                    var fwd = (-c.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                    var (cx, _, cz) = World.WorldOrigin.ToWorld(c.GlobalPosition);
                    float steer = 0, target = 17f;
                    if (_main.Nav?.Route is { } route)
                    {
                        var here = new Vector2((float)cx, (float)cz);
                        _routeHint = route.Nearest(here, _routeHint, out _);
                        // a short look-ahead at low speed so sharp town corners are not cut into the kerb
                        float look = Math.Clamp(Math.Abs(c.Speed) * 0.9f, 7f, 40f);
                        int i = _routeHint;
                        while (i + 1 < route.Points.Length && route.Along[i] - route.Along[_routeHint] < look) i++;
                        // keep right: aim 1.8 m to the right of the centre line
                        var seg = route.Points[Math.Min(i + 1, route.Points.Length - 1)] - route.Points[Math.Max(i - 1, 0)];
                        var right = seg.LengthSquared() > 0.01f ? new Vector2(-seg.Y, seg.X).Normalized() : Vector2.Zero;
                        var aim = route.Points[i] + right * 1.8f;
                        var to = World.WorldOrigin.ToLocal(aim.X, 0, aim.Y) - c.GlobalPosition;
                        to.Y = 0;
                        steer = Mathf.Clamp(fwd.Cross(to.Normalized()).Y * 2.2f, -1f, 1f);
                        // speed from the sharpest bend in the next 120 m: v = sqrt(a * r) with a = 3.5 m/s²
                        float maxAng = 0;
                        for (int k = _routeHint; k + 2 < route.Points.Length && route.Along[k] - route.Along[_routeHint] < 120f; k++)
                        {
                            var a = route.Points[k + 1] - route.Points[k];
                            int m = k + 1;
                            while (m + 1 < route.Points.Length && route.Points[m].DistanceTo(route.Points[k + 1]) < 15f) m++;
                            var b = route.Points[m] - route.Points[k + 1];
                            if (a.LengthSquared() > 0.01f && b.LengthSquared() > 0.01f) maxAng = Math.Max(maxAng, Math.Abs(a.AngleTo(b)));
                        }
                        float radius = 15f / Math.Max(0.02f, maxAng);
                        target = Math.Clamp(MathF.Sqrt(3.5f * radius), 6f, 38f);
                    }
                    else if (map.NearestRoad(cx + fwd.X * 18, cz + fwd.Z * 18, 25f, 1.8f, out double px, out _, out double pz, out _, Map.RoadClass.Residential))
                    {
                        var to = World.WorldOrigin.ToLocal(px, 0, pz) - c.GlobalPosition;
                        to.Y = 0;
                        steer = Mathf.Clamp(fwd.Cross(to.Normalized()).Y * 2.5f, -1f, 1f);
                    }
                    // pinned (wanting to go, not moving): back off for a moment with the wheel the other way
                    float dt = 1f / Engine.PhysicsTicksPerSecond;
                    if (_reverseLeft > 0f)
                    {
                        _reverseLeft -= dt;
                        return (0f, 0.7f, _reverseSteer);
                    }
                    if (MathF.Abs(c.Speed) < 0.6f) _stuckTime += dt; else _stuckTime = 0f;
                    if (_stuckTime > 2.5f)
                    {
                        _stuckTime = 0f; _reverseLeft = 2.5f; _reverseSteer = -MathF.Sign(steer == 0 ? 1f : steer); _reversals++;
                        Log.Info("Test", $"drive: stuck at {cx:0},{cz:0}, backing off");
                    }
                    return (c.Speed < target ? 0.8f : 0f, c.Speed > target + 2 ? 0.5f : 0f, steer);
                };
                Log.Info("Test", "drive: start");
            }
            _driveTime += delta;
            _driven += Math.Abs(car.Speed) * delta;
            _maxSpeed = Math.Max(_maxSpeed, car.Speed);
            if (_main.Nav is { } nv && nv.Route == null && !nv.Routing && nv.Destination == null && _driveLimit > 20) _driveTime = _driveLimit; // arrived
            if (!car.OnRoad) _offRoad += delta;
            if (Math.Abs(_driveTime - 8) < delta) Shot("drive_8s");
            if (_driveTime >= 60 && (int)(_driveTime / 60) != (int)((_driveTime - delta) / 60)) Shot($"drive_{(int)_driveTime}s");
            if (_driveTime < _driveLimit) return;
            Shot("drive_end");
            var (wx, _, wz) = World.WorldOrigin.ToWorld(car.GlobalPosition);
            Log.Info("Test", $"drive: car condition after the drive: {(car.Parts.Report() is { Length: > 0 } r ? r : "all parts over 70 %")}; lowest {car.Parts.All.Min(p => p.Condition):0} %");
            Log.Info("Test", $"drive: {_driveTime:F0} s, {_driven / 1000:F2} km, {(_main.Nav?.Destination == null && _driveLimit > 20 ? "ARRIVED, " : "")}{(_main.Nav?.Route != null ? $"{_main.Nav.Remaining / 1000:F2} km left, " : "")}top {_maxSpeed * 3.6:F0} km/h, {_offRoad:F1} s off road, {_reversals} times stuck, ended at {wx:F0},{wz:F0}, upright {car.GlobalTransform.Basis.Y.Y:F2}");
            _drive = false;
        }
        if (Map.BadBuildingAdapter.Enabled) Log.Info("Test", Map.BadBuildingAdapter.Stats());
        lock (Vehicle.ParkedCars.TruckLog) if (Vehicle.ParkedCars.TruckLog.Count > 0) Log.Info("Test", $"trucks: {Vehicle.ParkedCars.TruckLog.Count} ({Vehicle.ParkedCars.TruckLog.Count(t => t.Yard)} in yards), e.g. {string.Join("; ", Vehicle.ParkedCars.TruckLog.Take(4).Select(t => $"{t.X:0},{t.Z:0}"))}");
        lock (Map.MapMesher.NestLog) if (Map.MapMesher.NestLog.Count > 0) Log.Info("Test", $"stork nests: {Map.MapMesher.NestLog.Count}, e.g. {string.Join("; ", Map.MapMesher.NestLog.Take(6).Select(n => $"{n.X:0},{n.Z:0}"))}");
        Log.Info("Test", "done");
        _main.GetTree().Quit(0);
    }

    // ---- --autotest=tip (D97) -----------------------------------------------------------------------------------
    /// <summary>The car through a double lane change (full steer one way, the other, back) at 50, 70 and 90 km/h, a
    /// full-lock turn at 40, and a handbrake turn at 50: the worst roll in each, and whether it went over. True while
    /// running.</summary>
    private bool TipTest(Vehicle.Car car, double delta)
    {
        if (!_tipStarted)
        {
            if (_settledFor < 1.5) return true;
            _tipStarted = true; _tipAt = 0; _tipT = 0;
            car.Autopilot = Pilot;
            Log.Info("Test", "tip: start");
        }
        _tipT += delta;
        var up = car.GlobalTransform.Basis.Y;
        float roll = Mathf.RadToDeg(MathF.Acos(Math.Clamp(up.Y, -1f, 1f)));
        _tipRoundRoll = MathF.Max(_tipRoundRoll, roll);
        _tipMaxRoll = MathF.Max(_tipMaxRoll, roll);
        if (up.Y < 0.3f && _tipStep != 99)
        {
            _tipFlips++;
            Shot($"tip_flip_{_tipFlips}");
            _tipReport.Add($"{Rounds[Math.Min(_tipRound, Rounds.Length - 1)].Kind} at {Rounds[Math.Min(_tipRound, Rounds.Length - 1)].Kmh} km/h: FLIPPED");
            Log.Info("Test", $"tip: {_tipReport[^1]}");
            car.Recover();
            NextRound();
        }
        if (_tipRound >= Rounds.Length)
        {
            Log.Info("Test", $"tip: {string.Join("; ", _tipReport)}");
            Log.Info("Test", $"tip: worst roll {_tipMaxRoll:0} degrees, {_tipFlips} flips");
            car.Autopilot = null;
            return false;
        }
        return true;

        (float, float, float) Pilot(Vehicle.Car c)
        {
            var r = Rounds[Math.Min(_tipRound, Rounds.Length - 1)];
            double t = _tipT - _tipAt;
            switch (_tipStep)
            {
                case 0:   // up to speed, straight
                    if (c.Speed * 3.6f >= r.Kmh) { _tipStep = 1; _tipAt = _tipT; _tipRoundRoll = 0; }
                    else if (t > 25) { _tipReport.Add($"{r.Kind} at {r.Kmh} km/h: never got up to speed"); Log.Info("Test", $"tip: {_tipReport[^1]}"); NextRound(); }
                    return (1f, 0f, 0f);
                case 1:   // the manoeuvre
                {
                    float steer = r.Kind switch
                    {
                        "lane" => t < 1.0 ? 1f : t < 2.2 ? -1f : t < 3.0 ? 1f : 0f,
                        _ => t < 2.5 ? 1f : 0f,
                    };
                    c.AutoHandbrake = r.Kind == "handbrake" && t < 2.0;
                    if (t > (r.Kind == "lane" ? 3.6 : 3.0)) { _tipStep = 2; _tipAt = _tipT; }
                    return (r.Kind == "full" ? 0.6f : 0.3f, 0f, steer);
                }
                default:  // stop, report, next
                    c.AutoHandbrake = false;
                    if (MathF.Abs(c.Speed) < 1f || t > 8)
                    {
                        _tipReport.Add($"{r.Kind} at {r.Kmh} km/h: roll {_tipRoundRoll:0} degrees");
                        Log.Info("Test", $"tip: {_tipReport[^1]}");
                        NextRound();
                    }
                    return (0f, 1f, 0f);
            }
        }
    }

    /// <summary>Out of the car, to the nearest wreck, take it (or hear why not), drive it, check the old car stayed. True
    /// while running.</summary>
    private bool TakeCarTest(double delta)
    {
        if (_takeT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _takeT = 0;
            _takeOld = _main.Car;
            _main.ToggleOnFoot();
            Log.Info("Test", "takecar: on foot, looking for wrecks");
            return true;
        }
        double before = _takeT;
        _takeT += delta;
        bool At(double t) => before < t && _takeT >= t;
        var w = _main.Walker!;
        if (At(6))   // the wrecks have had time to appear around us
        {
            // no wreck about: walk up to the nearest parked car (D100), which wakes it
            if (_main.Wrecks?.Live.Any(c => c != _takeOld) != true && _main.Parked?.Nearest(w.GlobalPosition) is { } pc)
            {
                w.GlobalPosition = pc + new Vector3(3f, 0.5f, 0);
                _main.Parked.Update(w.GlobalPosition, true, 0);
                Log.Info("Test", "takecar: walked up to a parked car");
            }
            var wreck = _main.Wrecks?.Live.Where(c => c != _takeOld).OrderBy(c => c.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            if (wreck == null) { Log.Info("Test", "takecar: no wreck within reach"); return false; }
            Log.Info("Test", $"takecar: nearest wreck {wreck.Name} ({wreck.Model.GetFile()}) {wreck.GlobalPosition.DistanceTo(w.GlobalPosition):0} m away; won't go: {Main.WontGo(wreck) ?? "-"}");
            // stand by its driver's door
            var t = wreck.GlobalTransform;
            w.GlobalPosition = t.Origin - t.Basis.X * 2.2f + Vector3.Up * 0.5f;
            _takeNew = wreck;
        }
        if (At(7) && _takeNew != null)
        {
            _main.TryTakeCar(_takeNew);
            bool took = _main.Car == _takeNew;
            Log.Info("Test", $"takecar: {(took ? "took it" : "didn't take it")}; driving {(_main.Car?.Driving ?? false)}");
            if (!took) { Shot("takecar_refused"); return false; }
            _takeFrom = _takeNew.GlobalPosition;
            _takeNew.Autopilot = c => (1f, 0f, 0f);
        }
        if (At(15) && _takeNew != null)
        {
            _takeNew.Autopilot = c => (0f, 1f, 0f);
            Shot("takecar_driving");
            Log.Info("Test", $"takecar: drove {_takeNew.GlobalPosition.DistanceTo(_takeFrom):0} m, {_takeNew.Speed * 3.6f:0} km/h, upright {_takeNew.GlobalTransform.Basis.Y.Y:0.00}");
            bool kept = _takeOld != null && GodotObject.IsInstanceValid(_takeOld) && (_main.Wrecks?.Live.Contains(_takeOld) ?? false);
            Log.Info("Test", $"takecar: the old car is {(kept ? "still there, parked" : "GONE")} {(_takeOld != null ? _takeOld.GlobalPosition.DistanceTo(_takeNew.GlobalPosition) : 0):0} m back");
        }
        if (At(19) && _takeNew != null && _takeOld != null)
        {
            // out, back to the old car, take it again
            _takeNew.Autopilot = null;
            _main.ToggleOnFoot();
            var t = _takeOld.GlobalTransform;
            w.GlobalPosition = t.Origin - t.Basis.X * 2.2f + Vector3.Up * 0.5f;
            _main.TryTakeCar(_takeOld);
            Log.Info("Test", $"takecar: back in the old car: {_main.Car == _takeOld}; the taken one left parked: {_main.Wrecks?.Live.Contains(_takeNew)}");
            return false;
        }
        return true;
    }

    /// <summary>--autotest=craft (D106, D107): ingredients in the pack, the crafting list photographed; a bandage made
    /// in real time (photographed half-way); kartacze started and interrupted by a hit; then rags, rice (water by tag,
    /// the bottle comes back), herbata (a mug is both the water holder and the cup), kartacze and a molotov (by tag: the
    /// vodka's alcohol, refused when there's none) made straight away. True while running.</summary>
    private bool CraftTest(double delta)
    {
        if (_craftT < 0)
        {
            if (_settledFor < 2.0 || _main.Car == null) return true;
            _craftT = 0;
            _main.ToggleOnFoot();
            return true;
        }
        double before = _craftT;
        _craftT += delta;
        bool At(double t) => before < t && _craftT >= t;
        var body = _main.Walker!.Body;
        var pack = body.Pack;
        var inv = _main.InventoryWindow!;
        Items.Recipe R(string id) => Items.Recipes.All.First(x => x.Id == id);
        if (At(1))
        {
            pack.Stacks.Clear();
            body.Health = 80;
            foreach (var (id, n) in new[] { ("potatoes", 2), ("onion", 2), ("luncheon_meat", 2), ("saucepan", 1), ("scissors", 1), ("matches", 1), ("camping_stove", 1), ("gas_canister", 3),
                         ("rice", 1), ("mineral_water", 2), ("sheet", 1), ("rag", 2), ("tea", 1), ("coffee_mug", 1), ("instant_soup", 1) })
                pack.Add(id, n);
            _knowBackup = System.IO.File.Exists(KnowFile) ? System.IO.File.ReadAllText(KnowFile) : null;
            foreach (var b in Items.ItemDb.All.Values.Where(d => d.Category == "book")) Items.Knowledge.Learn(b);
            Log.Info("Test", $"craft: pack {Items.Inventory.Say(pack.Stacks)}");
            _main.OpenInventory(null);
        }
        if (At(2)) Shot("craft_list");
        if (At(2.5)) { inv.Craft(R("makeshift_bandage")); Log.Info("Test", $"craft: making {inv.Making?.Id}, {inv.Making?.Seconds} s"); }
        if (At(5.0)) { Log.Info("Test", $"craft: half-way {inv.Progress:P0}, rags still {pack.Count("rag")}"); Shot("craft_progress"); }
        if (At(8.0)) Log.Info("Test", $"craft: bandage done: busy {inv.Busy}, bandages {pack.Count("makeshift_bandage")}, rags {pack.Count("rag")}");
        if (At(8.5)) inv.Craft(R("kartacze"));
        if (At(11.0))
        {
            Log.Info("Test", $"craft: kartacze at {inv.Progress:P0}; bandits! (-12 health)");
            body.Health -= 12;
        }
        if (At(11.5))
            Log.Info("Test", $"craft: after the hit: window open {inv.IsOpen}, busy {inv.Busy}, kartacze {pack.Count("kartacze")}, potatoes {pack.Count("potatoes")}");
        if (At(12.0))
        {
            _main.OpenInventory(null);
            foreach (var id in new[] { "tear_sheet", "boil_rice", "herbata", "kartacze", "molotov" })
            {
                bool ok = Items.Recipes.Make(R(id), pack, out var said);
                Log.Info("Test", $"craft: {id}: {(ok ? "made" : "not made")}: {said}");
            }
            pack.Add("vodka", 1);
            bool m = Items.Recipes.Make(R("molotov"), pack, out var said2);
            Log.Info("Test", $"craft: molotov with vodka: {(m ? "made" : "not made")}: {said2}");
            inv.Refresh();
            Log.Info("Test", $"craft: pack now {Items.Inventory.Say(pack.Stacks)}");
        }
        if (At(12.8)) Shot("craft_after");
        if (At(13.2)) { inv.Close(); RestoreKnowledge(); return false; }
        return true;
    }

    private void NextRound() { _tipRound++; _tipStep = 0; _tipAt = _tipT; _tipRoundRoll = 0; _main.Car?.Recover(); }

    private static readonly (string Kind, float Kmh)[] Rounds =
    {
        ("lane", 50), ("lane", 70), ("lane", 90), ("full", 40), ("full", 60), ("handbrake", 50),
    };

    // ---- --autotest=interior (D81) -----------------------------------------------------------------------------
    private double _iTime = -1, _iPhaseAt;
    private int _iPhase;
    private string? _iId;
    private BadBuilding.Interior.InteriorModel? _iModel;
    private float _iY0;
    private List<double[]>? _iCams;
    private List<string>? _iLook;
    private string? _iSafeBackup, _iFlat, _deathSafeBackup;
    private BadBuilding.Interior.BalconySite? _iBal;

    /// <summary>--autotest=interior (D81): out of the car, to the door of the nearest block of flats that has an
    /// interior; walk in, look round the landing, a living room, climb a storey on foot, look out of a top-floor window,
    /// third person in a flat, and walk back out. Photographed at each step; the climb and the indoor/outdoor switch
    /// are logged.</summary>
    private void InteriorTest(double delta)
    {
        if (_main.Car is not { } car || _main.Interiors is not { } ints) return;
        if (_iTime < 0)
        {
            if (_settledFor < 2.5) return;
            if (!_main.OnFoot && !_main.ToggleOnFoot()) return;
            var w0 = _main.Walker!;
            var (wx, _, wz) = WorldOrigin.ToWorld(w0.GlobalPosition);
            // the nearest shell that makes an interior
            foreach (var (sh, x, z) in World.Interiors.NearestShells(wx, wz, 40))
            {
                // --interior-want=loggia: the nearest block with loggias (D84)
                if (Main.Args.GetValueOrDefault("interior-want") == "loggia" && !sh.Balconies.Any(b => b.Loggia)) continue;
                var m = BadBuilding.Interior.InteriorGenerator.Generate(sh, out var why);
                if (m == null) { Log.Info("Test", $"interior: {sh.Id} ({sh.Style}, {sh.Levels} storeys) has none: {why}"); continue; }
                _iId = sh.Id; _iModel = m;
                var e = m.Entrances[0];
                var door = WorldOrigin.ToLocal(x + e.Position.X, e.Position.Y, z + e.Position.Z);
                var o = new Vector3(e.Out.X, 0, e.Out.Z);
                // --interior-angle=M: start M metres to the side as well, to walk at the door at an angle (D85)
                float sideways = Main.Args.TryGetValue("interior-angle", out var ang) ? (float)Num(ang) : 0f;
                w0.GlobalPosition = door + o * 5f + o.Cross(Vector3.Up) * sideways + Vector3.Up * 0.5f;
                w0.Velocity = Vector3.Zero;
                w0.Face(door + Vector3.Up * 1.4f);
                w0.SetView(false);
                Log.Info("Test", $"interior: {sh.Id}, {sh.Style}, {m.Levels} storeys, {m.Entrances.Count} staircases, {m.Rooms.Count} rooms, " +
                    $"{World.Interiors.Known} shells known; {Math.Sqrt((x - wx) * (x - wx) + (z - wz) * (z - wz)):0} m away");
                break;
            }
            if (_iModel == null) { Log.Error("Test", $"interior: no block of flats with an interior near the start ({World.Interiors.Known} shells known)"); _main.GetTree().Quit(); return; }
            _iTime = 0;
            return;
        }
        double before = _iTime;
        _iTime += delta;
        double t = _iTime - _iPhaseAt, tb = before - _iPhaseAt;
        bool At(double x) => tb < x && t >= x;
        var w = _main.Walker!;
        var mdl = _iModel!;
        var loaded = ints.All.FirstOrDefault(l => l.Id == _iId);
        void Phase(int p) { _iPhase = p; _iPhaseAt = _iTime; w.Autopilot = null; Log.Info("Test", $"interior: phase {p} at {_iTime:0.0} s; {ints.Describe()}"); }
        // building space and back (needs the loaded node)
        Vector3 P(float u, float y, float v) { var b = mdl.Frame.P(u, y, v); return loaded!.ToScene(b); }
        (float U, float V, float Y) Here() { var b = loaded!.ToBuilding(w.GlobalPosition); var (u, v) = mdl.Frame.Local(new System.Numerics.Vector2(b.X, b.Z)); return (u, v, b.Y); }
        float e0 = mdl.Frame.Local(new System.Numerics.Vector2(mdl.Entrances[0].Position.X, mdl.Entrances[0].Position.Z)).U;
        float F(int st) => mdl.Floor + st * mdl.StoreyHeight;
        void Put(Vector3 at, Vector3 look) { w.GlobalPosition = at; w.Velocity = Vector3.Zero; w.Face(look); }
        BadBuilding.Interior.Room? RoomOf(string kind, int st) => mdl.Rooms.FirstOrDefault(r => r.Kind == kind && r.Storey == st && r.Flat?.EndsWith("/c0/L") == true)
                                                              ?? mdl.Rooms.FirstOrDefault(r => r.Kind == kind && r.Storey == st);
        // a room's free floor nearest its middle (clear of furniture by 0.45 m: the walker never starts inside a table)
        bool Clear(float u, float v, int st) => !mdl.Furniture.Any(f => mdl.StoreyAt(f.Floor + 0.1f) == st && u > f.Min.X - 0.45f && u < f.Max.X + 0.45f && v > f.Min.Y - 0.45f && v < f.Max.Y + 0.45f);
        Vector3 Centre(BadBuilding.Interior.Room r, float up = 0)
        {
            var q = r.Rects[0];
            float cu = (q.Min.X + q.Max.X) * 0.5f, cv = (q.Min.Y + q.Max.Y) * 0.5f, bu = cu, bv = cv, best = float.MaxValue;
            for (float u = q.Min.X + 0.5f; u <= q.Max.X - 0.5f; u += 0.1f)
                for (float v = q.Min.Y + 0.5f; v <= q.Max.Y - 0.5f; v += 0.1f)
                {
                    float d = (u - cu) * (u - cu) + (v - cv) * (v - cv);
                    if (d < best && Clear(u, v, r.Storey)) { best = d; bu = u; bv = v; }
                }
            return P(bu, r.Floor + up, bv);
        }
        // a room's outer wall with windows in it (to look at): the middle of the nearest such wall, at eye height
        Vector3 WindowWall(BadBuilding.Interior.Room r)
        {
            var q = r.Rects[0];
            float uMid = (q.Min.X + q.Max.X) * 0.5f, vMid = (q.Min.Y + q.Max.Y) * 0.5f, y0 = r.Floor, y1 = r.Floor + 2.6f;
            float uLo = mdl.Footprint.Min(p => mdl.Frame.Local(p).U), uHi = mdl.Footprint.Max(p => mdl.Frame.Local(p).U), depth = mdl.Footprint.Max(p => mdl.Frame.Local(p).V);
            foreach (var o in mdl.Openings)
            {
                if (o.Kind is "door" or "shut" || o.Y1 < y0 || o.Y0 > y1) continue;
                bool alongU = o.Side < 2;
                float lo = alongU ? q.Min.X : q.Min.Y, hi = alongU ? q.Max.X : q.Max.Y;
                if (o.A1 < lo || o.A0 > hi) continue;
                bool touches = o.Side switch { 0 => q.Min.Y < 0.5f, 1 => q.Max.Y > depth - 0.5f, 2 => q.Min.X < uLo + 0.5f, _ => q.Max.X > uHi - 0.5f };
                if (!touches) continue;
                float a = (MathF.Max(o.A0, lo) + MathF.Min(o.A1, hi)) * 0.5f;
                return o.Side switch { 0 => P(a, y0 + 1.3f, 0), 1 => P(a, y0 + 1.3f, depth), 2 => P(uLo, y0 + 1.3f, a), _ => P(uHi, y0 + 1.3f, a) };
            }
            return Centre(r, 1.3f) + new Vector3(mdl.Frame.AxisV.X, 0, mdl.Frame.AxisV.Z) * 6f;
        }

        switch (_iPhase)
        {
            case 0:   // at the door, waiting for the interior to load
                if (loaded == null) { if (t > 15) { Log.Error("Test", "interior: never loaded"); _main.GetTree().Quit(); } break; }
                if (t < 1.5) break;
                // --cams="x,y,z,yaw,pitch;..." (world metres, degrees, from F9 dumps' Camera line): stand where the
                // first-person camera was and photograph, then stop
                // --interior-loot: search furniture, take things, look in the pack (D89), then stop
                if (Main.Args.ContainsKey("interior-loot")) { Phase(13); break; }
                // --interior-safehouse: claim a flat, die far away, wake up in it (D88), then stop
                if (Main.Args.ContainsKey("interior-safehouse")) { Phase(12); break; }
                // --look=sofa,nightstand,...: photograph a piece of each kind from in front of it (D87), then stop
                if (Main.Args.TryGetValue("look", out var look))
                {
                    _iLook = look.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    Phase(11);
                    break;
                }
                if (Main.Args.TryGetValue("cams", out var cams))
                {
                    _iCams = cams.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split(',').Select(Num).ToArray()).ToList();
                    Phase(10);
                    break;
                }
                Shot("int_0_door");
                Phase(1);
                w.Autopilot = _ => (new Vector2(0, 1), false);
                break;
            case 1:   // walk in
            {
                var h = Here();
                // like a player: to the doorway first, then in
                float front = mdl.Frame.Local(new System.Numerics.Vector2(mdl.Entrances[0].Position.X, mdl.Entrances[0].Position.Z)).V;
                w.Face(h.V < front - 1.0f && MathF.Abs(h.U - e0) > 0.3f ? P(e0, F(0) + 1.5f, front - 0.8f) : P(e0, F(0) + 1.5f, front + 3f));
                if ((int)(tb * 2) != (int)(t * 2)) Log.Info("Test", $"interior: walking in: v {h.V:0.0} m, height {h.Y - F(0):0.00} above the floor, indoors {w.Indoors}");
                if (h.V > 0.9f)
                {
                    Log.Info("Test", $"interior: on the landing after {t:0.0} s, indoors {w.Indoors}, room {ints.Room?.Kind}");
                    w.Autopilot = _ => (Vector2.Zero, false);
                    Phase(2);
                }
                else if (t > 10) { Log.Error("Test", $"interior: stuck at the door, v {h.V:0.0}, height {h.Y - F(0):0.00}"); Shot("int_stuck"); Phase(2); }
                break;
            }
            case 2:   // the landing: the door to the left flat, then up the stairwell
                if (At(0.3)) { Put(P(e0 + 0.6f, F(0), 0.9f), P(e0 - 1.45f, F(0) + 1.4f, 0.95f)); w.SetView(true); }
                if (At(1.0)) Shot("int_1_landing");
                if (At(1.3)) Put(P(e0 + 0.4f, F(0), 0.8f), P(e0 - 0.4f, F(0) + 2.6f, 4.6f));
                if (At(2.0)) Shot("int_2_stairs");
                if (At(2.3) && RoomOf("living", 0) is { } lr) { var ww = WindowWall(lr); var back = Centre(lr) + (Centre(lr) - (ww with { Y = Centre(lr).Y })).Normalized() * 1.2f; Put(back + Vector3.Up * 0.1f, ww); }
                if (At(3.0)) { Shot("int_3_living"); Log.Info("Test", $"interior: living room: {ints.Describe()}"); }
                // from the hall into the living room, through its door
                if (At(3.3) && RoomOf("hall", 0) is { } hr && RoomOf("living", 0) is { } lv2)
                {
                    var q = hr.Rects[0];
                    var at = P((q.Min.X + q.Max.X) * 0.5f, hr.Floor + 0.1f, q.Max.Y - 1.6f);
                    Put(at, Centre(lv2, 1.2f));
                }
                if (At(4.0)) Shot("int_4_hall");
                if (At(4.3))
                {
                    // the climb, on foot: from the landing, up the left flight
                    Put(P(e0 - 0.7f, F(0) + 0.1f, 1.0f), P(e0 - 0.7f, F(0) + 1.6f, 4f));
                    _iY0 = Here().Y;
                    Phase(3);
                    w.Autopilot = _ => (new Vector2(0, 1), false);
                }
                break;
            case 3:   // up the first flight to the half landing
            {
                var h = Here();
                w.Face(P(e0 - 0.7f, h.Y + 1.2f, 5f));
                if (h.V > 4.15f || t > 6)
                {
                    Log.Info("Test", $"interior: half landing: rose {h.Y - _iY0:0.00} m (should be ~{mdl.StoreyHeight / 2:0.0}) in {t:0.0} s, v {h.V:0.0}");
                    Phase(4);
                    w.Autopilot = _ => (new Vector2(0, 1), false);
                }
                break;
            }
            case 4:   // across to the right
            {
                var h = Here();
                w.Face(P(e0 + 2f, h.Y + 1.4f, 4.5f));
                if (h.U > e0 + 0.65f || t > 4) { Phase(5); w.Autopilot = _ => (new Vector2(0, 1), false); }
                break;
            }
            case 5:   // up the second flight to the next floor
            {
                var h = Here();
                w.Face(P(e0 + 0.7f, h.Y + 1.0f, -2f));
                if (At(0.8)) Shot("int_5_climbing");
                if (h.V < 1.1f || t > 6)
                {
                    Log.Info("Test", $"interior: next floor: rose {h.Y - _iY0:0.00} m (storey {mdl.StoreyHeight:0.00}), storey {mdl.StoreyAt(h.Y + 0.3f)}, {ints.Describe()}");
                    Phase(6);
                }
                break;
            }
            case 6:   // top floor window, third person in a flat
                if (At(0.3) && RoomOf("living", mdl.Levels - 1) is { } top) { var ww = WindowWall(top); var back = Centre(top) + (Centre(top) - (ww with { Y = Centre(top).Y })).Normalized() * 1.5f; Put(back + Vector3.Up * 0.1f, ww - Vector3.Up * 0.5f); w.SetView(true); }
                if (At(1.0)) Shot("int_6_top_window");
                if (At(1.3) && (RoomOf("bedroom", 1) ?? RoomOf("living", 1)) is { } bed) { Put(Centre(bed) + Vector3.Up * 0.1f, Centre(bed, 1.2f) + new Vector3(mdl.Frame.AxisU.X, 0, mdl.Frame.AxisU.Z) * 5f); w.SetView(false); }
                if (At(2.2)) Shot("int_7_third_person");
                if (At(2.5) && RoomOf("kitchen", 0) is { } kit) { Put(Centre(kit) + Vector3.Up * 0.1f, Centre(kit, 1.2f) - new Vector3(mdl.Frame.AxisV.X, 0, mdl.Frame.AxisV.Z) * 5f); w.SetView(true); }
                if (At(3.2)) Shot("int_8_kitchen");
                if (At(3.5))
                {
                    // out onto a balcony above the ground floor (D83), if the block has one
                    bool wantLoggia = Main.Args.GetValueOrDefault("interior-want") == "loggia";
                    var bal = mdl.Balconies.Where(b => b.Depth > 0.3f && b.Position.Y > mdl.Floor + 1f && b.Loggia == wantLoggia).OrderBy(b => b.Position.Y).FirstOrDefault();
                    if (bal == null) { Log.Info("Test", "interior: no balcony to step onto"); Phase(9); break; }
                    _iBal = bal;
                    var o = new Vector3(bal.Out.X, 0, bal.Out.Z);
                    var door = loaded!.ToScene(bal.Position);
                    Put(door - o * (1.8f + (bal.Loggia ? bal.Depth : 0f)) + Vector3.Up * 0.1f, door + o * 4f + Vector3.Up * 1.4f);
                    w.SetView(true);
                    Phase(8);
                    w.Autopilot = _ => (new Vector2(0, 1), false);
                }
                break;
            case 8:   // walk out onto the balcony and stop at the rail
            {
                var bal = _iBal!;
                var o = new Vector3(bal.Out.X, 0, bal.Out.Z);
                var door = loaded!.ToScene(bal.Position);
                float outward = (w.GlobalPosition - door).Dot(o), drop = door.Y - w.GlobalPosition.Y;
                if (At(0.6)) Shot("int_b0_balcony_door");
                if (t > 3.0 && w.Autopilot != null)
                {
                    w.Autopilot = null;
                    Log.Info("Test", $"interior: {(bal.Loggia ? "loggia" : "balcony")}: {outward:0.00} m out of the {(bal.Loggia ? "facade" : "door")} ({bal.Depth:0.0} m deep), {drop:0.00} m below its floor, indoors {w.Indoors}");
                    w.Face(door + o * 6f + Vector3.Up * 0.6f);
                }
                if (At(3.4)) Shot("int_b1_on_balcony");
                if (At(3.6)) w.Face(door - o * 4f + Vector3.Up * 1.4f);
                if (At(4.1)) Shot("int_b2_looking_in");
                if (At(4.3)) Phase(9);
                break;
            }
            case 9:
                if (At(0.1))
                {
                    // and out again
                    Put(P(e0, F(0) + 0.1f, 1.0f), P(e0, F(0) + 1.4f, -6f));
                    w.SetView(false);
                    Phase(7);
                    w.Autopilot = _ => (new Vector2(0, 1), false);
                }
                break;
            case 13:
            {
                string file = ProjectSettings.GlobalizePath("user://containers.json");
                string[] kinds = { "counter", "wardrobe", "basin", "fridge" };
                if (At(0.1))
                {
                    _iSafeBackup = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null;
                    Items.Containers.Forget(Main.SaveWorld);
                    w.Body.Pack.Stacks.Clear();
                    if (Main.Args.ContainsKey("interior-fridge") && _main.Generators is { } gens)
                    {
                        // D127: a generator running by the block (in the test, right under you)
                        gens.Frozen = true; Power.Fridges.Frozen = true;
                        var g = gens.PlaceHere(w.GlobalPosition, 0, Power.Generators.Tank);
                        gens.Toggle(g, out _);
                        Log.Info("Test", "loot: a generator running for the fridge");
                    }
                    Log.Info("Test", $"loot: {Items.ItemDb.All.Count} items known");
                    Log.Info("Test", $"loot: furniture here: {string.Join(", ", mdl.Furniture.GroupBy(f => f.Kind).Select(g => $"{g.Key} {g.Count()}"))}; loot markers {mdl.Markers.Count(k => k.Kind.StartsWith("loot_"))}; rooms: {string.Join(", ", mdl.Rooms.GroupBy(r => r.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
                }
                for (int k = 0; k < kinds.Length; k++)
                {
                    double t0 = 0.5 + k * 3.0;
                    var item = mdl.Furniture.Where(f => f.Kind == kinds[k] && mdl.StoreyAt(f.Floor + 0.1f) == 1).FirstOrDefault()
                               ?? mdl.Furniture.FirstOrDefault(f => f.Kind == kinds[k]);
                    if (item == null) { if (At(t0)) Log.Info("Test", $"loot: no {kinds[k]}"); continue; }
                    if (At(t0))
                    {
                        // stand before it and look at it
                        var c = new System.Numerics.Vector2((item.Min.X + item.Max.X) * 0.5f, (item.Min.Y + item.Max.Y) * 0.5f);
                        float reach = MathF.Abs(item.Facing.Y) > 0.5f ? item.Max.Y - item.Min.Y : item.Max.X - item.Min.X;
                        var stand = c + item.Facing * (reach * 0.5f + 0.55f);
                        w.SetView(true);
                        Put(P(stand.X, item.Floor + 0.1f, stand.Y), P(c.X, item.Floor + 0.6f, c.Y));
                    }
                    if (At(t0 + 0.5) && kinds[k] == "fridge" && Main.Args.ContainsKey("interior-fridge") && ints.ContainerInReach(w.GlobalPosition, w.Yaw) is { } fs)
                    {
                        // shut 20 minutes ago (D127)
                        Power.Fridges.Shut(Main.SaveWorld, fs.Id);
                        Core.WorldClock.Skip(1200);
                    }
                    if (At(t0 + 0.75) && kinds[k] == "fridge") Log.Info("Test", $"loot: the fridge's prompt: \"{_main.PromptText}\"");
                    if (At(t0 + 0.8))
                    {
                        var spot = ints.ContainerInReach(w.GlobalPosition, w.Yaw);
                        Log.Info("Test", $"loot: by the {kinds[k]}: {(spot == null ? "nothing to search" : $"{Items.Containers.Label(spot.FurnitureKind)} ({spot.LootKind}, {spot.Room})")}");
                        if (spot != null) _main.OpenInventory(spot);
                    }
                    if (At(t0 + 1.4)) Shot($"loot_{k}_{kinds[k]}");
                    if (At(t0 + 1.8) && _main.InventoryWindow!.IsOpen)
                    {
                        _main.InventoryWindow.TakeAll();
                        Log.Info("Test", $"loot: took all; pack {w.Body.Pack.Weight:0.00} kg: {Items.Inventory.Say(w.Body.Pack.Stacks)}");
                    }
                    if (At(t0 + 2.4)) _main.InventoryWindow!.Close();
                }
                double tp = 0.5 + kinds.Length * 3.0;
                if (At(tp)) { _main.OpenInventory(null); }
                if (At(tp + 0.6)) Shot("loot_pack");
                if (At(tp + 1.0))
                {
                    _main.InventoryWindow!.Close();
                    w.Body.Hunger = 30;
                    Log.Info("Test", $"loot: hungry (30), food {w.Body.Food} in the pack");
                }
                if (At(tp + 2.0))
                {
                    Log.Info("Test", $"loot: hunger now {w.Body.Hunger:0}, pack {Items.Inventory.Say(w.Body.Pack.Stacks)}");
                    // searched again: what's left is remembered
                    var spot = ints.ContainerInReach(w.GlobalPosition, w.Yaw);
                    Log.Info("Test", $"loot: the last one searched before: {spot != null && Items.Containers.Searched(Main.SaveWorld, spot.Id)}");
                    if (_iSafeBackup != null) System.IO.File.WriteAllText(file, _iSafeBackup); else Items.Containers.Forget(Main.SaveWorld);
                    Log.Info("Test", "loot: done (the old containers file restored)");
                    _main.GetTree().Quit();
                }
                break;
            }
            case 12:
            {
                string file = ProjectSettings.GlobalizePath("user://safehouses.json");
                if (At(0.1))
                {
                    // keep Tom's own safehouse: back it up, restore it at the end
                    _iSafeBackup = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null;
                    OnFoot.Safehouse.Clear(Main.SaveWorld);
                    var lv = RoomOf("living", Math.Min(3, mdl.Levels - 1)) ?? RoomOf("living", 1);
                    _iFlat = lv?.Flat;
                    if (lv != null) { Put(Centre(lv) + Vector3.Up * 0.1f, Centre(lv, 1.4f) + new Vector3(mdl.Frame.AxisV.X, 0, mdl.Frame.AxisV.Z) * 5f); w.SetView(true); }
                }
                if (At(1.0)) { Shot("safe_0_prompt"); _main.TestClaim = true; }
                if (At(3.6))
                {
                    _main.TestClaim = false;
                    var sh = _main.SafehouseAt;
                    Log.Info("Test", $"safehouse: claimed {(sh is { } x ? $"{x.Address}, flat {x.Flat}, at height {x.Y:0.0}" : "nothing")} (wanted {_iFlat})");
                    Shot("safe_1_claimed");
                    // as if far away: no interiors loaded, the walker 300 m off, shot dead
                    _main.Interiors!.DropAll();
                    var far = w.GlobalPosition + new Vector3(300, 0, 0);
                    var (fx, _, fz) = WorldOrigin.ToWorld(far);
                    w.Indoors = false;
                    w.GlobalPosition = WorldOrigin.ToLocal(fx, _main.World!.Height(fx, fz) + 0.5, fz);
                    w.LastShot = new Vector3(0, 0, -800);
                    w.Body.Damage(500);
                }
                if (At(6.0))
                {
                    _main.Respawn();
                    Log.Info("Test", $"safehouse: woke up; {_main.Interiors!.Describe()}");
                }
                if (At(7.5))
                {
                    var room = _main.Interiors!.Room;
                    Log.Info("Test", $"safehouse: after 1.5 s: {_main.Interiors.Describe()}; in the claimed flat: {room?.Flat == _iFlat}; height {w.GlobalPosition.Y:0.0}");
                    Shot("safe_2_woke");
                }
                if (At(8.0))
                {
                    if (_iSafeBackup != null) System.IO.File.WriteAllText(file, _iSafeBackup); else OnFoot.Safehouse.Clear(Main.SaveWorld);
                    Log.Info("Test", "safehouse: done (the old safehouse file restored)");
                    _main.GetTree().Quit();
                }
                break;
            }
            case 11:
            {
                int i = (int)(t / 1.5);
                if (i >= _iLook!.Count) { Log.Info("Test", "interior: looks done"); _main.GetTree().Quit(); break; }
                double ti = t - i * 1.5;
                var item = mdl.Furniture.Where(f => f.Kind == _iLook[i]).OrderBy(f => MathF.Abs(f.Floor - (mdl.Floor + mdl.StoreyHeight))).FirstOrDefault();
                if (item == null) { if (ti < delta * 1.5) Log.Info("Test", $"interior: no {_iLook[i]}"); break; }
                if (ti < 0.5)
                {
                    var c = new System.Numerics.Vector2((item.Min.X + item.Max.X) * 0.5f, (item.Min.Y + item.Max.Y) * 0.5f);
                    float reach = MathF.Abs(item.Facing.Y) > 0.5f ? item.Max.Y - item.Min.Y : item.Max.X - item.Min.X;
                    var stand = c + item.Facing * (reach * 0.5f + 1.6f);
                    w.SetView(true);
                    Put(P(stand.X, item.Floor + 0.1f, stand.Y), P(c.X, item.Floor + item.Height * 0.5f, c.Y));
                }
                if (tb < i * 1.5 + 1.2 && t >= i * 1.5 + 1.2) Shot($"look_{_iLook[i]}");
                break;
            }
            case 10:
            {
                int i = (int)(t / 1.5);
                if (i >= _iCams!.Count) { Log.Info("Test", "interior: cams done"); _main.GetTree().Quit(); break; }
                var c = _iCams[i];
                double ti = t - i * 1.5;
                if (ti < 0.5)
                {
                    w.SetView(true);
                    var eye = WorldOrigin.ToLocal(c[0], c[1], c[2]);
                    float yaw = Mathf.DegToRad((float)c[3]), pitch = Mathf.DegToRad((float)c[4]);
                    var fwd = new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
                    w.GlobalPosition = eye - Vector3.Up * 1.66f;
                    w.Velocity = Vector3.Zero;
                    w.Face(eye + fwd * 5f);
                }
                if (tb < i * 1.5 + 1.2 && t >= i * 1.5 + 1.2) { Shot($"cam_{i}"); string where = "";
                    if (ints.Inside is { } li) { var bp = li.ToBuilding(w.GlobalPosition); var (cu, cv) = li.Model.Frame.Local(new System.Numerics.Vector2(bp.X, bp.Z)); where = $"u {cu:0.00} v {cv:0.00} above the floor {bp.Y - li.Model.Floor:0.00}; "; }
                    Log.Info("Test", $"interior: cam {i}: {where}{ints.Describe()}"); }
                break;
            }
            case 7:
            {
                var h = Here();
                w.Face(P(e0, F(0) + 1.4f, -8f));
                if (h.V < -3.5f || t > 8)
                {
                    Log.Info("Test", $"interior: back out at v {h.V:0.0} after {t:0.0} s, indoors {w.Indoors}; {ints.Describe()}");
                    w.Autopilot = null;
                    Shot("int_9_outside");
                    Log.Info("Test", "interior: done");
                    _main.GetTree().Quit();
                }
                break;
            }
        }
        if (_iTime > 90) { Log.Error("Test", "interior: timed out"); _main.GetTree().Quit(); }
    }

    private void Shot(string name)
    {
        var img = _main.GetViewport().GetTexture().GetImage();
        string path = Path.Combine(_shots, name + ".png");
        img.SavePng(path);
        Log.Info("Test", $"saved {path}");
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
