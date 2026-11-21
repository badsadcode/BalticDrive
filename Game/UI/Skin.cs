using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.UI;

/// <summary>
/// The game's interface art (D102): Tom's UI set (<c>Assets/UI</c>, made with Codex from docs/UI_ASSETS_BRIEF.md). The
/// pictures are drawn at 2× size; this shrinks each to screen size once and builds nine-slice styles from the margins
/// in <c>manifest.json</c>. Everything is optional: a missing file gives null, and callers keep their old look.
/// </summary>
public static class Skin
{
    public const string Root = "res://Assets/UI/";
    private const float Scale = 0.5f;

    private static Dictionary<string, int[]?>? _margins;
    private static readonly Dictionary<string, Texture2D?> _tex = new();

    private static void Load()
    {
        if (_margins != null) return;
        _margins = new();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(Root + "manifest.json"));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string file = e.GetProperty("file").GetString()!;
                int[]? m = null;
                if (e.TryGetProperty("nine_slice", out var ns) && ns.ValueKind == JsonValueKind.Array)
                {
                    m = new int[4];
                    for (int i = 0; i < 4; i++) m[i] = ns[i].GetInt32();
                }
                _margins[file] = m;
            }
        }
        catch (Exception ex) { Log.Error("Skin", ex.Message); }
    }

    /// <summary>A picture at screen size (e.g. "bars/bar_frame.png"), or null.</summary>
    public static Texture2D? Tex(string file)
    {
        if (_tex.TryGetValue(file, out var t)) return t;
        t = null;
        string path = Root + file;
        if (ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } src)
        {
            var img = src.GetImage();
            if (img != null)
            {
                if (img.IsCompressed()) img.Decompress();
                img.Resize(Math.Max(1, (int)(img.GetWidth() * Scale)), Math.Max(1, (int)(img.GetHeight() * Scale)), Image.Interpolation.Lanczos);
                t = ImageTexture.CreateFromImage(img);
            }
        }
        _tex[file] = t;
        return t;
    }

    /// <summary>A nine-slice style from a picture, its margins from the manifest; extra padding for the content.</summary>
    public static StyleBoxTexture? Box(string file, float padX = 0, float padY = 0, Color? tint = null)
    {
        Load();
        if (Tex(file) is not { } t) return null;
        var m = _margins!.GetValueOrDefault(file) ?? new[] { 8, 8, 8, 8 };
        var sb = new StyleBoxTexture { Texture = t, ModulateColor = tint ?? Colors.White };
        sb.TextureMarginLeft = m[0] * Scale; sb.TextureMarginTop = m[1] * Scale; sb.TextureMarginRight = m[2] * Scale; sb.TextureMarginBottom = m[3] * Scale;
        sb.ContentMarginLeft = m[0] * Scale + padX; sb.ContentMarginRight = m[2] * Scale + padX;
        sb.ContentMarginTop = m[1] * Scale + padY; sb.ContentMarginBottom = m[3] * Scale + padY;
        return sb;
    }

    /// <summary>Dresses a button in the set's four states: "button" (wide), "button_small" or "button_icon".</summary>
    public static void Button(Button b, string kind = "button")
    {
        foreach (var (state, file) in new[] { ("normal", "normal"), ("hover", "hover"), ("pressed", "pressed"), ("disabled", "disabled"), ("focus", "hover") })
            if (Box($"buttons/{kind}_{file}.png", 8, 2) is { } sb) b.AddThemeStyleboxOverride(state, sb);
        b.AddThemeColorOverride("font_color", Text);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_disabled_color", new Color(Text, 0.4f));
    }

    /// <summary>The set's warm off-white for text, and its amber accent.</summary>
    public static readonly Color Text = new(0.92f, 0.89f, 0.82f), Accent = new(0.85f, 0.62f, 0.25f);
}
