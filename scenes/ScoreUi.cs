using Godot;
using System.Collections.Generic;

// One row per mask-defined organ; destroyed organs show red name only (no score/damage).
// Organ thumbnails: cached Image/ImageTexture, updated on a fixed Hz timer (no per-refresh texture Dispose).
public partial class ScoreUi : CanvasLayer
{
	private const string OrganKeyMeta = "organ_key";
	private const string OrganThumbName = "OrganThumb";
	private const string OrganNameName = "OrganName";
	private const string ScoreLabelName = "ScoreLabel";
	private const string DamageLabelName = "DamageLabel";

	private sealed class OrganThumbRaster
	{
		public Image Img = null!;
		public ImageTexture Tex = null!;
		public int MinX;
		public int MinY;
		public int Width;
		public int Height;
	}

	[Export]
	public Color DestroyedOrganNameColor { get; set; } = new Color(0.86f, 0.18f, 0.18f);

	[Export]
	public Color HarvestedOrganTextColor { get; set; } = new Color(0.22f, 0.85f, 0.35f);

	[Export]
	public NodePath ControllerPath { get; set; } = new("..");

	[Export]
	public NodePath SheetPath { get; set; } = new("../DestructiblePixelSheet");

	[Export]
	public NodePath RowsHostPath { get; set; } = new("MarginContainer/ScrollContainer/RowsHost");

	[Export]
	public int OrganThumbnailMaxEdgePx { get; set; } = 48;

	[Export(PropertyHint.Range, "0.25,30,0.25")]
	public float OrganThumbnailRefreshHz { get; set; } = 2f;

	private MainSceneController _controller = null!;
	private DestructiblePixelSheet _sheet = null!;
	private VBoxContainer _rowsHost = null!;
	private Godot.Timer _thumbnailRefreshTimer = null!;
	private bool _organThumbnailsDirty;
	private bool _thumbForceFullNextRefresh;
	private readonly Dictionary<int, OrganThumbRaster> _thumbCache = new();
	private Godot.Collections.Dictionary _organHarvestedUiFlags = new();

	public override void _Ready()
	{
		_rowsHost = GetNode<VBoxContainer>(RowsHostPath);
		_controller = GetNode<MainSceneController>(ControllerPath);
		_sheet = GetNode<DestructiblePixelSheet>(SheetPath);
		_controller.ScoreChanged += OnScoreChanged;
		_controller.OrganDamageDisplayChanged += OnOrganDamageDisplayChanged;

		_thumbnailRefreshTimer = new Godot.Timer
		{
			WaitTime = 1.0 / Mathf.Max(0.01f, OrganThumbnailRefreshHz),
			Autostart = true,
			OneShot = false,
		};
		_thumbnailRefreshTimer.Timeout += OnThumbnailRefreshTimer;
		AddChild(_thumbnailRefreshTimer);
	}

	public override void _ExitTree()
	{
		if (_thumbnailRefreshTimer != null)
		{
			_thumbnailRefreshTimer.Timeout -= OnThumbnailRefreshTimer;
		}

		if (IsInstanceValid(_controller))
		{
			_controller.ScoreChanged -= OnScoreChanged;
			_controller.OrganDamageDisplayChanged -= OnOrganDamageDisplayChanged;
		}

		ClearThumbCache();
	}

	private void OnScoreChanged(Godot.Collections.Dictionary scoreByShapeKey, Godot.Collections.Dictionary damagePercentByShapeKey, Godot.Collections.Array organKeysSorted, Godot.Collections.Dictionary organDestroyedUiFlags, Godot.Collections.Dictionary organHarvestedUiFlags)
	{
		_organHarvestedUiFlags = CloneVariantDictionary(organHarvestedUiFlags);
		while (_rowsHost.GetChildCount() > 0)
		{
			var child = _rowsHost.GetChild(0);
			_rowsHost.RemoveChild(child);
			child.QueueFree();
		}

		var activeKeys = new HashSet<int>();
		foreach (Variant vk in organKeysSorted)
		{
			activeKeys.Add(vk.AsInt32());
		}

		PruneThumbCache(activeKeys);

		foreach (Variant vk in organKeysSorted)
		{
			var key = vk.AsInt32();
			var destroyed = organDestroyedUiFlags.ContainsKey(key) && organDestroyedUiFlags[key].AsBool();

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 12);
			row.SetMeta(OrganKeyMeta, key);

			var nameRtl = new RichTextLabel();
			nameRtl.Name = OrganNameName;
			nameRtl.FitContent = true;
			nameRtl.ScrollActive = false;
			nameRtl.AutowrapMode = TextServer.AutowrapMode.Off;
			nameRtl.BbcodeEnabled = true;
			nameRtl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
			var displayName = _sheet.GetShapeDisplayName(key);
			var safe = EscapeBbcode(displayName);
			var harvested = organHarvestedUiFlags.ContainsKey(key) && organHarvestedUiFlags[key].AsBool();
			nameRtl.Text = BuildOrganNameBbcode(safe, destroyed, harvested);
			row.AddChild(nameRtl);

			var thumb = new TextureRect();
			thumb.Name = OrganThumbName;
			thumb.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
			thumb.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
			thumb.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
			thumb.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
			if (TryBindThumbnailToRect(key, thumb))
			{
				row.AddChild(thumb);
			}

			if (!destroyed)
			{
				var score = scoreByShapeKey.ContainsKey(key) ? scoreByShapeKey[key].AsSingle() : 0f;
				if (!Mathf.IsFinite(score))
				{
					score = 0f;
				}

				var dmg = damagePercentByShapeKey.ContainsKey(key) ? damagePercentByShapeKey[key].AsSingle() : 0f;
				var scoreLabel = new Label { Name = ScoreLabelName };
				scoreLabel.Text = $"Score: {score:F1}%";
				scoreLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
				var dmgLabel = new Label { Name = DamageLabelName };
				dmgLabel.Text = $"Damage: {dmg:0.0}%";
				dmgLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
				ApplyLabelColors(scoreLabel, dmgLabel, destroyed, harvested);
				row.AddChild(scoreLabel);
				row.AddChild(dmgLabel);
			}

			_rowsHost.AddChild(row);
		}

		_organThumbnailsDirty = true;
		_thumbForceFullNextRefresh = true;
	}

	private void OnOrganDamageDisplayChanged(Godot.Collections.Dictionary scoreByShapeKey, Godot.Collections.Dictionary damagePercentByShapeKey, Godot.Collections.Dictionary organDestroyedUiFlags, Godot.Collections.Dictionary organHarvestedUiFlags)
	{
		_organHarvestedUiFlags = CloneVariantDictionary(organHarvestedUiFlags);
		foreach (var child in _rowsHost.GetChildren())
		{
			if (!child.HasMeta(OrganKeyMeta))
			{
				continue;
			}

			var key = child.GetMeta(OrganKeyMeta).AsInt32();
			var destroyed = organDestroyedUiFlags.ContainsKey(key) && organDestroyedUiFlags[key].AsBool();
			var harvested = organHarvestedUiFlags.ContainsKey(key) && organHarvestedUiFlags[key].AsBool();
			var nameRtl = child.GetNodeOrNull<RichTextLabel>(OrganNameName);
			if (nameRtl != null)
			{
				var safe = EscapeBbcode(_sheet.GetShapeDisplayName(key));
				nameRtl.Text = BuildOrganNameBbcode(safe, destroyed, harvested);
			}

			var scoreLabel = child.GetNodeOrNull<Label>(ScoreLabelName);
			var dmgLabel = child.GetNodeOrNull<Label>(DamageLabelName);
			if (scoreLabel is null || dmgLabel is null)
			{
				continue;
			}

			var score = scoreByShapeKey.ContainsKey(key) ? scoreByShapeKey[key].AsSingle() : 0f;
			if (!Mathf.IsFinite(score))
			{
				score = 0f;
			}

			var dmg = damagePercentByShapeKey.ContainsKey(key) ? damagePercentByShapeKey[key].AsSingle() : 0f;
			scoreLabel.Text = $"Score: {score:F1}%";
			dmgLabel.Text = $"Damage: {dmg:0.0}%";
			ApplyLabelColors(scoreLabel, dmgLabel, destroyed, harvested);
		}

		_organThumbnailsDirty = true;
	}

	private bool TryBindThumbnailToRect(int key, TextureRect thumb)
	{
		var raster = GetOrCreateRaster(key);
		if (raster is null)
		{
			ConfigureThumbnailRect(thumb, null);
			return false;
		}

		thumb.Texture = raster.Tex;
		ConfigureThumbnailRect(thumb, raster.Tex);
		return true;
	}

	private OrganThumbRaster? GetOrCreateRaster(int key)
	{
		if (!_sheet.TryGetOrganThumbnailExtents(key, out var minX, out var minY, out var w, out var h))
		{
			return null;
		}

		if (_thumbCache.TryGetValue(key, out var existing))
		{
			if (existing.MinX == minX && existing.MinY == minY && existing.Width == w && existing.Height == h)
			{
				return existing;
			}

			DisposeRaster(existing);
			_thumbCache.Remove(key);
		}

		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
		_sheet.WriteOrganThumbnailToImage(key, img, minX, minY, w, h);
		var tex = ImageTexture.CreateFromImage(img);
		var entry = new OrganThumbRaster
		{
			Img = img,
			Tex = tex,
			MinX = minX,
			MinY = minY,
			Width = w,
			Height = h,
		};
		_thumbCache[key] = entry;
		return entry;
	}

	private void OnThumbnailRefreshTimer()
	{
		if (!_organThumbnailsDirty)
		{
			return;
		}

		_organThumbnailsDirty = false;
		if (_thumbForceFullNextRefresh)
		{
			_thumbForceFullNextRefresh = false;
			_sheet.ClearOrganThumbnailDirtyKeys();
			RefreshOrganThumbnailsFull();
			return;
		}

		var dirtyKeys = _sheet.ConsumeOrganThumbnailDirtyKeys();
		if (dirtyKeys.Count > 0)
		{
			RefreshOrganThumbnailsForKeys(dirtyKeys);
		}
	}

	private void RefreshOrganThumbnailsFull()
	{
		foreach (var child in _rowsHost.GetChildren())
		{
			if (!child.HasMeta(OrganKeyMeta))
			{
				continue;
			}

			var key = child.GetMeta(OrganKeyMeta).AsInt32();
			var thumb = child.GetNodeOrNull<TextureRect>(OrganThumbName);
			if (thumb is null || !_thumbCache.TryGetValue(key, out var raster))
			{
				continue;
			}

			_sheet.WriteOrganThumbnailToImage(key, raster.Img, raster.MinX, raster.MinY, raster.Width, raster.Height);
			if (IsOrganHarvestedForUi(key))
			{
				_sheet.ComposeHarvestedThumbnailOverlay(key, raster.Img, raster.MinX, raster.MinY, raster.Width, raster.Height);
			}
			raster.Tex.SetImage(raster.Img);
		}
	}

	private void RefreshOrganThumbnailsForKeys(Godot.Collections.Array keys)
	{
		var want = new HashSet<int>();
		foreach (Variant vk in keys)
		{
			want.Add(vk.AsInt32());
		}

		foreach (var child in _rowsHost.GetChildren())
		{
			if (!child.HasMeta(OrganKeyMeta))
			{
				continue;
			}

			var key = child.GetMeta(OrganKeyMeta).AsInt32();
			if (!want.Contains(key))
			{
				continue;
			}

			var thumb = child.GetNodeOrNull<TextureRect>(OrganThumbName);
			if (thumb is null || !_thumbCache.TryGetValue(key, out var raster))
			{
				continue;
			}

			_sheet.WriteOrganThumbnailToImage(key, raster.Img, raster.MinX, raster.MinY, raster.Width, raster.Height);
			if (IsOrganHarvestedForUi(key))
			{
				_sheet.ComposeHarvestedThumbnailOverlay(key, raster.Img, raster.MinX, raster.MinY, raster.Width, raster.Height);
			}
			raster.Tex.SetImage(raster.Img);
		}
	}

	private void PruneThumbCache(HashSet<int> activeKeys)
	{
		var toRemove = new List<int>();
		foreach (var kv in _thumbCache)
		{
			if (!activeKeys.Contains(kv.Key))
			{
				toRemove.Add(kv.Key);
			}
		}

		foreach (var k in toRemove)
		{
			if (_thumbCache.TryGetValue(k, out var r))
			{
				DisposeRaster(r);
			}

			_thumbCache.Remove(k);
		}
	}

	private void ClearThumbCache()
	{
		foreach (var r in _thumbCache.Values)
		{
			DisposeRaster(r);
		}

		_thumbCache.Clear();
	}

	private static void DisposeRaster(OrganThumbRaster r)
	{
		r.Tex.Dispose();
		r.Img.Dispose();
	}

	private void ConfigureThumbnailRect(TextureRect thumb, ImageTexture? tex)
	{
		var maxEdge = Mathf.Max(1, OrganThumbnailMaxEdgePx);
		if (tex == null)
		{
			thumb.CustomMinimumSize = new Vector2(maxEdge, maxEdge);
			return;
		}

		var iw = tex.GetWidth();
		var ih = tex.GetHeight();
		var scale = Mathf.Max(iw, ih) > 0 ? maxEdge / (float)Mathf.Max(iw, ih) : 1f;
		var dispW = Mathf.Max(1, Mathf.RoundToInt(iw * scale));
		var dispH = Mathf.Max(1, Mathf.RoundToInt(ih * scale));
		thumb.CustomMinimumSize = new Vector2(dispW, dispH);
	}

	private static string EscapeBbcode(string text)
	{
		return text.Replace("[", "[lb]").Replace("]", "[rb]");
	}

	private bool IsOrganHarvestedForUi(int shapeKey)
	{
		return _organHarvestedUiFlags.ContainsKey(shapeKey) && _organHarvestedUiFlags[shapeKey].AsBool();
	}

	private static Godot.Collections.Dictionary CloneVariantDictionary(Godot.Collections.Dictionary src)
	{
		var copy = new Godot.Collections.Dictionary();
		foreach (var kv in src)
		{
			copy[kv.Key] = kv.Value;
		}

		return copy;
	}

	private string BuildOrganNameBbcode(string safeName, bool destroyed, bool harvested)
	{
		if (destroyed)
		{
			return $"[color={DestroyedOrganNameColor.ToHtml(false)}]{safeName}[/color]";
		}

		if (harvested)
		{
			return $"[color={HarvestedOrganTextColor.ToHtml(false)}]{safeName}[/color]";
		}

		return safeName;
	}

	private void ApplyLabelColors(Label scoreLabel, Label dmgLabel, bool destroyed, bool harvested)
	{
		if (destroyed)
		{
			scoreLabel.AddThemeColorOverride("font_color", DestroyedOrganNameColor);
			dmgLabel.AddThemeColorOverride("font_color", DestroyedOrganNameColor);
			return;
		}

		if (harvested)
		{
			scoreLabel.AddThemeColorOverride("font_color", HarvestedOrganTextColor);
			dmgLabel.AddThemeColorOverride("font_color", HarvestedOrganTextColor);
			return;
		}

		scoreLabel.RemoveThemeColorOverride("font_color");
		dmgLabel.RemoveThemeColorOverride("font_color");
	}
}
