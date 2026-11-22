using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.UI;
using BalticDrive.Vehicle;
using BalticDrive.World;
using Godot;

namespace BalticDrive;

/// <summary>
/// Start-up and the frame loop: opens the map, places the focus, drives the streamers and the moving origin.
/// Command line (after <c>--</c>): <c>--map=mazowsze --place=Warszawa | --start=x,z --views=... --shots=dir --quit</c>.
/// </summary>
public partial class Main : Node3D
{
	public static Dictionary<string, string> Args { get; } = new();

	/// <summary>Whether the game may capture the mouse. Self-test runs leave it free (Tom works while they run and the
	/// window popping up shouldn't trap his cursor), unless asked with --capture-mouse.</summary>
	public static bool CaptureMouse => !(Args.ContainsKey("autotest") || Args.ContainsKey("views")) || Args.ContainsKey("capture-mouse");

	private MapWorld? _world;
	private TerrainStreamer _terrain = null!;
	private MapContentStreamer _content = null!;
	private Trees.TreeField _trees = null!;
	private Trees.GrassField _grass = null!;
	public Trees.GrassField Grass => _grass;
	private FarRoads _farRoads = null!;
	private Sky.Atmosphere _atmosphere = null!;
	public Sky.Atmosphere Atmosphere => _atmosphere;
	private FlyCamera _camera = null!;
	private Label _hud = null!;
	private Tests? _tests;
	private Car? _car;
	private Navigation? _nav;
	private MapScreen? _mapScreen;
	private OnFoot.Walker? _walker;
	private Minimap? _minimap;
	private CanvasLayer _ui = null!;
	/// <summary>True while the free camera is in charge (F toggles; views tests and --fly start in it).</summary>
	public bool Flying { get; private set; }
	private string _where = "";
	private bool _minimapOn = true;
	private ulong _whereAt;
	private int _focusedMaxFps = -1;

	/// <summary>
	/// Polite in the background (D44, Tom's humming GPU): with the window out of focus the game drops to 10 frames a
	/// second, and gets its own limit back when it has the focus again. Self-tests keep full speed.
	/// </summary>
	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest || what == NotificationPredelete) Core.WorldClock.Save();   // the play clock (D115)
		if (_tests != null) return;
		if (what == NotificationApplicationFocusOut && _focusedMaxFps < 0)
		{
			_focusedMaxFps = Engine.MaxFps;
			Engine.MaxFps = 10;
		}
		else if (what == NotificationApplicationFocusIn && _focusedMaxFps >= 0)
		{
			Engine.MaxFps = _focusedMaxFps;
			_focusedMaxFps = -1;
		}
	}

	public override void _Ready()
	{
		Instance = this;
		// a skill went up (D113): say so
		Items.Skills.LevelUp += (d, lvl) => ShowNote(Core.Lang.T("skill.level", d.Name, lvl, char.ToUpper(Items.Skills.Says(d, lvl)[0]) + Items.Skills.Says(d, lvl)[1..]), 6);
		// another language (D119): everything that keeps names reads them again
		Core.Lang.Changed += () =>
		{
			Items.ItemDb.Reload(); Items.Recipes.Reload(); Items.Evolved.Reload(); Items.Skills.ReloadDefs(); Core.Sandbox.ReloadLabels();
			// the F10 window is built once: build it again in the new language (it's open: that's where the switch is)
			if (_sandboxMenu != null && _ui != null)
			{
				bool open = _sandboxMenu.IsOpen;
				if (open) _sandboxMenu.Close();
				_sandboxMenu.QueueFree();
				_sandboxMenu = new UI.SandboxMenu();
				_ui.AddChild(_sandboxMenu);
				if (open) OpenSandboxMenu();
			}
		};
		// sound (D53): buses with a reverb that follows the space, the listener's acoustics, the world's ambience
		Audio.Buses.Setup();
		AddChild(new Audio.Acoustics());
		AddChild(new Audio.Ambience());
		Log.Sink = (level, line) =>
		{
			if (level == Log.Level.Info) GD.Print(line);
			else if (level == Log.Level.Warn) GD.PushWarning(line);
			else GD.PushError(line);
		};
		foreach (var a in OS.GetCmdlineUserArgs())
		{
			var s = a.TrimStart('-');
			int eq = s.IndexOf('=');
			Args[eq < 0 ? s : s[..eq]] = eq < 0 ? "" : s[(eq + 1)..];
		}
		Map.BadBuildingAdapter.Enabled = Args.TryGetValue("buildings", out var bmode) && bmode == "bb";
		if (Args.TryGetValue("preset", out var preset)) Map.BadBuildingAdapter.Preset = preset;
		BalticDrive.OnFoot.BanditNav.Enabled = Args.GetValueOrDefault("nav", "1") != "0";
		// colour variety of the ground (D60): --hue=0 off, 1 the default
		RenderingServer.GlobalShaderParameterSet("terrain_hue", float.Parse(Args.GetValueOrDefault("hue", "1"), System.Globalization.CultureInfo.InvariantCulture));

		_terrain = new TerrainStreamer { Name = "Terrain" };
		_content = new MapContentStreamer { Name = "MapContent", FarOn = Args.GetValueOrDefault("far", "1") != "0" };
		_trees = new Trees.TreeField();
		// grass blades near the camera (D59); G toggles, --grass=0 starts without
		_grass = new Trees.GrassField { On = Args.GetValueOrDefault("grass", "1") != "0" };
		AddChild(_grass);
		_farRoads = new FarRoads();
		AddChild(_farRoads);
		AddChild(_terrain);
		AddChild(_content);
		AddChild(_trees);
		BuildEnvironment();
		_camera = new FlyCamera { Name = "Camera", Current = true, Far = 30000f, Near = 0.1f };
		AddChild(_camera);
		_hud = new Label { Position = new Vector2(12, 8) };
		_hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
		var ui = _ui = new CanvasLayer();
		ui.AddChild(_hud);
		// ODbL: the credit stays visible whenever the map is on screen.
		var credit = new Label { Text = Core.Lang.T("hud.credit"), Modulate = new Color(1, 1, 1, 0.75f) };
		credit.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 10);
		credit.GrowHorizontal = Control.GrowDirection.Begin;
		credit.GrowVertical = Control.GrowDirection.Begin;
		credit.AddThemeColorOverride("font_shadow_color", Colors.Black);
		ui.AddChild(credit);
		AddChild(ui);
		WorldOrigin.Shifted += (dx, dz) => _camera.Position -= new Vector3((float)dx, 0, (float)dz);

		string mapName = MapName;
		string dir = MapDirectory();
		try { _world = new MapWorld(new MapData(dir, mapName)); }
		catch (Exception e)
		{
			Log.Error("Main", $"Map '{mapName}' could not be opened from {dir}: {e.Message}");
			_hud.Text = $"Map '{mapName}' is missing or out of date.\nExpected {MapData.IndexPath(dir, mapName)}\nBuild it with tools/osm_import (see its README).";
			return;
		}
		_terrain.Configure(_world);
		_content.Configure(_world);
		_trees.Configure(_world);
		_grass.Configure(_world);
		_farRoads.Configure(_world);
		_nav = new Navigation(_world.Data);
		var gps = new GpsHud { Name = "Gps" };
		_ui.AddChild(gps);
		gps.Setup(_nav);
		// the round radar in the corner (D35)
		_minimap = new Minimap { Name = "Minimap" };
		_ui.AddChild(_minimap);
		_minimap.Setup(_world, _nav, Subject);
		_mapScreen = new MapScreen { Name = "MapScreen" };
		_ui.AddChild(_mapScreen);
		_mapScreen.Setup(_world, _nav, () => { var (x, z, f, _) = Subject(); return (x, z, f); }, (x, z) =>
		{
			Teleport(x, z, 40f, Mathf.RadToDeg(_camera.Yaw), -12f);
			if (!Flying) SpawnCar(x, z);
		});

		double sx, sz;
		if (Args.TryGetValue("start", out var start))
		{
			var p = start.Split(',');
			sx = double.Parse(p[0], CultureInfo.InvariantCulture); sz = double.Parse(p[1], CultureInfo.InvariantCulture);
		}
		else
		{
			var place = _world.Data.FindPlace(Args.GetValueOrDefault("place", "Warszawa")) ?? _world.Data.Places[0];
			sx = place.X; sz = place.Z;
			Log.Info("Main", $"Starting at {place.Name} ({sx:F0}, {sz:F0})");
		}
		Teleport(sx, sz, 40f, float.Parse(Args.GetValueOrDefault("yaw", "0"), CultureInfo.InvariantCulture), -12f);
		Flying = Args.ContainsKey("fly") || Args.ContainsKey("views") || Args.GetValueOrDefault("autotest", "").Contains("fly");
		if (!Flying) SpawnCar(sx, sz);
		if (Args.ContainsKey("views") || Args.ContainsKey("autotest")) _tests = new Tests(this);
	}

	/// <summary>Where map packs live: <c>map/</c> in the project while developing, or beside the executable.</summary>
	private static string MapDirectory()
	{
		if (Args.TryGetValue("mapdir", out var d)) return d;
		if (OS.HasFeature("editor")) return ProjectSettings.GlobalizePath("res://map");
		return Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "map");
	}

	public MapWorld? World => _world;
	/// <summary>The running game (the audio reads the world and the hour, D53).</summary>
	public static Main? Instance { get; private set; }
	public float Hour => (float)_atmosphere.Hour;
	public TerrainStreamer Terrain => _terrain;
	public MapContentStreamer Content => _content;
	public FlyCamera Camera => _camera;
	public Car? Car => _car;
	public OnFoot.Walker? Walker => _walker;
	public OnFoot.BanditField? Bandits => _bandits;
	/// <summary>Self-tests: what the prompt says, or "" when there's none.</summary>
	public string PromptText => _prompt is { Visible: true } p ? p.Text : "";
	public Power.Generators? Generators => _generators;
	private Power.Generators? _generators;
	private Power.Generators.Gen? _genHere, _canHere;
	private Power.Campfires? _fires;
	private Power.VillageWells? _villageWells;
	public Power.VillageWells? VillageWells => _villageWells;
	private Power.Stations? _stations;
	public Power.Stations? Stations => _stations;
	private Power.Stations.Pump? _pumpHere;
	public Power.Campfires? Fires => _fires;
	private Power.Campfires.Fire? _fireHere;
	private Trees.TreePlacement.Tree? _treeHere;
	private bool _waterHere;
	private readonly Random _heatRng = new();
	// chopping (D111): seconds into it (-1: not chopping), the tree, where you stood, your health then
	private double _chopT = -1, _nextThunk;
	private Trees.TreePlacement.Tree _chopTree;
	private Vector3 _chopFrom;
	private float _chopHealth;
	private double _nextWoodLook;
	/// <summary>Seconds a tree takes you (D113: woodcraft makes it quicker).</summary>
	private static double ChopTime => Power.Woodland.ChopSeconds * Items.Skills.TimeFactor("woodcraft");
	private bool _poweredHome;

	private OnFoot.ShootingRange? _range;
	private OnFoot.CacheField? _caches;
	public OnFoot.CacheField? Caches => _caches;
	private Label? _prompt;
	private OnFoot.BanditField? _bandits;
	public World.Interiors? Interiors => _interiors;
	private World.Interiors? _interiors;
	private OnFoot.Leads? _leads;
	public OnFoot.Leads? Leads => _leads;
	private ColorRect? _hurt;
	private Control? _bars;

	/// <summary>E on foot by an unopened crate (D46): opens it; otherwise E is the car, as before.</summary>
	public bool TryOpenCache(OnFoot.CacheField.Cache? which = null)
	{
		if (!OnFoot || _caches == null || _walker == null) return false;
		var c = which ?? _caches.InReach(_walker.GlobalPosition);
		if (c == null) return false;
		_caches.Open(c);
		ShowNote(_walker.Body.Take(c.Loot) + (string.IsNullOrEmpty(c.Place) ? "" : $"  ({c.Place})"));
		return true;
	}

	/// <summary>The survival HUD (D46): health, hunger and thirst bars over what you carry, bottom right; the crate
	/// prompt in the middle.</summary>
	private void BuildSurvivalHud()
	{
		_bars = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
		_bars.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
		_bars.OffsetLeft = -230; _bars.OffsetTop = -150; _bars.OffsetRight = -14; _bars.OffsetBottom = -70;
		_bars.Draw += () =>
		{
			var b = _walker?.Body;
			if (b == null) return;
			var font = ThemeDB.FallbackFont;
			// D102: Tom's UI set: an icon, the gauge's housing, a tinted fill (glowing when low); else plain rectangles
			var frame = UI.Skin.Box("bars/bar_frame.png");
			void Bar(int row, string name, string icon, float v, Color col)
			{
				float y = row * 22;
				float k = Mathf.Clamp(v / 100f, 0, 1);
				if (frame != null && UI.Skin.Box(v < 25 ? "bars/bar_fill_glow.png" : "bars/bar_fill.png", 0, 0, v < 25 ? col.Lightened(0.3f) : col) is { } fill)
				{
					if (UI.Skin.Tex($"bar_icons/{icon}.png") is { } ic) _bars.DrawTextureRect(ic, new Rect2(46, y + 1, 18, 18), false, col.Lightened(0.25f));
					_bars.DrawStyleBox(frame, new Rect2(70, y, 146, 20));
					if (k > 0.01f) _bars.DrawStyleBox(fill, new Rect2(73, y + 3, MathF.Max(14, 140 * k), 14));
					return;
				}
				_bars.DrawRect(new Rect2(70, y + 4, 140, 10), new Color(0, 0, 0, 0.45f));
				_bars.DrawRect(new Rect2(70, y + 4, 140 * k, 10), v < 25 ? col.Lightened(0.35f) : col);
				_bars.DrawString(font, new Vector2(0, y + 14), name, HorizontalAlignment.Left, 66, 14, new Color(1, 1, 1, 0.9f));
			}
			Bar(0, "health", "icon_health", b.Health, new Color(0.85f, 0.22f, 0.2f));
			Bar(1, "food", "icon_food", b.Hunger, new Color(0.9f, 0.6f, 0.2f));
			Bar(2, "water", "icon_water", b.Thirst, new Color(0.25f, 0.55f, 0.95f));
			if (!OnFoot && _car != null && IsInstanceValid(_car)) Bar(-1, "petrol", "icon_fuel", _car.Fuel / _car.Tank * 100, new Color(0.88f, 0.72f, 0.25f));   // D123
			_bars.DrawString(font, new Vector2(-170, 74), Core.Lang.T("hud.carry", b.Pack.Weight.ToString("0.0"), BalticDrive.OnFoot.Survival.MaxCarry.ToString("0"), b.Food, b.Water, b.Medkits), HorizontalAlignment.Right, 380, 13, new Color(1, 1, 1, 0.8f));
		};
		_ui.AddChild(_bars);
		_prompt = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
		_prompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
		_prompt.OffsetTop = 70; _prompt.OffsetBottom = 100; _prompt.OffsetLeft = -250; _prompt.OffsetRight = 250;
		_prompt.AddThemeFontSizeOverride("font_size", 18);
		_prompt.AddThemeColorOverride("font_shadow_color", Colors.Black);
		_ui.AddChild(_prompt);
		_inventory = new UI.InventoryScreen();
		_ui.AddChild(_inventory);
		_sandboxMenu = new UI.SandboxMenu();
		_ui.AddChild(_sandboxMenu);
	}

	private UI.InventoryScreen? _inventory;
	private UI.SandboxMenu? _sandboxMenu;
	public UI.SandboxMenu? SandboxMenu => _sandboxMenu;

	/// <summary>The sandbox settings (D103, F10): the walker stands still meanwhile.</summary>
	public void OpenSandboxMenu()
	{
		if (_sandboxMenu == null || _sandboxMenu.IsOpen) return;
		var w = _walker;
		var parked = w?.Autopilot;
		if (w != null && OnFoot) w.Autopilot = _ => (Vector2.Zero, false);
		_sandboxMenu.Open(() => { if (w != null && OnFoot) w.Autopilot = parked; });
	}
	public UI.InventoryScreen? InventoryWindow => _inventory;
	private Items.ContainerSpot? _spot;
	private Func<OnFoot.Walker, (Vector2, bool)>? _parkedAutopilot;

	/// <summary>Opens the inventory window (D89): on a container if one is given, else on your pack alone. The walker
	/// stands still while it's open.</summary>
	public void OpenInventory(Items.ContainerSpot? spot)
	{
		if (_walker == null || _inventory == null || _inventory.IsOpen) return;
		Items.Inventory? other = spot == null ? null : Items.Containers.Get(SaveWorld, spot.Id, spot.LootKind, spot.Room);
		OpenInventoryOn(other, spot == null ? "" : Items.Containers.Label(spot.FurnitureKind));
		Core.Log.Info("Items", spot == null ? $"pack: {Items.Inventory.Say(_walker.Body.Pack.Stacks)}" : $"{spot.FurnitureKind} ({spot.LootKind}, {spot.Room}): {Items.Inventory.Say(other!.Stacks)}");
	}

	/// <summary>The inventory window on your pack and another inventory (a container, a car's boot), or the pack alone.</summary>
	private void OpenInventoryOn(Items.Inventory? other, string title, Action? closed = null)
	{
		if (_walker == null || _inventory == null || _inventory.IsOpen) return;
		_parkedAutopilot = _walker.Autopilot;
		_walker.Autopilot = _ => (Vector2.Zero, false);
		var w = _walker;
		_inventory.Health = () => w.Body.Health;
		_inventory.Note = s => w.Body.Say(s);
		_inventory.Open(w.Body.Pack, other, title, BalticDrive.OnFoot.Survival.Usable, (id, stage) => w.Body.UseItem(id, stage),
			() => { w.Autopilot = _parkedAutopilot; _parkedAutopilot = null; closed?.Invoke(); });
	}

	// ---- the boot (D125): a car's boot and back seat, kept like any container ----
	/// <summary>Within this of the boot's lid, F opens it.</summary>
	public const float BootReach = 1.5f;
	private Vehicle.Car? _bootHere;

	/// <summary>A car's boot: kept with the containers (yours starts empty, a found car's is rolled from the "boot" loot),
	/// holding what its spec says.</summary>
	public Items.Inventory Boot(Vehicle.Car c)
	{
		var inv = Items.Containers.Get(SaveWorld, c.BootId, c.BootId == "car:own" ? "none" : "boot", null);
		inv.MaxWeight = c.Spec.Boot;
		return inv;
	}

	/// <summary>The car whose boot you stand at, or null.</summary>
	public Vehicle.Car? BootInReach()
	{
		if (_walker == null) return null;
		var at = _walker.GlobalPosition;
		Vehicle.Car? best = null; float bd = BootReach;
		foreach (var c in (_car != null ? new[] { _car } : Array.Empty<Vehicle.Car>()).Concat(_wrecks?.Live ?? Enumerable.Empty<Vehicle.Car>()))
		{
			if (!IsInstanceValid(c) || c.GlobalPosition.DistanceTo(at) > 6) continue;
			var b = c.ToGlobal(c.BootAnchor);
			float d = (b - at with { Y = b.Y }).Length();
			if (d < bd) { bd = d; best = c; }
		}
		return best;
	}

	/// <summary>Opens a car's boot beside your pack; closing it puts the load's weight on the car.</summary>
	public void OpenBoot(Vehicle.Car c)
	{
		var inv = Boot(c);
		OpenInventoryOn(inv, Core.Lang.T("boot.title"), () => { if (IsInstanceValid(c)) c.Mass = c.Spec.Mass + inv.Weight; Items.Containers.Save(); });
		Core.Log.Info("Items", $"boot of {c.Name} ({c.BootId}, {inv.Weight:0.0} of {inv.MaxWeight:0} kg): {Items.Inventory.Say(inv.Stacks)}");
	}

	private float _downTime = -1;
	private string _downCause = "";
	public bool PlayerDown => _downTime >= 0;

	/// <summary>
	/// Dying (D55, Tom): the body drops where it was shot and the view goes down with the eyes; the screen dims and
	/// waits for a click. Then you wake up somewhere else: your safehouse if you've claimed one, otherwise outside a
	/// real home 1.5-3 km away. What you carried (food, water, medkits, spare rounds) stays where you fell, with your
	/// body, marked on the map: go back for it. Not by the car any more.
	/// </summary>
	private void UpdateDown(float dt)
	{
		if (_hurt == null || _walker == null) return;
		if (_wakeTime >= 0)
		{
			// waking: from black to clear
			_wakeTime += dt;
			_hurt.Color = new Color(0, 0, 0, Mathf.Clamp(1 - (_wakeTime - 0.3f) / 1.4f, 0, 1));
			if (_wakeTime > 1.7f) { _wakeTime = -1; _hurt.Color = new Color(0.7f, 0, 0, 0); }
			return;
		}
		if (_downTime < 0) return;
		_downTime += dt;
		float dark = Mathf.Clamp(_downTime / 1.5f, 0, 1);
		_hurt.Color = new Color(0.12f * (1 - dark * 0.6f), 0, 0, 0.62f * dark);
		if (_deathLabel != null)
		{
			_deathLabel.Visible = _downTime > 1.2f;
			string what = Core.Lang.T(_downCause switch { "starved" => "die.hunger", "dehydrated" => "die.thirst", _ => "die.shot" });
			_deathLabel.Text = Core.Lang.T("die.rest", what, Core.Lang.T(BalticDrive.OnFoot.Safehouse.Get(SaveWorld) is { } sh ? "die.in_safehouse" : "die.elsewhere"));
		}
	}

	private float _wakeTime = -1;
	private Label? _deathLabel;
	private BalticDrive.OnFoot.Doorstep? _safehouse;
	private bool _safehouseLoaded;
	private float _claim;
	private double _nextHomeLook;
	private (BalticDrive.OnFoot.Doorstep Step, string Name)? _homeHere;
	/// <summary>Self-tests: acts as holding K.</summary>
	public bool TestClaim;
	/// <summary>The map packs to open: <c>--map=</c>, else the whole of Poland (D105: "poland") when it's built, else
	/// Mazowsze and Podlaskie (D90: "polska"), else "mazowsze".</summary>
	public static string MapName => _mapName ??= Args.GetValueOrDefault("map",
		new[] { "poland", "polska" }.FirstOrDefault(m => File.Exists(MapData.IndexPath(MapDirectory(), m))) ?? "mazowsze");
	private static string? _mapName;

	/// <summary>What the player's saves are kept under (safehouse, searched furniture). Every map is cut from the same
	/// Baltic-wide projection (D2), so a place has the same coordinates in any of them: one world, whichever packs are
	/// open (D90). Saves from before D90, kept under "mazowsze", are read as this world's.</summary>
	public const string SaveWorld = "baltic";
	public BalticDrive.OnFoot.Doorstep? SafehouseAt => _safehouse;

	/// <summary>Down: out of the car first if it happened at the wheel (D52), then the fall.</summary>
	private void OnDown(string cause)
	{
		_downCause = cause; _downTime = 0;
		if (_walker == null) return;
		if (!_walker.Active && _car != null && _car.Driving)
		{
			var t = _car.GlobalTransform;
			_car.Driving = false;
			_walker.Activate(t.Origin - t.Basis.X * 1.7f + Vector3.Up * 0.3f, _car.Rotation.Y);
		}
		_walker.Die();
	}

	/// <summary>The click after dying (D55). Public for the self-test.</summary>
	public void Respawn()
	{
		if (_walker == null || _world == null || _caches == null || _downTime < 0) return;
		var fell = _walker.BodyAt;
		var (fx, _, fz) = WorldOrigin.ToWorld(fell);
		var loot = _walker.Body.PackUp();
		var package = _caches.PlacePackage(fell, loot, _walker.CorpseTransform, _walker.CopyPose);
		if (loot.ItemCount + loot.Ammo.Sum(a => a.Rounds) == 0) _caches.Open(package);   // nothing to go back for
		foreach (var b in _bandits!.All) b.QueueFree();
		_bandits.Clear();
		var home = _safehouse;
		var step = home ?? BalticDrive.OnFoot.Homes.Pick(_world, fx, fz, new Random()) ?? new BalticDrive.OnFoot.Doorstep(fx + 40, fz, 0, 0, "");
		// a flat safehouse (D88): its interior rebuilt here and now, so there's a floor to wake up on
		if (home is { IsFlat: true } flatHome && _interiors?.LoadNow(flatHome.Shell, flatHome.BX, flatHome.BZ) is { } flatInside)
		{
			_walker.Indoors = true;
			_walker.Revive(WorldOrigin.ToLocal(flatHome.X, flatHome.Y + 0.1, flatHome.Z), flatHome.Yaw);
			Core.Log.Info("Safehouse", $"woke in flat {flatHome.Flat} of {flatInside.Id}");
		}
		else
		{
			float h = _world.Height(step.X, step.Z);
			_walker.Revive(WorldOrigin.ToLocal(step.X, h + 0.4, step.Z), step.Yaw);
		}
		_walker.Body.Revive();
		_downTime = -1;
		_wakeTime = 0;
		if (_deathLabel != null) _deathLabel.Visible = false;
		double dx = fx - step.X, dz = fz - step.Z, km = Math.Sqrt(dx * dx + dz * dz) / 1000;
		string toward = Compass(dx, dz);
		string where = home is { IsFlat: true } ? Core.Lang.T("wake.flat", Core.Lang.Pl ? home.Value.Address : home.Value.Address.Replace("the flat", "your flat"))
			: home != null ? Core.Lang.T("wake.safehouse", home.Value.Address.Length > 0 ? ", " + home.Value.Address : "")
			: step.Address.Length > 0 ? Core.Lang.T("wake.outside", step.Address) : Core.Lang.T("wake.outside_block");
		ShowNote(package.Opened ? Core.Lang.T("wake.opened", where) : km < 0.05 ? Core.Lang.T("wake.here", where)
			: Core.Lang.T("wake.far", where, km.ToString("0.0"), toward), 12);
		Core.Log.Info("Death", $"woke {where}, {km:0.00} km from the body (package {(package.Opened ? "empty" : "left")})");
	}

	/// <summary>"the flat on the 4th floor, left" (Polish style: the ground floor is parter, then 1st, 2nd...).</summary>
	private static string FlatName(BadBuilding.Interior.InteriorModel m, int storey, string flat)
	{
		string floor = storey == 0 ? Core.Lang.T("flat.ground") : Core.Lang.T("flat.floor", Core.Lang.Pl ? storey.ToString() : $"{storey}{(storey % 100 is 11 or 12 or 13 ? "th" : (storey % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })}");
		string side = flat.EndsWith("/L") ? Core.Lang.T("flat.left") : flat.EndsWith("/R") ? Core.Lang.T("flat.right") : "";
		int staircases = m.Entrances.Count;
		string stair = "";
		if (staircases > 1 && System.Text.RegularExpressions.Regex.Match(flat, @"/c(\d+)/") is { Success: true } mc) stair = Core.Lang.T("flat.staircase", int.Parse(mc.Groups[1].Value) + 1);
		return Core.Lang.T("flat.name", floor, side.Length > 0 ? ", " + side : "", stair);
	}

	private static string Compass(double dx, double dz)
	{
		// world +Z is south
		string[] names = { Core.Lang.T("dir.n"), Core.Lang.T("dir.ne"), Core.Lang.T("dir.e"), Core.Lang.T("dir.se"), Core.Lang.T("dir.s"), Core.Lang.T("dir.sw"), Core.Lang.T("dir.w"), Core.Lang.T("dir.nw") };
		double a = Math.Atan2(dx, -dz);
		int i = (int)Math.Round(a / (Math.PI / 4));
		return names[((i % 8) + 8) % 8];
	}

	/// <summary>Hold K by the wall of a home (D55): it becomes your safehouse.</summary>
	private bool UpdateClaim(double now, float dt)
	{
		if (_walker == null || _world == null) return false;
		// inside a block of flats (D88): the flat you're standing in, not the whole building
		if (_interiors?.Inside is { } inside)
		{
			_homeHere = null;
			var room = _interiors.Room;
			if (room?.Flat is not { } flat || (_safehouse is { } mineF && mineF.Flat == flat)) { _claim = 0; return false; }
			string name = FlatName(inside.Model, room.Storey, flat);
			bool holdingF = TestClaim || Input.IsKeyPressed(Key.K);
			_claim = holdingF ? _claim + dt : 0;
			_prompt!.Visible = true;
			_prompt.Text = holdingF ? Core.Lang.T("safe.claiming", name, MathF.Min(_claim / 2f, 1).ToString("P0")) : Core.Lang.T("safe.hold", name);
			if (_claim >= 2f && BalticDrive.World.Interiors.Find(inside.Id) is { } found)
			{
				var (wx, wy, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
				var s = new BalticDrive.OnFoot.Doorstep(wx, wz, _walker.Yaw, 0, name)
				{
					Building = inside.Id, Flat = flat, Y = (float)wy + 0.05f, BX = found.X, BZ = found.Z, Shell = found.Json,
				};
				BalticDrive.OnFoot.Safehouse.Set(SaveWorld, s);
				_safehouse = s;
				_claim = 0;
				ShowNote(Core.Lang.T("safe.done", char.ToUpper(name[0]) + name[1..]), 8);
				Core.Log.Info("Safehouse", $"claimed flat {flat} ({name}) at {wx:0.0},{wy:0.0},{wz:0.0}");
			}
			return true;
		}
		if (now >= _nextHomeLook)
		{
			_nextHomeLook = now + 0.3;
			var (wx, _, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
			_homeHere = BalticDrive.OnFoot.Homes.At(_world, wx, wz, _walker.Yaw + MathF.PI) is { } at ? (at.Step, at.Step.Address.Length > 0 ? at.Step.Address : at.Home.Type == BuildingKind.House ? Core.Lang.T("home.this_house") : Core.Lang.T("home.this_block")) : null;
		}
		if (_homeHere is not { } here || (_safehouse is { } mine && mine.OsmId == here.Step.OsmId && mine.OsmId != 0)) { _claim = 0; return false; }
		bool holding = TestClaim || Input.IsKeyPressed(Key.K);
		_claim = holding ? _claim + dt : 0;
		_prompt!.Visible = true;
		_prompt.Text = holding ? Core.Lang.T("safe.claiming", here.Name, MathF.Min(_claim / 2f, 1).ToString("P0")) : Core.Lang.T("safe.hold", here.Name);
		if (_claim >= 2f)
		{
			// facing out from the wall when you wake: the way you'd walk out of the door
			var (wx, _, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
			var s = here.Step with { X = wx, Z = wz };
			BalticDrive.OnFoot.Safehouse.Set(SaveWorld, s);
			_safehouse = s;
			_claim = 0;
			ShowNote(Core.Lang.T("safe.done", char.ToUpper(here.Name[0]) + here.Name[1..]), 8);
			Core.Log.Info("Safehouse", $"claimed {here.Name} (osm {s.OsmId}) at {s.X:0},{s.Z:0}");
		}
		return true;
	}

	private void UpdateSurvival(double now)
	{
		if (_world == null) return;
		if (_caches == null)
		{
			_caches = new OnFoot.CacheField(_world); AddChild(_caches); BuildSurvivalHud();
			_bandits = new OnFoot.BanditField(_world, _caches); AddChild(_bandits);
			_interiors = new World.Interiors(this); AddChild(_interiors);   // walk into blocks of flats (D81)
			_wrecks = new Vehicle.WreckField(_world); AddChild(_wrecks);   // cars to strip (D69)
			_parked = new Vehicle.ParkedCars(_world, _wrecks); AddChild(_parked);   // car parks and kerbs (D100)
			_generators = new Power.Generators(_world, SaveWorld); AddChild(_generators);   // portable generators (D109)
			// a can pours into the car in the workshop, else a generator in reach, else a car beside you (D123)
			BalticDrive.OnFoot.Survival.PourFuel = litres =>
				_walker == null ? -1
				: _workshop?.Car is { } wc && IsInstanceValid(wc) ? wc.Pour(litres)
				: _generators?.InReach(_walker.GlobalPosition) is { } g ? _generators.Pour(g, litres)
				: OnFoot && NearestCar() is { } nc ? nc.Pour(litres) : -1;
			_fires = new Power.Campfires(_world); AddChild(_fires);   // campfires (D111)
			_villageWells = new Power.VillageWells(_world); AddChild(_villageWells);   // wells in village yards (D122)
			_stations = new Power.Stations(_world, SaveWorld) { PoweredAt = (x, z) => _generators?.PoweredAt(x, z, Power.Stations.PowerReach) == true };
			AddChild(_stations);   // petrol stations that pump (D124)
			Items.Recipes.HeatHere = pack =>
				_walker != null && _fires?.Near(_walker.GlobalPosition, Power.Campfires.HeatReach, true) != null ? Core.Lang.T("heat.campfire")
				: pack.CountMatching("tag:stove") > 0 && pack.CountMatching("tag:fuel_gas") > 0 ? Core.Lang.T("heat.stove") : null;
			Items.Recipes.HeatUsed = (heat, pack) =>
			{
				// a canister lasts about four cookings
				if (heat == Core.Lang.T("heat.stove") && _heatRng.NextDouble() < 0.25) { pack.UseUp("tag:fuel_gas", 1); ShowNote(Core.Lang.T("heat.canister_empty"), 4); }
			};
			Items.Recipes.Place = what =>
			{
				if (what != "campfire" || _walker == null || _fires == null) return "Nowhere to put it";
				if (!OnFoot || _interiors?.Inside != null) return "Not indoors: outside, away from walls";
				var fwd = new Vector3(-MathF.Sin(_walker.Yaw), 0, -MathF.Cos(_walker.Yaw));
				var spot = _walker.GlobalPosition + fwd * 1.3f;
				var (sx, _, sz) = WorldOrigin.ToWorld(spot);
				if (!float.IsNaN(_world.Water(sx, sz))) return "Not in the water";
				if (_world.InBuilding(sx, sz, 0.8f)) return "Too close to the building";
				if (_fires.Near(spot, 2f, false) != null) return "There's a fire here already";
				_fires.Build(spot);
				return null;
			};
			// a reason to roam (D54): notes and radios name stashes across the map
			_leads = new OnFoot.Leads(_world, _caches, _bandits); AddChild(_leads);
			_leads.Said += s => ShowNote(s, 12, keep: true);
			// the map's own places: leads (D54), your things where you fell and your safehouse (D55)
			_mapScreen?.SetPlaces(() => _leads.All.Select(l => (l.X, l.Z, l.Where, l.Stage == BalticDrive.OnFoot.Leads.Stage.Taken ? 1 : 0))
				.Concat(Packages().Select(p => (p.X, p.Z, "your things", 2)))
				.Concat(_safehouse is { } sh ? new[] { (sh.X, sh.Z, "safehouse" + (sh.Address.Length > 0 ? ": " + sh.Address : ""), 3) } : Array.Empty<(double, double, string, int)>()));
			// crates and bodies on the radar while on foot (D50)
			if (_minimap != null)
				_minimap.Markers = () => OnFoot && _caches != null
					? _caches.Live.Where(c => !c.Keep).Select(c => { var (wx, _, wz) = WorldOrigin.ToWorld(c.GlobalPosition); return (wx, wz, c.Key.StartsWith("drop:") ? 2 : c.Kind == MapPoiKind.Medical ? 1 : 0, c.Opened); })
						.Concat(FarMarkers()).ToList()
					: FarMarkers().ToList();
			// hit: the screen edge flashes red (D47)
			_hurt = new ColorRect { Color = new Color(0.7f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
			_hurt.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			_ui.AddChild(_hurt);
			_deathLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
			_deathLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			_deathLabel.AddThemeFontSizeOverride("font_size", 26);
			_deathLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
			_ui.AddChild(_deathLabel);
		}
		// the walker carries your health even before you first get out (bandits shoot at the car, D52)
		if (_walker == null && _car != null) _walker = CreateWalker();
		if (_car != null && _bandits!.Car != _car)
		{
			_bandits.Car = _car;
			// the bodywork stops most of it (D52): a fifth reaches the driver. At a half, three bandits took a parked
			// driver from 100 to 8 in ten seconds: a car is a big target that's hard to miss
			_car.Shot += d => { if (_car.Driving) _walker?.Body.Damage(d * 0.2f); };
			_car.Said += s => ShowNote(s);   // a part broke, the engine won't start (D67)
		}
		if (_walker != null && _bandits!.Player != _walker)
		{
			_bandits.Player = _walker;
			_walker.Body.Hurt += a => { if (_hurt != null) _hurt.Color = _hurt.Color with { A = Mathf.Clamp(_hurt.Color.A + a / 60f, 0, 0.5f) }; };
			_walker.Body.Down += OnDown;
		}
		if (_hurt != null && _hurt.Color.A > 0 && _downTime < 0) _hurt.Color = _hurt.Color with { A = MathF.Max(0, _hurt.Color.A - (float)GetProcessDeltaTime() * 0.8f) };
		UpdateDown((float)GetProcessDeltaTime());
		_bandits!.Update(now);
		bool on = OnFoot && _walker != null;
		_leads!.Update(on ? _walker!.GlobalPosition : _car != null && _car.Driving ? _car.GlobalPosition : null);
		_bars!.Visible = (on || (_car != null && _car.Driving && !Flying && _walker != null)) && !PlayerDown;   // health in the car too (D52)
		if (!on && _bars.Visible) _bars.QueueRedraw();   // the petrol gauge moves (D123)
		if (!_safehouseLoaded) { _safehouseLoaded = true; _safehouse = BalticDrive.OnFoot.Safehouse.Get(SaveWorld); }
		// wrecks along the way, on foot or driving (D69)
		if (on) _wrecks?.Update(_walker!.GlobalPosition, now);
		else if (_car != null && _car.Driving && !Flying) _wrecks?.Update(_car.GlobalPosition, now);
		// parked cars (D100): built around you; the nearest wakes up when you walk up to it
		if (on) _parked?.Update(_walker!.GlobalPosition, true, now);
		else if (_car != null && _car.Driving && !Flying) _parked?.Update(_car.GlobalPosition, false, now);
		else if (Flying) _parked?.Update(_camera.GlobalPosition with { Y = 0 }, false, now);   // the free camera and test views see them too
		// generators (D109): fuel burns and noise carries wherever you are
		var genAt = on ? _walker!.GlobalPosition : _car != null && _car.Driving ? _car.GlobalPosition : (Vector3?)null;
		if (genAt is { } ga) _generators?.Update(ga, now, (float)GetProcessDeltaTime());
		if (_bandits != null) _bandits.NoiseBoost = _generators?.Loud == true ? 2f : 1f;
		if (genAt is { } fa) _fires?.Update(fa, now, (float)GetProcessDeltaTime());
		if (genAt is { } va) _villageWells?.Update(va, now);
		if (genAt is { } sa) _stations?.Update(sa, now);
		if (_generators?.Carried != null && _walker != null && (PlayerDown || !on)) { _generators.PutDown(_walker.GlobalPosition, _walker.Yaw); _walker.Burden = 0; }   // dropped when you fall or get in a car
		if (_generators != null && _safehouse is { } sh)
		{
			bool p = _generators.PoweredAt(sh.X, sh.Z);
			if (p && !_poweredHome) ShowNote(Core.Lang.T("safe.power"));
			_poweredHome = p;
		}
		if (!on || PlayerDown || (_inventory?.IsOpen ?? false)) { _prompt!.Visible = false; _claim = 0; return; }
		if (_generators?.Carried != null)
		{
			_genHere = null;
			_prompt!.Visible = true;
			_prompt.Text = Core.Lang.T("p.carrying");
			_bars.QueueRedraw();
			return;
		}
		_waterHere = false; _treeHere = null; _wellHere = null;
		if (_chopT >= 0)
		{
			_prompt!.Visible = true;
			_prompt.Text = Core.Lang.T("p.chopping", Mathf.Clamp(_chopT / ChopTime, 0, 1).ToString("P0"));
			_bars.QueueRedraw();
			return;
		}
		// a petrol pump (D124): filling the car runs by itself until it stops
		if (_stations?.Filling is { } fl)
		{
			if (_stations.Tick(_walker!.GlobalPosition, (float)GetProcessDeltaTime()) is { } stopped) ShowNote(stopped, 4);
			else
			{
				_prompt!.Visible = true;
				_prompt.Text = Core.Lang.T("p.pumping", fl.Car.Fuel.ToString("0.0"), fl.Car.Tank.ToString("0"));
				_bars.QueueRedraw();
				return;
			}
		}
		_pumpHere = _stations?.InReach(_walker!.GlobalPosition);
		if (_pumpHere != null)
		{
			_prompt!.Visible = true;
			_prompt.Text = Power.Stations.Prompt(_pumpHere, PumpCar(_pumpHere), _walker!.Body.Pack.Count("empty_petrol_can"));
			_bars.QueueRedraw();
			return;
		}
		_bootHere = BootInReach();
		if (_bootHere != null)
		{
			var boot = Boot(_bootHere);
			_prompt!.Visible = true;
			_prompt.Text = Core.Lang.T("p.boot", boot.Weight.ToString("0.0"), boot.MaxWeight.ToString("0"));
			_bars.QueueRedraw();
			return;
		}
		_fireHere = _fires?.Near(_walker!.GlobalPosition, Power.Campfires.Reach, false);
		if (_fireHere != null)
		{
			int wood = _walker!.Body.Pack.Count("firewood");
			_prompt!.Visible = true;
			_prompt.Text = _fireHere.Burning
				? Core.Lang.T("p.fire", Power.Campfires.Left(_fireHere)) + (wood > 0 ? Core.Lang.T("p.fire_add", wood) : Core.Lang.T("p.fire_nowood")) + Core.Lang.T("p.fire_cook")
				: Core.Lang.T("p.burnt") + (wood > 0 && _walker.Body.Pack.CountMatching("tag:fire") > 0 ? Core.Lang.T("p.relight") : Core.Lang.T("p.relight_need"));
			_bars.QueueRedraw();
			return;
		}
		_canHere = _generators?.CanInReach(_walker!.GlobalPosition);
		if (_canHere != null)
		{
			_genHere = null;
			_prompt!.Visible = true;
			_prompt.Text = Core.Lang.T("p.take_can");
			_bars.QueueRedraw();
			return;
		}
		_genHere = _generators?.InReach(_walker!.GlobalPosition);
		if (_genHere != null)
		{
			_prompt!.Visible = true;
			_prompt.Text = _genHold >= 0 ? Core.Lang.T("p.lifting", MathF.Min((float)_genHold / 0.6f, 1).ToString("P0")) : Power.Generators.Prompt(_genHere, _walker!.Body.Pack.Count("petrol_can"));
			_bars.QueueRedraw();
			return;
		}
		_caches.Update(_walker!.GlobalPosition, now);
		var c = _caches.InReach(_walker.GlobalPosition);
		// furniture to search, indoors (D89)
		_spot = c == null && _interiors != null && !(_inventory?.IsOpen ?? false) ? _interiors.ContainerInReach(_walker.GlobalPosition, _walker.Yaw) : null;
		if (_spot != null)
		{
			_prompt!.Visible = true;
			_prompt.Text = Core.Lang.T("p.search", Items.Containers.Label(_spot.FurnitureKind)) + (Items.Containers.Searched(SaveWorld, _spot.Id) ? Core.Lang.T("p.searched") : "");
			_bars.QueueRedraw();
			return;
		}
		_prompt!.Visible = c != null;
		if (c != null) _prompt.Text = c.Key.StartsWith("drop:") ? Core.Lang.T("p.body") : c.Key.StartsWith("death:") ? Core.Lang.T("p.your_things") : c.Keep ? Core.Lang.T("p.stash") : Core.Lang.T("p.crate") + (string.IsNullOrEmpty(c.Place) ? "" : $" ({c.Place})");
		else if (!WoodAndWater(now)) UpdateClaim(now, (float)GetProcessDeltaTime());
		_bars.QueueRedraw();
	}
	/// <summary>Outdoors, nothing else in reach (D111): water at your feet to fill a bottle from, or a tree to chop.
	/// True when it put up a prompt.</summary>
	private bool WoodAndWater(double now)
	{
		if (_walker == null || _world == null || _interiors?.Inside != null) return false;
		var pack = _walker.Body.Pack;
		// a well, a hand pump or a spring (D121): clean water, before any river
		var (qx, _, qz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
		if (now >= _nextWellLook)
		{
			_nextWellLook = now + 0.25;
			_wellLook = Power.Wells.Nearest(_world, qx, qz)
				?? (_villageWells?.Near_(_walker.GlobalPosition, Power.Wells.Reach) is { } vw ? (vw.Pump ? Map.MapPointKind.Pump : Map.MapPointKind.Well) : null);
		}
		if (_wellLook is { } wk)
		{
			_wellHere = wk;
			_prompt!.Visible = true;
			_prompt.Text = pack.Count("empty_bottle") > 0 ? Core.Lang.T("p.well_fill", Power.Wells.Name(wk), pack.Count("empty_bottle")) : Core.Lang.T("p.well_nobottle", Power.Wells.Name(wk));
			return true;
		}
		var fwd = new Vector3(-MathF.Sin(_walker.Yaw), 0, -MathF.Cos(_walker.Yaw));
		foreach (float k in new[] { 0.6f, 1.2f, 1.8f })
		{
			var (wx, _, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition + fwd * k);
			if (float.IsNaN(_world.Water(wx, wz))) continue;
			_waterHere = true;
			_prompt!.Visible = true;
			_prompt.Text = pack.Count("empty_bottle") > 0 ? Core.Lang.T("p.fill", pack.Count("empty_bottle")) : Core.Lang.T("p.water_nobottle");
			return true;
		}
		if (now >= _nextWoodLook)
		{
			_nextWoodLook = now + 0.25;
			var (px, _, pz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
			_woodLook = Power.Woodland.Nearest(_world, px, pz);
		}
		if (_woodLook is not { } t) return false;
		_treeHere = t;
		bool axe = pack.CountMatching("tag:chopping") > 0, used = Power.Woodland.Used(SaveWorld, Power.Woodland.Key(t));
		if (!axe && !used) return false;   // no axe: trees are just trees
		_prompt!.Visible = true;
		_prompt.Text = used ? Core.Lang.T("p.tree_done") : Core.Lang.T("p.chop", ChopTime.ToString("0"));
		return true;
	}
	private Trees.TreePlacement.Tree? _woodLook;
	private byte? _wellLook, _wellHere;
	private double _nextWellLook;

	public OnFoot.ShootingRange? Range => _range;

	/// <summary>Your things left where you fell, not yet taken (world x, z).</summary>
	private IEnumerable<(double X, double Z)> Packages() => _caches == null ? Enumerable.Empty<(double, double)>()
		: _caches.Live.Where(c => c.Key.StartsWith("death:") && !c.Opened).Select(c => { var (x, _, z) = WorldOrigin.ToWorld(c.GlobalPosition); return (x, z); });

	/// <summary>Radar marks held at the rim when far (D54, D55): leads 3, your things 4, the safehouse 5.</summary>
	private IEnumerable<(double, double, int, bool)> FarMarkers() =>
		_leads!.All.Where(l => l.Stage != BalticDrive.OnFoot.Leads.Stage.Taken).Select(l => (l.X, l.Z, 3, false))
			.Concat(Packages().Select(p => (p.X, p.Z, 4, false)))
			.Concat(_safehouse is { } sh ? new[] { (sh.X, sh.Z, 5, false) } : Array.Empty<(double, double, int, bool)>());

	/// <summary>T on foot (D42): a little shooting range in front of the walker; T again sets it up afresh where you stand.</summary>
	public OnFoot.ShootingRange? SetUpRange()
	{
		if (_walker == null || !OnFoot) return null;
		_range?.QueueFree();
		_range = new OnFoot.ShootingRange();
		AddChild(_range);
		_range.Build(_walker.GlobalPosition, _walker.Yaw);
		ShowNote(Core.Lang.T("n.range"));
		return _range;
	}
	/// <summary>Walking (D38): the walker has the controls.</summary>
	public bool OnFoot => _walker?.Active ?? false;
	public Navigation? Nav => _nav;
	public MapScreen? MapView => _mapScreen;

	/// <summary>Puts the car on the nearest road to a world point and hands it the controls.</summary>
	public void SpawnCar(double x, double z)
	{
		if (_car == null)
		{
			_car = new Car { Name = "Car", Map = _world, HasGround = _terrain.HasGround, Model = Car.ChooseModel(Args.GetValueOrDefault("car")) };
			AddChild(_car);
			_car.AddChild(new Audio.EngineSound(_car));   // engine, tyres, wind (D53)
		}
		_walker?.Deactivate();
		_car.Recover(x, z);
		_car.Driving = true;
		_car.SetView(false);
		Flying = false;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10 } && _world != null && !(_sandboxMenu?.IsOpen ?? false))
		{
			OpenSandboxMenu();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 } && _world != null)
		{
			// debug dump (D28): screenshot + everything about this spot, for bug reports
			try
			{
				string path = DebugDump.Take(this);
				ShowNote(Core.Lang.T("n.dump", path));
				Core.Log.Info("Dump", path);
			}
			catch (Exception ex) { ShowNote(Core.Lang.T("n.dump_failed", ex.Message)); }
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _world != null)
		{
			_grass.On = !_grass.On;
			ShowNote(Core.Lang.T(_grass.On ? "n.grass_on" : "n.grass_off"));
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F7, ShiftPressed: true } && _world != null)
		{
			// the Brutalist Warsaw that never was (D79): BadBuilding's "brutalist" preset on or off (turns BadBuilding on)
			Map.BadBuildingAdapter.Preset = Map.BadBuildingAdapter.Preset == null ? "brutalist" : null;
			Map.BadBuildingAdapter.Enabled = true;
			Map.BadBuildingAdapter.ResetStats();
			_content.Configure(_world);
			ShowNote(Core.Lang.T(Map.BadBuildingAdapter.Preset != null ? "n.bld_brutal" : "n.bld_bb"));
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F7 } && _world != null)
		{
			// BadBuilding buildings on or off (D29): the streamer rebuilds every cell
			Map.BadBuildingAdapter.Enabled = !Map.BadBuildingAdapter.Enabled;
			Map.BadBuildingAdapter.ResetStats();
			_content.Configure(_world);
			ShowNote(Core.Lang.T(Map.BadBuildingAdapter.Enabled ? "n.bld_bb" : "n.bld_classic"));
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Keycode: Key.Bracketleft or Key.Bracketright } k)
		{
			_atmosphere.AdvanceClock(k.Keycode == Key.Bracketright ? 0.5 : -0.5);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B } && OnFoot && _bandits != null)
		{
			int n = _bandits.SpawnAhead(_walker!.GlobalPosition, _walker.Yaw);
			ShowNote(n > 0 ? Core.Lang.T("n.bandits", n) : Core.Lang.T("n.no_bandits"));
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T } && OnFoot)
		{
			SetUpRange();
			GetViewport().SetInputAsHandled();
			return;
		}
		// dead: a click or F wakes you up somewhere else (D55)
		if (PlayerDown && _downTime > 1.2f && (e is InputEventMouseButton { Pressed: true } || e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F }))
		{
			Respawn();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (PlayerDown) return;
		// F: crates, the car (D55: E leans right now, Tom's pick); the free camera moved to F8. On foot by the car a tap
		// gets in and a hold opens the workshop (D68)
		if (e is InputEventKey { Keycode: Key.F, Echo: false } fk && _world != null && !Flying)
		{
			if (fk.Pressed)
			{
				// petrol pumps (D124), before crates (the prompt shows the pump first): F stops the filling; at a working pump, fills the car beside it, else a can
				if (OnFoot && _stations?.Filling != null) { ShowNote(_stations.StopFilling(), 4); GetViewport().SetInputAsHandled(); return; }
				if (OnFoot && _pumpHere is { } pump)
				{
					var pack = _walker!.Body.Pack;
					if (!Power.Stations.Works(pump)) ShowNote(Power.Stations.Prompt(pump, null, 0), 4);
					else if (PumpCar(pump) is { } pc)
					{
						if (pc.Fuel >= pc.Tank - 0.05f) ShowNote(Core.Lang.T("n.pump_full", "0", pc.Fuel.ToString("0.0")), 3);
						else _stations.StartFilling(pump, pc);
					}
					else if (pack.Count("empty_petrol_can") > 0)
					{
						if (pump.Station.Litres < Vehicle.Workshop.CanLitres) ShowNote(Core.Lang.T("n.pump_ran_dry"), 3);
						else
						{
							pack.Remove("empty_petrol_can", 1);
							if (pack.Room("petrol_can") < 1) { pack.Add("empty_petrol_can", 1); ShowNote(Core.Lang.T("gen.too_heavy"), 3); }
							else { _stations.Draw(pump.Station, Vehicle.Workshop.CanLitres); pack.Add("petrol_can", 1); ShowNote(Core.Lang.T("n.pump_can", pack.Count("petrol_can")), 4); }
						}
					}
					else ShowNote(Power.Stations.Prompt(pump, null, 0), 4);
					GetViewport().SetInputAsHandled(); return;
				}
				if (OnFoot && _bootHere != null && IsInstanceValid(_bootHere)) { OpenBoot(_bootHere); GetViewport().SetInputAsHandled(); return; }   // D125
				if (TryOpenCache()) { GetViewport().SetInputAsHandled(); return; }
				// generators (D109): put down what you carry; by one, a tap starts or stops it and a hold lifts it
				if (OnFoot && _generators?.Carried != null) { _generators.PutDown(_walker!.GlobalPosition, _walker.Yaw); _walker.Burden = 0; GetViewport().SetInputAsHandled(); return; }
				if (OnFoot && _fireHere != null)
				{
					var pack = _walker!.Body.Pack;
					if (pack.Count("firewood") == 0) ShowNote(Core.Lang.T("n.no_wood"), 3);
					else if (!_fireHere.Burning && pack.CountMatching("tag:fire") == 0) ShowNote(Core.Lang.T("n.no_light"), 3);
					else { pack.Remove("firewood", 1); bool was = _fireHere.Burning; _fires!.AddWood(_fireHere); ShowNote(was ? Core.Lang.T("n.feed", Power.Campfires.Left(_fireHere)) : Core.Lang.T("n.catches"), 3); }
					GetViewport().SetInputAsHandled(); return;
				}
				if (OnFoot && _canHere != null) { _generators!.TakeCan(_canHere, _walker!.Body.Pack, out var took); ShowNote(took, 4); _canHere = null; GetViewport().SetInputAsHandled(); return; }
				if (OnFoot && _genHere != null) { _genHold = 0; GetViewport().SetInputAsHandled(); return; }
				if (OnFoot && _spot != null) { OpenInventory(_spot); GetViewport().SetInputAsHandled(); return; }
				if (OnFoot && _wellHere is { } well)
				{
					// a well (D121): clean water, one empty bottle per press
					var pack = _walker!.Body.Pack;
					if (pack.Count("empty_bottle") == 0) ShowNote(Core.Lang.T("n.no_bottle"), 3);
					else { pack.Remove("empty_bottle", 1); pack.Add("water", 1); ShowNote(Core.Lang.T("n.well_filled", Power.Wells.Name(well), pack.Count("water")), 4); }
					GetViewport().SetInputAsHandled(); return;
				}
				if (OnFoot && _waterHere)
				{
					// river water (D111): one empty bottle filled per press
					var pack = _walker!.Body.Pack;
					if (pack.Count("empty_bottle") == 0) ShowNote(Core.Lang.T("n.no_bottle"), 3);
					else { pack.Remove("empty_bottle", 1); pack.Add("dirty_water", 1); ShowNote(Core.Lang.T("n.filled", pack.Count("dirty_water")), 4); }
					GetViewport().SetInputAsHandled(); return;
				}
				if (OnFoot && _treeHere is { } tree)
				{
					if (_walker!.Body.Pack.CountMatching("tag:chopping") == 0) ShowNote(Core.Lang.T("n.need_axe"), 3);
					else if (Power.Woodland.Used(SaveWorld, Power.Woodland.Key(tree))) ShowNote(Core.Lang.T("n.tree_taken"), 3);
					else { _chopT = 0; _chopTree = tree; _chopFrom = _walker.GlobalPosition; _chopHealth = _walker.Body.Health; _nextThunk = 0; }
					GetViewport().SetInputAsHandled(); return;
				}
				_fTarget = OnFoot ? NearestCar() : null;
				if (_fTarget != null) _fHeld = 0;
				else ToggleOnFoot();
			}
			else if (_genHold >= 0)
			{
				// a tap: on or off
				_genHold = -1;
				if (_genHere != null && _generators!.Toggle(_genHere, out var said)) ShowNote(said, 4); else if (_genHere != null) ShowNote(Core.Lang.T("n.no_petrol") + (_walker!.Body.Pack.Count("petrol_can") > 0 ? Core.Lang.T("n.pour_hint") : ""), 4);
			}
			else if (_fHeld >= 0)
			{
				// a tap: get in (your car, or another that still runs: D99), or why it won't go
				_fHeld = -1;
				if (_fTarget == _car) ToggleOnFoot(); else if (_fTarget != null) TryTakeCar(_fTarget);
			}
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } && OnFoot)
		{
			OpenInventory(null);
			GetViewport().SetInputAsHandled();
			return;
		}
		// in the car, stopped: the pack and the boot behind you (D125)
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } && !OnFoot && !Flying && _car is { Driving: true } && _walker != null)
		{
			if (MathF.Abs(_car.Speed) < 0.6f) OpenBoot(_car); else ShowNote(Core.Lang.T("n.boot_moving"), 2);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N } && _minimap != null)
		{
			_minimapOn = !_minimapOn;
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } && _mapScreen != null && !_mapScreen.Visible)
		{
			_mapScreen.Toggle();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 } && _world != null) ToggleFreeCamera();
	}

	/// <summary>Why a car can't be driven away (D99), or null when it can: the engine, the battery, the wheels.</summary>
	public static string? WontGo(Vehicle.Car c)
	{
		var p = c.Parts;
		if (p["engine"].Missing) return Core.Lang.T("car.no_engine");
		if (p["engine"].Health <= 0) return Core.Lang.T("car.engine_wrecked");
		if (p["battery"].Missing) return Core.Lang.T("car.no_battery");
		if (p["battery"].Health <= 0.08f) return Core.Lang.T("car.battery_flat");
		int wheels = 0;
		for (int i = 0; i < 4; i++) if (p.Tyre(i).Missing) wheels++;
		if (wheels > 0) return wheels == 1 ? Core.Lang.T("car.wheel_missing") : Core.Lang.T("car.wheels_missing", wheels);
		if (c.Dry) return Core.Lang.T("car.tank_empty");   // D123
		return null;
	}

	/// <summary>Another car (a wreck that still runs, or one you left, D99): you get in and it's yours; the car you had
	/// stays parked where it stands for the rest of the session.</summary>
	public void TryTakeCar(Vehicle.Car c)
	{
		if (WontGo(c) is { } why) { ShowNote(Core.Lang.T("n.wont_start", why)); return; }
		if (_car != null && _car != c)
		{
			_car.Driving = false;
			_car.Parked = true;
			_wrecks?.Leave(_car);
		}
		_wrecks?.Take(c);
		c.Parked = false;
		c.Freeze = false;
		c.HasGround ??= _terrain.HasGround;
		if (c.GetChildren().OfType<Audio.EngineSound>().FirstOrDefault() == null) c.AddChild(new Audio.EngineSound(c));
		_car = c;
		_walker?.Deactivate();
		c.Driving = true;
		c.SetView(false);
		ShowNote(c.Parts.Report() is { Length: > 0 } r ? Core.Lang.T("n.starts", r) : Core.Lang.T("n.starts_clean"));
		Core.Log.Info("Main", $"took the car {c.Name} ({c.Model.GetFile()}, drives as '{c.Spec.Id}')");
	}

	/// <summary>E (D38): out of the car on the driver's side, or back in when standing by it.</summary>
	public bool ToggleOnFoot()
	{
		if (_car == null || Flying) return false;
		if (OnFoot)
		{
			if (_walker!.GlobalPosition.DistanceTo(_car.GlobalPosition) > 4.5f) { ShowNote(Core.Lang.T("p.walk_to_car")); return false; }
			_walker.Deactivate();
			_car.Driving = true;
			_car.SetView(false);
			return true;
		}
		if (MathF.Abs(_car.Speed) > 2f) { ShowNote(Core.Lang.T("p.stop_car")); return false; }
		_walker ??= CreateWalker();
		_car.Driving = false;
		var t = _car.GlobalTransform;
		// out by the driver's door (left), facing the way the car faces
		var at = t.Origin - t.Basis.X * 1.7f + Vector3.Up * 0.3f;
		_walker.Activate(at, _car.Rotation.Y);
		return true;
	}

	private OnFoot.Walker CreateWalker()
	{
		var w = new OnFoot.Walker { Name = "Walker", HasGround = _terrain.HasGround };
		AddChild(w);
		return w;
	}

	/// <summary>F: from the car to the free camera, or from the free camera back to driving at the nearest road.</summary>
	public void ToggleFreeCamera()
	{
		if (OnFoot)
		{
			Flying = true;
			_camera.GlobalTransform = _walker!.ActiveCamera.GlobalTransform;
			_camera.Yaw = _camera.Rotation.Y; _camera.Pitch = _camera.Rotation.X;
			_walker.Deactivate();
			_camera.Current = true;
			return;
		}
		if (Flying)
		{
			var (x, _, z) = WorldOrigin.ToWorld(_camera.GlobalPosition);
			SpawnCar(x, z);
		}
		else if (_car != null)
		{
			Flying = true;
			_car.Driving = false;
			_camera.GlobalTransform = _car.ActiveCamera.GlobalTransform;
			_camera.Yaw = _camera.Rotation.Y; _camera.Pitch = _camera.Rotation.X;
			_camera.Current = true;
		}
	}

	/// <summary>Moves the camera to a world point, <paramref name="above"/> metres over the ground (yaw/pitch in degrees).</summary>
	public void Teleport(double x, double z, float above, float yawDeg, float pitchDeg)
	{
		WorldOrigin.Reset(x, z);
		_terrain.Configure(_world!);
		_content.Configure(_world!);
		_trees.Configure(_world!);
		_grass.Configure(_world!);
		_farRoads.Configure(_world!);
		float ground = _world!.Height(x, z);
		_camera.Position = WorldOrigin.ToLocal(x, ground + above, z);
		_camera.Yaw = Mathf.DegToRad(yawDeg);
		_camera.Pitch = Mathf.DegToRad(pitchDeg);
	}

	/// <summary>Who the player is right now (the walker, the car or the free camera): where, which way, how fast.</summary>
	private (double X, double Z, Vector2 Forward, float SpeedKmh) Subject()
	{
		var (x, z) = Focus;
		Vector3 f; float kmh = 0;
		if (OnFoot) { f = new Vector3(-MathF.Sin(_walker!.Yaw), 0, -MathF.Cos(_walker.Yaw)); kmh = _walker.Speed * 3.6f; }
		else if (!Flying && _car != null) { f = -_car.GlobalTransform.Basis.Z; kmh = _car.Speed * 3.6f; }
		else f = -_camera.GlobalTransform.Basis.Z;
		var d = new Vector2(f.X, f.Z);
		return (x, z, d.LengthSquared() > 1e-4f ? d.Normalized() : new Vector2(0, -1), kmh);
	}

	public (double X, double Z) Focus
	{
		get
		{
			var at = OnFoot ? _walker!.GlobalPosition : !Flying && _car != null ? _car.GlobalPosition : _camera.GlobalPosition;
			var (x, _, z) = WorldOrigin.ToWorld(at);
			return (x, z);
		}
	}

	private double _fHeld = -1, _genHold = -1;
	private Vehicle.Car? _fTarget;
	private Vehicle.WreckField? _wrecks;
	private Vehicle.ParkedCars? _parked;
	public Vehicle.ParkedCars? Parked => _parked;
	public Vehicle.WreckField? Wrecks => _wrecks;

	/// <summary>The car (yours or a wreck, D69) the walker stands by, within 4.5 m.</summary>
	/// <summary>The car standing at a pump (D124): yours or any other, within the hose's reach.</summary>
	public Vehicle.Car? PumpCar(Power.Stations.Pump p)
	{
		if (p.Node == null) return null;
		var at = p.Node.GlobalPosition;
		Vehicle.Car? best = null; float bd = Power.Stations.CarReach;
		foreach (var c in (_car != null ? new[] { _car } : Array.Empty<Vehicle.Car>()).Concat(_wrecks?.Live ?? Enumerable.Empty<Vehicle.Car>()))
			if (IsInstanceValid(c) && c.GlobalPosition.DistanceTo(at) is var d && d < bd) { bd = d; best = c; }
		return best;
	}

	public Vehicle.Car? NearestCar()
	{
		if (_walker == null) return null;
		var at = _walker.GlobalPosition;
		Vehicle.Car? best = null; float bd = 4.5f;
		foreach (var c in (_car != null ? new[] { _car } : Array.Empty<Vehicle.Car>()).Concat(_wrecks?.Live ?? Enumerable.Empty<Vehicle.Car>()))
		{
			if (!IsInstanceValid(c)) continue;
			float d = c.GlobalPosition.DistanceTo(at);
			if (d < bd) { bd = d; best = c; }
		}
		return best;
	}
	private Vehicle.Workshop? _workshop;
	public Vehicle.Workshop? Workshop => _workshop;

	/// <summary>Opens the workshop on the car (D68): the walker stands still meanwhile.</summary>
	public void OpenWorkshop(Vehicle.Car? on = null)
	{
		on ??= _car;
		if (on == null || _walker == null) return;
		if (_workshop == null)
		{
			_workshop = new Vehicle.Workshop { Name = "Workshop" };
			_ui.AddChild(_workshop);
			_workshop.Said += s => ShowNote(s);
			_workshop.Body = () => _walker?.Body;
		}
		_walker.Autopilot = _ => (Vector2.Zero, false);
		_workshop.Begin(on);
	}

	public override void _Process(double delta)
	{
		if (_world != null) Core.WorldClock.Tick(delta);   // food ages while you play (D115)
		// holding F by the car: the workshop (D68)
		if (_chopT >= 0 && _walker != null)
		{
			// chopping (D111): a knock every 0.7 s; a hit or a step away stops it
			_chopT += delta;
			if (_walker.Body.Health < _chopHealth - 0.5f || (_walker.GlobalPosition - _chopFrom).Length() > 0.8f || !OnFoot || PlayerDown)
			{
				_chopT = -1;
				ShowNote(Core.Lang.T(_walker.Body.Health < _chopHealth - 0.5f ? "n.hit_axe" : "n.stop_chop"), 3);
			}
			else
			{
				if (_chopT >= _nextThunk)
				{
					_nextThunk += 0.7;
					var thunk = new AudioStreamPlayer3D { Stream = Audio.SoundBank.Get("chop", Power.Woodland.Thunk), Bus = Audio.Buses.Sfx, UnitSize = 4f, MaxDistance = 80f };
					AddChild(thunk);
					thunk.GlobalPosition = _walker.GlobalPosition + Vector3.Up;
					thunk.Play();
					thunk.Finished += thunk.QueueFree;
				}
				if (_chopT >= ChopTime)
				{
					_chopT = -1;
					int y = Power.Woodland.Yield(_chopTree) + (Items.Skills.Level("woodcraft") >= 3 ? 1 : 0), n = _walker.Body.Pack.Add("firewood", y);
					Items.Skills.Add("woodcraft", 10);
					Power.Woodland.Use(SaveWorld, Power.Woodland.Key(_chopTree));
					string got = n == 1 ? Core.Lang.T("n.armful") : Core.Lang.T("n.armfuls", n);
					ShowNote(n == y ? Core.Lang.T("n.chopped", got) : n > 0 ? Core.Lang.T("n.chopped_some", y, got) : Core.Lang.T("n.chopped_none"), 4);
					Core.Log.Info("Wood", $"chopped {n} at {Power.Woodland.Key(_chopTree)}");
				}
			}
		}
		if (_genHold >= 0)
		{
			_genHold += delta;
			if (_genHold > 0.6 && _genHere != null && _walker != null)
			{
				_genHold = -1;
				_generators!.PickUp(_genHere);
				_walker.Burden = 1;
				ShowNote(Core.Lang.T("n.lift_gen"), 4);
			}
		}
		if (_fHeld >= 0)
		{
			_fHeld += delta;
			if (_fHeld > 0.45) { _fHeld = -1; OpenWorkshop(_fTarget); }
		}
		if (_workshop != null && !_workshop.Open && _walker != null && _walker.Autopilot != null && _tests == null) _walker.Autopilot = null;
		if (_world == null) return;
		var (fx, fz) = Focus;
		WorldOrigin.Follow(fx, fz);
		(fx, fz) = Focus;
		_terrain.Update(fx, fz);
		_content.Update(fx, fz);
		_farRoads.Update(fx, fz);
		_trees.Update(fx, fz, GetViewport().GetCamera3D()?.GlobalPosition ?? _camera.GlobalPosition);
		{
			var cam = GetViewport().GetCamera3D()?.GlobalPosition ?? _camera.GlobalPosition;
			var (gx, gy, gz) = WorldOrigin.ToWorld(cam);
			_grass.Update(gx, gz, (float)gy - _world.Height(gx, gz));
		}
		_atmosphere.Update(delta, GetViewport().GetCamera3D());
		_camera.GroundHeight = _world.Height(fx, fz);
		RenderingServer.GlobalShaderParameterSet("ocean_time", (float)(Time.GetTicksMsec() / 1000.0));
		var (lat, lon) = Core.Projection.ToGeo(fx, fz);
		// where am I (D33): street and number, district, town; worked out twice a second
		if (Time.GetTicksMsec() > _whereAt) { _where = _world.WhereAmI(fx, fz); _whereAt = Time.GetTicksMsec() + 500; }
		string speed = OnFoot ? Core.Lang.T("hud.on_foot", (_walker!.Speed * 3.6f).ToString("F0"), _walker.Crouching ? Core.Lang.T("hud.crouching") : "")
			: !Flying && _car != null ? $"{MathF.Abs(_car.Speed) * 3.6f:F0} km/h{(_car.OnRoad ? "" : Core.Lang.T("hud.off_road"))}   " : "";
		string carState = !OnFoot && !Flying && _car != null && _car.Parts.Report() is { Length: > 0 } rep ? Core.Lang.T("hud.car", rep) : "";
		_hud.Text = $"{speed}{Engine.GetFramesPerSecond()} fps   {lat:F5}° N  {lon:F5}° E   {_where}\n" + carState +
					$"x {fx:F0}  z {fz:F0}  alt {_camera.GlobalPosition.Y:F0} m (ground {_camera.GroundHeight:F1})\n" +
					$"terrain {_terrain.Resident} tiles, {_terrain.Missing} missing, {_terrain.Pending} building, commit {_terrain.FrameCommitMs:F1} ms (max {_terrain.MaxCommitMs:F1})\n" +
					$"map {_content.Cells} cells, {_content.Pending} building, trees {_trees.Instances3D} near + {_trees.ImpostorInstances} far ({_trees.Missing} missing)\n" +
					(Map.BadBuildingAdapter.Enabled ? Map.BadBuildingAdapter.Stats() + "   (F7: classic buildings)\n" : "") +
					(Core.Lang.T("hud.keys_top", $"{(int)_atmosphere.Hour:00}:{(int)(_atmosphere.Hour % 1 * 60):00}") + Core.Lang.T(Flying ? "hud.keys_fly" : OnFoot ? "hud.keys_foot" : "hud.keys_car"));
		_nav?.Update(new Vector2((float)fx, (float)fz), Time.GetTicksMsec() / 1000.0);
		UpdateSurvival(Time.GetTicksMsec() / 1000.0);
		_tests?.Process(delta);
		if (_note != null && _note.Visible && Time.GetTicksMsec() / 1000.0 > _noteUntil) _note.Visible = false;
		bool shop = _workshop?.Open ?? false;   // the workshop has the screen to itself (D68)
		if (_minimap != null) _minimap.Visible = _minimapOn && !(_mapScreen?.Visible ?? false) && !shop;
		_hud.Visible = !shop;
		if (_bars != null && shop) _bars.Visible = false;
		if (_prompt != null && shop) _prompt.Visible = false;
	}

	private Label? _note;
	private double _noteUntil, _noteAt;

	/// <summary>A short message at the bottom of the screen for a few seconds.</summary>
	/// <param name="keep">Add under a note that's just gone up instead of replacing it (D54: a lead found in a crate
	/// mustn't wipe what the crate held).</param>
	public void ShowNote(string text, double seconds = 6, bool keep = false)
	{
		double now = Time.GetTicksMsec() / 1000.0;
		if (keep && _note != null && _note.Visible && now - _noteAt < 2) text = _note.Text + "\n" + text;
		_noteAt = now;
		if (_note == null)
		{
			_note = new Label { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -78, OffsetBottom = -40, HorizontalAlignment = HorizontalAlignment.Center, GrowVertical = Control.GrowDirection.Begin, GrowHorizontal = Control.GrowDirection.Both };
			_note.AddThemeColorOverride("font_shadow_color", Colors.Black);
			// D102: on the set's message strip
			if (UI.Skin.Box("panels/note_toast.png", 14, 2) is { } strip) { _note.AddThemeStyleboxOverride("normal", strip); _note.AddThemeColorOverride("font_color", UI.Skin.Text); }
			_ui.AddChild(_note);
		}
		_note.Text = text;
		_note.Visible = true;
		_noteUntil = Time.GetTicksMsec() / 1000.0 + seconds;
	}

	private void BuildEnvironment()
	{
		_atmosphere = new Sky.Atmosphere { Name = "Atmosphere" };
		// --hour=HH.H (local solar time) and --day=N (day of the year) for screenshots; T+/- keys change the hour
		if (Args.TryGetValue("hour", out var h)) _atmosphere.Hour = double.Parse(h, System.Globalization.CultureInfo.InvariantCulture);
		if (Args.TryGetValue("day", out var d)) _atmosphere.DayOfYear = double.Parse(d, System.Globalization.CultureInfo.InvariantCulture);
		if (Args.TryGetValue("jump", out var jmp)) _atmosphere.AdvanceClock(double.Parse(jmp, System.Globalization.CultureInfo.InvariantCulture));
		if (Args.TryGetValue("clouds", out var c)) { _atmosphere.Coverage = float.Parse(c, System.Globalization.CultureInfo.InvariantCulture); _atmosphere.WeatherDrift = false; }
		if (Args.GetValueOrDefault("mood", "1") == "0") _atmosphere.Mood = false;
		AddChild(_atmosphere);
		// look experiments: --grade=N (0 none), --volfog=0, --haze=F, --sat=F
		var env = _atmosphere.Environment;
		var inv = System.Globalization.CultureInfo.InvariantCulture;
		if (Args.TryGetValue("grade", out var g)) { env.AdjustmentColorCorrection = g == "0" ? null : Sky.ColorGrading.Lut(int.Parse(g), .6f); _atmosphere.GradeFixed = true; }
		if (Args.TryGetValue("volfog", out var v)) env.VolumetricFogEnabled = v != "0";
		if (Args.TryGetValue("haze", out var hz)) { _atmosphere.HazeFixed = true; env.FogDensity = .00007f * float.Parse(hz, inv); env.FogAerialPerspective = .26f * float.Parse(hz, inv); }
		if (Args.TryGetValue("sat", out var st)) { env.AdjustmentSaturation = float.Parse(st, inv); _atmosphere.SaturationFixed = true; }
		// diagnostics: --off=ssao,ssil,glow,fog,water,aa
		if (Args.TryGetValue("off", out var off))
			foreach (var f in off.Split(','))
				switch (f)
				{
					case var t when t.StartsWith("tshader="): if (GD.Load<Material>("res://Shaders/terrain.tres") is ShaderMaterial tt) tt.Shader = GD.Load<Shader>(t[8..]); break;
					case "ssao": env.SsaoEnabled = false; break;
					case "ssil": env.SsilEnabled = false; break;
					case "glow": env.GlowEnabled = false; break;
					case "fog": env.FogEnabled = false; break;
					case "watershader": if (GD.Load<Material>("res://Shaders/water.tres") is ShaderMaterial wm2) wm2.Shader = new Shader { Code = "shader_type spatial; void fragment() { ALBEDO = vec3(0.1, 0.2, 0.5); }" }; break;
					case "terrainshader": if (GD.Load<Material>("res://Shaders/terrain.tres") is ShaderMaterial tm) tm.Shader = new Shader { Code = "shader_type spatial; void fragment() { ALBEDO = vec3(0.2, 0.3, 0.1); }" }; break;
					case "terrainunlit": if (GD.Load<Material>("res://Shaders/terrain.tres") is ShaderMaterial tu) tu.Shader = new Shader { Code = "shader_type spatial; render_mode unshaded; void fragment() { ALBEDO = vec3(0.2, 0.3, 0.1); }" }; break;
					case "decal": foreach (var ch in _atmosphere.GetChildren()) if (ch is Decal dd) dd.Visible = false; break;
					case "farroads": _farRoads.Visible = false; break;
					case "trees": _trees.Visible = false; break;
					case "content": _content.Visible = false; break;
					case "water": if (GD.Load<Material>("res://Shaders/water.tres") is ShaderMaterial wm) wm.SetShaderParameter("ssr_steps", 0); break;
				}
	}
}
