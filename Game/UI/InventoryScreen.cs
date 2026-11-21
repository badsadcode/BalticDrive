using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using BalticDrive.Items;

namespace BalticDrive.UI;

/// <summary>
/// The inventory window (D89): what's in a container on the left, what you carry on the right. Drag an item across, or
/// double-click it to move the whole pile, right-click to move one; "Take all" (E) takes what fits. Food, drink and
/// medicine in your pack have a Use button. Tab opens your pack on its own; Esc, Tab or F closes.
/// </summary>
public partial class InventoryScreen : Control
{
    private Inventory? _mine, _other;
    private string _otherTitle = "";
    private Func<ItemDef, bool>? _usable;
    private Action<string>? _use;
    private Action? _closed;
    private readonly ItemColumn _left, _right;
    private readonly CraftingColumn _craft;
    private readonly Button _takeAll;
    private readonly Label _hint;

    public bool IsOpen => Visible;

    public InventoryScreen()
    {
        Name = "Inventory";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var centre = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(centre);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        centre.AddChild(box);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        box.AddChild(row);
        _left = new ItemColumn(this, true);
        _right = new ItemColumn(this, false);
        _craft = new CraftingColumn(this);
        row.AddChild(_left);
        row.AddChild(_craft);
        row.AddChild(_right);
        _takeAll = new Button { Text = "Take all (E)", CustomMinimumSize = new Vector2(170, 38) };
        Skin.Button(_takeAll);
        _takeAll.Pressed += TakeAll;
        _left.Footer.AddChild(_takeAll);
        _hint = new Label { Text = "drag, or double-click: move the pile · right-click: move one · Esc closes", HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.65f));
        box.AddChild(_hint);
    }

    /// <summary>Opens on a container (<paramref name="other"/>) or, when it's null, on your pack alone.</summary>
    public void Open(Inventory mine, Inventory? other, string otherTitle, Func<ItemDef, bool> usable, Action<string> use, Action closed)
    {
        _mine = mine; _other = other; _otherTitle = otherTitle; _usable = usable; _use = use; _closed = closed;
        _left.Visible = other != null;
        _craft.Visible = other == null;   // your pack alone (Tab): crafting on the left (D106)
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Refresh();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
        _closed?.Invoke();
    }

    public void Refresh()
    {
        if (_mine == null) return;
        if (_other != null) _left.Fill(_other, $"{char.ToUpper(_otherTitle[0]) + _otherTitle[1..]}", $"{_other.Weight:0.0} kg", null);
        _right.Fill(_mine, "You", $"{_mine.Weight:0.0} / {_mine.MaxWeight:0} kg", _usable);
        _takeAll.Disabled = _other == null || _other.IsEmpty;
        if (_other == null) _craft.Fill(_mine);
    }

    /// <summary>Makes a recipe from your pack (D106) and says how it went.</summary>
    public void Craft(Recipe r)
    {
        if (_mine == null) return;
        Recipes.Make(r, _mine, out var said);
        Flash(said);
        Refresh();
    }

    /// <summary>Moves items between the two sides: from the container (<paramref name="fromLeft"/>) or from you.</summary>
    public void Move(bool fromLeft, string id, int n)
    {
        if (_mine == null || _other == null) return;
        var (from, to) = fromLeft ? (_other, _mine) : (_mine, _other);
        int moved = from.MoveTo(to, id, n);
        if (moved == 0 && fromLeft) Flash("Too heavy to carry");
        Containers.Save();
        Refresh();
    }

    public void Use(string id) { _use?.Invoke(id); Refresh(); }

    public void TakeAll()
    {
        if (_mine == null || _other == null) return;
        _other.MoveAllTo(_mine);
        if (!_other.IsEmpty) Flash("You can't carry it all");
        Containers.Save();
        Refresh();
    }

    public void Flash(string s) { _hint.Text = s; GetTree().CreateTimer(2.0).Timeout += () => _hint.Text = "drag, or double-click: move the pile · right-click: move one · Esc closes"; }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode is Key.Escape or Key.Tab or Key.F) { Close(); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode == Key.E) { TakeAll(); GetViewport().SetInputAsHandled(); }
    }
}

/// <summary>One side of the inventory window: a title, the weight, the list of items, and a drop target.</summary>
public partial class ItemColumn : PanelContainer
{
    private readonly InventoryScreen _screen;
    public readonly bool Left;
    private readonly Label _title, _weight;
    private readonly VBoxContainer _rows;
    public readonly HBoxContainer Footer;

    public ItemColumn(InventoryScreen screen, bool left)
    {
        _screen = screen; Left = left;
        CustomMinimumSize = new Vector2(420, 480);
        // D102: Tom's UI set: a crate-like frame for what's in the furniture, the painted-metal window for you
        if (Skin.Box(left ? "panels/container_frame.png" : "panels/window_frame.png", 6, 4) is { } skin) AddThemeStyleboxOverride("panel", skin);
        else AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.92f), BorderColor = new Color(1, 1, 1, 0.12f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
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
        head.AddChild(_weight);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
        Footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        v.AddChild(Footer);
    }

    public void Fill(Inventory inv, string title, string weight, Func<ItemDef, bool>? usable)
    {
        _title.Text = title;
        _weight.Text = weight;
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        var groups = inv.Stacks.GroupBy(s => s.Id).Select(g => (Def: ItemDb.Get(g.Key), Id: g.Key, N: g.Sum(s => s.Count)))
            .OrderBy(g => g.Def?.Category).ThenBy(g => g.Def?.Name).ToList();
        if (groups.Count == 0) _rows.AddChild(new Label { Text = "  (empty)", Modulate = new Color(1, 1, 1, 0.5f) });
        foreach (var (def, id, n) in groups)
            _rows.AddChild(new ItemRow(_screen, this, id, n, def, usable != null && def != null && usable(def)));
    }

    public override bool _CanDropData(Vector2 at, Variant data) =>
        data.VariantType == Variant.Type.Dictionary && (bool)data.AsGodotDictionary()["left"] != Left;

    public override void _DropData(Vector2 at, Variant data)
    {
        var d = data.AsGodotDictionary();
        _screen.Move((bool)d["left"], (string)d["id"], (int)d["n"]);
    }
}

/// <summary>An item in a column: its name and count, its weight; dragged, double-clicked or right-clicked.</summary>
public partial class ItemRow : HBoxContainer
{
    private readonly InventoryScreen _screen;
    private readonly ItemColumn _column;
    private readonly string _id;
    private readonly int _n;

    public ItemRow(InventoryScreen screen, ItemColumn column, string id, int n, ItemDef? def, bool usable)
    {
        _screen = screen; _column = column; _id = id; _n = n;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, 32);
        var name = new Label { Text = (n > 1 ? $"{n} × " : "") + (def?.Name ?? id), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        name.AddThemeColorOverride("font_color", def?.Category switch
        {
            "food" => new Color(0.95f, 0.85f, 0.6f), "drink" => new Color(0.65f, 0.85f, 1f), "medical" => new Color(1f, 0.6f, 0.6f),
            "tool" => new Color(0.8f, 0.9f, 0.75f), "material" => new Color(0.85f, 0.8f, 0.9f), _ => new Color(0.85f, 0.85f, 0.85f),
        });
        AddChild(name);
        var w = new Label { Text = $"{(def?.Weight ?? 0) * n:0.00} kg", MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0.6f) };
        AddChild(w);
        if (usable)
        {
            var b = new Button { Text = def!.Category == "food" ? "Eat" : def.Category == "drink" ? "Drink" : "Use", CustomMinimumSize = new Vector2(70, 0) };
            Skin.Button(b, "button_small");
            b.Pressed += () => _screen.Use(_id);
            AddChild(b);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.Left && mb.DoubleClick) { _screen.Move(_column.Left, _id, _n); AcceptEvent(); }
        else if (mb.ButtonIndex == MouseButton.Right) { _screen.Move(_column.Left, _id, 1); AcceptEvent(); }
    }

    public override Variant _GetDragData(Vector2 at)
    {
        int n = Input.IsKeyPressed(Key.Ctrl) ? 1 : _n;
        SetDragPreview(new Label { Text = n > 1 ? $"{n} × {ItemDb.Get(_id)?.Name ?? _id}" : ItemDb.Get(_id)?.Name ?? _id });
        return new Godot.Collections.Dictionary { ["left"] = _column.Left, ["id"] = _id, ["n"] = n };
    }

    private static StyleBox? _rowNormal, _rowHover;

    public override void _Draw()
    {
        bool hover = GetGlobalRect().HasPoint(GetGlobalMousePosition());
        // D102: the set's list rows, else a light wash on hover
        _rowNormal ??= Skin.Box("panels/row_normal.png");
        _rowHover ??= Skin.Box("panels/row_hover.png");
        if ((hover ? _rowHover : _rowNormal) is { } row) DrawStyleBox(row, new Rect2(-4, 0, Size.X + 8, Size.Y));
        else if (hover) DrawRect(new Rect2(Vector2.Zero, Size), new Color(1, 1, 1, 0.08f));
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationMouseEnter or (int)NotificationMouseExit) QueueRedraw();
    }
}

/// <summary>The crafting list (D106): every recipe by category, what it uses and needs (what you lack in red), what it
/// makes, and a Make button when your pack has it all.</summary>
public partial class CraftingColumn : PanelContainer
{
    private readonly InventoryScreen _screen;
    private readonly VBoxContainer _rows;
    private static readonly Color Have = new(0.65f, 0.85f, 0.6f), Lack = new(0.95f, 0.45f, 0.4f);

    public CraftingColumn(InventoryScreen screen)
    {
        _screen = screen;
        CustomMinimumSize = new Vector2(460, 480);
        if (Skin.Box("panels/window_frame.png", 6, 4) is { } frame) AddThemeStyleboxOverride("panel", frame);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        AddChild(v);
        var title = new Label { Text = "Crafting" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", Skin.Text);
        v.AddChild(title);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_rows);
    }

    public void Fill(Inventory pack)
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        // what you can make first, then by category
        var list = Recipes.All.Select(r => (R: r, Miss: Recipes.Missing(r, pack))).OrderBy(x => x.Miss.Count > 0).ThenBy(x => x.R.Category).ToList();
        foreach (var (r, miss) in list)
        {
            bool can = miss.Count == 0;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            text.AddThemeConstantOverride("separation", 0);
            var name = new Label { Text = r.Name };
            name.AddThemeFontSizeOverride("font_size", 16);
            name.AddThemeColorOverride("font_color", can ? Skin.Text : new Color(Skin.Text, 0.55f));
            text.AddChild(name);
            // uses: each input, green when you have enough; needs: each tool
            var uses = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            uses.AddThemeFontSizeOverride("normal_font_size", 13);
            string Part(string label, bool ok) => $"[color=#{(ok ? Have : Lack).ToHtml(false)}]{label}[/color]";
            var parts = r.Inputs.Select(p => Part(p.Count > 1 ? $"{p.Count} × {ItemDb.Get(p.Item)?.Name ?? p.Item}" : ItemDb.Get(p.Item)?.Name ?? p.Item, pack.Count(p.Item) >= p.Count)).ToList();
            var tools = r.Tools.Select(alts => Part(string.Join(" or ", alts.Select(a => ItemDb.Get(a)?.Name ?? a)), alts.Any(a => pack.Count(a) > 0))).ToList();
            uses.Text = (parts.Count > 0 ? "uses " + string.Join(", ", parts) : "") + (tools.Count > 0 ? "  ·  needs " + string.Join(", ", tools) : "")
                + $"  →  [color=#{Skin.Accent.ToHtml(false)}]{Inventory.Say(r.Outputs.Select(o => new ItemStack(o.Item, o.Count)))}[/color]";
            text.AddChild(uses);
            if (r.About.Length > 0)
            {
                var about = new Label { Text = r.About, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                about.AddThemeFontSizeOverride("font_size", 12);
                about.AddThemeColorOverride("font_color", new Color(Skin.Text, 0.45f));
                text.AddChild(about);
            }
            row.AddChild(text);
            var make = new Button { Text = "Make", Disabled = !can, CustomMinimumSize = new Vector2(76, 34), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            Skin.Button(make, "button_small");
            var rec = r;
            make.Pressed += () => _screen.Craft(rec);
            row.AddChild(make);
            _rows.AddChild(row);
        }
    }
}
