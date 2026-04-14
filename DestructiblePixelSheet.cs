using Godot;
using System.Collections.Generic;

// Per-cell integrity 0–100 across stacked z-layers. Lower layers are damaged only after upper layers at that pixel are depleted.
// Visual: all layers with integrity are alpha-composited (back to front); gameplay uses the same integrity data as before.
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

	// Optional per-layer bitmap masks: red channel > 0.5 marks shape pixels used for harvest scoring.
	[Export]
	public Texture2D[] LayerShapeMasks { get; set; } = new Texture2D[0];

	// Display names per layer: each entry is a PackedStringArray (or string array) — index 0 = local shape id 1, etc. (UI only.)
	[Export]
	public Godot.Collections.Array LayerShapeDisplayNames { get; set; } = new Godot.Collections.Array();

	// RGB brightness at 0 integrity (1 = no darkening, 0 = black). Render only.
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float IntegrityVisualDarkenMin { get; set; } = 0.38f;

	// Per-layer visual transparency (0 = opaque, 1 = fully transparent). Render only; does not change integrity or scoring.
	private float[] _layerVisualTransparency = System.Array.Empty<float>();

	[Export]
	public float[] LayerVisualTransparency
	{
		get => _layerVisualTransparency;
		set
		{
			_layerVisualTransparency = value ?? System.Array.Empty<float>();
			RequestRebuildForVisualTransparency();
		}
	}

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
	private bool[] _shapeMaskMembership = null!;
	// Per-pixel local connected-component id within layer (0 = non-shape); see EncodeShapeGlobalKey.
	private int[] _shapeIslandLocalId = null!;
	private readonly Dictionary<int, int> _shapeMaxIntegrityByKey = new();
	private readonly Dictionary<int, int> _shapeDamageLostByKey = new();
	public const int ShapeKeyLayerStride = 100000;
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

	[Signal]
	public delegate void PointsAwardedEventHandler(int totalPoints, Godot.Collections.Array blobPayloads);

	[Signal]
	public delegate void ShapeDamageStatsChangedEventHandler(Godot.Collections.Dictionary damagePercentByShapeKey);

	public static int EncodeShapeGlobalKey(int layer, int localComponentId)
	{
		return layer * ShapeKeyLayerStride + localComponentId;
	}

	/// <summary>Human-readable name for a shape global key, or fallback "L{layer} shape {id}".</summary>
	public string GetShapeDisplayName(int globalShapeKey)
	{
		var layer = globalShapeKey / ShapeKeyLayerStride;
		var localId = globalShapeKey % ShapeKeyLayerStride;
		if (localId <= 0 || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return $"Shape {globalShapeKey}";
		}

		var index = localId - 1;
		if (LayerShapeDisplayNames == null || layer < 0 || layer >= LayerShapeDisplayNames.Count)
		{
			return ShapeNameFallback(layer, localId);
		}

		var inner = LayerShapeDisplayNames[layer];
		string? n = null;
		if (inner.VariantType == Variant.Type.PackedStringArray)
		{
			var strings = inner.AsStringArray();
			if (strings != null && index >= 0 && index < strings.Length)
			{
				n = strings[index];
			}
		}
		else if (inner.VariantType == Variant.Type.Array)
		{
			var arr = inner.AsGodotArray<Variant>();
			if (index >= 0 && index < arr.Count)
			{
				n = arr[index].AsString();
			}
		}

		return string.IsNullOrWhiteSpace(n) ? ShapeNameFallback(layer, localId) : n!;
	}

	private static string ShapeNameFallback(int layer, int localId)
	{
		return $"Layer {layer} Shape {localId}";
	}

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
		BuildShapeMaskMembership();
		RecomputeShapeMaxIntegrity();
		EmitShapeDamageStatsChanged();

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

		var nonShapeIntegrityTotal = 0;
		foreach (var p in islandCells)
		{
			var integrity = GetLayerIntegrity(p.X, p.Y, layer);
			if (integrity <= IntegrityMin)
			{
				continue;
			}

			if (!IsShapeCell(p.X, p.Y, layer))
			{
				nonShapeIntegrityTotal += integrity;
			}
		}

		var subIslands = FindShapeSubIslands(islandCells, layer);
		var shapeIntTotal = 0;
		foreach (var blob in subIslands)
		{
			foreach (var p in blob)
			{
				shapeIntTotal += GetLayerIntegrity(p.X, p.Y, layer);
			}
		}

		var denom = Mathf.Max(1, shapeIntTotal);
		var mergedByKey = new Dictionary<int, int>();
		foreach (var blob in subIslands)
		{
			var shapeIntB = 0;
			foreach (var p in blob)
			{
				shapeIntB += GetLayerIntegrity(p.X, p.Y, layer);
			}

			var pointsB = Mathf.Max(0, shapeIntB - nonShapeIntegrityTotal * shapeIntB / denom);
			if (pointsB <= 0)
			{
				continue;
			}

			var first = blob[0];
			var localId = _shapeIslandLocalId[LayeredIndex(first.X, first.Y, layer)];
			if (localId <= 0)
			{
				continue;
			}

			var shapeKey = EncodeShapeGlobalKey(layer, localId);
			mergedByKey.TryGetValue(shapeKey, out var prev);
			mergedByKey[shapeKey] = prev + pointsB;
		}

		var payloads = new Godot.Collections.Array();
		var totalAwarded = 0;
		foreach (var kv in mergedByKey)
		{
			var pts = kv.Value;
			totalAwarded += pts;
			var d = new Godot.Collections.Dictionary
			{
				["ShapeKey"] = kv.Key,
				["Points"] = pts,
			};
			payloads.Add(d);
		}

		if (totalAwarded > 0 && payloads.Count > 0)
		{
			EmitSignal(SignalName.PointsAwarded, totalAwarded, payloads);
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

	private List<List<Vector2I>> FindShapeSubIslands(List<Vector2I> islandCells, int layer)
	{
		var width = SheetSize.X;
		var height = SheetSize.Y;
		var inIsland = new bool[width * height];
		foreach (var p in islandCells)
		{
			inIsland[p.Y * width + p.X] = true;
		}

		var visited = new bool[width * height];
		var result = new List<List<Vector2I>>();
		foreach (var start in islandCells)
		{
			var si = start.Y * width + start.X;
			if (visited[si])
			{
				continue;
			}

			if (!IsShapeCell(start.X, start.Y, layer) || GetLayerIntegrity(start.X, start.Y, layer) <= IntegrityMin)
			{
				continue;
			}

			var blob = new List<Vector2I>();
			var stack = new Stack<Vector2I>();
			stack.Push(start);
			while (stack.Count > 0)
			{
				var c = stack.Pop();
				var vi = c.Y * width + c.X;
				if (visited[vi] || !inIsland[vi])
				{
					continue;
				}

				if (!IsShapeCell(c.X, c.Y, layer) || GetLayerIntegrity(c.X, c.Y, layer) <= IntegrityMin)
				{
					continue;
				}

				visited[vi] = true;
				blob.Add(c);
				var x = c.X;
				var y = c.Y;
				stack.Push(new Vector2I(x, y - 1));
				stack.Push(new Vector2I(x - 1, y));
				stack.Push(new Vector2I(x + 1, y));
				stack.Push(new Vector2I(x, y + 1));
			}

			if (blob.Count > 0)
			{
				result.Add(blob);
			}
		}

		return result;
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
			_image.SetPixel(p.X, p.Y, BorderTouchesShapePixel(p.X, p.Y, layer) ? Colors.Green : Colors.Blue);
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

		// Gaussian drill falloff within the disk: peak at center, softer at edge.
		var sigma = Mathf.Max(1e-4f, r / 3f);
		var twoSigmaSq = 2f * sigma * sigma;
		var weightSum = 0f;
		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var dx = x - cx;
				var dy = y - cy;
				var d2 = dx * (float)dx + dy * (float)dy;
				if (d2 <= r2)
				{
					weightSum += Mathf.Exp(-d2 / twoSigmaSq);
				}
			}
		}

		if (weightSum <= 1e-6f)
		{
			return;
		}

		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var dx = x - cx;
				var dy = y - cy;
				var d2 = dx * (float)dx + dy * (float)dy;
				if (d2 <= r2)
				{
					var w = Mathf.Exp(-d2 / twoSigmaSq);
					ApplyDamage(x, y, totalAmount * (w / weightSum));
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

		var damageChanged = false;
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
			var integrityLost = current - next;
			remaining -= integrityLost;
			if (integrityLost > 0 && IsShapeCell(x, y, layer))
			{
				var localId = _shapeIslandLocalId[LayeredIndex(x, y, layer)];
				if (localId > 0)
				{
					var gk = EncodeShapeGlobalKey(layer, localId);
					_shapeDamageLostByKey.TryGetValue(gk, out var prevLost);
					_shapeDamageLostByKey[gk] = prevLost + integrityLost;
					damageChanged = true;
				}
			}

			changed = true;
		}

		if (!changed)
		{
			return;
		}

		SyncPixel(x, y);
		_textureDirty = true;
		if (damageChanged)
		{
			EmitShapeDamageStatsChanged();
		}
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

			var img = CreateSizedImageCopy(tex);
			if (img is null)
			{
				GD.PushWarning($"{nameof(DestructiblePixelSheet)}: Layer texture image unreadable at layer {layer}; using fallback color.");
				for (var i = 0; i < stride; i++)
				{
					_layerBaseColors[layer * stride + i] = fallback;
				}

				continue;
			}

			for (var y = 0; y < SheetSize.Y; y++)
			{
				for (var x = 0; x < SheetSize.X; x++)
				{
					_layerBaseColors[layer * stride + y * SheetSize.X + x] = img.GetPixel(x, y);
				}
			}
		}
	}

	private void BuildShapeMaskMembership()
	{
		var stride = SheetSize.X * SheetSize.Y;
		var w = SheetSize.X;
		var h = SheetSize.Y;
		_shapeMaskMembership = new bool[stride * _resolvedLayerCount];
		_shapeIslandLocalId = new int[stride * _resolvedLayerCount];

		for (var layer = 0; layer < _resolvedLayerCount; layer++)
		{
			var tex = layer < LayerShapeMasks.Length ? LayerShapeMasks[layer] : null;
			if (tex is null)
			{
				continue;
			}

			var img = CreateSizedImageCopy(tex);
			if (img is null)
			{
				GD.PushWarning($"{nameof(DestructiblePixelSheet)}: Shape mask image unreadable at layer {layer}; defaulting to non-shape.");
				continue;
			}

			var baseIndex = layer * stride;
			for (var y = 0; y < h; y++)
			{
				for (var x = 0; x < w; x++)
				{
					var c = img.GetPixel(x, y);
					var isShape = c.R > 0.5f;
					var idx = y * w + x;
					_shapeMaskMembership[baseIndex + idx] = isShape;
				}
			}

			var nextLocalId = 0;
			for (var y = 0; y < h; y++)
			{
				for (var x = 0; x < w; x++)
				{
					var idx = y * w + x;
					if (!_shapeMaskMembership[baseIndex + idx] || _shapeIslandLocalId[baseIndex + idx] != 0)
					{
						continue;
					}

					nextLocalId++;
					var stack = new Stack<Vector2I>();
					stack.Push(new Vector2I(x, y));
					while (stack.Count > 0)
					{
						var c = stack.Pop();
						var cx = c.X;
						var cy = c.Y;
						var ci = cy * w + cx;
						if ((uint)cx >= (uint)w || (uint)cy >= (uint)h)
						{
							continue;
						}

						if (!_shapeMaskMembership[baseIndex + ci] || _shapeIslandLocalId[baseIndex + ci] != 0)
						{
							continue;
						}

						_shapeIslandLocalId[baseIndex + ci] = nextLocalId;
						stack.Push(new Vector2I(cx, cy - 1));
						stack.Push(new Vector2I(cx - 1, cy));
						stack.Push(new Vector2I(cx + 1, cy));
						stack.Push(new Vector2I(cx, cy + 1));
					}
				}
			}
		}
	}

	private Image? CreateSizedImageCopy(Texture2D tex)
	{
		var source = tex.GetImage();
		if (source is null)
		{
			return null;
		}

		var copy = source.Duplicate() as Image;
		if (copy is null)
		{
			return null;
		}

		copy.Resize(SheetSize.X, SheetSize.Y, Image.Interpolation.Nearest);
		return copy;
	}

	private void RecomputeShapeMaxIntegrity()
	{
		_shapeMaxIntegrityByKey.Clear();
		_shapeDamageLostByKey.Clear();
		var stride = SheetSize.X * SheetSize.Y;
		for (var layer = 0; layer < _resolvedLayerCount; layer++)
		{
			var layerMax = GetLayerMaxHealth(layer);
			var baseIndex = layer * stride;
			for (var i = 0; i < stride; i++)
			{
				var localId = _shapeIslandLocalId[baseIndex + i];
				if (localId <= 0)
				{
					continue;
				}

				var key = EncodeShapeGlobalKey(layer, localId);
				if (!_shapeMaxIntegrityByKey.TryGetValue(key, out var acc))
				{
					acc = 0;
				}

				_shapeMaxIntegrityByKey[key] = acc + layerMax;
			}
		}
	}

	private Godot.Collections.Dictionary BuildDamagePercentByShapeKey()
	{
		var dict = new Godot.Collections.Dictionary();
		foreach (var kv in _shapeMaxIntegrityByKey)
		{
			var key = kv.Key;
			var maxV = kv.Value;
			var lost = _shapeDamageLostByKey.TryGetValue(key, out var l) ? l : 0;
			var pct = maxV <= 0 ? 0f : Mathf.Clamp(100f * lost / maxV, 0f, 100f);
			dict[key] = pct;
		}

		return dict;
	}

	private void EmitShapeDamageStatsChanged()
	{
		EmitSignal(SignalName.ShapeDamageStatsChanged, BuildDamagePercentByShapeKey());
	}

	/// <summary>Initial max integrity for the shape (sum of layer max health over masked cells). Zero if unknown.</summary>
	public int GetShapeInitialMaxIntegrity(int globalShapeKey)
	{
		return _shapeMaxIntegrityByKey.TryGetValue(globalShapeKey, out var v) ? v : 0;
	}

	/// <summary>All mask-defined shape islands (global keys), sorted for stable UI row order.</summary>
	public Godot.Collections.Array GetShapeIslandGlobalKeysSorted()
	{
		var keys = new List<int>(_shapeMaxIntegrityByKey.Keys);
		keys.Sort();
		var arr = new Godot.Collections.Array();
		foreach (var k in keys)
		{
			arr.Add(k);
		}

		return arr;
	}

	private int LayeredIndex(int x, int y, int layer)
	{
		var stride = SheetSize.X * SheetSize.Y;
		return layer * stride + y * SheetSize.X + x;
	}

	private bool IsShapeCell(int x, int y, int layer)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return false;
		}

		return _shapeMaskMembership[LayeredIndex(x, y, layer)];
	}

	private bool BorderTouchesShapePixel(int x, int y, int layer)
	{
		return IsLiveShapePixel(x, y - 1, layer)
			|| IsLiveShapePixel(x - 1, y, layer)
			|| IsLiveShapePixel(x + 1, y, layer)
			|| IsLiveShapePixel(x, y + 1, layer);
	}

	private bool IsLiveShapePixel(int x, int y, int layer)
	{
		if ((uint)x >= (uint)SheetSize.X || (uint)y >= (uint)SheetSize.Y || (uint)layer >= (uint)_resolvedLayerCount)
		{
			return false;
		}

		return GetLayerIntegrity(x, y, layer) > IntegrityMin && IsShapeCell(x, y, layer);
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

		// Composite all layers that still have integrity: back (highest index) to front (layer 0).
		// Premultiplied alpha accumulation: dst = src_p + dst * (1 - src_a).
		float pr = 0f;
		float pg = 0f;
		float pb = 0f;
		float pa = 0f;

		for (var li = _resolvedLayerCount - 1; li >= 0; li--)
		{
			var v = GetLayerIntegrity(x, y, li);
			if (v <= IntegrityMin)
			{
				continue;
			}

			var c = GetLayerBaseColor(x, y, li);
			var intFrac = Mathf.Clamp(v, IntegrityMin, GetLayerMaxHealth(li)) / (float)GetLayerMaxHealth(li);
			var rgbDarken = Mathf.Lerp(Mathf.Clamp(IntegrityVisualDarkenMin, 0f, 1f), 1f, intFrac);
			var straightA = c.A * intFrac * GetLayerVisualOpaqueFactor(li);
			straightA = Mathf.Clamp(straightA, 0f, 1f);
			if (straightA <= 1e-6f)
			{
				continue;
			}

			var sr = c.R * rgbDarken * straightA;
			var sg = c.G * rgbDarken * straightA;
			var sb = c.B * rgbDarken * straightA;
			var oneMinus = 1f - straightA;
			pr = sr + pr * oneMinus;
			pg = sg + pg * oneMinus;
			pb = sb + pb * oneMinus;
			pa = straightA + pa * oneMinus;
		}

		if (pa <= 1e-6f)
		{
			_image.SetPixel(x, y, Colors.Transparent);
			return;
		}

		_image.SetPixel(x, y, new Color(pr / pa, pg / pa, pb / pa, pa));
	}

	/// <summary>Multiplier applied to alpha after integrity fade: 1 - transparency.</summary>
	private float GetLayerVisualOpaqueFactor(int layer)
	{
		if ((uint)layer >= (uint)_resolvedLayerCount)
		{
			return 1f;
		}

		if (layer >= _layerVisualTransparency.Length)
		{
			return 1f;
		}

		var t = Mathf.Clamp(_layerVisualTransparency[layer], 0f, 1f);
		return 1f - t;
	}

	private void RequestRebuildForVisualTransparency()
	{
		if (_image is null)
		{
			return;
		}

		RebuildImageFromIntegrity();
		_textureDirty = true;
		FlushTextureIfDirty();
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
