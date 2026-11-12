using System;

namespace BalticDrive.Core;

/// <summary>Tagged log lines. Plain C# so data code runs in headless tests; the game points <see cref="Sink"/> at
/// Godot's log at start-up. Safe from worker threads.</summary>
public static class Log
{
    public enum Level { Info, Warn, Error }

    /// <summary>Where lines go (default: the console).</summary>
    public static Action<Level, string> Sink = (level, line) =>
    {
        if (level == Level.Info) Console.WriteLine(line); else Console.Error.WriteLine(line);
    };

    public static void Info(string tag, string msg) => Sink(Level.Info, $"[{tag}] {msg}");
    public static void Warn(string tag, string msg) => Sink(Level.Warn, $"[{tag}] {msg}");
    public static void Error(string tag, string msg) => Sink(Level.Error, $"[{tag}] {msg}");
}
