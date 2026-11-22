using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.Vehicle;

/// <summary>
/// How a car drives (D98), from <c>GameData/cars.json</c>: weight, engine pull, top speed, which wheels drive, tyre
/// grip front and rear, steering lock, brakes, springs and dampers, the centre of mass, the anti-roll helper's
/// strength (D97), where the engine sits (D96), and the petrol tank (litres) and what it burns (litres per 100 km
/// at a steady cruise, D123), and what the boot and back seat hold together (kg, D125). A car takes the first entry whose <c>models</c> match its model's
/// file name, else the default. <c>--grip=K</c> still scales the grip for tuning.
/// </summary>
public sealed record CarSpec(string Id, string Name, float Mass, float Power, float TopKmh, string Drive,
    float GripFront, float GripRear, float Steer, float Brakes, float Suspension, float DampCompression, float DampRebound,
    float ComY, float AntiRoll, string Engine, float Tank = 50, float Consumption = 8, float Boot = 60)
{
    public const string DataPath = "res://GameData/cars.json";
    public float TopSpeed => TopKmh / 3.6f;
    public bool FrontDriven => Drive is "front" or "all";
    public bool RearDriven => Drive is "rear" or "all";

    /// <summary>The fallback when the file can't be read: the sedan as it drove before D98.</summary>
    public static readonly CarSpec Fallback = new("sedan", "Sedan", 1300, 8500, 180, "front", 1.75f, 1.6f, 0.6f, 45, 32, 2.8f, 3.8f, 0, 1, "front");

    private static List<(string[] Models, CarSpec Spec)>? _all;
    private static CarSpec? _default;

    public static CarSpec For(string modelPath)
    {
        Load();
        string file = modelPath.GetFile();
        foreach (var (models, spec) in _all!)
            foreach (var m in models)
                if (file.Contains(m, StringComparison.OrdinalIgnoreCase)) return Scaled(spec);
        return Scaled(_default ?? Fallback);
    }

    public static CarSpec ById(string id)
    {
        Load();
        foreach (var (_, spec) in _all!) if (spec.Id == id) return Scaled(spec);
        return Scaled(_default ?? Fallback);
    }

    private static CarSpec Scaled(CarSpec s)
    {
        float k = Main.Args.TryGetValue("grip", out var g) && float.TryParse(g, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 1f;
        float roll = Main.Args.GetValueOrDefault("antiroll", "1") == "0" ? 0f : s.AntiRoll;
        return s with { GripFront = s.GripFront * k, GripRear = s.GripRear * k, AntiRoll = roll };
    }

    private static void Load()
    {
        if (_all != null) return;
        _all = new();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(DataPath));
            string def = doc.RootElement.TryGetProperty("default", out var d) ? d.GetString() ?? "" : "";
            foreach (var c in doc.RootElement.GetProperty("cars").EnumerateArray())
            {
                float F(string k, float fallback) => c.TryGetProperty(k, out var v) ? (float)v.GetDouble() : fallback;
                (float, float) Pair(string k, float a, float b) => c.TryGetProperty(k, out var v) ? ((float)v[0].GetDouble(), (float)v[1].GetDouble()) : (a, b);
                var (gf, gr) = Pair("grip", Fallback.GripFront, Fallback.GripRear);
                var (dc, dr) = Pair("damping", Fallback.DampCompression, Fallback.DampRebound);
                var spec = new CarSpec(c.GetProperty("id").GetString()!, c.TryGetProperty("name", out var n) ? n.GetString()! : "",
                    F("mass", Fallback.Mass), F("power", Fallback.Power), F("top_kmh", Fallback.TopKmh),
                    c.TryGetProperty("drive", out var dv) ? dv.GetString()! : "front", gf, gr, F("steer", Fallback.Steer), F("brakes", Fallback.Brakes),
                    F("suspension", Fallback.Suspension), dc, dr, F("com_y", 0), F("antiroll", 1),
                    c.TryGetProperty("engine", out var e) ? e.GetString()! : "front", F("tank", 50), F("consumption", 8), F("boot", 60));
                var models = new List<string>();
                if (c.TryGetProperty("models", out var ms)) foreach (var m in ms.EnumerateArray()) models.Add(m.GetString()!);
                _all.Add((models.ToArray(), spec));
                if (spec.Id == def) _default = spec;
            }
            Log.Info("Cars", $"{_all.Count} car settings: {string.Join(", ", _all.ConvertAll(x => x.Spec.Id))}");
        }
        catch (Exception ex) { Log.Error("Cars", ex.Message); }
    }
}
