using Godot;

// Shows one of two textures depending on DestructiblePixelSheet.WaveClickMode (left-click: brush vs wave).
public partial class ClickModeIndicator : CanvasLayer
{
	[Export]
	public NodePath SheetPath { get; set; } = new("../DestructiblePixelSheet");

	[Export]
	public NodePath IconPath { get; set; } = new("MarginContainer/ModeIcon");

	[Export]
	public Texture2D? BrushModeTexture { get; set; }

	[Export]
	public Texture2D? WaveModeTexture { get; set; }

	private DestructiblePixelSheet _sheet = null!;
	private TextureRect _icon = null!;

	public override void _Ready()
	{
		_sheet = GetNode<DestructiblePixelSheet>(SheetPath);
		_icon = GetNode<TextureRect>(IconPath);
		_sheet.WaveClickModeChanged += OnWaveClickModeChanged;
		ApplyMode(_sheet.WaveClickMode);
	}

	public override void _ExitTree()
	{
		if (IsInstanceValid(_sheet))
		{
			_sheet.WaveClickModeChanged -= OnWaveClickModeChanged;
		}
	}

	private void OnWaveClickModeChanged(bool waveClickMode)
	{
		ApplyMode(waveClickMode);
	}

	private void ApplyMode(bool waveClickMode)
	{
		_icon.Texture = waveClickMode ? WaveModeTexture : BrushModeTexture;
	}
}
