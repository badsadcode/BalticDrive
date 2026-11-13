using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
	private CanvasLayer _ui = null!;
	/// <summary>True while the free camera is in charge (F toggles; views tests and --fly start in it).</summary>
	public bool Flying { get; private set; }

	public override void _Ready()
	{
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
		_mapScreen = new MapScreen { Name = "MapScreen" };
		_ui.AddChild(_mapScreen);
		_mapScreen.Setup(_world, _nav, () =>
		{
			var (x, z) = Focus;
			var f = !Flying && _car != null ? -_car.GlobalTransform.Basis.Z : -_camera.GlobalTransform.Basis.Z;
			var d = new Vector2(f.X, f.Z);
			return (x, z, d.LengthSquared() > 1e-4f ? d.Normalized() : new Vector2(0, -1));
		}, (x, z) =>
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
	public TerrainStreamer Terrain => _terrain;
	public MapContentStreamer Content => _content;
	public FlyCamera Camera => _camera;
	public Car? Car => _car;
	public Navigation? Nav => _nav;
	public MapScreen? MapView => _mapScreen;

	/// <summary>Puts the car on the nearest road to a world point and hands it the controls.</summary>
	public void SpawnCar(double x, double z)
	{
		if (_car == null)
		{
			_car = new Car { Name = "Car", Map = _world, HasGround = _terrain.HasGround, Model = Car.ChooseModel(Args.GetValueOrDefault("car")) };
			AddChild(_car);
		}
		_car.Recover(x, z);
		_car.Driving = true;
		_car.SetView(false);
		Flying = false;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Keycode: Key.Bracketleft or Key.Bracketright } k)
		{
			_atmosphere.AdvanceClock(k.Keycode == Key.Bracketright ? 0.5 : -0.5);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } && _mapScreen != null && !_mapScreen.Visible)
		{
			_mapScreen.Toggle();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && _world != null)
		{
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

	public (double X, double Z) Focus
	{
		get
		{
			var at = !Flying && _car != null ? _car.GlobalPosition : _camera.GlobalPosition;
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
		var place = _world.NearestPlace(fx, fz, 15000);
		string speed = !Flying && _car != null ? $"{MathF.Abs(_car.Speed) * 3.6f:F0} km/h{(_car.OnRoad ? "" : " (off road)")}   " : "";
		_hud.Text = $"{speed}{Engine.GetFramesPerSecond()} fps   {lat:F5}° N  {lon:F5}° E   {(place != null ? "near " + place.Name : "")}\n" +
					$"x {fx:F0}  z {fz:F0}  alt {_camera.GlobalPosition.Y:F0} m (ground {_camera.GroundHeight:F1})\n" +
					$"terrain {_terrain.Resident} tiles, {_terrain.Missing} missing, {_terrain.Pending} building, commit {_terrain.FrameCommitMs:F1} ms (max {_terrain.MaxCommitMs:F1})\n" +
					$"map {_content.Cells} cells, {_content.Pending} building, trees {_trees.Instances3D} near + {_trees.ImpostorInstances} far ({_trees.Missing} missing)\n" +
					($"{(int)_atmosphere.Hour:00}:{(int)(_atmosphere.Hour % 1 * 60):00} ([ ] time)   " + (Flying ? "F: drive here   right mouse: look   WASD/QE move, Shift/Ctrl faster" : "F: free camera   W/S drive, A/D steer, Space handbrake, C camera, H lights, R back to road"));
		_nav?.Update(new Vector2((float)fx, (float)fz), Time.GetTicksMsec() / 1000.0);
		_tests?.Process(delta);
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
