using Godot;
using System.Collections.Generic;

// Zoom: mouse wheel. Pan: middle mouse drag. Child nodes: Camera2D, DestructiblePixelSheet.
public partial class MainSceneController : Node2D
{
	[Signal]
	public delegate void ScoreChangedEventHandler(Godot.Collections.Dictionary scoreByShapeKey, Godot.Collections.Dictionary damagePercentByShapeKey, Godot.Collections.Array shapeIslandKeysSorted);

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

	[Export]
	public float MaxPanMomentumSpeed { get; set; } = 2500f;

	[Export]
	public bool UseCameraBounds { get; set; } = true;

	[Export]
	public Vector2 CameraBoundsMin { get; set; } = Vector2.Zero;

	[Export]
	public Vector2 CameraBoundsMax { get; set; } = new(800f, 600f);

	private Camera2D _camera = null!;
	private DestructiblePixelSheet _sheet = null!;
	private bool _panning;
	private Vector2 _panVelocity;
	private readonly Dictionary<int, int> _scoreByShapeKey = new();
	private Godot.Collections.Dictionary _damagePercentByShapeKey = new();

	public override void _Ready()
	{
		SetProcess(true);
		_camera = GetNode<Camera2D>("Camera2D");
		_sheet = GetNode<DestructiblePixelSheet>("DestructiblePixelSheet");
		_sheet.PointsAwarded += OnPointsAwarded;
		_sheet.ShapeDamageStatsChanged += OnShapeDamageStatsChanged;
		_camera.MakeCurrent();
		_camera.Position = new Vector2(_sheet.SheetSize.X * 0.5f, _sheet.SheetSize.Y * 0.5f);
		if (UseCameraBounds)
		{
			_camera.Position = ClampToBounds(_camera.Position);
		}
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
		if (UseCameraBounds)
		{
			var clamped = ClampToBounds(_camera.Position);
			if ((clamped - _camera.Position).LengthSquared() > 0f)
			{
				// Hitting bounds cancels inertial drift into the wall.
				_panVelocity = Vector2.Zero;
			}

			_camera.Position = clamped;
		}

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
			if (UseCameraBounds)
			{
				_camera.Position = ClampToBounds(_camera.Position);
			}

			var dt = (float)GetProcessDeltaTime();
			if (dt > 1e-4f)
			{
				// Inertial velocity in world units/second (same direction as camera motion).
				var instant = (-panDelta / dt) * Mathf.Max(0f, PanMomentumScale);
				_panVelocity = ClampMomentum(instant);
			}

			GetViewport().SetInputAsHandled();
		}
	}

	private Vector2 ClampToBounds(Vector2 p)
	{
		var minX = Mathf.Min(CameraBoundsMin.X, CameraBoundsMax.X);
		var maxX = Mathf.Max(CameraBoundsMin.X, CameraBoundsMax.X);
		var minY = Mathf.Min(CameraBoundsMin.Y, CameraBoundsMax.Y);
		var maxY = Mathf.Max(CameraBoundsMin.Y, CameraBoundsMax.Y);
		return new Vector2(Mathf.Clamp(p.X, minX, maxX), Mathf.Clamp(p.Y, minY, maxY));
	}

	private Vector2 ClampMomentum(Vector2 v)
	{
		var max = Mathf.Max(0f, MaxPanMomentumSpeed);
		var len = v.Length();
		if (len <= max || len <= 1e-5f)
		{
			return v;
		}

		return v / len * max;
	}

	private void ApplyZoom(float factor)
	{
		var z = Mathf.Clamp(_camera.Zoom.X * factor, MinZoom, MaxZoom);
		_camera.Zoom = new Vector2(z, z);
	}

	private void OnPointsAwarded(int totalPoints, Godot.Collections.Array blobPayloads)
	{
		foreach (Variant item in blobPayloads)
		{
			if (item.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var d = item.AsGodotDictionary();
			if (!d.ContainsKey("ShapeKey") || !d.ContainsKey("Points"))
			{
				continue;
			}

			var key = d["ShapeKey"].AsInt32();
			var pts = d["Points"].AsInt32();
			_scoreByShapeKey.TryGetValue(key, out var prev);
			_scoreByShapeKey[key] = prev + pts;
		}

		EmitSignal(SignalName.ScoreChanged, BuildHarvestScorePercentByShapeKey(), _damagePercentByShapeKey, _sheet.GetShapeIslandGlobalKeysSorted());
		GD.Print($"Harvest +{totalPoints}");
	}

	private void OnShapeDamageStatsChanged(Godot.Collections.Dictionary damagePercentByShapeKey)
	{
		_damagePercentByShapeKey = damagePercentByShapeKey;
		EmitSignal(SignalName.ScoreChanged, BuildHarvestScorePercentByShapeKey(), _damagePercentByShapeKey, _sheet.GetShapeIslandGlobalKeysSorted());
	}

	private Godot.Collections.Dictionary BuildHarvestScorePercentByShapeKey()
	{
		var gd = new Godot.Collections.Dictionary();
		foreach (Variant vk in _sheet.GetShapeIslandGlobalKeysSorted())
		{
			var key = vk.AsInt32();
			_scoreByShapeKey.TryGetValue(key, out var cumulative);
			var maxV = _sheet.GetShapeInitialMaxIntegrity(key);
			var pct = maxV <= 0
				? 0f
				: Mathf.Clamp(100f * cumulative / maxV, 0f, 100f);
			gd[key] = pct;
		}

		return gd;
	}
}
