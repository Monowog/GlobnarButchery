using Godot;
using System.Collections.Generic;

// Per-cell integrity 0–100 across stacked z-layers. Lower layers are damaged only after upper layers at that pixel are depleted.
// Normal mode: disk brush + drag (DPS and brush radius scale with cursor px/s). Wave mode: Gaussian radial wave.
public partial class DestructiblePixelSheet : Sprite2D
{
	private const int IntegrityMin = 0;
	private const int DefaultLayerHealth = 100;
	private const float BaseClickDamage = 30f;
	private const float BaseDragDamagePerSecond = 100f;
	private const float MaxDamageRadius = 256f;
	private const float MaxWaveDistanceCap = 512f;

	[Export]
	public float Strength { get; set; } = 1f;

	[Export]
	public float DamageRadius { get; set; } = 3f;

	[Export]
	public bool WaveClickMode { get; set; }

	[Export]
	public float WaveMaxDistance { get; set; } = 128f;

	[Export]
	public float WaveSpeed { get; set; } = 200f;

	[Export]
	public float WavePeakDamage { get; set; } = 40f;

	[Export]
	public Vector2I SheetSize { get; set; } = new(1024, 768);

	[Export]
	public int LayerCount { get; set; } = 3;

	[Export]
	public int[] LayerHealth { get; set; } = new int[0];

	[Export]
	public Texture2D[] LayerTextures { get; set; } = new Texture2D[0];

	// Viewport px/s: drag DPS uses 1× scale here; brush radius lerps linearly from DamageRadius down to 1 here.
	[Export]
	public float DragVelocityReference { get; set; } = 400f;

	[Export]
	public float DragVelocityMaxScale { get; set; } = 10f;

	private Image _image = null!;
	private ImageTexture _texture = null!;
	private int[] _integrity = null!;
	private int[] _layerMaxHealth = null!;
	private Color[] _layerBaseColors = null!;
	private int _resolvedLayerCount;
	private Vector2I? _lastHeldCell;
	private bool _textureDirty;
	private readonly List<Vector2I> _hoverBorderPixels = new();
	private Vector2I _hoverSourceCell;
	private int _hoverSourceLayer = -1;
	private bool _hoverActive;
	private Vector2 _lastDragMouseCanvas;
	private bool _dragMouseSampleValid;

	private bool _waveRunning;
	private Vector2I _waveOrigin;
	private float _waveOuter = -1f;

	public override void _Ready()
	{
		SetProcess(true);
		TextureFilter = TextureFilterEnum.Nearest;
		Centered = false;

		var w = Mathf.Max(1, SheetSize.X);
		var h = Mathf.Max(1, SheetSize.Y);
		SheetSize = new Vector2I(w, h);
		_resolvedLayerCount = Mathf.Max(1, LayerCount);
		LayerCount = _resolvedLayerCount;

		_integrity = new int[SheetSize.X * SheetSize.Y * _resolvedLayerCount];
		_layerMaxHealth = new int[_resolvedLayerCount];
		var stride = SheetSize.X * SheetSize.Y;
		for (var layer = 0; layer < _resolvedLayerCount; layer++)
		{
			var maxHealth = layer < LayerHealth.Length ? LayerHealth[layer] : DefaultLayerHealth;
			maxHealth = Mathf.Max(1, maxHealth);
			_layerMaxHealth[layer] = maxHealth;
			var baseIndex = layer * stride;
			for (var i = 0; i < stride; i++)
			{
				_integrity[baseIndex + i] = maxHealth;
			}
		}
		BuildLayerRasterColors();

		_image = Image.CreateEmpty(SheetSize.X, SheetSize.Y, false, Image.Format.Rgba8);
		RebuildImageFromIntegrity();

		_texture = ImageTexture.CreateFromImage(_image);
		Texture = _texture;
	}

	public override void _Process(double delta)
	{
		if (!Input.IsMouseButtonPressed(MouseButton.Left))
		{
			UpdateHoverBorderHighlight();
		}
		else
		{
			ClearHoverBorderHighlight();
		}

		if (_waveRunning)
		{
			StepWave((float)delta);
			return;
		}

		if (WaveClickMode)
		{
			return;
		}

		if (!Input.IsMouseButtonPressed(MouseButton.Left) || !_lastHeldCell.HasValue)
		{
			return;
		}

		var s = Mathf.Max(0f, Strength);
		if (s <= 0f)
		{
			return;
		}

		var mouseCanvas = GetViewport().GetMousePosition();
		if (!TryCanvasToCell(mouseCanvas, out var currentCell))
		{
			return;
		}

		var dt = (float)delta;
		if (dt <= 0f)
		{
			return;
		}

		var refSpeed = Mathf.Max(1e-4f, DragVelocityReference);
		var speed = 0f;
		float velScale = 1f;
		if (_dragMouseSampleValid)
		{
			var disp = mouseCanvas - _lastDragMouseCanvas;
			speed = disp.Length() / dt;
			var ratio = speed / refSpeed;
			velScale = Mathf.Min(Mathf.Max(0f, ratio), DragVelocityMaxScale);
		}

		_lastDragMouseCanvas = mouseCanvas;

		var pool = BaseDragDamagePerSecond * s * dt * velScale;
		if (pool <= 0f)
		{
			_lastHeldCell = currentCell;
			return;
		}

		var cap = SheetSize.X + SheetSize.Y + 4;
		Span<Vector2I> scratch = stackalloc Vector2I[cap];
		var nLine = FillBresenhamInclusive(_lastHeldCell.Value, currentCell, scratch);
		if (nLine <= 0)
		{
			_lastHeldCell = currentCell;
			return;
		}

		var brushR = BrushRadiusForSpeed(speed);
		var perDisk = pool / nLine;
		for (var i = 0; i < nLine; i++)
		{
			DamageDisk(scratch[i], perDisk, brushR);
		}

		_lastHeldCell = currentCell;
		FlushTextureIfDirty();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed)
		{
			ClearHoverBorderHighlight();
			var popped = false;
			if (TryCanvasToCell(mb.Position, out var popCell) && TryGetTopNonZeroLayer(popCell.X, popCell.Y, out var popLayer))
			{
				popped = PopIslandAt(popCell.X, popCell.Y, popLayer);
			}

			if (!popped)
			{
				WaveClickMode = !WaveClickMode;
			}

			FlushTextureIfDirty();

			_lastHeldCell = null;
			_dragMouseSampleValid = false;
			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event is InputEventMouseButton mb2 && mb2.ButtonIndex == MouseButton.Left)
		{
			if (mb2.Pressed)
			{
				ClearHoverBorderHighlight();
				if (!TryCanvasToCell(mb2.Position, out var pressCell))
				{
					return;
				}

				if (WaveClickMode)
				{
					_lastHeldCell = null;
					if (!_waveRunning)
					{
						var s = Mathf.Max(0f, Strength);
						if (s > 0f && WaveSpeed > 0f && GetEffectiveWaveMax() > 0f)
						{
							_waveOrigin = pressCell;
							_waveOuter = -1f;
							_waveRunning = true;
						}
					}

					GetViewport().SetInputAsHandled();
					return;
				}

				var sBrush = Mathf.Max(0f, Strength);
				if (sBrush > 0f)
				{
					DamageDisk(pressCell, BaseClickDamage * sBrush, BrushRadiusForSpeed(0f));
				}

				_lastHeldCell = pressCell;
				_lastDragMouseCanvas = mb2.Position;
				_dragMouseSampleValid = true;
				FlushTextureIfDirty();
				GetViewport().SetInputAsHandled();
				return;
			}

			if (_lastHeldCell.HasValue)
			{
				_lastHeldCell = null;
				_dragMouseSampleValid = false;
				GetViewport().SetInputAsHandled();
			}

			return;
		}
	}

	private bool PopIslandAt(int startX, int startY, int layer)
	{
		if ((uint)startX >= (uint)SheetSize.X || (uint)startY >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return false;
		}

		if (GetLayerIntegrity(startX, startY, layer) <= IntegrityMin)
		{
			return false;
		}

		AnalyzeIsland(startX, startY, layer, out var islandCells, out _, out var touchesEdge);

		if (touchesEdge || islandCells.Count == 0)
		{
			return false;
		}

		foreach (var p in islandCells)
		{
			SetLayerIntegrity(p.X, p.Y, layer, IntegrityMin);
			SyncPixel(p.X, p.Y);
		}

		if (islandCells.Count > 0)
		{
			_textureDirty = true;
		}

		return true;
	}

	private void AnalyzeIsland(int startX, int startY, int layer, out List<Vector2I> islandCells, out List<Vector2I> borderZeroCells, out bool touchesEdge)
	{
		var width = SheetSize.X;
		var height = SheetSize.Y;
		islandCells = new List<Vector2I>();
		borderZeroCells = new List<Vector2I>();
		touchesEdge = false;

		if ((uint)startX >= (uint)width || (uint)startY >= (uint)height || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return;
		}

		if (GetLayerIntegrity(startX, startY, layer) <= IntegrityMin)
		{
			return;
		}

		var visited = new bool[width * height];
		var borderVisited = new bool[width * height];
		var borders = borderZeroCells;
		var stack = new Stack<Vector2I>();
		stack.Push(new Vector2I(startX, startY));

		while (stack.Count > 0)
		{
			var c = stack.Pop();
			var x = c.X;
			var y = c.Y;
			if ((uint)x >= (uint)width || (uint)y >= (uint)height)
			{
				continue;
			}

			var vi = y * width + x;
			if (visited[vi])
			{
				continue;
			}

			visited[vi] = true;
			if (GetLayerIntegrity(x, y, layer) <= IntegrityMin)
			{
				continue;
			}

			islandCells.Add(c);
			if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
			{
				touchesEdge = true;
			}

			// 4-way island connectivity.
			stack.Push(new Vector2I(x, y - 1));
			stack.Push(new Vector2I(x - 1, y));
			stack.Push(new Vector2I(x + 1, y));
			stack.Push(new Vector2I(x, y + 1));

			// 4-way nearest zero-integrity border around this island (same layer).
			TryAddBorderZero(x, y - 1);
			TryAddBorderZero(x - 1, y);
			TryAddBorderZero(x + 1, y);
			TryAddBorderZero(x, y + 1);
		}

		void TryAddBorderZero(int bx, int by)
		{
			if ((uint)bx >= (uint)width || (uint)by >= (uint)height)
			{
				return;
			}

			var bi = by * width + bx;
			if (borderVisited[bi] || visited[bi])
			{
				return;
			}

			if (GetLayerIntegrity(bx, by, layer) > IntegrityMin)
			{
				return;
			}

			borderVisited[bi] = true;
			borders.Add(new Vector2I(bx, by));
		}
	}

	private void UpdateHoverBorderHighlight()
	{
		if (!TryCanvasToCell(GetViewport().GetMousePosition(), out var hoverCell))
		{
			ClearHoverBorderHighlight();
			FlushTextureIfDirty();
			return;
		}

		if (!TryGetTopNonZeroLayer(hoverCell.X, hoverCell.Y, out var layer))
		{
			ClearHoverBorderHighlight();
			FlushTextureIfDirty();
			return;
		}

		if (_hoverActive && _hoverSourceLayer == layer && _hoverSourceCell == hoverCell)
		{
			return;
		}

		ClearHoverBorderHighlight();
		AnalyzeIsland(hoverCell.X, hoverCell.Y, layer, out var islandCells, out var borderCells, out var touchesEdge);
		if (touchesEdge || islandCells.Count == 0 || borderCells.Count == 0)
		{
			FlushTextureIfDirty();
			return;
		}

		_hoverBorderPixels.AddRange(borderCells);
		_hoverSourceCell = hoverCell;
		_hoverSourceLayer = layer;
		_hoverActive = true;
		foreach (var p in _hoverBorderPixels)
		{
			_image.SetPixel(p.X, p.Y, Colors.Blue);
		}

		_textureDirty = true;
		FlushTextureIfDirty();
	}

	private void ClearHoverBorderHighlight()
	{
		if (!_hoverActive)
		{
			return;
		}

		foreach (var p in _hoverBorderPixels)
		{
			SyncPixel(p.X, p.Y);
		}

		_hoverBorderPixels.Clear();
		_hoverSourceLayer = -1;
		_hoverActive = false;
		_textureDirty = true;
	}

	private float GetEffectiveWaveMax()
	{
		return Mathf.Min(Mathf.Max(1e-4f, WaveMaxDistance), MaxWaveDistanceCap);
	}

	private void StepWave(float delta)
	{
		var maxR = GetEffectiveWaveMax();
		var speed = Mathf.Max(0f, WaveSpeed);
		if (speed <= 0f)
		{
			_waveRunning = false;
			_waveOuter = -1f;
			FlushTextureIfDirty();
			return;
		}

		var prevOuter = _waveOuter;
		var baseR = prevOuter < 0f ? 0f : prevOuter;
		var nextR = Mathf.Min(maxR, baseR + speed * delta);

		var ox = _waveOrigin.X;
		var oy = _waveOrigin.Y;
		var ext = Mathf.CeilToInt(nextR);
		var x0 = Mathf.Max(0, ox - ext);
		var x1 = Mathf.Min(SheetSize.X - 1, ox + ext);
		var y0 = Mathf.Max(0, oy - ext);
		var y1 = Mathf.Min(SheetSize.Y - 1, oy + ext);

		var s = Mathf.Max(0f, Strength);
		var peak = Mathf.Max(0f, WavePeakDamage);
		// Gaussian envelope: peak at d=0, ~small at d≈maxR with sigma = maxR/3.
		var sigma = maxR / 3f;
		var twoSigmaSq = 2f * sigma * sigma;

		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var dx = x - ox;
				var dy = y - oy;
				var d = Mathf.Sqrt(dx * (float)dx + dy * (float)dy);
				if (d <= prevOuter || d > nextR)
				{
					continue;
				}

				var gaussian = Mathf.Exp(-(d * d) / twoSigmaSq);
				var amount = peak * s * gaussian;
				if (amount > 0f)
				{
					ApplyDamage(x, y, amount);
				}
			}
		}

		_waveOuter = nextR;
		FlushTextureIfDirty();

		if (nextR >= maxR - 1e-5f)
		{
			_waveRunning = false;
			_waveOuter = -1f;
		}
	}

	private float BrushRadiusForSpeed(float speedPixelsPerSecond)
	{
		var rMax = Mathf.Min(Mathf.Max(1f, DamageRadius), MaxDamageRadius);
		var refSpeed = Mathf.Max(1e-4f, DragVelocityReference);
		var t = Mathf.Clamp(speedPixelsPerSecond / refSpeed, 0f, 1f);
		return Mathf.Lerp(rMax, 1f, t);
	}

	private void DamageDisk(Vector2I center, float totalAmount, float brushRadiusPixels)
	{
		if (totalAmount <= 0f)
		{
			return;
		}

		var r = Mathf.Clamp(brushRadiusPixels, 1f, MaxDamageRadius);
		var r2 = r * r;
		var cx = center.X;
		var cy = center.Y;
		var ext = Mathf.CeilToInt(r);
		var x0 = Mathf.Max(0, cx - ext);
		var x1 = Mathf.Min(SheetSize.X - 1, cx + ext);
		var y0 = Mathf.Max(0, cy - ext);
		var y1 = Mathf.Min(SheetSize.Y - 1, cy + ext);

		var n = 0;
		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var dx = x - cx;
				var dy = y - cy;
				var d2 = dx * (float)dx + dy * (float)dy;
				if (d2 <= r2)
				{
					n++;
				}
			}
		}

		if (n <= 0)
		{
			return;
		}

		var perCell = totalAmount / n;
		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var dx = x - cx;
				var dy = y - cy;
				var d2 = dx * (float)dx + dy * (float)dy;
				if (d2 <= r2)
				{
					ApplyDamage(x, y, perCell);
				}
			}
		}
	}

	private void FlushTextureIfDirty()
	{
		if (!_textureDirty)
		{
			return;
		}

		_texture.SetImage(_image);
		_textureDirty = false;
	}

	private void ApplyDamage(int x, int y, float amount)
	{
		if (amount <= 0f || (uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y)
		{
			return;
		}

		var remaining = amount;
		var changed = false;
		for (var layer = 0; layer < _resolvedLayerCount && remaining > 0f; layer++)
		{
			var current = GetLayerIntegrity(x, y, layer);
			if (current <= IntegrityMin)
			{
				continue;
			}

			var next = Mathf.Max(IntegrityMin, Mathf.FloorToInt(current - remaining));
			if (next == current)
			{
				// Preserve prior behavior where any positive damage causes at least 1 integrity loss.
				next = Mathf.Max(IntegrityMin, current - 1);
			}

			SetLayerIntegrity(x, y, layer, next);
			remaining -= current - next;
			changed = true;
		}

		if (!changed)
		{
			return;
		}

		SyncPixel(x, y);
		_textureDirty = true;
	}

	private static int FillBresenhamInclusive(Vector2I from, Vector2I to, Span<Vector2I> dest)
	{
		var x0 = from.X;
		var y0 = from.Y;
		var x1 = to.X;
		var y1 = to.Y;
		var dx = Mathf.Abs(x1 - x0);
		var dy = -Mathf.Abs(y1 - y0);
		var sx = x0 < x1 ? 1 : -1;
		var sy = y0 < y1 ? 1 : -1;
		var err = dx + dy;
		var count = 0;

		while (true)
		{
			if (count >= dest.Length)
			{
				return count;
			}

			dest[count++] = new Vector2I(x0, y0);
			if (x0 == x1 && y0 == y1)
			{
				break;
			}

			var e2 = 2 * err;
			if (e2 >= dy)
			{
				err += dy;
				x0 += sx;
			}

			if (e2 <= dx)
			{
				err += dx;
				y0 += sy;
			}
		}

		return count;
	}

	private void BuildLayerRasterColors()
	{
		var stride = SheetSize.X * SheetSize.Y;
		_layerBaseColors = new Color[stride * _resolvedLayerCount];
		var fallback = new Color(0.85f, 0.85f, 0.85f, 1f);

		for (var layer = 0; layer < _resolvedLayerCount; layer++)
		{
			var tex = layer < LayerTextures.Length ? LayerTextures[layer] : null;
			if (tex is null)
			{
				for (var i = 0; i < stride; i++)
				{
					_layerBaseColors[layer * stride + i] = fallback;
				}

				continue;
			}

			var img = tex.GetImage();
			if (img is null)
			{
				for (var i = 0; i < stride; i++)
				{
					_layerBaseColors[layer * stride + i] = fallback;
				}

				continue;
			}

			img.Resize(SheetSize.X, SheetSize.Y, Image.Interpolation.Nearest);
			for (var y = 0; y < SheetSize.Y; y++)
			{
				for (var x = 0; x < SheetSize.X; x++)
				{
					_layerBaseColors[layer * stride + y * SheetSize.X + x] = img.GetPixel(x, y);
				}
			}
		}
	}

	private int LayeredIndex(int x, int y, int layer)
	{
		var stride = SheetSize.X * SheetSize.Y;
		return layer * stride + y * SheetSize.X + x;
	}

	private int GetLayerIntegrity(int x, int y, int layer)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return IntegrityMin;
		}

		return _integrity[LayeredIndex(x, y, layer)];
	}

	private Color GetLayerBaseColor(int x, int y, int layer)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return new Color(0.85f, 0.85f, 0.85f, 1f);
		}

		return _layerBaseColors[LayeredIndex(x, y, layer)];
	}

	private int GetLayerMaxHealth(int layer)
	{
		if ((uint)layer >= (uint)_resolvedLayerCount)
		{
			return DefaultLayerHealth;
		}

		return _layerMaxHealth[layer];
	}

	private void SetLayerIntegrity(int x, int y, int layer, int value)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return;
		}

		_integrity[LayeredIndex(x, y, layer)] = Mathf.Clamp(value, IntegrityMin, GetLayerMaxHealth(layer));
	}

	private bool TryGetVisibleLayer(int x, int y, out int layer, out int integrity)
	{
		for (var i = 0; i < _resolvedLayerCount; i++)
		{
			var v = GetLayerIntegrity(x, y, i);
			if (v > IntegrityMin)
			{
				layer = i;
				integrity = v;
				return true;
			}
		}

		layer = -1;
		integrity = IntegrityMin;
		return false;
	}

	private bool TryGetTopNonZeroLayer(int x, int y, out int layer)
	{
		for (var i = 0; i < _resolvedLayerCount; i++)
		{
			if (GetLayerIntegrity(x, y, i) > IntegrityMin)
			{
				layer = i;
				return true;
			}
		}

		layer = -1;
		return false;
	}

	private void SyncPixel(int x, int y)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y)
		{
			return;
		}

		if (!TryGetVisibleLayer(x, y, out var layer, out var integrity))
		{
			_image.SetPixel(x, y, Colors.Transparent);
			return;
		}

		var c = GetLayerBaseColor(x, y, layer);
		c.A *= Mathf.Clamp(integrity, IntegrityMin, GetLayerMaxHealth(layer)) / (float)GetLayerMaxHealth(layer);
		_image.SetPixel(x, y, c);
	}

	private void RebuildImageFromIntegrity()
	{
		for (var y = 0; y < SheetSize.Y; y++)
		{
			for (var x = 0; x < SheetSize.X; x++)
			{
				SyncPixel(x, y);
			}
		}
	}

	private bool TryCanvasToCell(Vector2 canvasPosition, out Vector2I cell)
	{
		var local = GetGlobalTransformWithCanvas().AffineInverse() * canvasPosition;
		var rect = new Rect2(Vector2.Zero, (Vector2)SheetSize);
		if (!rect.HasPoint(local))
		{
			cell = default;
			return false;
		}

		cell = new Vector2I(
			Mathf.Clamp(Mathf.FloorToInt(local.X), 0, SheetSize.X - 1),
			Mathf.Clamp(Mathf.FloorToInt(local.Y), 0, SheetSize.Y - 1));
		return true;
	}
}
