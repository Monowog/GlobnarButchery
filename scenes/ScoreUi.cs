using Godot;
using System.Collections.Generic;

// One row per mask-defined shape island: score and damage only (no total).
public partial class ScoreUi : CanvasLayer
{
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

	private void OnScoreChanged(Godot.Collections.Dictionary scoreByShapeKey, Godot.Collections.Dictionary damagePercentByShapeKey, Godot.Collections.Array shapeIslandKeysSorted)
	{
		foreach (var child in _rowsHost.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Variant vk in shapeIslandKeysSorted)
		{
			var key = vk.AsInt32();
			var score = scoreByShapeKey.ContainsKey(key) ? scoreByShapeKey[key].AsSingle() : 0f;
			if (!Mathf.IsFinite(score))
			{
				score = 0f;
			}

			var dmg = damagePercentByShapeKey.ContainsKey(key) ? damagePercentByShapeKey[key].AsSingle() : 0f;

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 12);
			var nameLabel = new Label();
			nameLabel.Text = _sheet.GetShapeDisplayName(key);
			var scoreLabel = new Label();
			scoreLabel.Text = $"Score: {score:F1}%";
			var dmgLabel = new Label();
			dmgLabel.Text = $"Damage: {dmg:0.0}%";
			row.AddChild(nameLabel);
			row.AddChild(scoreLabel);
			row.AddChild(dmgLabel);
			_rowsHost.AddChild(row);
		}
	}
}
