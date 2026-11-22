using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using BalticDrive.Items;

namespace BalticDrive.UI;

/// <summary>The crafting side of the inventory window (both the old list and the notebook are one).</summary>
public interface ICraftColumn
{
    void Fill(Inventory pack);
    void ShowProgress(Recipe r, float k, float t);
    void ScrollToEnd();
}

/// <summary>
/// Crafting as "zeszyt w kratkę" (D117, Tom's pick "A + B"): a survivor's squared school notebook with a red margin,
/// written in Caveat (OFL) in ink. Polish headings (Tom: "Polish headings only"), the recipes as the game names them.
/// Sticker tabs by category; what you have ticked in green ink, what you lack in red; a pencil circle round what you
/// can make; recipes you haven't learnt at the end, with the book they're in. Same crafting underneath: the Make
/// button starts the timed craft (D107), the bar shows it.
/// </summary>
public partial class NotebookColumn : PanelContainer, ICraftColumn
{
    private readonly InventoryScreen _screen;
    private readonly VBoxContainer _rows;
    private readonly HFlowContainer _tabs;
    private readonly Control _progress;
    private readonly Label _progressText;
    private readonly ScrollContainer _scroll;
    private float _progressK;
    private string _tab = "all";
    private Inventory? _pack;
    private readonly StyleBoxTexture? _page;
    private static StyleBoxTexture? _ring, _ringHover, _ringOff, _underline;
    private static StyleBoxTexture? Ring
    {
        get
        {
            if (_ring == null && Skin.Box("icons/notebook/pencil_circle.png") is { } r)
            {
                // the ring's corners are wide; the words sit closer to it than its nine-slice margins say
                r.ContentMarginLeft = 22; r.ContentMarginRight = 22; r.ContentMarginTop = 6; r.ContentMarginBottom = 6;
                _ring = r;
            }
            return _ring;
        }
    }
    /// <summary>A pencil mark inline in handwriting: a tick for what you have, a cross for what you lack.</summary>
    private static string Pencil_(string mark) => ResourceLoader.Exists($"res://Assets/UI/icons/notebook/pencil_{mark}.png")
        ? $"[img=18x18]res://Assets/UI/icons/notebook/pencil_{mark}.png[/img]" : mark == "tick" ? "✓" : "✗";

    // ink on paper: the paper's light, so these are fixed colours (not the dark UI's)
    public static readonly Color Paper = new(0.95f, 0.93f, 0.86f), Grid = new(0.62f, 0.7f, 0.78f, 0.45f), Margin = new(0.8f, 0.3f, 0.28f, 0.75f),
        Ink = new(0.14f, 0.19f, 0.32f), Pencil = new(0.38f, 0.38f, 0.38f), Green = new(0.18f, 0.45f, 0.24f), Red = new(0.66f, 0.2f, 0.16f),
        Amber = new(0.55f, 0.36f, 0.08f);
    private static FontFile? _hand;
    /// <summary>The handwriting (Caveat, OFL, Polish letters), or the default font if it's missing.</summary>
    public static Font Hand => _hand ??= ResourceLoader.Exists("res://Assets/Fonts/Caveat/Caveat-Variable.ttf") ? GD.Load<FontFile>("res://Assets/Fonts/Caveat/Caveat-Variable.ttf") : null!;

    private static readonly (string Id, string Pl, Color Sticker)[] Tabs =
    {
        ("all", "Wszystko", new Color(0.85f, 0.83f, 0.78f)), ("Cooking", "Gotowanie", new Color(0.96f, 0.85f, 0.35f)),
        ("Bigos", "Bigos", new Color(0.62f, 0.8f, 0.5f)), ("Medicine", "Apteczka", new Color(0.93f, 0.55f, 0.52f)),
        ("Fire", "Ogień", new Color(0.97f, 0.65f, 0.3f)), ("Materials", "Materiały", new Color(0.75f, 0.75f, 0.75f)),
        ("Trouble", "Kłopoty", new Color(0.4f, 0.4f, 0.42f)),
    };

    public NotebookColumn(InventoryScreen screen)
    {
        _screen = screen;
        CustomMinimumSize = new Vector2(560, 560);
        // the painted page (D118, Codex's set); else squared paper drawn here
        _page = Skin.Box("icons/notebook/page_left.png", 6, 0);
        if (_page != null) AddThemeStyleboxOverride("panel", _page);
        else AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Paper, ContentMarginLeft = 52, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12,
            CornerRadiusTopLeft = 3, CornerRadiusBottomLeft = 3, CornerRadiusTopRight = 6, CornerRadiusBottomRight = 6,
            BorderWidthLeft = 6, BorderColor = new Color(0.25f, 0.38f, 0.5f),   // a bit of the soft cover
        });
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        AddChild(v);
        var title = new Label { Text = "Przepisy" };
        Handwrite(title, 36, Ink);
        title.Draw += () =>
        {
            _underline ??= Skin.Box("icons/notebook/pencil_underline.png");
            if (_underline != null) title.DrawStyleBox(_underline, new Rect2(0, title.Size.Y - 10, 150, 12));
        };
        v.AddChild(title);
        _tabs = new HFlowContainer();
        _tabs.AddThemeConstantOverride("h_separation", 6);
        _tabs.AddThemeConstantOverride("v_separation", 4);
        v.AddChild(_tabs);
        // what's being made: a line of handwriting and a pencil bar
        var pv = new VBoxContainer { Visible = false };
        _progressText = new Label();
        Handwrite(_progressText, 22, Amber);
        pv.AddChild(_progressText);
        var gauge = new Control { CustomMinimumSize = new Vector2(0, 14) };
        gauge.Draw += () =>
        {
            var size = gauge.Size;
            gauge.DrawRect(new Rect2(0, 3, size.X, 8), Pencil, false, 1.5f);
            if (_progressK > 0.01f)
                for (float x = 2; x < (size.X - 2) * _progressK; x += 4)   // hatched in pencil
                    gauge.DrawLine(new Vector2(x, 10), new Vector2(x + 5, 4), new Color(Amber, 0.85f), 1.6f);
        };
        pv.AddChild(gauge);
        _progress = pv;
        v.AddChild(pv);
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(_scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(_rows);
    }

    /// <summary>Squared paper: a 16 px grid, the red margin line, the shadow of the spine on the left.</summary>
    public override void _Draw()
    {
        var size = Size;
        if (_page != null)
        {
            // the painted page has its own squares and margin: a coffee ring someone left, and that's all
            if (Skin.Tex("icons/notebook/coffee_ring.png") is { } ring) DrawTextureRect(ring, new Rect2(size.X - 132, 14, 110, 110), false, new Color(1, 1, 1, 0.4f));   // top right, by the title
            return;
        }
        for (float x = 6 + 16; x < size.X; x += 16) DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), Grid, 1);
        for (float y = 16; y < size.Y; y += 16) DrawLine(new Vector2(6, y), new Vector2(size.X, y), Grid, 1);
        DrawLine(new Vector2(44, 0), new Vector2(44, size.Y), Margin, 1.5f);
        for (int i = 0; i < 6; i++) DrawLine(new Vector2(6 + i, 0), new Vector2(6 + i, size.Y), new Color(0, 0, 0, 0.12f - i * 0.02f), 1);
    }

    public static void Handwrite(Control c, int size, Color col)
    {
        if (Hand != null) c.AddThemeFontOverride(c is RichTextLabel ? "normal_font" : "font", Hand);
        c.AddThemeFontSizeOverride(c is RichTextLabel ? "normal_font_size" : "font_size", size);
        c.AddThemeColorOverride(c is RichTextLabel ? "default_color" : "font_color", col);
    }

    public void ScrollToEnd() => _scroll.ScrollVertical = 100000;

    public void ShowProgress(Recipe r, float k, float t)
    {
        _progress.Visible = true;
        _progressK = k;
        _progressText.Text = $"{(r.Id.StartsWith("read:") ? r.Name : "Making: " + r.Name)}  {t:0} / {r.Seconds:0} s   (Esc stops)";
        foreach (var c in _progress.GetChildren()) if (c is Control g && g is not Label) g.QueueRedraw();
    }

    public void Fill(Inventory pack)
    {
        _pack = pack;
        _progress.Visible = _screen.Busy;
        foreach (var c in _tabs.GetChildren()) c.QueueFree();
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        // sticker tabs, for the categories there are recipes in
        var cats = Recipes.All.Select(r => r.Category).ToHashSet();
        foreach (var (id, pl, sticker) in Tabs)
        {
            if (id != "all" && !cats.Contains(id)) continue;
            var b = new Button { Text = pl, ToggleMode = true, ButtonPressed = id == _tab, CustomMinimumSize = new Vector2(0, 30) };
            string art = id switch { "Cooking" => "cooking", "Medicine" => "medicine", "Fire" => "fire", "Bigos" => "bigos", "Trouble" => "trouble", _ => "materials" };
            StyleBox sb = Skin.Box($"icons/notebook/tab_{art}.png", 8, 0, id == _tab ? Colors.White : new Color(1, 1, 1, 0.6f)) as StyleBox
                ?? new StyleBoxFlat { BgColor = id == _tab ? sticker : new Color(sticker, 0.55f), ContentMarginLeft = 10, ContentMarginRight = 10, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2 };
            if (id == "all" && sb is StyleBoxTexture st0) st0.ModulateColor = id == _tab ? new Color(1.15f, 1.12f, 1.05f) : new Color(1, 1, 1, 0.6f);
            foreach (var st in new[] { "normal", "hover", "pressed", "focus" }) b.AddThemeStyleboxOverride(st, sb);
            Handwrite(b, 20, id == "Trouble" ? new Color(0.95f, 0.95f, 0.9f) : Ink);
            b.AddThemeColorOverride("font_hover_color", id == "Trouble" ? Colors.White : Ink);
            b.AddThemeColorOverride("font_pressed_color", id == "Trouble" ? Colors.White : Ink);
            string tab = id;
            b.Pressed += () => { _tab = tab; Fill(_pack!); };
            _tabs.AddChild(b);
        }
        var list = Recipes.All.Where(r => _tab == "all" || r.Category == _tab).Select(r => (R: r, Miss: Recipes.Missing(r, pack)))
            .OrderBy(x => !Knowledge.Known(x.R)).ThenBy(x => x.Miss.Count > 0).ThenBy(x => x.R.Category).ToList();
        bool headed = false;
        foreach (var (r, miss) in list)
        {
            if (!Knowledge.Known(r))
            {
                if (!headed)
                {
                    headed = true;
                    var head = new Label { Text = "Jeszcze nie umiem" };
                    Handwrite(head, 28, Ink);
                    _rows.AddChild(head);
                }
                var line = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                Handwrite(line, 21, Pencil);
                bool have = pack.Count(r.Book!) > 0;
                line.Text = $"? ? ?  {r.Name}  [color=#{(have ? Green : Pencil).ToHtml(false)}]— learn it from {ItemDb.Say(r.Book!)}{(have ? " (it's in your pack: Read)" : "")}[/color]";
                _rows.AddChild(line);
                continue;
            }
            _rows.AddChild(Entry(r, miss, pack));
        }
    }

    /// <summary>One recipe, written out: its name (circled in pencil when you can make it), what it uses and needs
    /// (ticked green when you have it, red when not), what comes of it, its flavour line, and the Make button.</summary>
    private Control Entry(Recipe r, List<string> miss, Inventory pack)
    {
        bool can = miss.Count == 0;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", -4);
        var name = new Label { Text = r.Name };
        Handwrite(name, 27, can ? Ink : new Color(Ink, 0.6f));
        if (can)
            name.Draw += () =>
            {
                // a pencil ring round it
                var s = name.Size; float w = MathF.Min(s.X, name.GetThemeFont("font").GetStringSize(r.Name, HorizontalAlignment.Left, -1, 27).X + 14);
                if (Ring != null) { name.DrawStyleBox(Ring, new Rect2(-8, 0, w + 10, s.Y + 2)); return; }
                var pts = new List<Vector2>();
                for (int i = 0; i <= 40; i++) { float a = i / 40f * MathF.Tau * 1.04f; pts.Add(new Vector2(w / 2 - 4 + MathF.Cos(a) * (w / 2 + 2), s.Y / 2 + 2 + MathF.Sin(a) * (s.Y / 2 - 2))); }
                name.DrawPolyline(pts.ToArray(), new Color(Pencil, 0.7f), 1.4f);
            };
        text.AddChild(name);
        string Mark(string label, bool ok) => ok ? $"[color=#{Green.ToHtml(false)}]{label}[/color] {Pencil_("tick")}" : $"{Pencil_("cross")} [color=#{Red.ToHtml(false)}]{label}[/color]";
        var parts = r.Inputs.Select(p => Mark(p.Count > 1 ? $"{p.Count} × {ItemDb.Say(p.Item)}" : ItemDb.Say(p.Item), pack.CountMatching(p.Item) >= p.Count)).ToList();
        var tools = r.Tools.Select(alts => Mark(string.Join(" or ", alts.Select(ItemDb.Say)), alts.Any(a => pack.CountMatching(a) > 0))).ToList();
        if (r.Heat) { var heat = Recipes.HeatHere?.Invoke(pack); tools.Add(Mark(heat != null ? $"heat ({heat})" : Recipes.HeatName, heat != null)); }
        if (r.Evolve != null && miss.FirstOrDefault(m => m.Contains(ItemDb.Get(r.Evolve)?.Name ?? r.Evolve)) is { } potMiss) tools.Add(Mark(potMiss, false));
        string makes = r.Places != null ? $"a {r.Places} on the ground" : r.Evolve != null ? $"a better {r.Evolve}" : Inventory.Say(r.Outputs.Select(o => new ItemStack(o.Item, o.Count)));
        var uses = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        Handwrite(uses, 20, Ink);
        uses.Text = (parts.Count > 0 ? string.Join(", ", parts) : "") + (tools.Count > 0 ? (parts.Count > 0 ? "  ·  " : "") + string.Join(", ", tools) : "")
            + $"  [color=#{Amber.ToHtml(false)}]→ {makes}[/color]";
        text.AddChild(uses);
        if (r.About.Length > 0)
        {
            var about = new Label { Text = r.About, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            Handwrite(about, 18, Pencil);
            text.AddChild(about);
        }
        row.AddChild(text);
        var make = new Button { Text = r.Seconds > 0 ? $"Make · {Recipes.SecondsFor(r):0} s" : "Make", Disabled = !can || _screen.Busy, CustomMinimumSize = new Vector2(124, 40), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var ring = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0), BorderColor = Ink, BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, CornerRadiusTopLeft = 18, CornerRadiusTopRight = 14, CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 18, ContentMarginLeft = 8, ContentMarginRight = 8 };
        var hover = (StyleBoxFlat)ring.Duplicate(); hover.BgColor = new Color(Amber, 0.15f);
        var off = (StyleBoxFlat)ring.Duplicate(); off.BorderColor = new Color(Pencil, 0.35f);
        if (Ring != null)
        {
            _ringHover ??= (StyleBoxTexture)Ring.Duplicate(); _ringHover.ModulateColor = new Color(0.75f, 0.45f, 0.1f);
            _ringOff ??= (StyleBoxTexture)Ring.Duplicate(); _ringOff.ModulateColor = new Color(1, 1, 1, 0.3f);
            make.AddThemeStyleboxOverride("normal", Ring); make.AddThemeStyleboxOverride("hover", _ringHover); make.AddThemeStyleboxOverride("pressed", _ringHover);
            make.AddThemeStyleboxOverride("disabled", _ringOff); make.AddThemeStyleboxOverride("focus", Ring);
        }
        else
        {
            make.AddThemeStyleboxOverride("normal", ring); make.AddThemeStyleboxOverride("hover", hover); make.AddThemeStyleboxOverride("pressed", hover);
            make.AddThemeStyleboxOverride("disabled", off); make.AddThemeStyleboxOverride("focus", ring);
        }
        Handwrite(make, 22, Ink);
        make.AddThemeColorOverride("font_hover_color", Ink);
        make.AddThemeColorOverride("font_disabled_color", new Color(Pencil, 0.5f));
        var rec = r;
        make.Pressed += () => _screen.Craft(rec);
        row.AddChild(make);
        return row;
    }
}
