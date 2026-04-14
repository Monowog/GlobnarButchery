using Godot;

// Zoom: mouse wheel. Pan: middle mouse drag. Child nodes: Camera2D, DestructiblePixelSheet.
public partial class MainSceneController : Node2D
{
	[Export]
	public float ZoomStep { get; set; } = 0.15f;

	[Export]
	public float MinZoom { get; set; } = 0.2f;

	[Export]
	public float MaxZoom { get; set; } = 6f;

	[Export]
	public float PanMomentumScale { get; set; } = 60f;

	[Export]
	public float PanFriction { get; set; } = 8f;

	private Camera2D _camera = null!;
	private DestructiblePixelSheet _sheet = null!;
	private bool _panning;
	private Vector2 _panVelocity;

	public override void _Ready()
	{
		SetProcess(true);
		_camera = GetNode<Camera2D>("Camera2D");
		_sheet = GetNode<DestructiblePixelSheet>("DestructiblePixelSheet");
		_camera.MakeCurrent();
		_camera.Position = new Vector2(_sheet.SheetSize.X * 0.5f, _sheet.SheetSize.Y * 0.5f);
	}

	public override void _Process(double delta)
	{
		var dt = (float)delta;
		if (dt <= 0f || _panning)
		{
			return;
		}

		if (_panVelocity.LengthSquared() < 1e-4f)
		{
			_panVelocity = Vector2.Zero;
			return;
		}

		_camera.Position += _panVelocity * dt;
		var decay = Mathf.Exp(-Mathf.Max(0f, PanFriction) * dt);
		_panVelocity *= decay;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
			{
				ApplyZoom(1f + ZoomStep);
				GetViewport().SetInputAsHandled();
				return;
			}

			if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
			{
				ApplyZoom(1f / (1f + ZoomStep));
				GetViewport().SetInputAsHandled();
				return;
			}

			if (mb.ButtonIndex == MouseButton.Middle)
			{
				_panning = mb.Pressed;
				if (_panning)
				{
					_panVelocity = Vector2.Zero;
				}

				GetViewport().SetInputAsHandled();
			}
		}
		else if (@event is InputEventMouseMotion mm && _panning && (mm.ButtonMask & MouseButtonMask.Middle) != 0)
		{
			var z = _camera.Zoom;
			var panDelta = new Vector2(mm.Relative.X / z.X, mm.Relative.Y / z.Y);
			_camera.Position -= panDelta;

			var dt = (float)GetProcessDeltaTime();
			if (dt > 1e-4f)
			{
				// Inertial velocity in world units/second (same direction as camera motion).
				var instant = (-panDelta / dt) * Mathf.Max(0f, PanMomentumScale);
				_panVelocity = instant;
			}

			GetViewport().SetInputAsHandled();
		}
	}

	private void ApplyZoom(float factor)
	{
		var z = Mathf.Clamp(_camera.Zoom.X * factor, MinZoom, MaxZoom);
		_camera.Zoom = new Vector2(z, z);
	}
}
