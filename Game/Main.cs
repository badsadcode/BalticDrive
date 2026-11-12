using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BalticDrive.Core;
using BalticDrive.Map;
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
	private FlyCamera _camera = null!;
	private Label _hud = null!;
	private Tests? _tests;
	private Car? _car;
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

		BuildEnvironment();
		_terrain = new TerrainStreamer { Name = "Terrain" };
		_content = new MapContentStreamer { Name = "MapContent" };
		AddChild(_terrain);
		AddChild(_content);
		_camera = new FlyCamera { Name = "Camera", Current = true, Far = 30000f, Near = 0.1f };
		AddChild(_camera);
		_hud = new Label { Position = new Vector2(12, 8) };
		_hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
		var ui = new CanvasLayer();
		ui.AddChild(_hud);
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

	/// <summary>Puts the car on the nearest road to a world point and hands it the controls.</summary>
	public void SpawnCar(double x, double z)
	{
		if (_car == null)
		{
			_car = new Car { Name = "Car", Map = _world, HasGround = _terrain.HasGround };
			AddChild(_car);
		}
		_car.Recover(x, z);
		_car.Driving = true;
		_car.SetView(false);
		Flying = false;
	}

	public override void _UnhandledInput(InputEvent e)
	{
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
		_camera.GroundHeight = _world.Height(fx, fz);
		RenderingServer.GlobalShaderParameterSet("ocean_time", (float)(Time.GetTicksMsec() / 1000.0));
		var (lat, lon) = Core.Projection.ToGeo(fx, fz);
		var place = _world.NearestPlace(fx, fz, 15000);
		string speed = !Flying && _car != null ? $"{MathF.Abs(_car.Speed) * 3.6f:F0} km/h{(_car.OnRoad ? "" : " (off road)")}   " : "";
		_hud.Text = $"{speed}{Engine.GetFramesPerSecond()} fps   {lat:F5}° N  {lon:F5}° E   {(place != null ? "near " + place.Name : "")}\n" +
					$"x {fx:F0}  z {fz:F0}  alt {_camera.GlobalPosition.Y:F0} m (ground {_camera.GroundHeight:F1})\n" +
					$"terrain {_terrain.Resident} tiles, {_terrain.Missing} missing, {_terrain.Pending} building, commit {_terrain.FrameCommitMs:F1} ms (max {_terrain.MaxCommitMs:F1})\n" +
					$"map {_content.Cells} cells, {_content.Pending} building\n" +
					(Flying ? "F: drive here   right mouse: look   WASD/QE move, Shift/Ctrl faster" : "F: free camera   W/S drive, A/D steer, Space handbrake, C camera, H lights, R back to road");
		_tests?.Process(delta);
	}

	private void BuildEnvironment()
	{
		var sky = new ProceduralSkyMaterial
		{
			SkyTopColor = new Color(0.32f, 0.47f, 0.66f), SkyHorizonColor = new Color(0.68f, 0.74f, 0.80f),
			GroundHorizonColor = new Color(0.55f, 0.58f, 0.60f), GroundBottomColor = new Color(0.2f, 0.2f, 0.2f),
		};
		var env = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Sky,
			Sky = new Sky { SkyMaterial = sky },
			AmbientLightSource = Godot.Environment.AmbientSource.Sky,
			TonemapMode = Godot.Environment.ToneMapper.Filmic,
			TonemapExposure = 1.1f,
			FogEnabled = true,
			FogLightColor = new Color(0.66f, 0.72f, 0.80f),
			FogDensity = 0.00006f,
			FogAerialPerspective = 0.6f,
			SsaoEnabled = true,
		};
		AddChild(new WorldEnvironment { Environment = env });
		var sun = new DirectionalLight3D
		{
			Name = "Sun", ShadowEnabled = true, LightEnergy = 1.3f,
			DirectionalShadowMaxDistance = 600f,
			RotationDegrees = new Vector3(-38f, -35f, 0f),
		};
		AddChild(sun);
	}
}
