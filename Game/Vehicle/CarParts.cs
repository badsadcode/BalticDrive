using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace BalticDrive.Vehicle;

/// <summary>One part of a car (D67): what it is, where it sits, how good it is (0 broken … 100 new).</summary>
public sealed class CarPart
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required PartKind Kind { get; init; }
    /// <summary>Which corner or side: wheels, brakes and suspension per corner; doors and windows per side.</summary>
    public int Slot { get; init; }
    public float Condition = 100f;
    /// <summary>Taken off (the workshop, later): there's nothing in its place.</summary>
    public bool Missing;
    public float Health => Missing ? 0f : Condition / 100f;
}

public enum PartKind { Engine, Battery, Tyre, Brakes, Suspension, Windshield, Window, Door, Hood, Trunk, Seat }

/// <summary>
/// A car's parts and what they do (D67, Tom: "Project Zomboid complexity for cars", repaired like a car workshop
/// game). Each part has a condition; bullets wear the part they hit, crashes the parts at the end that hit, and every
/// part changes the car in a way you can feel:
/// * engine: less power below 100 %, coughing and smoke below 35 %, dead at 0;
/// * battery: below 8 % the engine won't start (and the lights are dim);
/// * tyres: worn ones grip less, a flat one (0) runs on the rim and pulls the car to its side;
/// * brakes (front, rear): stopping power falls with wear;
/// * suspension (front, rear): softer, lower, wallowing;
/// * windshield, windows, doors, hood, trunk, seats: protection: a broken window or door lets more of a bullet reach
///   the driver, a broken hood lets bullets into the engine; a cracked windshield shows cracks (later).
/// </summary>
public sealed class CarParts
{
    public readonly List<CarPart> All = new();
    public static string[] Corner => new[] { Core.Lang.T("corner.0"), Core.Lang.T("corner.1"), Core.Lang.T("corner.2"), Core.Lang.T("corner.3") };
    public static string[] Side => new[] { Core.Lang.T("side.0"), Core.Lang.T("side.1") };

    public CarParts()
    {
        Add("engine", Core.Lang.T("part.engine"), PartKind.Engine);
        Add("battery", Core.Lang.T("part.battery"), PartKind.Battery);
        for (int i = 0; i < 4; i++) Add($"tyre.{i}", Core.Lang.T("part.tyre", Corner[i]), PartKind.Tyre, i);
        Add("brakes.front", Core.Lang.T("part.brakes.front"), PartKind.Brakes, 0);
        Add("brakes.rear", Core.Lang.T("part.brakes.rear"), PartKind.Brakes, 1);
        Add("suspension.front", Core.Lang.T("part.suspension.front"), PartKind.Suspension, 0);
        Add("suspension.rear", Core.Lang.T("part.suspension.rear"), PartKind.Suspension, 1);
        Add("windshield", Core.Lang.T("part.windshield"), PartKind.Windshield);
        for (int s = 0; s < 2; s++)
        {
            Add($"window.{s}", Core.Lang.T("part.window", Side[s]), PartKind.Window, s);
            Add($"door.{s}", Core.Lang.T("part.door", Side[s]), PartKind.Door, s);
        }
        Add("hood", Core.Lang.T("part.hood"), PartKind.Hood);
        Add("trunk", Core.Lang.T("part.trunk"), PartKind.Trunk);
        Add("seat.driver", Core.Lang.T("part.seat.driver"), PartKind.Seat, 0);
    }

    private void Add(string id, string name, PartKind kind, int slot = 0) => All.Add(new CarPart { Id = id, Name = name, Kind = kind, Slot = slot });

    public CarPart this[string id] => All.First(p => p.Id == id);

    /// <summary>Takes over another car's part states (a wreck put away and met again).</summary>
    public void CopyFrom(CarParts o)
    {
        foreach (var p in All) { var q = o[p.Id]; p.Condition = q.Condition; p.Missing = q.Missing; }
    }
    public CarPart Tyre(int corner) => this[$"tyre.{corner}"];

    /// <summary>Wears a part; returns true when this broke it.</summary>
    public bool Wear(CarPart p, float amount)
    {
        if (p.Missing || amount <= 0) return false;
        bool was = p.Condition > 0;
        p.Condition = MathF.Max(0, p.Condition - amount);
        return was && p.Condition <= 0;
    }

    // ---- what the parts do to the car ------------------------------------------------------------------------------

    public bool EngineRuns => this["engine"].Health > 0 && this["battery"].Health > 0.08f;
    /// <summary>Engine power factor: 25 % at the edge of breaking, all of it when new.</summary>
    public float Power => this["engine"].Health <= 0 ? 0 : 0.25f + 0.75f * this["engine"].Health;
    public float Brakes(bool rear) => 0.25f + 0.75f * this[rear ? "brakes.rear" : "brakes.front"].Health;
    /// <summary>Grip of a wheel: a flat tyre on its rim keeps 45 %.</summary>
    public float Grip(int corner) { var t = Tyre(corner); return t.Health <= 0 ? 0.45f : 0.8f + 0.2f * t.Health; }
    public bool Flat(int corner) => Tyre(corner).Health <= 0;
    public float Suspension(bool rear) => this[rear ? "suspension.rear" : "suspension.front"].Health;

    /// <summary>How much of a bullet hitting this side gets through to the driver (0.2 through intact panels and glass,
    /// D52; up to 0.45 through a broken window and door).</summary>
    public float Exposure(int side)
    {
        float window = this[$"window.{side}"].Health, door = this[$"door.{side}"].Health;
        return 0.2f + 0.15f * (1 - window) + 0.1f * (1 - door);
    }

    /// <summary>A short list of what's wrong (for the HUD): parts under 70 %, worst first.</summary>
    public string Report()
    {
        var bad = All.Where(p => p.Missing || p.Condition < 70).OrderBy(p => p.Health).Take(5).ToList();
        if (bad.Count == 0) return "";
        return string.Join("   ", bad.Select(p => p.Missing ? Core.Lang.T("car.missing", p.Name) : p.Condition <= 0 ? Core.Lang.T("car.broken", p.Name) : $"{p.Name} {p.Condition:0} %"));
    }
}
