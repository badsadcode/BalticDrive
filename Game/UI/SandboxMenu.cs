using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.UI;

/// <summary>
/// The sandbox settings menu (D103, F10): Project Zomboid style. A preset (with what it's for), then every setting in
/// its group with a slider, its value in words and a line saying what it does. Save keeps the preset and only the
/// settings changed from it (<c>user://sandbox.json</c>, <see cref="Sandbox.Save"/>); what's generated from then on
/// follows them, places already met keep theirs. Dressed in Tom's UI set (D102).
/// </summary>
public partial class SandboxMenu : Control
{
    private static readonly (string Title, string[] Prefixes)[] Groups =
    {
        ("Finding things", new[] { "loot.", "caches." }),
        ("Bandits", new[] { "bandits." }),
        ("Cars", new[] { "parked.", "wrecks." }),
    };

    private readonly OptionButton _preset;
    private readonly Label _presetAbout, _status;
    private readonly Dictionary<string, (HSlider Slider, Label Value)> _rows = new();
    private readonly List<string> _presetNames = new();
    private Action? _closed;
    public bool IsOpen => Visible;

    public SandboxMenu()
    {
        Name = "SandboxMenu";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var centre = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(centre);
        var window = new PanelContainer { CustomMinimumSize = new Vector2(780, 760) };
        if (Skin.Box("panels/window_frame.png", 10, 8) is { } frame) window.AddThemeStyleboxOverride("panel", frame);
        centre.AddChild(window);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        window.AddChild(v);

        // the title on the set's title bar
        var titleBar = new PanelContainer();
        if (Skin.Box("panels/window_title_bar.png", 12, 2) is { } bar) titleBar.AddThemeStyleboxOverride("panel", bar);
        var title = new Label { Text = "Sandbox settings" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", Skin.Text);
        titleBar.AddChild(title);
        v.AddChild(titleBar);

        // the preset
        var pr = new HBoxContainer();
        pr.AddThemeConstantOverride("separation", 12);
        pr.AddChild(Text("Preset", 17, Skin.Text, 120));
        _preset = new OptionButton { CustomMinimumSize = new Vector2(240, 34) };
        if (Skin.Box("controls/dropdown_normal.png", 8, 2) is { } dn) { _preset.AddThemeStyleboxOverride("normal", dn); _preset.AddThemeStyleboxOverride("focus", dn); }
        if (Skin.Box("controls/dropdown_hover.png", 8, 2) is { } dh) { _preset.AddThemeStyleboxOverride("hover", dh); _preset.AddThemeStyleboxOverride("pressed", dh); }
        _preset.AddThemeColorOverride("font_color", Skin.Text);
        _preset.ItemSelected += i => ApplyPreset(_presetNames[(int)i]);
        pr.AddChild(_preset);
        _presetAbout = Text("", 14, new Color(Skin.Text, 0.75f));
        _presetAbout.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _presetAbout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        pr.AddChild(_presetAbout);
        v.AddChild(pr);

        // the settings, in groups
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);
        var settings = Sandbox.Settings.Values.ToList();
        foreach (var (groupTitle, prefixes) in Groups)
        {
            var inGroup = settings.Where(s => prefixes.Any(p => s.Key.StartsWith(p))).ToList();
            if (inGroup.Count == 0) continue;
            var head = Text(groupTitle, 18, Skin.Accent);
            list.AddChild(head);
            if (Skin.Tex("panels/divider.png") is { } div) list.AddChild(new TextureRect { Texture = div, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, CustomMinimumSize = new Vector2(0, 4) });
            foreach (var s in inGroup) list.AddChild(Row(s));
        }

        // the buttons
        _status = Text("New settings apply to what the world makes from now on; places you've already seen keep theirs.", 13, new Color(Skin.Text, 0.6f));
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_status);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        buttons.AddChild(MakeButton("Reset to preset", () => ApplyPreset(_presetNames[_preset.Selected])));
        buttons.AddChild(MakeButton("Cancel", Close));
        buttons.AddChild(MakeButton("Save", Save));
        v.AddChild(buttons);
    }

    private static Label Text(string text, int size, Color col, float minWidth = 0)
    {
        var l = new Label { Text = text, CustomMinimumSize = new Vector2(minWidth, 0), VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", col);
        return l;
    }

    private static Button MakeButton(string text, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(150, 40) };
        Skin.Button(b);
        b.Pressed += pressed;
        return b;
    }

    private Control Row(Sandbox.Setting s)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 12);
        h.AddChild(Text(s.Name, 16, Skin.Text, 210));
        // the step as an exact decimal: 0.05f is 0.0500000007, and 1.0 then snapped down to 0.95
        var slider = new HSlider { MinValue = s.Min, MaxValue = s.Max, Step = Math.Round((double)s.Step, 6), SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) };
        // Tom's slider: the groove, the filled part, the knob
        if (Skin.Box("controls/slider_track.png") is { } track) slider.AddThemeStyleboxOverride("slider", track);
        if (Skin.Box("controls/slider_fill.png") is { } fill) { slider.AddThemeStyleboxOverride("grabber_area", fill); slider.AddThemeStyleboxOverride("grabber_area_highlight", fill); }
        if (Skin.Tex("controls/slider_knob_normal.png") is { } knob) slider.AddThemeIconOverride("grabber", knob);
        if (Skin.Tex("controls/slider_knob_hover.png") is { } knobH) slider.AddThemeIconOverride("grabber_highlight", knobH);
        h.AddChild(slider);
        var value = Text("", 16, Skin.Accent, 72);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        h.AddChild(value);
        slider.ValueChanged += x => value.Text = Say(s, (float)x);
        box.AddChild(h);
        var about = Text(s.About, 13, new Color(Skin.Text, 0.55f));
        about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(about);
        _rows[s.Key] = (slider, value);
        return box;
    }

    /// <summary>A value in words: "off", "×1.5", "20 %", "3".</summary>
    private static string Say(Sandbox.Setting s, float x)
    {
        if (x <= 0.0001f && s.Min <= 0) return "off";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s.Step >= 1) return x.ToString("0", inv);
        if (s.Key == "wrecks.runners") return (x * 100).ToString("0", inv) + " %";
        return "×" + x.ToString("0.##", inv);
    }

    public void Open(Action closed)
    {
        _closed = closed;
        _preset.Clear();
        _presetNames.Clear();
        foreach (var p in Sandbox.Presets) { _presetNames.Add(p); _preset.AddItem(Pretty(p)); }
        int at = Math.Max(0, _presetNames.IndexOf(Sandbox.Preset));
        _preset.Select(at);
        _presetAbout.Text = Sandbox.PresetAbout(_presetNames[at]);
        foreach (var (key, row) in _rows) row.Slider.Value = Sandbox.Get(key);
        foreach (var (key, row) in _rows) row.Value.Text = Say(Sandbox.Settings[key], (float)row.Slider.Value);
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private static string Pretty(string preset) => string.Join(' ', preset.Split('_').Select(w => char.ToUpper(w[0]) + w[1..]));

    private void ApplyPreset(string preset)
    {
        _presetAbout.Text = Sandbox.PresetAbout(preset);
        foreach (var (key, row) in _rows) row.Slider.Value = Sandbox.PresetValue(preset, key);
    }

    private void Save()
    {
        string preset = _presetNames[_preset.Selected];
        var overrides = new Dictionary<string, float>();
        foreach (var (key, row) in _rows)
            if (MathF.Abs((float)row.Slider.Value - Sandbox.PresetValue(preset, key)) > 0.0001f) overrides[key] = (float)row.Slider.Value;
        Sandbox.Save(preset, overrides);
        Log.Info("Sandbox", $"saved: preset '{preset}', {overrides.Count} changed");
        Close();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
        _closed?.Invoke();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode is Key.Escape or Key.F10) { Close(); GetViewport().SetInputAsHandled(); }
    }
}
