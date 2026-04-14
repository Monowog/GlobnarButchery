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

	private Camera2D _camera = null!;
	private DestructiblePixelSheet _sheet = null!;
	private bool _panning;

	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_sheet = GetNode<DestructiblePixelSheet>("DestructiblePixelSheet");
		_camera.MakeCurrent();
		_camera.Position = new Vector2(_sheet.SheetSize.X * 0.5f, _sheet.SheetSize.Y * 0.5f);
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
				GetViewport().SetInputAsHandled();
			}
		}
		else if (@event is InputEventMouseMotion mm && _panning && (mm.ButtonMask & MouseButtonMask.Middle) != 0)
		{
			var z = _camera.Zoom;
			_camera.Position -= new Vector2(mm.Relative.X / z.X, mm.Relative.Y / z.Y);
			GetViewport().SetInputAsHandled();
		}
	}

	private void ApplyZoom(float factor)
	{
		var z = Mathf.Clamp(_camera.Zoom.X * factor, MinZoom, MaxZoom);
		_camera.Zoom = new Vector2(z, z);
	}
}
