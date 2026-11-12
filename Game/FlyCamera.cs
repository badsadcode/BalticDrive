using Godot;

namespace BalticDrive;

/// <summary>Free camera for looking at the map: right mouse to look, WASD / QE to move, Shift fast, Ctrl very fast.
/// Never goes below the ground.</summary>
public partial class FlyCamera : Camera3D
{
    public float Yaw, Pitch;
    public float GroundHeight;
    public float Speed = 40f;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
            Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Yaw -= mm.Relative.X * 0.003f;
            Pitch = Mathf.Clamp(Pitch - mm.Relative.Y * 0.003f, -1.5f, 1.5f);
        }
    }

    public override void _Process(double delta)
    {
        Rotation = new Vector3(Pitch, Yaw, 0);
        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move -= Basis.Z;
        if (Input.IsKeyPressed(Key.S)) move += Basis.Z;
        if (Input.IsKeyPressed(Key.A)) move -= Basis.X;
        if (Input.IsKeyPressed(Key.D)) move += Basis.X;
        if (Input.IsKeyPressed(Key.E)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) move -= Vector3.Up;
        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? 6f : 1f) * (Input.IsKeyPressed(Key.Ctrl) ? 25f : 1f);
        Position += move.Normalized() * speed * (float)delta;
        if (Position.Y < GroundHeight + 1.6f) Position = Position with { Y = GroundHeight + 1.6f };
    }
}
