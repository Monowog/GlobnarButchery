using Godot;

// One row per mask-defined organ; destroyed organs show red name only (no score/damage).
public partial class ScoreUi : CanvasLayer
{
	[Export]
	public Color DestroyedOrganNameColor { get; set; } = new Color(0.86f, 0.18f, 0.18f);

	[Export]
	public NodePath ControllerPath { get; set; } = new("..");

	[Export]
	public NodePath SheetPath { get; set; } = new("../DestructiblePixelSheet");

	[Export]
	public NodePath RowsHostPath { get; set; } = new("MarginContainer/ScrollContainer/RowsHost");

	private MainSceneController _controller = null!;
	private DestructiblePixelSheet _sheet = null!;
	private VBoxContainer _rowsHost = null!;

	public override void _Ready()
	{
		_rowsHost = GetNode<VBoxContainer>(RowsHostPath);
		_controller = GetNode<MainSceneController>(ControllerPath);
		_sheet = GetNode<DestructiblePixelSheet>(SheetPath);
		_controller.ScoreChanged += OnScoreChanged;
	}

	public override void _ExitTree()
	{
		if (IsInstanceValid(_controller))
		{
			_controller.ScoreChanged -= OnScoreChanged;
		}
	}

	private void OnScoreChanged(Godot.Collections.Dictionary scoreByShapeKey, Godot.Collections.Dictionary damagePercentByShapeKey, Godot.Collections.Array organKeysSorted, Godot.Collections.Dictionary organDestroyedUiFlags)
	{
		foreach (var child in _rowsHost.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Variant vk in organKeysSorted)
		{
			var key = vk.AsInt32();
			var destroyed = organDestroyedUiFlags.ContainsKey(key) && organDestroyedUiFlags[key].AsBool();

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 12);
			var nameRtl = new RichTextLabel();
			nameRtl.FitContent = true;
			nameRtl.ScrollActive = false;
			nameRtl.AutowrapMode = TextServer.AutowrapMode.Off;
			nameRtl.BbcodeEnabled = true;
			nameRtl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
			var displayName = _sheet.GetShapeDisplayName(key);
			var safe = EscapeBbcode(displayName);
			nameRtl.Text = destroyed
				? $"[color={DestroyedOrganNameColor.ToHtml(false)}]{safe}[/color]"
				: safe;
			row.AddChild(nameRtl);

			if (!destroyed)
			{
				var score = scoreByShapeKey.ContainsKey(key) ? scoreByShapeKey[key].AsSingle() : 0f;
				if (!Mathf.IsFinite(score))
				{
					score = 0f;
				}

				var dmg = damagePercentByShapeKey.ContainsKey(key) ? damagePercentByShapeKey[key].AsSingle() : 0f;
				var scoreLabel = new Label();
				scoreLabel.Text = $"Score: {score:F1}%";
				scoreLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
				var dmgLabel = new Label();
				dmgLabel.Text = $"Damage: {dmg:0.0}%";
				dmgLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
				row.AddChild(scoreLabel);
				row.AddChild(dmgLabel);
			}

			_rowsHost.AddChild(row);
		}
	}

	private static string EscapeBbcode(string text)
	{
		return text.Replace("[", "[lb]").Replace("]", "[rb]");
	}
}
