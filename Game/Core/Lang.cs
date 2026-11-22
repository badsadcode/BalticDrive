using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace BalticDrive.Core;

/// <summary>
/// The interface's language (D119, Tom: "one game, but two versions of the interface"; his friends play in English).
/// English by default, Polish as the option (F10). Words live in <c>GameData/lang/en.json</c> and <c>pl.json</c>
/// (key → text, {0} for what goes in); a key missing in Polish falls back to English, and one missing in both shows as
/// itself. Data files carry their own translations next to the English (<c>name_pl</c> beside <c>name</c>), read
/// through <see cref="Pick"/>. Polish messages are worded so they never need a noun declined ("Zjedzone: kiełbasa").
/// The choice is kept in <c>user://settings.json</c>; <c>--lang=pl</c> overrides it for one run (self-tests).
/// </summary>
public static class Lang
{
    public const string SettingsPath = "user://settings.json";
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("pl", "Polski") };

    private static string? _code;
    private static Dictionary<string, string>? _en, _cur;

    /// <summary>The language changed: everything that caches names reloads.</summary>
    public static event Action? Changed;

    public static string Code { get { if (_code == null) Init(); return _code!; } }
    public static bool Pl => Code == "pl";

    private static void Init()
    {
        _code = "en";
        try
        {
            string path = ProjectSettings.GlobalizePath(SettingsPath);
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } s && s.TryGetValue("lang", out var c)) _code = c;
        }
        catch (Exception e) { Log.Error("Lang", e.Message); }
        if (Main.Args.TryGetValue("lang", out var arg)) _code = arg;
        if (Array.FindIndex(Languages, l => l.Code == _code) < 0) _code = "en";
        _en = Read("en");
        _cur = _code == "en" ? _en : Read(_code);
        Log.Info("Lang", $"{_code}: {_cur.Count} words");
    }

    private static Dictionary<string, string> Read(string code)
    {
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString($"res://GameData/lang/{code}.json"));
            var d = new Dictionary<string, string>();
            foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString()!;
            return d;
        }
        catch (Exception e) { Log.Error("Lang", $"{code}.json: {e.Message}"); return new(); }
    }

    /// <summary>The words for a key, in the current language (English if Polish lacks them).</summary>
    public static string T(string key)
    {
        if (_code == null) Init();
        return _cur!.TryGetValue(key, out var s) ? s : _en!.TryGetValue(key, out var e) ? e : key;
    }

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>A data field in the current language: <c>name_pl</c> when it's there and we're in Polish, else <c>name</c>.</summary>
    public static string? Pick(JsonElement e, string field)
    {
        if (Pl && e.TryGetProperty(field + "_pl", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
        return e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Polish plural: 1 porcja, 2-4 porcje, 5+ porcji (and 12-14 porcji, 22 porcje).</summary>
    public static string Plural(int n, string one, string few, string many) =>
        n == 1 ? one : n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many;

    /// <summary>Changes the language and keeps the choice (the F10 window).</summary>
    public static void Set(string code)
    {
        if (code == Code) return;
        _code = code;
        _cur = code == "en" ? _en : Read(code);
        try
        {
            string path = ProjectSettings.GlobalizePath(SettingsPath);
            var s = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new();
            s["lang"] = code;
            File.WriteAllText(path, JsonSerializer.Serialize(s));
        }
        catch (Exception e) { Log.Error("Lang", e.Message); }
        Log.Info("Lang", $"now {code}");
        Changed?.Invoke();
    }
}
