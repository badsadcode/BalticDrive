using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using BalticDrive.Items;

namespace BalticDrive.UI;

/// <summary>One side of the inventory window (both the old list and the rucksack are one).</summary>
public interface IItemColumn
{
    void Fill(Inventory inv, string title, string weight, Func<ItemDef, bool>? usable);
    HBoxContainer Footer { get; }
    bool Left { get; }
}

/// <summary>Item pictures (D117): <c>Assets/UI/icons/items/&lt;item id&gt;.png</c> from the Codex set when there is one;
/// a pot of an evolved dish uses its template's. Null until the icons arrive (the tile draws a badge instead).</summary>
public static class ItemIcons
{
    private static readonly Dictionary<string, Texture2D?> _cache = new();

    public static Texture2D? Get(string id)
    {
        if (id.IndexOf(':') is var c && c > 0) id = id[..c];
        if (_cache.TryGetValue(id, out var t)) return t;
        string path = $"res://Assets/UI/icons/items/{id}.png";
        return _cache[id] = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public static Texture2D? Category(string cat)
    {
        string key = "cat:" + cat;
        if (_cache.TryGetValue(key, out var t)) return t;
        string path = $"res://Assets/UI/icons/categories/{cat}.png";
        return _cache[key] = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    /// <summary>A category's colour (the placeholder badges and the names in lists).</summary>
    public static Color Of(string? cat) => cat switch
    {
        "food" => new Color(0.86f, 0.66f, 0.32f), "drink" => new Color(0.42f, 0.66f, 0.86f), "medical" => new Color(0.84f, 0.4f, 0.4f),
        "tool" => new Color(0.6f, 0.72f, 0.52f), "material" => new Color(0.68f, 0.62f, 0.78f), "fuel" => new Color(0.86f, 0.44f, 0.24f),
        "book" => new Color(0.74f, 0.58f, 0.42f), _ => new Color(0.62f, 0.62f, 0.6f),
    };

    public static readonly (string Id, string Name)[] Categories =
    {
        ("all", "All"), ("food", "Food"), ("drink", "Drink"), ("medical", "Medical"), ("tool", "Tools"), ("material", "Materials"),
        ("fuel", "Fuel"), ("book", "Books"), ("junk", "Junk"),
    };
}

/// <summary>
/// The rucksack (D117, Tom's pick "A + B"): one side of the inventory window as a grid of item tiles under category
/// tabs, with a weight bar and a card for the item you've picked (what it is, how fresh, what it counts as, its
/// actions). Same moves as the old list: drag a tile across, double-click to use it (or move the pile when a container
/// is open), right-click to move one.
/// </summary>
public partial class RucksackColumn : PanelContainer, IItemColumn
{
    private readonly InventoryScreen _screen;
    public bool Left { get; }
    public HBoxContainer Footer { get; } = new();
    private readonly Label _title, _weight;
    private readonly Control _weightBar;
    private readonly HFlowContainer _tabs;
    private readonly GridContainer _grid;
    private readonly VBoxContainer _card;
    private Inventory? _inv;
    private Func<ItemDef, bool>? _usable;
    private string _category = "all";
    private (string Id, int Stage)? _selected;
    private float _load;

    public RucksackColumn(InventoryScreen screen, bool left)
    {
        _screen = screen; Left = left;
        CustomMinimumSize = new Vector2(500, 560);
        if (Skin.Box(left ? "panels/container_frame.png" : "panels/window_frame.png", 6, 4) is { } frame) AddThemeStyleboxOverride("panel", frame);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        AddChild(v);
        var head = new HBoxContainer();
        v.AddChild(head);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 20);
        _title.AddThemeColorOverride("font_color", Skin.Text);
        head.AddChild(_title);
        _weight = new Label();
        _weight.AddThemeColorOverride("font_color", new Color(Skin.Text, 0.8f));
        head.AddChild(_weight);
        _weightBar = new Control { CustomMinimumSize = new Vector2(0, 8) };
        _weightBar.Draw += DrawWeight;
        v.AddChild(_weightBar);
        _tabs = new HFlowContainer();
        _tabs.AddThemeConstantOverride("h_separation", 4);
        _tabs.AddThemeConstantOverride("v_separation", 4);
        v.AddChild(_tabs);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(scroll);
        _grid = new GridContainer { Columns = 5, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 6);
        _grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(_grid);
        var cardBox = new PanelContainer { CustomMinimumSize = new Vector2(0, 118) };
        if (Skin.Box("panels/tooltip.png", 6, 4) is { } paper) cardBox.AddThemeStyleboxOverride("panel", paper);
        else cardBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.25f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        v.AddChild(cardBox);
        _card = new VBoxContainer();
        _card.AddThemeConstantOverride("separation", 3);
        cardBox.AddChild(_card);
        v.AddChild(Footer);
    }

    public void Fill(Inventory inv, string title, string weight, Func<ItemDef, bool>? usable)
    {
        _inv = inv; _usable = usable;
        _title.Text = title;
        _weight.Text = weight;
        _load = inv.MaxWeight < float.MaxValue ? Math.Clamp(inv.Weight / inv.MaxWeight, 0, 1) : -1;
        _weightBar.Visible = _load >= 0;
        _weightBar.QueueRedraw();
        // the piles: one per item and freshness (D115)
        var piles = inv.Stacks.GroupBy(s => (s.Id, Stage: Freshness.Stage(s)))
            .Select(g => (Def: ItemDb.Get(g.Key.Id), g.Key.Id, g.Key.Stage, N: g.Sum(s => s.Count), Made: g.Min(s => s.Made)))
            .OrderBy(p => Array.FindIndex(ItemIcons.Categories, c => c.Id == p.Def?.Category)).ThenBy(p => p.Def?.Name).ThenBy(p => p.Stage).ToList();
        // tabs: all, and each category there is something of
        foreach (var c in _tabs.GetChildren()) c.QueueFree();
        var counts = piles.GroupBy(p => p.Def?.Category ?? "junk").ToDictionary(g => g.Key, g => g.Sum(p => p.N));
        if (_category != "all" && !counts.ContainsKey(_category)) _category = "all";
        foreach (var (id, name) in ItemIcons.Categories)
        {
            int n = id == "all" ? piles.Sum(p => p.N) : counts.GetValueOrDefault(id);
            if (id != "all" && n == 0) continue;
            var b = new Button { Text = $"{name} {n}", ToggleMode = true, ButtonPressed = id == _category, Icon = ItemIcons.Category(id), ExpandIcon = false };
            b.AddThemeFontSizeOverride("font_size", 13);
            Skin.Button(b, "button_small");
            if (id != "all") b.AddThemeColorOverride("font_color", ItemIcons.Of(id));
            string cat = id;
            b.Pressed += () => { _category = cat; Fill(_inv!, _title.Text, _weight.Text, _usable); };
            _tabs.AddChild(b);
        }
        // tiles
        foreach (var c in _grid.GetChildren()) c.QueueFree();
        var shown = piles.Where(p => _category == "all" || p.Def?.Category == _category).ToList();
        if (_selected is { } sel && !shown.Any(p => p.Id == sel.Id && p.Stage == sel.Stage)) _selected = null;
        if (shown.Count == 0) _grid.AddChild(new Label { Text = "  (empty)", Modulate = new Color(1, 1, 1, 0.5f) });
        foreach (var p in shown)
            _grid.AddChild(new ItemTile(this, p.Id, p.N, p.Def, p.Stage, _selected == (p.Id, p.Stage)));
        ShowCard(shown.FirstOrDefault(p => _selected == (p.Id, p.Stage)) is var s && s.Id != null ? s : default);
    }

    public InventoryScreen GetScreen() => _screen;

    public void Select(string id, int stage) { _selected = (id, stage); if (_inv != null) Fill(_inv, _title.Text, _weight.Text, _usable); }

    /// <summary>The picked item's card: what it is, how fresh, what it counts as, and what you can do with it.</summary>
    private void ShowCard((ItemDef? Def, string Id, int Stage, int N, double Made) p)
    {
        foreach (var c in _card.GetChildren()) c.QueueFree();
        if (p.Id == null || p.Def is not { } d)
        {
            var hint = new Label { Text = Left ? "Pick something to take" : "Pick something to see what it is", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            hint.AddThemeColorOverride("font_color", new Color(NotebookColumn.Pencil, 0.8f));
            _card.AddChild(hint);
            return;
        }
        var name = new Label { Text = (p.N > 1 ? $"{p.N} × " : "") + d.Name, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        name.AddThemeFontSizeOverride("font_size", 17);
        name.AddThemeColorOverride("font_color", NotebookColumn.Ink);   // dark ink on the paper label
        _card.AddChild(name);
        var facts = new List<string> { $"{d.Weight * p.N:0.00} kg" };
        if (d.Food > 0) facts.Add($"food {d.Food:0}");
        if (d.Drink > 0) facts.Add($"drink {d.Drink:0}");
        if (d.Heal > 0) facts.Add($"heals {d.Heal:0}");
        if (Freshness.Perishable(d))
        {
            double age = Freshness.AgeDays(p.Made);
            facts.Add(p.Stage switch
            {
                Freshness.Fresh => $"fresh, {Math.Max(0, d.Fresh - age):0.#} days left",
                Freshness.Stale => $"stale: eat it soon ({Math.Max(0, d.Fresh + d.Stale - age):0.#} days)",
                Freshness.Rotten => "rotten: only good for the bin",
                _ => "",
            });
        }
        var line = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.AddThemeFontSizeOverride("normal_font_size", 13);
        var fc = p.Stage switch { Freshness.Stale => NotebookColumn.Amber, Freshness.Rotten => new Color(0.35f, 0.42f, 0.15f), _ => NotebookColumn.Ink };
        line.Text = $"[color=#{fc.ToHtml(false)}]{string.Join("  ·  ", facts.Where(f => f.Length > 0))}[/color]";
        if (d.Note.Length > 0) line.Text += $"\n[color=#{NotebookColumn.Pencil.ToHtml(false)}]{d.Note}[/color]";
        // what it stands in for in recipes (D107)
        var counts = d.Tags.Select(t => ItemDb.Say("tag:" + t)).Distinct().ToList();
        if (counts.Count > 0) line.Text += $"\n[color=#{NotebookColumn.Green.ToHtml(false)}]counts as: {string.Join(", ", counts)}[/color]";
        _card.AddChild(line);
        var acts = new HBoxContainer();
        acts.AddThemeConstantOverride("separation", 6);
        _card.AddChild(acts);
        void Act(string text, Action a)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(84, 30) };
            Skin.Button(b, "button_small");
            b.Pressed += a;
            acts.AddChild(b);
        }
        int? stage = p.Stage > 0 ? p.Stage : null;
        if (!Left && _usable != null && _usable(d))
            Act(d.Category == "food" ? "Eat" : d.Category == "drink" ? "Drink" : d.Category == "fuel" ? "Pour" : d.Category == "book" ? "Read" : "Use", () => _screen.Use(p.Id, stage));
        if (_screen.HasContainer)
        {
            Act(Left ? "Take one" : "Put one", () => _screen.Move(Left, p.Id, 1, stage));
            if (p.N > 1) Act(Left ? "Take all" : "Put all", () => _screen.Move(Left, p.Id, p.N, stage));
        }
    }

    private void DrawWeight()
    {
        var r = new Rect2(Vector2.Zero, _weightBar.Size);
        _weightBar.DrawRect(r, new Color(0, 0, 0, 0.35f));
        var col = _load < 0.7f ? new Color(0.55f, 0.72f, 0.45f) : _load < 0.92f ? Skin.Accent : new Color(0.85f, 0.35f, 0.3f);
        _weightBar.DrawRect(new Rect2(0, 0, r.Size.X * _load, r.Size.Y), col);
    }

    public override bool _CanDropData(Vector2 at, Variant data) =>
        data.VariantType == Variant.Type.Dictionary && (bool)data.AsGodotDictionary()["left"] != Left;

    public override void _DropData(Vector2 at, Variant data)
    {
        var d = data.AsGodotDictionary();
        int st = d.ContainsKey("stage") ? (int)d["stage"] : 0;
        _screen.Move((bool)d["left"], (string)d["id"], (int)d["n"], st > 0 ? st : null);
    }
}

/// <summary>One pile in the rucksack: its icon (or a badge until the icons come), how many, a freshness strip.</summary>
public partial class ItemTile : Control
{
    private readonly RucksackColumn _column;
    private readonly string _id;
    private readonly int _n, _stage;
    private readonly ItemDef? _def;
    private readonly bool _selected;
    private static StyleBox? _slot, _slotHover, _slotSelected;

    public ItemTile(RucksackColumn column, string id, int n, ItemDef? def, int stage, bool selected)
    {
        _column = column; _id = id; _n = n; _def = def; _stage = stage; _selected = selected;
        CustomMinimumSize = new Vector2(88, 96);
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = (def?.Name ?? id) + (stage is Freshness.Stale or Freshness.Rotten ? $" ({Freshness.Word(stage)})" : "");
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
    }

    public override void _Draw()
    {
        bool hover = GetGlobalRect().HasPoint(GetGlobalMousePosition());
        _slot ??= Skin.Box("panels/slot_empty.png");
        _slotHover ??= Skin.Box("panels/slot_hover.png");
        _slotSelected ??= Skin.Box("panels/slot_selected.png");
        var r = new Rect2(Vector2.Zero, Size);
        var box = _selected ? _slotSelected : hover ? _slotHover : _slot;
        if (box != null) DrawStyleBox(box, r);
        else DrawRect(r, _selected ? new Color(Skin.Accent, 0.35f) : new Color(0, 0, 0, hover ? 0.4f : 0.28f));
        var col = ItemIcons.Of(_def?.Category);
        var icon = ItemIcons.Get(_id);
        var area = new Rect2(14, 8, Size.X - 28, Size.X - 28);
        var font = ThemeDB.FallbackFont;
        if (icon != null) DrawTextureRect(icon, area, false, _stage == Freshness.Rotten ? new Color(0.75f, 0.85f, 0.6f) : Colors.White);
        else
        {
            // a badge until the icon set comes: the category's colour and the item's initials
            DrawCircle(area.GetCenter(), area.Size.X * 0.42f, new Color(col, 0.85f));
            string name = _def?.Name ?? _id;
            var words = name.Replace("(", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && w is not "of" and not "and").ToList();
            string ini = words.Count >= 2 ? $"{char.ToUpper(words[^1][0])}{words[^1][1]}" : name.Length >= 2 ? $"{char.ToUpper(name[0])}{name[1]}" : name;
            if (Evolved.Is(_id)) ini = "Pot";
            DrawString(font, new Vector2(area.Position.X, area.GetCenter().Y + 7), ini, HorizontalAlignment.Center, area.Size.X, 20, new Color(0.12f, 0.1f, 0.08f));
        }
        // the name under it, and how many
        string label = _def?.Name ?? _id;
        if (label.Length > 13) label = label[..12] + "…";
        DrawString(font, new Vector2(2, Size.Y - 12), label, HorizontalAlignment.Center, Size.X - 4, 12, new Color(Skin.Text, 0.85f));
        if (_n > 1) DrawString(font, new Vector2(Size.X - 34, 20), $"×{_n}", HorizontalAlignment.Right, 30, 14, Skin.Text);
        // freshness strip (D115)
        if (_stage > 0)
        {
            var fc = _stage switch { Freshness.Fresh => new Color(0.45f, 0.75f, 0.4f), Freshness.Stale => new Color(0.9f, 0.72f, 0.3f), _ => new Color(0.55f, 0.5f, 0.3f) };
            DrawRect(new Rect2(10, Size.Y - 6, (Size.X - 20) * (_stage == Freshness.Fresh ? 1f : _stage == Freshness.Stale ? 0.5f : 0.15f), 3), fc);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        int? stage = _stage > 0 ? _stage : null;
        if (mb.ButtonIndex == MouseButton.Left && mb.DoubleClick)
        {
            var screen = _column.GetScreen();
            if (screen.HasContainer) screen.Move(_column.Left, _id, _n, stage);
            else if (_def != null && OnFoot.Survival.Usable(_def)) screen.Use(_id, stage);
            AcceptEvent();
        }
        else if (mb.ButtonIndex == MouseButton.Left) { _column.Select(_id, _stage); AcceptEvent(); }
        else if (mb.ButtonIndex == MouseButton.Right) { _column.GetScreen().Move(_column.Left, _id, 1, stage); AcceptEvent(); }
    }

    public override Variant _GetDragData(Vector2 at)
    {
        int n = Input.IsKeyPressed(Key.Ctrl) ? 1 : _n;
        SetDragPreview(new Label { Text = n > 1 ? $"{n} × {_def?.Name ?? _id}" : _def?.Name ?? _id });
        return new Godot.Collections.Dictionary { ["left"] = _column.Left, ["id"] = _id, ["n"] = n, ["stage"] = _stage };
    }
}
