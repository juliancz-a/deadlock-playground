using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using DeadlockPlayground.Painter;
using DeadlockPlayground.UI;

public partial class ExportBaseTexturesModalUI : PanelContainer
{
    public static bool IsOpen { get; private set; } = false;

    public class BaseTextureExportItem
    {
        public string MaterialKey { get; set; }
        public string DisplayName { get; set; }
        public string SubmeshInfo { get; set; }
        public Texture2D Texture { get; set; }
        public Vector2I Resolution { get; set; }
        public MeshInstance3D Mesh { get; set; }
        public int SurfaceIndex { get; set; }
        public bool IsSelected { get; set; } = true;
        public CheckBox CheckBoxRef { get; set; }
    }

    [ExportCategory("Header")]
    [Export] private Control _headerContainer;
    [Export] private Label _lblTitle;
    [Export] private Label _lblSubtitle;
    [Export] private Button _btnClose;

    [ExportCategory("Action Bar")]
    [Export] private Button _btnSelectAll;
    [Export] private Button _btnDeselectAll;
    [Export] private Label _lblCount;

    [ExportCategory("Content")]
    [Export] private VBoxContainer _itemsContainer;
    [Export] private CheckBox _chkIncludeUvWireframe;

    [ExportCategory("Footer")]
    [Export] private Label _lblSelectionSummary;
    [Export] private Button _btnCancel;
    [Export] private Button _btnExport;

    private readonly List<BaseTextureExportItem> _items = new();
    private FileDialog _folderDialog;
    private bool _isDraggingModal = false;
    private Vector2 _dragMouseStart;
    private Vector2 _dragPositionStart;

    public override void _Ready()
    {
        Visible = false;
        IsOpen = false;

        WireSignals();
    }

    private void WireSignals()
    {
        if (_btnClose != null) _btnClose.Pressed += Close;
        if (_btnCancel != null) _btnCancel.Pressed += Close;
        if (_btnSelectAll != null) _btnSelectAll.Pressed += () => SetAllSelected(true);
        if (_btnDeselectAll != null) _btnDeselectAll.Pressed += () => SetAllSelected(false);
        if (_btnExport != null) _btnExport.Pressed += OnExportPressed;

        if (_headerContainer != null)
        {
            _headerContainer.GuiInput += OnHeaderGuiInput;
        }
    }

    private void OnHeaderGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isDraggingModal = true;
                    _dragMouseStart = mb.GlobalPosition;
                    _dragPositionStart = Position;
                }
                else
                {
                    _isDraggingModal = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mm && _isDraggingModal)
        {
            Position = _dragPositionStart + (mm.GlobalPosition - _dragMouseStart);
        }
    }

    public void Open(PaintTabUI paintTab)
    {
        _items.Clear();
        if (_itemsContainer != null)
        {
            foreach (var child in _itemsContainer.GetChildren())
            {
                child.QueueFree();
            }
        }

        PopulateItems(paintTab);

        CenterOrPositionModal();
        Visible = true;
        IsOpen = true;

        UpdateSelectionSummary();
        StudioUIManager.Instance?.UpdateModalsState();
    }

    public void Close()
    {
        Visible = false;
        IsOpen = false;
        _isDraggingModal = false;
        StudioUIManager.Instance?.UpdateModalsState();
    }

    private void CenterOrPositionModal()
    {
        var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float modalW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 520;
        float modalH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 420;

        float targetX = Mathf.Clamp((vpSize.X - modalW) * 0.5f, 50, vpSize.X - modalW - 50);
        float targetY = Mathf.Clamp((vpSize.Y - modalH) * 0.4f, 50, vpSize.Y - modalH - 50);
        Position = new Vector2(targetX, targetY);
    }

    private void PopulateItems(PaintTabUI paintTab)
    {
        if (paintTab == null) return;

        var meshHierarchy = paintTab.MeshHierarchy;
        var particleStage = paintTab.ParticleStage;

        var uniqueMaterials = new Dictionary<string, (Texture2D Tex, List<string> SubmeshNames, MeshInstance3D Mesh, int SurfaceIdx, string DisplayName)>(StringComparer.OrdinalIgnoreCase);

        // 1. Collect from 3D character / model submeshes
        if (meshHierarchy != null && meshHierarchy.Submeshes != null)
        {
            foreach (var sub in meshHierarchy.Submeshes)
            {
                if (sub == null) continue;
                string matKey = sub.OriginalVmatPath;
                if (string.IsNullOrEmpty(matKey)) matKey = sub.MaterialName;
                if (string.IsNullOrEmpty(matKey)) matKey = sub.OriginalColorVtexCPath;
                if (string.IsNullOrEmpty(matKey)) matKey = $"{sub.Mesh?.Name}_surf{sub.SurfaceIndex}";

                Material mat = sub.OriginalMaterial ?? sub.Mesh?.GetActiveMaterial(sub.SurfaceIndex);
                var tex = SkinLayerManager.ExtractBaseTexture(mat);
                if (tex == null) continue;

                string subName = sub.DisplayName ?? sub.RawName ?? sub.Mesh?.Name ?? "Mesh";

                if (uniqueMaterials.TryGetValue(matKey, out var existing))
                {
                    if (!existing.SubmeshNames.Contains(subName))
                    {
                        existing.SubmeshNames.Add(subName);
                    }
                }
                else
                {
                    string cleanDisplayName = !string.IsNullOrWhiteSpace(sub.MaterialName)
                        ? sub.MaterialName
                        : (!string.IsNullOrWhiteSpace(sub.OriginalVmatPath) ? Path.GetFileNameWithoutExtension(sub.OriginalVmatPath) : subName);

                    uniqueMaterials[matKey] = (tex, new List<string> { subName }, sub.Mesh, sub.SurfaceIndex, cleanDisplayName);
                }
            }
        }

        // 2. Collect from particle stage if active
        if (particleStage != null && particleStage.HasActiveParticle)
        {
            var pTextures = particleStage.GetActiveTextures();
            if (pTextures != null)
            {
                foreach (var kvp in pTextures)
                {
                    if (kvp.Value == null) continue;
                    string vtexPath = kvp.Key;
                    string cleanDisplayName = Path.GetFileNameWithoutExtension(vtexPath);
                    string matKey = "particle_" + vtexPath;

                    uniqueMaterials[matKey] = (kvp.Value, new List<string> { "Particle Texture" }, null, 0, cleanDisplayName);
                }
            }
        }

        // Build UI list
        foreach (var kvp in uniqueMaterials)
        {
            var data = kvp.Value;
            var item = new BaseTextureExportItem
            {
                MaterialKey = kvp.Key,
                DisplayName = data.DisplayName,
                SubmeshInfo = string.Join(", ", data.SubmeshNames),
                Texture = data.Tex,
                Resolution = new Vector2I(data.Tex.GetWidth(), data.Tex.GetHeight()),
                Mesh = data.Mesh,
                SurfaceIndex = data.SurfaceIdx,
                IsSelected = true
            };
            _items.Add(item);

            if (_itemsContainer != null)
            {
                var row = CreateItemRowUI(item);
                _itemsContainer.AddChild(row);
            }
        }

        if (_lblCount != null)
        {
            _lblCount.Text = $"{_items.Count} texture(s) available";
        }
    }

    private Control CreateItemRowUI(BaseTextureExportItem item)
    {
        var rowPanel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.14f, 0.18f, 0.75f),
            BorderColor = new Color(0.24f, 0.28f, 0.36f, 0.6f),
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            ContentMarginBottom = 6,
            ContentMarginTop = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8
        };
        rowPanel.AddThemeStyleboxOverride("panel", style);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        rowPanel.AddChild(hbox);

        // Checkbox
        var chk = new CheckBox
        {
            ButtonPressed = item.IsSelected,
            CustomMinimumSize = new Vector2(24, 24),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        chk.Toggled += (on) =>
        {
            item.IsSelected = on;
            UpdateSelectionSummary();
        };
        item.CheckBoxRef = chk;
        hbox.AddChild(chk);

        // Thumbnail Preview
        var thumb = new TextureRect
        {
            Texture = item.Texture,
            CustomMinimumSize = new Vector2(40, 40),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        hbox.AddChild(thumb);

        // Name & Submesh labels
        var vboxLabels = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        vboxLabels.AddThemeConstantOverride("separation", 2);

        var lblName = new Label
        {
            Text = item.DisplayName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        lblName.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
        lblName.AddThemeFontSizeOverride("font_size", 13);
        vboxLabels.AddChild(lblName);

        var lblSubmesh = new Label
        {
            Text = $"Submeshes: {item.SubmeshInfo}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        lblSubmesh.AddThemeColorOverride("font_color", new Color(0.60f, 0.66f, 0.74f));
        lblSubmesh.AddThemeFontSizeOverride("font_size", 11);
        vboxLabels.AddChild(lblSubmesh);

        hbox.AddChild(vboxLabels);

        // Resolution Badge
        var badgePanel = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        var badgeStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.22f, 0.30f, 0.9f),
            BorderColor = new Color(0.32f, 0.40f, 0.52f, 0.8f),
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginBottom = 3,
            ContentMarginTop = 3,
            ContentMarginLeft = 6,
            ContentMarginRight = 6
        };
        badgePanel.AddThemeStyleboxOverride("panel", badgeStyle);

        var lblRes = new Label
        {
            Text = $"{item.Resolution.X} × {item.Resolution.Y}"
        };
        lblRes.AddThemeColorOverride("font_color", new Color(0.85f, 0.90f, 0.96f));
        lblRes.AddThemeFontSizeOverride("font_size", 11);
        badgePanel.AddChild(lblRes);

        hbox.AddChild(badgePanel);

        return rowPanel;
    }

    private void SetAllSelected(bool select)
    {
        foreach (var item in _items)
        {
            item.IsSelected = select;
            if (item.CheckBoxRef != null)
            {
                item.CheckBoxRef.ButtonPressed = select;
            }
        }
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        int selCount = 0;
        foreach (var item in _items)
        {
            if (item.IsSelected) selCount++;
        }

        if (_lblSelectionSummary != null)
        {
            _lblSelectionSummary.Text = $"{selCount} of {_items.Count} texture(s) selected";
        }

        if (_btnExport != null)
        {
            _btnExport.Disabled = (selCount == 0);
        }
    }

    private void OnExportPressed()
    {
        int selCount = 0;
        foreach (var item in _items) if (item.IsSelected) selCount++;

        if (selCount == 0)
        {
            StudioUIManager.Instance?.ShowToast("No textures selected for export.");
            return;
        }

        if (_folderDialog == null)
        {
            _folderDialog = new FileDialog
            {
                Title = "Select Destination Folder for Base Textures",
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true
            };
            _folderDialog.DirSelected += ExecuteBatchExport;
            AddChild(_folderDialog);
        }

        _folderDialog.PopupCentered(new Vector2I(750, 500));
    }

    private void ExecuteBatchExport(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        int exportedCount = 0;
        int uvExportedCount = 0;
        bool includeUv = _chkIncludeUvWireframe != null && _chkIncludeUvWireframe.ButtonPressed;

        foreach (var item in _items)
        {
            if (!item.IsSelected || item.Texture == null) continue;

            try
            {
                var img = item.Texture.GetImage();
                if (img == null || img.IsEmpty()) continue;

                if (img.IsCompressed())
                {
                    img.Decompress();
                }
                if (img.GetFormat() != Image.Format.Rgba8)
                {
                    img.Convert(Image.Format.Rgba8);
                }

                string safeName = SanitizeFileName(item.DisplayName);
                string outPath = Path.Combine(dir, $"{safeName}_base.png");
                var err = img.SavePng(outPath);
                if (err == Error.Ok)
                {
                    exportedCount++;
                }

                if (includeUv && item.Mesh != null)
                {
                    var uvImg = GenerateUvWireframeImage(item.Mesh, item.SurfaceIndex, item.Resolution.X, item.Resolution.Y);
                    if (uvImg != null)
                    {
                        string uvPath = Path.Combine(dir, $"{safeName}_uv.png");
                        if (uvImg.SavePng(uvPath) == Error.Ok)
                        {
                            uvExportedCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ExportBaseTextures] Error exporting '{item.DisplayName}': {ex.Message}");
            }
        }

        Close();

        string toastSub = includeUv && uvExportedCount > 0
            ? $"{exportedCount} base textures + {uvExportedCount} UV wireframes exported"
            : $"{exportedCount} base textures exported to {Path.GetFileName(dir)}";

        StudioUIManager.Instance?.ShowToast("Base Textures Exported", toastSub);
        OS.ShellOpen(dir);
    }

    private Image GenerateUvWireframeImage(MeshInstance3D meshInstance, int surfaceIndex, int width, int height)
    {
        if (meshInstance == null || meshInstance.Mesh == null) return null;
        var mesh = meshInstance.Mesh;
        if (surfaceIndex < 0 || surfaceIndex >= mesh.GetSurfaceCount()) surfaceIndex = 0;

        var arrays = mesh.SurfaceGetArrays(surfaceIndex);
        if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.TexUV) return null;

        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        if (uvs == null || uvs.Length == 0) return null;

        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (indices == null || indices.Length == 0) return null;

        if (width <= 0) width = 2048;
        if (height <= 0) height = 2048;

        var wireImg = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        Color wireColor = new Color(0.92f, 0.94f, 0.98f, 0.9f);

        void DrawLine(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                {
                    wireImg.SetPixel(x0, y0, wireColor);
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        void ProcessEdge(int i1, int i2)
        {
            if (i1 < 0 || i2 < 0 || i1 >= uvs.Length || i2 >= uvs.Length) return;
            Vector2 uv1 = uvs[i1];
            Vector2 uv2 = uvs[i2];
            if (Mathf.Abs(uv1.X - uv2.X) > 0.35f || Mathf.Abs(uv1.Y - uv2.Y) > 0.35f) return;

            int px1 = Mathf.Clamp((int)(uv1.X * (width - 1)), 0, width - 1);
            int py1 = Mathf.Clamp((int)(uv1.Y * (height - 1)), 0, height - 1);
            int px2 = Mathf.Clamp((int)(uv2.X * (width - 1)), 0, width - 1);
            int py2 = Mathf.Clamp((int)(uv2.Y * (height - 1)), 0, height - 1);
            DrawLine(px1, py1, px2, py2);
        }

        for (int i = 0; i < indices.Length; i += 3)
        {
            if (i + 2 >= indices.Length) break;
            int i0 = indices[i];
            int i1 = indices[i + 1];
            int i2 = indices[i + 2];
            ProcessEdge(i0, i1);
            ProcessEdge(i1, i2);
            ProcessEdge(i2, i0);
        }

        return wireImg;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "texture";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim();
    }
}
