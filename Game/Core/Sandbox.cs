using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace BalticDrive.Core;

/// <summary>
/// Sandbox settings (D99), Project Zomboid style: how the generated world is stocked and how hard it is. The settings,
/// their ranges and the presets are data (<c>GameData/sandbox.json</c>); the player's choice is kept in
/// <c>user://sandbox.json</c> (<c>{"preset": "...", "overrides": {"key": value}}</c>); <c>--sandbox=preset</c> and
/// <c>--sb.key=value</c> on the command line win (tests). Every spawner asks <see cref="Get"/> for its number.
/// </summary>
public static class Sandbox
{
    public const string DataPath = "res://GameData/sandbox.json", UserPath = "user://sandbox.json";

    public sealed record Setting(string Key, float Default, float Min, float Max, string About);

    private static Dictionary<string, Setting>? _settings;
    private static Dictionary<string, float> _values = new();
    private static readonly Dictionary<string, (string About, Dictionary<string, float> Values)> _presets = new();
    public static string Preset { get; private set; } = "baltic_drive";
    public static IReadOnlyDictionary<string, Setting> Settings { get { Load(); return _settings!; } }
    public static IReadOnlyCollection<string> Presets { get { Load(); return _presets.Keys; } }

    /// <summary>A setting's value (its default if unknown to the preset and the player).</summary>
    public static float Get(string key)
    {
        Load();
        if (_values.TryGetValue(key, out var v)) return v;
        if (_settings!.TryGetValue(key, out var s)) return s.Default;
        Log.Error("Sandbox", $"unknown setting '{key}'");
        return 1f;
    }

    private static void Load()
    {
        if (_settings != null) return;
        _settings = new();
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(DataPath));
            var root = doc.RootElement;
            foreach (var p in root.GetProperty("settings").EnumerateObject())
            {
                var e = p.Value;
                _settings[p.Name] = new Setting(p.Name, (float)e.GetProperty("default").GetDouble(), (float)e.GetProperty("min").GetDouble(),
                    (float)e.GetProperty("max").GetDouble(), e.TryGetProperty("about", out var a) ? a.GetString() ?? "" : "");
            }
            foreach (var p in root.GetProperty("presets").EnumerateObject())
            {
                var vals = new Dictionary<string, float>();
                if (p.Value.TryGetProperty("values", out var v))
                    foreach (var kv in v.EnumerateObject()) vals[kv.Name] = (float)kv.Value.GetDouble();
                _presets[p.Name] = (p.Value.TryGetProperty("about", out var a) ? a.GetString() ?? "" : "", vals);
            }
            Preset = root.TryGetProperty("default_preset", out var dp) ? dp.GetString() ?? Preset : Preset;
        }
        catch (Exception ex) { Log.Error("Sandbox", ex.Message); }

        // the player's choice, then the command line
        var overrides = new Dictionary<string, float>();
        try
        {
            string path = ProjectSettings.GlobalizePath(UserPath);
            if (File.Exists(path))
            {
                using var u = JsonDocument.Parse(File.ReadAllText(path));
                if (u.RootElement.TryGetProperty("preset", out var pr) && pr.GetString() is { } name && _presets.ContainsKey(name)) Preset = name;
                if (u.RootElement.TryGetProperty("overrides", out var o))
                    foreach (var kv in o.EnumerateObject()) overrides[kv.Name] = (float)kv.Value.GetDouble();
            }
        }
        catch (Exception ex) { Log.Error("Sandbox", $"{UserPath}: {ex.Message}"); }
        if (Main.Args.TryGetValue("sandbox", out var cli) && _presets.ContainsKey(cli)) Preset = cli;
        foreach (var (k, v) in Main.Args)
            if (k.StartsWith("sb.") && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) overrides[k[3..]] = f;

        _values = new();
        if (_presets.TryGetValue(Preset, out var preset))
            foreach (var (k, v) in preset.Values) _values[k] = v;
        foreach (var (k, v) in overrides) _values[k] = v;
        foreach (var k in new List<string>(_values.Keys))
            if (_settings.TryGetValue(k, out var s)) _values[k] = Math.Clamp(_values[k], s.Min, s.Max);
            else { Log.Error("Sandbox", $"unknown setting '{k}' ignored"); _values.Remove(k); }
        Log.Info("Sandbox", $"preset '{Preset}'" + (overrides.Count > 0 ? $", changed: {string.Join(", ", overrides.Keys)}" : ""));
    }

    /// <summary>Saves the player's choice (a future settings menu): a preset and the settings changed from it.</summary>
    public static void Save(string preset, IReadOnlyDictionary<string, float> overrides)
    {
        try
        {
            File.WriteAllText(ProjectSettings.GlobalizePath(UserPath), JsonSerializer.Serialize(new { preset, overrides }, new JsonSerializerOptions { WriteIndented = true }));
            _settings = null;   // read again
        }
        catch (Exception ex) { Log.Error("Sandbox", ex.Message); }
    }
}
