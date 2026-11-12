using System;
using System.Threading;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.UI;

/// <summary>
/// The sat-nav: a destination, the route to it (Dead Miles' A* router on a worker thread), progress along it and
/// re-routing when the driver strays more than 60 m from the line.
/// </summary>
public sealed class Navigation
{
    private readonly MapData _data;
    private MapRouter? _router;
    private CancellationTokenSource? _search;
    private Task<MapRoute?>? _task;
    private int _hint;
    private double _lastReroute = -100;

    public Vector2? Destination { get; private set; }
    public MapRoute? Route { get; private set; }
    public bool Routing => _task != null;
    /// <summary>Metres left along the route.</summary>
    public float Remaining { get; private set; }
    /// <summary>Signed turn at the next bend sharper than 30° (radians, left positive) and metres to it, or 0 / -1.</summary>
    public float NextTurn { get; private set; }
    public float NextTurnIn { get; private set; } = -1;

    public Navigation(MapData data) { _data = data; }

    public void SetDestination(Vector2? dest, Vector2 from)
    {
        Destination = dest;
        Route = null;
        _search?.Cancel();
        _task = null;
        if (dest == null) return;
        Start(from, dest.Value);
    }

    private void Start(Vector2 from, Vector2 to)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        _router ??= new MapRouter(_data);
        var router = _router;
        _task = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = router.Find(from, to, () => cts.IsCancellationRequested);
            Log.Info("Nav", r == null ? "no route" : $"route {r.Length / 1000f:F1} km in {watch.Elapsed.TotalSeconds:F1} s");
            return r;
        });
        _hint = 0;
    }

    /// <summary>Call every frame with the driver's world position (x, z as floats: fine within a region).</summary>
    public void Update(Vector2 pos, double now)
    {
        if (_task is { IsCompleted: true } t)
        {
            _task = null;
            if (!t.IsFaulted && t.Result != null) Route = t.Result;
            else if (t.IsFaulted) Log.Error("Nav", t.Exception?.ToString() ?? "route failed");
        }
        var route = Route;
        if (route == null || route.Points.Length < 2) return;
        _hint = route.Nearest(pos, _hint, out float off);
        Remaining = route.Length - route.Along[_hint] + off;
        if (Destination is { } dest && pos.DistanceTo(dest) < 40f) { Route = null; Destination = null; return; }
        if (off > 60f && now - _lastReroute > 4 && Destination is { } d2) { _lastReroute = now; Start(pos, d2); }
        // next real bend
        NextTurnIn = -1; NextTurn = 0;
        for (int i = _hint + 1; i + 1 < route.Points.Length && route.Along[i] - route.Along[_hint] < 1500f; i++)
        {
            var a = route.Points[i] - route.Points[i - 1];
            int j = i + 1;
            while (j + 1 < route.Points.Length && route.Points[j].DistanceTo(route.Points[i]) < 25f) j++;
            var b = route.Points[j] - route.Points[i];
            if (a.LengthSquared() < 0.01f || b.LengthSquared() < 0.01f) continue;
            float ang = a.AngleTo(b);
            if (MathF.Abs(ang) > 0.52f)
            {
                NextTurn = -ang; // screen angles: +Z is south, so a positive angle is a right turn
                NextTurnIn = route.Along[i] - route.Along[_hint];
                break;
            }
        }
    }
}
