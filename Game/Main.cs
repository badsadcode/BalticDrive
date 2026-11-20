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
		var credit = new Label { Text = "Map data © OpenStreetMap contributors · elevation © GUGiK", Modulate = new Color(1, 1, 1, 0.75f) };
		credit.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 10);
		credit.GrowHorizontal = Control.GrowDirection.Begin;
		credit.GrowVertical = Control.GrowDirection.Begin;
		credit.AddThemeColorOverride("font_shadow_color", Colors.Black);
		ui.AddChild(credit);
		AddChild(ui);
		WorldOrigin.Shifted += (dx, dz) => _camera.Position -= new Vector3((float)dx, 0, (float)dz);

		string mapName = Args.GetValueOrDefault("map", "mazowsze");
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
		Teleport(sx, sz, 40f, 0f, -12f);
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

	private OnFoot.ShootingRange? _range;
	private OnFoot.CacheField? _caches;
	public OnFoot.CacheField? Caches => _caches;
	private Label? _prompt;
	private OnFoot.BanditField? _bandits;
	public OnFoot.BanditField? Bandits => _bandits;
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
			void Bar(int row, string name, float v, Color col)
			{
				float y = row * 20;
				_bars.DrawRect(new Rect2(70, y + 4, 140, 10), new Color(0, 0, 0, 0.45f));
				_bars.DrawRect(new Rect2(70, y + 4, 140 * Mathf.Clamp(v / 100f, 0, 1), 10), v < 25 ? col.Lightened(0.35f) : col);
				_bars.DrawString(font, new Vector2(0, y + 14), name, HorizontalAlignment.Left, 66, 14, new Color(1, 1, 1, 0.9f));
			}
			Bar(0, "health", b.Health, new Color(0.85f, 0.22f, 0.2f));
			Bar(1, "food", b.Hunger, new Color(0.9f, 0.6f, 0.2f));
			Bar(2, "water", b.Thirst, new Color(0.25f, 0.55f, 0.95f));
			_bars.DrawString(font, new Vector2(0, 74), $"carrying: food {b.Food}, water {b.Water}, medkits {b.Medkits} (H)", HorizontalAlignment.Left, 220, 13, new Color(1, 1, 1, 0.8f));
		};
		_ui.AddChild(_bars);
		_prompt = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
		_prompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
		_prompt.OffsetTop = 70; _prompt.OffsetBottom = 100; _prompt.OffsetLeft = -250; _prompt.OffsetRight = 250;
		_prompt.AddThemeFontSizeOverride("font_size", 18);
		_prompt.AddThemeColorOverride("font_shadow_color", Colors.Black);
		_ui.AddChild(_prompt);
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
			string what = _downCause switch { "starved" => "You collapsed from hunger.", "dehydrated" => "You collapsed from thirst.", _ => "You were shot." };
			_deathLabel.Text = $"{what}\nYour things stay here with you.\n\nClick to wake up {(BalticDrive.OnFoot.Safehouse.Get(MapName) is { } sh ? "in your safehouse" : "somewhere else")}.";
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
	public static string MapName => Args.GetValueOrDefault("map", "mazowsze");
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
		if (loot.Food + loot.Water + loot.Medkits + loot.Ammo.Sum(a => a.Rounds) == 0) _caches.Open(package);   // nothing to go back for
		foreach (var b in _bandits!.All) b.QueueFree();
		_bandits.Clear();
		var home = _safehouse;
		var step = home ?? BalticDrive.OnFoot.Homes.Pick(_world, fx, fz, new Random()) ?? new BalticDrive.OnFoot.Doorstep(fx + 40, fz, 0, 0, "");
		float h = _world.Height(step.X, step.Z);
		_walker.Revive(WorldOrigin.ToLocal(step.X, h + 0.4, step.Z), step.Yaw);
		_walker.Body.Revive();
		_downTime = -1;
		_wakeTime = 0;
		if (_deathLabel != null) _deathLabel.Visible = false;
		double dx = fx - step.X, dz = fz - step.Z, km = Math.Sqrt(dx * dx + dz * dz) / 1000;
		string toward = Compass(dx, dz);
		string where = home != null ? $"in your safehouse{(home.Value.Address.Length > 0 ? ", " + home.Value.Address : "")}"
			: step.Address.Length > 0 ? $"outside {step.Address}" : "outside a block of flats";
		ShowNote(package.Opened ? $"You wake up {where}." : km < 0.05 ? $"You wake up {where}. Your things are right here."
			: $"You wake up {where}. Your things are {km:0.0} km {toward}, where you fell: marked on the map (M).", 12);
		Core.Log.Info("Death", $"woke {where}, {km:0.00} km from the body (package {(package.Opened ? "empty" : "left")})");
	}

	private static string Compass(double dx, double dz)
	{
		// world +Z is south
		string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
		double a = Math.Atan2(dx, -dz);
		int i = (int)Math.Round(a / (Math.PI / 4));
		return names[((i % 8) + 8) % 8];
	}

	/// <summary>Hold K by the wall of a home (D55): it becomes your safehouse.</summary>
	private bool UpdateClaim(double now, float dt)
	{
		if (_walker == null || _world == null) return false;
		if (now >= _nextHomeLook)
		{
			_nextHomeLook = now + 0.3;
			var (wx, _, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
			_homeHere = BalticDrive.OnFoot.Homes.At(_world, wx, wz, _walker.Yaw + MathF.PI) is { } at ? (at.Step, at.Step.Address.Length > 0 ? at.Step.Address : at.Home.Type == BuildingKind.House ? "this house" : "this block") : null;
		}
		if (_homeHere is not { } here || (_safehouse is { } mine && mine.OsmId == here.Step.OsmId && mine.OsmId != 0)) { _claim = 0; return false; }
		bool holding = TestClaim || Input.IsKeyPressed(Key.K);
		_claim = holding ? _claim + dt : 0;
		_prompt!.Visible = true;
		_prompt.Text = holding ? $"Claiming {here.Name}... {MathF.Min(_claim / 2f, 1):P0}" : $"Hold K: make {here.Name} your safehouse";
		if (_claim >= 2f)
		{
			// facing out from the wall when you wake: the way you'd walk out of the door
			var (wx, _, wz) = WorldOrigin.ToWorld(_walker.GlobalPosition);
			var s = here.Step with { X = wx, Z = wz };
			BalticDrive.OnFoot.Safehouse.Set(MapName, s);
			_safehouse = s;
			_claim = 0;
			ShowNote($"{char.ToUpper(here.Name[0]) + here.Name[1..]} is your safehouse now. You'll wake up here.", 8);
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
			_wrecks = new Vehicle.WreckField(_world); AddChild(_wrecks);   // cars to strip (D69)
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
		if (!_safehouseLoaded) { _safehouseLoaded = true; _safehouse = BalticDrive.OnFoot.Safehouse.Get(MapName); }
		// wrecks along the way, on foot or driving (D69)
		if (on) _wrecks?.Update(_walker!.GlobalPosition, now);
		else if (_car != null && _car.Driving && !Flying) _wrecks?.Update(_car.GlobalPosition, now);
		if (!on || PlayerDown) { _prompt!.Visible = false; _claim = 0; return; }
		_caches.Update(_walker!.GlobalPosition, now);
		var c = _caches.InReach(_walker.GlobalPosition);
		_prompt!.Visible = c != null;
		if (c != null) _prompt.Text = c.Key.StartsWith("drop:") ? "F: search the body" : c.Key.StartsWith("death:") ? "F: take your things" : c.Keep ? "F: take the stash" : "F: open the crate" + (string.IsNullOrEmpty(c.Place) ? "" : $" ({c.Place})");
		else UpdateClaim(now, (float)GetProcessDeltaTime());
		_bars.QueueRedraw();
	}
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
		ShowNote("Range set up: cans and bottles at 8 m, a paper target at 15 m, plates at 25, 50 and 100 m");
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
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 } && _world != null)
		{
			// debug dump (D28): screenshot + everything about this spot, for bug reports
			try
			{
				string path = DebugDump.Take(this);
				ShowNote($"Dump saved: {path}");
				Core.Log.Info("Dump", path);
			}
			catch (Exception ex) { ShowNote($"Dump failed: {ex.Message}"); }
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _world != null)
		{
			_grass.On = !_grass.On;
			ShowNote(_grass.On ? "Grass on (G)" : "Grass off (G)");
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
			ShowNote(Map.BadBuildingAdapter.Preset != null ? "Buildings: Brutalist Warsaw" : "Buildings: BadBuilding");
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F7 } && _world != null)
		{
			// BadBuilding buildings on or off (D29): the streamer rebuilds every cell
			Map.BadBuildingAdapter.Enabled = !Map.BadBuildingAdapter.Enabled;
			Map.BadBuildingAdapter.ResetStats();
			_content.Configure(_world);
			ShowNote(Map.BadBuildingAdapter.Enabled ? "Buildings: BadBuilding" : "Buildings: classic");
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
			ShowNote(n > 0 ? $"{n} bandits ahead!" : "No room for bandits here");
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
				if (TryOpenCache()) { GetViewport().SetInputAsHandled(); return; }
				_fTarget = OnFoot ? NearestCar() : null;
				if (_fTarget != null) _fHeld = 0;
				else ToggleOnFoot();
			}
			else if (_fHeld >= 0)
			{
				// a tap: get in (your car), or a word about a wreck
				_fHeld = -1;
				if (_fTarget == _car) ToggleOnFoot(); else ShowNote("A wreck: hold F to strip it for parts");
			}
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

	/// <summary>E (D38): out of the car on the driver's side, or back in when standing by it.</summary>
	public bool ToggleOnFoot()
	{
		if (_car == null || Flying) return false;
		if (OnFoot)
		{
			if (_walker!.GlobalPosition.DistanceTo(_car.GlobalPosition) > 4.5f) { ShowNote("Walk up to the car to get in (E)"); return false; }
			_walker.Deactivate();
			_car.Driving = true;
			_car.SetView(false);
			return true;
		}
		if (MathF.Abs(_car.Speed) > 2f) { ShowNote("Stop the car first"); return false; }
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

	private double _fHeld = -1;
	private Vehicle.Car? _fTarget;
	private Vehicle.WreckField? _wrecks;
	public Vehicle.WreckField? Wrecks => _wrecks;

	/// <summary>The car (yours or a wreck, D69) the walker stands by, within 4.5 m.</summary>
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
		}
		_walker.Autopilot = _ => (Vector2.Zero, false);
		_workshop.Begin(on);
	}

	public override void _Process(double delta)
	{
		// holding F by the car: the workshop (D68)
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
		string speed = OnFoot ? $"on foot, {_walker!.Speed * 3.6f:F0} km/h{(_walker.Crouching ? ", crouching" : "")}   "
			: !Flying && _car != null ? $"{MathF.Abs(_car.Speed) * 3.6f:F0} km/h{(_car.OnRoad ? "" : " (off road)")}   " : "";
		string carState = !OnFoot && !Flying && _car != null && _car.Parts.Report() is { Length: > 0 } rep ? "car: " + rep + "\n" : "";
		_hud.Text = $"{speed}{Engine.GetFramesPerSecond()} fps   {lat:F5}° N  {lon:F5}° E   {_where}\n" + carState +
					$"x {fx:F0}  z {fz:F0}  alt {_camera.GlobalPosition.Y:F0} m (ground {_camera.GroundHeight:F1})\n" +
					$"terrain {_terrain.Resident} tiles, {_terrain.Missing} missing, {_terrain.Pending} building, commit {_terrain.FrameCommitMs:F1} ms (max {_terrain.MaxCommitMs:F1})\n" +
					$"map {_content.Cells} cells, {_content.Pending} building, trees {_trees.Instances3D} near + {_trees.ImpostorInstances} far ({_trees.Missing} missing)\n" +
					(Map.BadBuildingAdapter.Enabled ? Map.BadBuildingAdapter.Stats() + "   (F7: classic buildings)\n" : "") +
					($"{(int)_atmosphere.Hour:00}:{(int)(_atmosphere.Hour % 1 * 60):00} ([ ] time, F9 dump, N radar)   " + (Flying ? "F8: drive here   right mouse: look   WASD/QE move, Shift/Ctrl faster"
						: OnFoot ? "F: get in (hold F: workshop)   WASD walk, Shift run, Alt slow, Ctrl crouch, Q/E lean, Space jump, V camera, Esc mouse, F8 free camera\n1 Mosin, 2 AK, 3 pistol (again: holster)   left mouse fire, right mouse aim (Shift: hold breath), R reload, T targets, B bandits, H medkit, F crates"
						: "F: get out   F8: free camera   W/S drive, A/D steer, Space handbrake, C camera, H lights, R back to road"));
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
			_note = new Label { AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -70, OffsetBottom = -40, HorizontalAlignment = HorizontalAlignment.Center, GrowVertical = Control.GrowDirection.Begin };
			_note.AddThemeColorOverride("font_shadow_color", Colors.Black);
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
