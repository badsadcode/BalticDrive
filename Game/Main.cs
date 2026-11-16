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

		_terrain = new TerrainStreamer { Name = "Terrain" };
		_content = new MapContentStreamer { Name = "MapContent" };
		_trees = new Trees.TreeField();
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
	private ColorRect? _hurt;
	private Control? _bars;

	/// <summary>E on foot by an unopened crate (D46): opens it; otherwise E is the car, as before.</summary>
	public bool TryOpenCache()
	{
		if (!OnFoot || _caches == null || _walker == null) return false;
		var c = _caches.InReach(_walker.GlobalPosition);
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
	/// Going down (D50): the screen goes dark over a second and a half; then you come round beside your car with your
	/// pack gone and the bandits moved on, and the screen clears again.
	/// </summary>
	private void UpdateDown(float dt)
	{
		if (_downTime < 0 || _hurt == null || _walker == null) return;
		float before = _downTime;
		_downTime += dt;
		float dark = _downTime < 1.6f ? _downTime / 1.6f : MathF.Max(0, 1 - (_downTime - 2.6f) / 1.2f);
		_hurt.Color = new Color(0.05f * (1 - dark), 0, 0, Mathf.Clamp(dark, 0, 1));
		if (before < 2.0f && _downTime >= 2.0f)
		{
			foreach (var b in _bandits!.All) b.QueueFree();
			_bandits.Clear();
			if (_car != null)
			{
				var t = _car.GlobalTransform;
				if (_car.Driving)
				{
					// shot at the wheel (D52): you come round beside the car
					_car.Driving = false;
					_walker.Activate(t.Origin - t.Basis.X * 1.7f + Vector3.Up * 0.3f, _car.Rotation.Y);
				}
				_walker.GlobalPosition = t.Origin - t.Basis.X * 1.7f + Vector3.Up * 0.3f;
				_walker.Velocity = Vector3.Zero;
			}
			string lost = _walker.Body.WakeUp();
			string what = _downCause switch { "starved" => "You collapsed from hunger", "dehydrated" => "You collapsed from thirst", _ => "You were shot and left for dead" };
			ShowNote($"{what}. You come round by your car{(lost.Length > 0 ? " — " + lost : "")}.");
		}
		if (_downTime > 3.8f) { _downTime = -1; _hurt.Color = new Color(0.7f, 0, 0, 0); }
	}

	private void UpdateSurvival(double now)
	{
		if (_world == null) return;
		if (_caches == null)
		{
			_caches = new OnFoot.CacheField(_world); AddChild(_caches); BuildSurvivalHud();
			_bandits = new OnFoot.BanditField(_world, _caches); AddChild(_bandits);
			// crates and bodies on the radar while on foot (D50)
			if (_minimap != null)
				_minimap.Markers = () => OnFoot && _caches != null
					? _caches.Live.Select(c => { var (wx, _, wz) = WorldOrigin.ToWorld(c.GlobalPosition); return (wx, wz, c.Key.StartsWith("drop:") ? 2 : c.Kind == MapPoiKind.Medical ? 1 : 0, c.Opened); }).ToList()
					: Enumerable.Empty<(double, double, int, bool)>();
			// hit: the screen edge flashes red (D47)
			_hurt = new ColorRect { Color = new Color(0.7f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
			_hurt.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			_ui.AddChild(_hurt);
		}
		// the walker carries your health even before you first get out (bandits shoot at the car, D52)
		if (_walker == null && _car != null) _walker = CreateWalker();
		if (_car != null && _bandits!.Car != _car)
		{
			_bandits.Car = _car;
			// the bodywork stops most of it (D52): a fifth reaches the driver. At a half, three bandits took a parked
			// driver from 100 to 8 in ten seconds: a car is a big target that's hard to miss
			_car.Shot += d => { if (_car.Driving) _walker?.Body.Damage(d * 0.2f); };
		}
		if (_walker != null && _bandits!.Player != _walker)
		{
			_bandits.Player = _walker;
			_walker.Body.Hurt += a => { if (_hurt != null) _hurt.Color = _hurt.Color with { A = Mathf.Clamp(_hurt.Color.A + a / 60f, 0, 0.5f) }; };
			_walker.Body.Down += cause => { _downCause = cause; _downTime = 0; };
		}
		if (_hurt != null && _hurt.Color.A > 0 && _downTime < 0) _hurt.Color = _hurt.Color with { A = MathF.Max(0, _hurt.Color.A - (float)GetProcessDeltaTime() * 0.8f) };
		UpdateDown((float)GetProcessDeltaTime());
		_bandits!.Update(now);
		bool on = OnFoot && _walker != null;
		_bars!.Visible = on || (_car != null && _car.Driving && !Flying && _walker != null);   // health in the car too (D52)
		if (!on) { _prompt!.Visible = false; return; }
		_caches.Update(_walker!.GlobalPosition, now);
		var c = _caches.InReach(_walker.GlobalPosition);
		_prompt!.Visible = c != null;
		if (c != null) _prompt.Text = c.Key.StartsWith("drop:") ? "E: search the body" : "E: open the crate" + (string.IsNullOrEmpty(c.Place) ? "" : $" ({c.Place})");
		_bars.QueueRedraw();
	}
	public OnFoot.ShootingRange? Range => _range;

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
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.E } && _world != null && !Flying)
		{
			if (TryOpenCache()) { GetViewport().SetInputAsHandled(); return; }
			ToggleOnFoot();
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
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && _world != null) ToggleFreeCamera();
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

	public override void _Process(double delta)
	{
		if (_world == null) return;
		var (fx, fz) = Focus;
		WorldOrigin.Follow(fx, fz);
		(fx, fz) = Focus;
		_terrain.Update(fx, fz);
		_content.Update(fx, fz);
		_farRoads.Update(fx, fz);
		_trees.Update(fx, fz, GetViewport().GetCamera3D()?.GlobalPosition ?? _camera.GlobalPosition);
		_atmosphere.Update(delta, GetViewport().GetCamera3D());
		_camera.GroundHeight = _world.Height(fx, fz);
		RenderingServer.GlobalShaderParameterSet("ocean_time", (float)(Time.GetTicksMsec() / 1000.0));
		var (lat, lon) = Core.Projection.ToGeo(fx, fz);
		// where am I (D33): street and number, district, town; worked out twice a second
		if (Time.GetTicksMsec() > _whereAt) { _where = _world.WhereAmI(fx, fz); _whereAt = Time.GetTicksMsec() + 500; }
		string speed = OnFoot ? $"on foot, {_walker!.Speed * 3.6f:F0} km/h{(_walker.Crouching ? ", crouching" : "")}   "
			: !Flying && _car != null ? $"{MathF.Abs(_car.Speed) * 3.6f:F0} km/h{(_car.OnRoad ? "" : " (off road)")}   " : "";
		_hud.Text = $"{speed}{Engine.GetFramesPerSecond()} fps   {lat:F5}° N  {lon:F5}° E   {_where}\n" +
					$"x {fx:F0}  z {fz:F0}  alt {_camera.GlobalPosition.Y:F0} m (ground {_camera.GroundHeight:F1})\n" +
					$"terrain {_terrain.Resident} tiles, {_terrain.Missing} missing, {_terrain.Pending} building, commit {_terrain.FrameCommitMs:F1} ms (max {_terrain.MaxCommitMs:F1})\n" +
					$"map {_content.Cells} cells, {_content.Pending} building, trees {_trees.Instances3D} near + {_trees.ImpostorInstances} far ({_trees.Missing} missing)\n" +
					(Map.BadBuildingAdapter.Enabled ? Map.BadBuildingAdapter.Stats() + "   (F7: classic buildings)\n" : "") +
					($"{(int)_atmosphere.Hour:00}:{(int)(_atmosphere.Hour % 1 * 60):00} ([ ] time, F9 dump, N radar)   " + (Flying ? "F: drive here   right mouse: look   WASD/QE move, Shift/Ctrl faster"
						: OnFoot ? "E: get in (by the car)   WASD walk, Shift run, Alt slow, Ctrl crouch, Space jump, V camera, Esc mouse, F free camera\n1 Mosin, 2 AK, 3 pistol (again: holster)   left mouse fire, right mouse aim, R reload, T targets, B bandits, H medkit, E crates"
						: "E: get out   F: free camera   W/S drive, A/D steer, Space handbrake, C camera, H lights, R back to road"));
		_nav?.Update(new Vector2((float)fx, (float)fz), Time.GetTicksMsec() / 1000.0);
		UpdateSurvival(Time.GetTicksMsec() / 1000.0);
		_tests?.Process(delta);
		if (_note != null && _note.Visible && Time.GetTicksMsec() / 1000.0 > _noteUntil) _note.Visible = false;
		if (_minimap != null) _minimap.Visible = _minimapOn && !(_mapScreen?.Visible ?? false);
	}

	private Label? _note;
	private double _noteUntil;

	/// <summary>A short message at the bottom of the screen for a few seconds.</summary>
	public void ShowNote(string text)
	{
		if (_note == null)
		{
			_note = new Label { AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -70, OffsetBottom = -40, HorizontalAlignment = HorizontalAlignment.Center };
			_note.AddThemeColorOverride("font_shadow_color", Colors.Black);
			_ui.AddChild(_note);
		}
		_note.Text = text;
		_note.Visible = true;
		_noteUntil = Time.GetTicksMsec() / 1000.0 + 6;
	}

	private void BuildEnvironment()
	{
		_atmosphere = new Sky.Atmosphere { Name = "Atmosphere" };
		// --hour=HH.H (local solar time) and --day=N (day of the year) for screenshots; T+/- keys change the hour
		if (Args.TryGetValue("hour", out var h)) _atmosphere.Hour = double.Parse(h, System.Globalization.CultureInfo.InvariantCulture);
		if (Args.TryGetValue("day", out var d)) _atmosphere.DayOfYear = double.Parse(d, System.Globalization.CultureInfo.InvariantCulture);
		if (Args.TryGetValue("jump", out var jmp)) _atmosphere.AdvanceClock(double.Parse(jmp, System.Globalization.CultureInfo.InvariantCulture));
		if (Args.TryGetValue("clouds", out var c)) _atmosphere.Coverage = float.Parse(c, System.Globalization.CultureInfo.InvariantCulture);
		AddChild(_atmosphere);
		// look experiments: --grade=N (0 none), --volfog=0, --haze=F, --sat=F
		var env = _atmosphere.Environment;
		var inv = System.Globalization.CultureInfo.InvariantCulture;
		if (Args.TryGetValue("grade", out var g)) env.AdjustmentColorCorrection = g == "0" ? null : Sky.ColorGrading.Lut(int.Parse(g), .6f);
		if (Args.TryGetValue("volfog", out var v)) env.VolumetricFogEnabled = v != "0";
		if (Args.TryGetValue("haze", out var hz)) { env.FogDensity = .00007f * float.Parse(hz, inv); env.FogAerialPerspective = .26f * float.Parse(hz, inv); }
		if (Args.TryGetValue("sat", out var st)) env.AdjustmentSaturation = float.Parse(st, inv);
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
