using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Materials;

public partial class PaintTabUI : VBoxContainer
{
    [ExportCategory("Subsystem References")]
    [Export] private Camera3D _camera;
    [Export] private SubViewport _worldViewport;
    [Export] private SubViewportContainer _viewportContainer;
    [Export] private FloatingBrushPaletteUI _brushPalette;

    // Subsystems
    private SkinLayerManager _layerManager;
    private MeshPainter3D _painter;
    private DecalStamper _decalStamper;
    private TextProjector _textProjector;
    private HeroMeshHierarchy _meshHierarchy;
    private SkinExporter _exporter;

    private Node3D _currentHero;
    private StudioUIManager _studioUI;

    private struct CachedBoneTransform
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    private readonly Dictionary<int, CachedBoneTransform> _cachedBonePoses = new();
    private string _cachedAnimationName = null;
    private double _cachedAnimationPosition = 0.0;
    private bool _cachedAnimationPlaying = false;
    private bool _hasCachedPose = false;

    private Dictionary<string, bool> _cachedPaintSubmeshVisibility = null;
    private Dictionary<string, bool> _cachedPaintPreSoloVisibility = null;
    private string _cachedPaintSoloedSubmesh = null;
    private bool _hasCachedPaintVisibility = false;

    private class SubmeshRowUI
    {
        public SubmeshNodeInfo Submesh;
        public HBoxContainer RowContainer;
        public CheckBox VisCheck;
        public Button SoloBtn;
        public Button SelectBtn;
    }

    private readonly List<SubmeshRowUI> _submeshRows = new();

    public SkinLayerManager LayerManager => _layerManager;
    public HeroMeshHierarchy MeshHierarchy => _meshHierarchy;
    public MeshPainter3D Painter => _painter;
    public FloatingBrushPaletteUI BrushPalette => _brushPalette;

    // --- UI Controls ---
    // Header
    private Label _lblHeroName;
    private Label _lblActiveMesh;
    private OptionButton _optTargetMesh;
    private OptionButton _optResolution;
    private ConfirmationDialog _confirmResDialog;
    private int _previousResolutionIndex = 1;
    private int _pendingResolution = 2048;
    private int _pendingResolutionIndex = 1;
    private Texture2D _iconPencil;

    // Submesh Hierarchy
    private VBoxContainer _submeshListContainer;
    private Button _btnShowAllMeshes;
    private Button _btnHideAccMeshes;

    private Button _btnAddLayer;
    private Button _btnDeleteLayer;
    private Button _btnMoveUp;
    private Button _btnMoveDown;
    private VBoxContainer _layersListContainer;
    private OptionButton _optLayerBlend;
    private HSlider _sliderLayerOpacity;
    private Label _lblLayerOpacity;
    private ConfirmationDialog _renameLayerDialog;
    private LineEdit _renameLayerInput;
    private int _layerIndexToRename = -1;
    public static bool IsRenameDialogOpen { get; private set; }

    private StyleBoxFlat _renameBtnNormalStyle;
    private StyleBoxFlat _renameBtnHoverStyle;
    private StyleBoxFlat _renameBtnPressedStyle;
    private StyleBoxFlat _renameBtnDisabledStyle;

    private void EnsureRenameButtonStyles()
    {
        if (_renameBtnNormalStyle != null) return;
        _renameBtnNormalStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.22f, 0.26f, 0.6f),
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 2,
            ContentMarginRight = 2,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        _renameBtnHoverStyle = (StyleBoxFlat)_renameBtnNormalStyle.Duplicate();
        _renameBtnHoverStyle.BgColor = new Color(0.28f, 0.35f, 0.44f, 0.9f);
        _renameBtnPressedStyle = (StyleBoxFlat)_renameBtnNormalStyle.Duplicate();
        _renameBtnPressedStyle.BgColor = new Color(0.12f, 0.15f, 0.18f, 1.0f);
        _renameBtnDisabledStyle = (StyleBoxFlat)_renameBtnNormalStyle.Duplicate();
        _renameBtnDisabledStyle.BgColor = new Color(0.12f, 0.12f, 0.12f, 0.3f);
    }

    // Export
    private Button _btnExportPng;
    private Button _btnExportVmat;
    private Button _btnInstallCitadel;
    private Label _lblExportStatus;
    private FileDialog _exportFileDialog;

    public Node3D CurrentHero => _currentHero;

    public override void _Ready()
    {
        InitializeSubsystems();
        LinkUI();
        ConnectEvents();
        UpdateControlsState(false);
    }

    private void InitializeSubsystems()
    {
        // 1. Layer Manager
        _layerManager = new SkinLayerManager { Name = "SkinLayerManager" };
        AddChild(_layerManager);

        // 2. Mesh Painter 3D
        _painter = new MeshPainter3D { Name = "MeshPainter3D" };
        AddChild(_painter);

        // 3. Decal Stamper
        _decalStamper = new DecalStamper { Name = "DecalStamper" };
        AddChild(_decalStamper);

        // 4. Text Projector
        _textProjector = new TextProjector { Name = "TextProjector" };
        AddChild(_textProjector);

        // 5. Hero Mesh Hierarchy
        _meshHierarchy = new HeroMeshHierarchy { Name = "HeroMeshHierarchy" };
        AddChild(_meshHierarchy);

        // 6. Exporter
        _exporter = new SkinExporter(_layerManager);

        // Find Camera, WorldViewport, and SubViewportContainer if not assigned
        _camera ??= GetNodeOrNull<Camera3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot/Camera3D")
                 ?? GetTree().Root.FindChild("Camera3D", true, false) as Camera3D;

        _worldViewport ??= GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                        ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;

        _viewportContainer ??= GetNodeOrNull<SubViewportContainer>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer")
                            ?? _worldViewport?.GetParent() as SubViewportContainer
                            ?? GetTree().Root.FindChild("SubViewportContainer", true, false) as SubViewportContainer;

        _brushPalette ??= GetNodeOrNull<FloatingBrushPaletteUI>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/FloatingBrushPalette")
                       ?? GetTree().Root.FindChild("FloatingBrushPalette", true, false) as FloatingBrushPaletteUI;

        _studioUI = GetNodeOrNull<StudioUIManager>("/root/Main/UIRoot")
                 ?? GetTree().Root.FindChild("UIRoot", true, false) as StudioUIManager;

        _painter.Setup(_camera, _worldViewport, _layerManager, _viewportContainer);
        _painter.MeshHierarchy = _meshHierarchy;
        _layerManager.MeshHierarchy = _meshHierarchy;
        _painter.DecalStamper = _decalStamper;
        _painter.TextProjector = _textProjector;
        _painter.IsPaintingActive = false; // Off by default until Paint tab is activated

        _decalStamper.Setup(_worldViewport, _layerManager, _painter);
        _textProjector.Setup(_worldViewport, _layerManager, _painter);

        if (_brushPalette != null)
        {
            _brushPalette.Setup(_painter, _decalStamper, _textProjector);
            _brushPalette.UndoRequested += () => _layerManager?.Undo();
            _brushPalette.RedoRequested += () => _layerManager?.Redo();
            _brushPalette.ClearRequested += () => _layerManager?.ClearCurrentLayer();
            _brushPalette.BlendModeChanged += (mode) =>
            {
                _layerManager?.SetOverlayBlendMode(mode);
                _painter?.SyncCameraBrushProperties(force: true);
            };
            _brushPalette.FillRequested += (color) => _layerManager?.FillCurrentSubmesh(color, null, _painter?.MagicWandTool);
        }
    }

    private void LinkUI()
    {
        // Header
        _lblHeroName = GetNodeOrNull<Label>("HeaderCard/VBox/HBoxHeader/HeroNameLabel");
        _optTargetMesh = GetNodeOrNull<OptionButton>("HeaderCard/VBox/HBoxTarget/OptTargetMesh");
        if (_optTargetMesh != null)
        {
            _optTargetMesh.FitToLongestItem = false;
            _optTargetMesh.ClipText = true;
            _optTargetMesh.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        _optResolution = GetNodeOrNull<OptionButton>("HeaderCard/VBox/HBoxRes/ResolutionOption");
        _lblActiveMesh = GetNodeOrNull<Label>("HeaderCard/VBox/ActiveMeshLabel");

        // Submesh Hierarchy
        _submeshListContainer = GetNodeOrNull<VBoxContainer>("SubmeshCard/VBox/ScrollContainer/SubmeshListContainer");
        _btnShowAllMeshes = GetNodeOrNull<Button>("SubmeshCard/VBox/HBoxActions/BtnShowAll");
        _btnHideAccMeshes = GetNodeOrNull<Button>("SubmeshCard/VBox/HBoxActions/BtnHideAcc");

        // Layers
        _btnAddLayer = GetNodeOrNull<Button>("LayersCard/VBox/HBoxToolbar/BtnAddLayer");
        _btnDeleteLayer = GetNodeOrNull<Button>("LayersCard/VBox/HBoxToolbar/BtnDeleteLayer");
        _btnMoveUp = GetNodeOrNull<Button>("LayersCard/VBox/HBoxToolbar/BtnMoveUp");
        _btnMoveDown = GetNodeOrNull<Button>("LayersCard/VBox/HBoxToolbar/BtnMoveDown");
        _layersListContainer = GetNodeOrNull<VBoxContainer>("LayersCard/VBox/ScrollContainer/LayersListContainer");
        _optLayerBlend = GetNodeOrNull<OptionButton>("LayersCard/VBox/HBoxBlend/OptLayerBlend");
        _sliderLayerOpacity = GetNodeOrNull<HSlider>("LayersCard/VBox/HBoxOpacity/SliderLayerOpacity");
        _lblLayerOpacity = GetNodeOrNull<Label>("LayersCard/VBox/HBoxOpacity/LblLayerOpacity");

        // Export
        _btnExportPng = GetNodeOrNull<Button>("ExportCard/VBox/BtnExportPng");
        _btnExportVmat = GetNodeOrNull<Button>("ExportCard/VBox/BtnExportVmat");
        _btnInstallCitadel = GetNodeOrNull<Button>("ExportCard/VBox/BtnInstallCitadel");
        _lblExportStatus = GetNodeOrNull<Label>("ExportCard/VBox/LblExportStatus");
        _exportFileDialog = GetNodeOrNull<FileDialog>("ExportFileDialog");

        // Populate Blend Modes
        if (_optLayerBlend != null)
        {
            _optLayerBlend.Clear();
            _optLayerBlend.AddItem("Normal", (int)LayerBlendMode.Normal);
            _optLayerBlend.AddItem("Multiply", (int)LayerBlendMode.Multiply);
            _optLayerBlend.AddItem("Screen", (int)LayerBlendMode.Screen);
            _optLayerBlend.AddItem("Overlay", (int)LayerBlendMode.Overlay);
            _optLayerBlend.AddItem("Darken", (int)LayerBlendMode.Darken);
            _optLayerBlend.AddItem("Lighten", (int)LayerBlendMode.Lighten);
            _optLayerBlend.AddItem("Color Dodge", (int)LayerBlendMode.ColorDodge);
            _optLayerBlend.Select(0);
        }

        PopulateResolutionDropdown();
    }

    private void PopulateResolutionDropdown()
    {
        if (_optResolution == null) return;
        _optResolution.Clear();
        _optResolution.AddItem("1024 x 1024", 1024);
        _optResolution.AddItem("2048 x 2048", 2048);
        _optResolution.AddItem("4096 x 4096", 4096);
        _optResolution.Select(1); // 2048 selected by default
        _previousResolutionIndex = 1;
    }

    private void ShowResolutionConfirmModal(int newRes, int newIdx)
    {
        _pendingResolution = newRes;
        _pendingResolutionIndex = newIdx;
        if (_confirmResDialog == null)
        {
            _confirmResDialog = new ConfirmationDialog
            {
                Title = "Change Canvas Resolution",
                OkButtonText = "Accept",
                CancelButtonText = "Cancel"
            };
            _confirmResDialog.Confirmed += () =>
            {
                _previousResolutionIndex = _pendingResolutionIndex;
                _optResolution.Select(_previousResolutionIndex);
                _layerManager?.SetCanvasResolution(new Vector2I(_pendingResolution, _pendingResolution));
                _painter?.SyncSelectionMaskState();
                if (_meshHierarchy?.ActiveTarget != null)
                {
                    _meshHierarchy.SelectTarget(_meshHierarchy.ActiveTarget, _meshHierarchy.ActiveTarget.SurfaceIndex);
                }
            };
            _confirmResDialog.Canceled += () =>
            {
                _optResolution.Select(_previousResolutionIndex);
            };
            _confirmResDialog.CloseRequested += () =>
            {
                _optResolution.Select(_previousResolutionIndex);
            };
            AddChild(_confirmResDialog);
        }

        _confirmResDialog.DialogText = "Changing the canvas resolution will clean the textures, are you sure?";
        _confirmResDialog.PopupCentered(new Vector2I(420, 160));
    }

    private void ConnectEvents()
    {
        // Subsystem events
        if (_meshHierarchy != null)
        {
            _meshHierarchy.HierarchyChanged += RefreshSubmeshListUI;
            _meshHierarchy.TargetMeshChanged += OnTargetMeshChanged;
        }

        if (_layerManager != null)
        {
            _layerManager.StackChanged += RefreshLayersListUI;
            _layerManager.LayerSelected += OnLayerSelected;
        }

        // Header controls
        if (_optTargetMesh != null)
        {
            _optTargetMesh.ItemSelected += (idx) =>
            {
                var submeshes = _meshHierarchy?.Submeshes;
                if (submeshes != null && idx >= 0 && idx < submeshes.Count)
                {
                    _meshHierarchy.SelectTarget(submeshes[(int)idx], submeshes[(int)idx].SurfaceIndex);
                }
            };
        }

        if (_optResolution != null)
        {
            _optResolution.ItemSelected += (idx) =>
            {
                int res = (int)_optResolution.GetItemId((int)idx);
                if (_layerManager != null && _layerManager.CanvasSize.X == res) return;

                ShowResolutionConfirmModal(res, (int)idx);
            };
        }

        // Submesh Actions
        if (_btnShowAllMeshes != null)
        {
            _btnShowAllMeshes.Pressed += () => _meshHierarchy?.ShowAll();
        }
        if (_btnHideAccMeshes != null)
        {
            _btnHideAccMeshes.Pressed += () => _meshHierarchy?.HideAccessories();
        }

        // Layer Actions
        if (_btnAddLayer != null)
        {
            _btnAddLayer.Pressed += () => _layerManager?.AddNewLayer();
        }
        if (_btnDeleteLayer != null)
        {
            _btnDeleteLayer.Pressed += () => _layerManager?.DeleteActiveLayer();
        }
        if (_btnMoveUp != null)
        {
            _btnMoveUp.Pressed += () =>
            {
                if (_layerManager == null) return;
                int idx = _layerManager.ActiveLayerIndex;
                if (idx > 0) _layerManager.MoveLayer(idx, idx - 1);
            };
        }
        if (_btnMoveDown != null)
        {
            _btnMoveDown.Pressed += () =>
            {
                if (_layerManager == null) return;
                int idx = _layerManager.ActiveLayerIndex;
                if (idx >= 0 && idx < _layerManager.Layers.Count - 1) _layerManager.MoveLayer(idx, idx + 1);
            };
        }

        if (_optLayerBlend != null)
        {
            _optLayerBlend.ItemSelected += (idx) =>
            {
                if (_layerManager == null) return;
                _layerManager.SetLayerBlendMode(_layerManager.ActiveLayerIndex, (LayerBlendMode)idx);
                _painter?.SyncCameraBrushProperties(force: true);
            };
        }

        if (_sliderLayerOpacity != null)
        {
            _sliderLayerOpacity.ValueChanged += (val) =>
            {
                if (_layerManager == null) return;
                _layerManager.SetLayerOpacity(_layerManager.ActiveLayerIndex, (float)val);
                if (_lblLayerOpacity != null) _lblLayerOpacity.Text = $"{Mathf.RoundToInt(val * 100)}%";
                _painter?.SyncCameraBrushProperties(force: true);
            };
        }

        // Export Actions
        if (_btnExportPng != null)
        {
            _btnExportPng.Pressed += () =>
            {
                if (_exportFileDialog != null)
                {
                    _exportFileDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
                    _exportFileDialog.Filters = new[] { "*.png ; PNG Image" };
                    _exportFileDialog.PopupCentered(new Vector2I(700, 500));
                }
            };
        }

        if (_btnExportVmat != null)
        {
            _btnExportVmat.Pressed += () =>
            {
                if (_exportFileDialog != null)
                {
                    _exportFileDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
                    _exportFileDialog.Filters = new[] { "*.vmat ; Valve Material" };
                    _exportFileDialog.PopupCentered(new Vector2I(700, 500));
                }
            };
        }

        if (_btnInstallCitadel != null)
        {
            _btnInstallCitadel.Pressed += () =>
            {
                OpenExportModDialog();
            };
        }

        if (_exportFileDialog != null)
        {
            _exportFileDialog.FileSelected += (path) =>
            {
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var res = _exporter?.ExportFlattenedPng(path);
                    if (_lblExportStatus != null && res != null)
                    {
                        _lblExportStatus.Text = res.Success ? $"Saved PNG: {Path.GetFileName(path)}" : $"Error: {res.ErrorMessage}";
                    }
                }
                else if (path.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                {
                    string heroName = _currentHero?.Name.ToString().Replace("Hero_", "") ?? "hero";
                    string meshName = _meshHierarchy?.ActiveTarget?.RawName ?? "body";
                    string relativeTexPath = $"materials/heroes/{heroName}/{heroName}_{meshName}_color.png";
                    var res = _exporter?.ExportVmat(path, relativeTexPath);
                    if (_lblExportStatus != null && res != null)
                    {
                        _lblExportStatus.Text = res.Success ? $"Saved VMAT: {Path.GetFileName(path)}" : $"Error: {res.ErrorMessage}";
                    }
                }
            };
        }
    }

    public void SetCurrentHero(Node3D hero)
    {
        SetHero(hero);
    }

    public void SetHero(Node3D heroNode)
    {
        _currentHero = heroNode;
        _hasCachedPaintVisibility = false;
        _cachedPaintSubmeshVisibility = null;
        _cachedPaintPreSoloVisibility = null;
        _cachedPaintSoloedSubmesh = null;

        if (heroNode == null)
        {
            ClearHero();
            return;
        }

        string heroName = heroNode.Name.ToString().Replace("Hero_", "");
        if (_lblHeroName != null) _lblHeroName.Text = $"HERO: {heroName.ToUpperInvariant()}";

        // 1. Scan submesh hierarchy (generates UV2 unwraps)
        _meshHierarchy?.ScanHero(heroNode);

        if (_painter != null && _painter.IsPaintingActive)
        {
            CacheCharacterPose();
            ResetCharacterPose();
            if (_meshHierarchy != null)
            {
                _meshHierarchy.IsPaintingModeActive = true;
            }
            _meshHierarchy?.ApplyToonShading(false);
        }

        // 2. Setup GPU Texture Painter OverlayAtlasManager on the hero
        _layerManager?.SetupForHero(heroNode);

        // 3. Register submeshes and apply overlay parameters to meshes
        if (_layerManager != null && _meshHierarchy != null)
        {
            _layerManager.ClearRegisteredSubmeshes();
            foreach (var submesh in _meshHierarchy.Submeshes)
            {
                if (submesh.Mesh != null)
                {
                    _layerManager.RegisterSubmesh(submesh.Mesh);
                }
            }
            _layerManager.ApplyOverlayParametersToMeshes();
            _layerManager.RebuildBaseAtlasBuffer();
        }

        // 4. Populate target dropdown and select default
        PopulateTargetDropdown();

        // 5. Populate resolution dropdown (default 2048) and allocate canvas
        PopulateResolutionDropdown();
        _layerManager?.SetCanvasResolution(new Vector2I(2048, 2048));

        // 6. Refresh submesh list UI
        RefreshSubmeshListUI();

        UpdateControlsState(true);

        if (_brushPalette != null)
        {
            _brushPalette.SyncFromPainter();
        }
    }

    private void PopulateTargetDropdown()
    {
        if (_optTargetMesh == null || _meshHierarchy == null) return;
        _optTargetMesh.Clear();

        var submeshes = _meshHierarchy.Submeshes;
        for (int i = 0; i < submeshes.Count; i++)
        {
            _optTargetMesh.AddItem(submeshes[i].DisplayName, i);
        }

        if (submeshes.Count > 0 && _optTargetMesh.ItemCount > 0)
        {
            int defaultIdx = 0;
            for (int i = 0; i < submeshes.Count; i++)
            {
                string rLower = submeshes[i].RawName.ToLowerInvariant();
                if (rLower.Contains("body") || rLower.Contains("head"))
                {
                    defaultIdx = i;
                    break;
                }
            }
            if (defaultIdx >= 0 && defaultIdx < _optTargetMesh.ItemCount)
            {
                _optTargetMesh.Select(defaultIdx);
            }
            _meshHierarchy.SelectTarget(submeshes[defaultIdx], submeshes[defaultIdx].SurfaceIndex);
        }
    }

    public void ClearHero()
    {
        _currentHero = null;
        _hasCachedPose = false;
        _cachedBonePoses.Clear();
        _cachedAnimationName = null;
        _cachedAnimationPlaying = false;
        _hasCachedPaintVisibility = false;
        _cachedPaintSubmeshVisibility = null;
        _cachedPaintPreSoloVisibility = null;
        _cachedPaintSoloedSubmesh = null;
        _submeshRows.Clear();

        if (_lblHeroName != null) _lblHeroName.Text = "HERO: NONE";
        if (_lblActiveMesh != null) _lblActiveMesh.Text = "Target: None";
        if (_optTargetMesh != null) _optTargetMesh.Clear();

        _painter?.SetTargetMesh(null);
        _layerManager?.CleanupHeroAtlas();
        _meshHierarchy?.ScanHero(null);
        _decalStamper?.HidePreview();

        UpdateControlsState(false);
    }

    private void OnTargetMeshChanged(MeshInstance3D mesh, int surfaceIndex)
    {
        if (mesh == null)
        {
            if (_lblActiveMesh != null) _lblActiveMesh.Text = "Target: None";
            _painter?.SetTargetMesh(null);
            UpdateSubmeshRowHighlights();
            return;
        }

        string displayName = _meshHierarchy?.ActiveTarget?.DisplayName ?? mesh.Name.ToString();
        if (_lblActiveMesh != null) _lblActiveMesh.Text = $"Target: {displayName}";

        // Synchronize dropdown selection if not matching
        if (_optTargetMesh != null && _meshHierarchy != null && _optTargetMesh.ItemCount > 0)
        {
            var submeshes = _meshHierarchy.Submeshes;
            for (int i = 0; i < submeshes.Count; i++)
            {
                if (submeshes[i].Mesh == mesh && submeshes[i].SurfaceIndex == surfaceIndex)
                {
                    if (i >= 0 && i < _optTargetMesh.ItemCount && _optTargetMesh.Selected != i)
                    {
                        _optTargetMesh.Select(i);
                    }
                    break;
                }
            }
        }

        UpdateSubmeshRowHighlights();
        _painter?.SetTargetMesh(mesh, surfaceIndex);
    }

    private void UpdateSubmeshRowHighlights()
    {
        if (_meshHierarchy == null) return;
        var active = _meshHierarchy.ActiveTarget;
        foreach (var row in _submeshRows)
        {
            if (row.SelectBtn != null && GodotObject.IsInstanceValid(row.SelectBtn))
            {
                row.SelectBtn.Modulate = (row.Submesh == active)
                    ? new Color(1.0f, 0.85f, 0.4f, 1.0f)
                    : Colors.White;
            }
        }
    }

    private void RefreshSubmeshListUI()
    {
        if (_submeshListContainer == null || _meshHierarchy == null) return;

        if (_layerManager != null && _layerManager.AtlasManager != null)
        {
            _layerManager.ClearRegisteredSubmeshes();
            foreach (var submesh in _meshHierarchy.Submeshes)
            {
                if (submesh.Mesh != null)
                {
                    _layerManager.RegisterSubmesh(submesh.Mesh);
                }
            }
            _layerManager.ApplyOverlayParametersToMeshes();
            _layerManager.RebuildBaseAtlasBuffer();
        }

        var submeshes = _meshHierarchy.Submeshes;

        // Check if existing rows can be updated in-place
        bool canUpdateInPlace = (_submeshRows.Count == submeshes.Count);
        if (canUpdateInPlace)
        {
            for (int i = 0; i < submeshes.Count; i++)
            {
                if (_submeshRows[i].Submesh != submeshes[i] ||
                    !GodotObject.IsInstanceValid(_submeshRows[i].RowContainer))
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        if (canUpdateInPlace)
        {
            for (int i = 0; i < submeshes.Count; i++)
            {
                var row = _submeshRows[i];
                var sub = submeshes[i];
                bool isVis = sub.Mesh != null && GodotObject.IsInstanceValid(sub.Mesh) && sub.Mesh.Visible;
                row.VisCheck.SetPressedNoSignal(isVis);
                row.SoloBtn.SetPressedNoSignal(sub.IsSoloed);
                row.SoloBtn.Text = sub.IsSoloed ? "[Solo]" : "Solo";
                row.SelectBtn.Modulate = (_meshHierarchy.ActiveTarget == sub)
                    ? new Color(1.0f, 0.85f, 0.4f, 1.0f)
                    : Colors.White;
            }
            return;
        }

        // Full rebuild needed
        foreach (Node child in _submeshListContainer.GetChildren())
        {
            child.QueueFree();
        }
        _submeshRows.Clear();

        foreach (var submesh in submeshes)
        {
            var row = new HBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };

            bool isVis = submesh.Mesh != null && GodotObject.IsInstanceValid(submesh.Mesh) && submesh.Mesh.Visible;
            var visCheck = new CheckBox
            {
                ButtonPressed = isVis,
                TooltipText = "Toggle submesh visibility",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            var localSub = submesh;
            visCheck.Toggled += (vis) =>
            {
                _meshHierarchy.SetMeshVisibility(localSub, vis);
            };

            var soloBtn = new Button
            {
                Text = submesh.IsSoloed ? "[Solo]" : "Solo",
                CustomMinimumSize = new Vector2(48, 26),
                ToggleMode = true,
                ButtonPressed = submesh.IsSoloed,
                TooltipText = "Solo submesh (hide others)",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            soloBtn.Pressed += () => _meshHierarchy.ToggleSolo(localSub);

            int charLen = submesh.DisplayName?.Length ?? 0;
            float minHeight = charLen > 40 ? 52f : (charLen > 22 ? 38f : 28f);

            var btnWrapper = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.Fill,
                CustomMinimumSize = new Vector2(0, minHeight),
                ClipContents = true
            };

            var selectBtn = new Button
            {
                Text = submesh.DisplayName,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = submesh.DisplayName
            };
            selectBtn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            selectBtn.OffsetLeft = 0;
            selectBtn.OffsetTop = 0;
            selectBtn.OffsetRight = 0;
            selectBtn.OffsetBottom = 0;
            selectBtn.AddThemeFontSizeOverride("font_size", 11);
            if (_meshHierarchy.ActiveTarget == submesh)
            {
                selectBtn.Modulate = new Color(1.0f, 0.85f, 0.4f, 1.0f);
            }
            selectBtn.Pressed += () =>
            {
                _meshHierarchy.SelectTarget(localSub, localSub.SurfaceIndex);
            };

            btnWrapper.AddChild(selectBtn);

            row.AddChild(visCheck);
            row.AddChild(soloBtn);
            row.AddChild(btnWrapper);

            _submeshListContainer.AddChild(row);

            _submeshRows.Add(new SubmeshRowUI
            {
                Submesh = submesh,
                RowContainer = row,
                VisCheck = visCheck,
                SoloBtn = soloBtn,
                SelectBtn = selectBtn
            });
        }
    }

    private void RefreshLayersListUI()
    {
        if (_layersListContainer == null || _layerManager == null) return;

        foreach (Node child in _layersListContainer.GetChildren())
        {
            child.QueueFree();
        }

        var layers = _layerManager.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            int layerIndex = i;
            var layer = layers[i];

            var row = new HBoxContainer();

            var visCheck = new CheckBox
            {
                ButtonPressed = layer.IsVisible,
                TooltipText = "Toggle layer visibility"
            };
            visCheck.Toggled += (vis) => _layerManager.SetLayerVisibility(layerIndex, vis);

            var btnWrapper = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.Fill,
                CustomMinimumSize = new Vector2(0, 26),
                ClipContents = true
            };

            var selectBtn = new Button
            {
                Text = layer.IsLocked ? $"🔒 {layer.Name}" : layer.Name,
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = layer.Name
            };
            selectBtn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            selectBtn.OffsetLeft = 0;
            selectBtn.OffsetTop = 0;
            selectBtn.OffsetRight = 0;
            selectBtn.OffsetBottom = 0;

            if (_layerManager.ActiveLayerIndex == layerIndex)
            {
                selectBtn.Modulate = new Color(1.0f, 0.85f, 0.4f, 1.0f);
            }

            selectBtn.Pressed += () => _layerManager.SelectLayer(layerIndex);
            btnWrapper.AddChild(selectBtn);

            EnsureRenameButtonStyles();
            _iconPencil ??= GD.Load<Texture2D>("res://assets/at-icons/pencil.svg");
            var renameBtn = new Button
            {
                Icon = _iconPencil,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                CustomMinimumSize = new Vector2(26, 26),
                TooltipText = "Rename layer",
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Disabled = layer.IsLocked
            };
            renameBtn.AddThemeStyleboxOverride("normal", _renameBtnNormalStyle);
            renameBtn.AddThemeStyleboxOverride("hover", _renameBtnHoverStyle);
            renameBtn.AddThemeStyleboxOverride("pressed", _renameBtnPressedStyle);
            renameBtn.AddThemeStyleboxOverride("disabled", _renameBtnDisabledStyle);
            renameBtn.AddThemeStyleboxOverride("focus", _renameBtnNormalStyle);

            int capturedIdx = layerIndex;
            renameBtn.Pressed += () => ShowRenameLayerDialog(capturedIdx);

            row.AddChild(visCheck);
            row.AddChild(btnWrapper);
            row.AddChild(renameBtn);

            _layersListContainer.AddChild(row);
        }

        UpdateActiveLayerUI();
    }

    private void OnLayerSelected(int index)
    {
        RefreshLayersListUI();
    }

    private void EnsureRenameLayerDialog()
    {
        if (_renameLayerDialog != null) return;

        _renameLayerDialog = new ConfirmationDialog
        {
            Title = "Rename Layer",
            OkButtonText = "Rename",
            CancelButtonText = "Cancel",
            MinSize = new Vector2I(360, 120),
            Size = new Vector2I(360, 120)
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        var lbl = new Label { Text = "Enter new layer name:" };
        vbox.AddChild(lbl);

        _renameLayerInput = new LineEdit
        {
            PlaceholderText = "Layer name",
            CustomMinimumSize = new Vector2(0, 30),
            SelectAllOnFocus = true
        };
        _renameLayerInput.TextSubmitted += (newText) =>
        {
            ConfirmRenameLayer();
            _renameLayerDialog.Hide();
            IsRenameDialogOpen = false;
        };
        vbox.AddChild(_renameLayerInput);

        _renameLayerDialog.AddChild(vbox);
        _renameLayerDialog.Confirmed += () =>
        {
            ConfirmRenameLayer();
            IsRenameDialogOpen = false;
        };
        _renameLayerDialog.Canceled += () =>
        {
            IsRenameDialogOpen = false;
        };
        _renameLayerDialog.CloseRequested += () =>
        {
            IsRenameDialogOpen = false;
        };
        _renameLayerDialog.VisibilityChanged += () =>
        {
            if (_renameLayerDialog != null)
                IsRenameDialogOpen = _renameLayerDialog.Visible;
        };
        AddChild(_renameLayerDialog);
    }

    private void ShowRenameLayerDialog(int index)
    {
        EnsureRenameLayerDialog();
        if (_layerManager == null || index < 0 || index >= _layerManager.Layers.Count) return;

        _layerIndexToRename = index;
        var layer = _layerManager.Layers[index];
        if (_renameLayerInput != null)
        {
            _renameLayerInput.Text = layer.Name;
            _renameLayerInput.SelectAll();
        }
        IsRenameDialogOpen = true;
        _renameLayerDialog.PopupCentered(new Vector2I(360, 120));
        _renameLayerInput?.GrabFocus();
    }

    private void ConfirmRenameLayer()
    {
        if (_layerManager == null || _layerIndexToRename < 0) return;
        string newName = _renameLayerInput?.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            _layerManager.RenameLayer(_layerIndexToRename, newName);
        }
        _layerIndexToRename = -1;
    }

    private void UpdateActiveLayerUI()
    {
        if (_layerManager == null) return;
        var activeLayer = _layerManager.ActiveLayer;
        if (activeLayer == null) return;

        if (_optLayerBlend != null)
        {
            _optLayerBlend.Select((int)activeLayer.BlendMode);
            _optLayerBlend.Disabled = activeLayer.IsLocked;
        }

        if (_sliderLayerOpacity != null)
        {
            _sliderLayerOpacity.SetValueNoSignal(activeLayer.Opacity);
            _sliderLayerOpacity.Editable = !activeLayer.IsLocked;
            if (_lblLayerOpacity != null)
            {
                _lblLayerOpacity.Text = $"{Mathf.RoundToInt(activeLayer.Opacity * 100)}%";
            }
        }

        _painter?.SyncCameraBrushProperties(force: true);
    }

    private void UpdateControlsState(bool hasHero)
    {
        if (_optTargetMesh != null) _optTargetMesh.Disabled = !hasHero;
        if (_optResolution != null) _optResolution.Disabled = !hasHero;
        if (_btnShowAllMeshes != null) _btnShowAllMeshes.Disabled = !hasHero;
        if (_btnHideAccMeshes != null) _btnHideAccMeshes.Disabled = !hasHero;
        if (_btnAddLayer != null) _btnAddLayer.Disabled = !hasHero;
        if (_btnDeleteLayer != null) _btnDeleteLayer.Disabled = !hasHero;
        if (_btnMoveUp != null) _btnMoveUp.Disabled = !hasHero;
        if (_btnMoveDown != null) _btnMoveDown.Disabled = !hasHero;
        if (_btnExportPng != null) _btnExportPng.Disabled = !hasHero;
        if (_btnExportVmat != null) _btnExportVmat.Disabled = !hasHero;
        if (_btnInstallCitadel != null) _btnInstallCitadel.Disabled = !hasHero;
    }

    public void OnTabActivated()
    {
        CacheCharacterPose();
        ResetCharacterPose();
        if (_meshHierarchy != null)
        {
            _meshHierarchy.IsPaintingModeActive = true;
            _meshHierarchy.ApplyToonShading(false);

            if (_hasCachedPaintVisibility && _cachedPaintSubmeshVisibility != null)
            {
                _meshHierarchy.RestoreVisibilityState(_cachedPaintSubmeshVisibility, _cachedPaintPreSoloVisibility, _cachedPaintSoloedSubmesh);
            }
        }
        if (_painter != null)
        {
            _painter.IsPaintingActive = true;
        }
        if (_brushPalette != null)
        {
            _brushPalette.Visible = true;
            _brushPalette.SyncFromPainter();
        }
        _layerManager?.RebuildBaseAtlasBuffer();
    }

    public void OnTabDeactivated()
    {
        if (_meshHierarchy != null)
        {
            _meshHierarchy.IsPaintingModeActive = false;
            if (UserSettings.ToonEnabled)
            {
                _meshHierarchy.ApplyToonShading(true);
            }

            // 1. Cache paint tab submesh visibility and solo state
            _meshHierarchy.CacheVisibilityState(out _cachedPaintSubmeshVisibility, out _cachedPaintPreSoloVisibility, out _cachedPaintSoloedSubmesh);
            _hasCachedPaintVisibility = true;
        }
        if (_painter != null)
        {
            _painter.OnTabDeactivated();
        }
        if (_brushPalette != null)
        {
            _brushPalette.Visible = false;
        }
        _decalStamper?.HidePreview();

        // 2. Restore character tab mesh visibility onto the character model
        if (_currentHero != null && GodotObject.IsInstanceValid(_currentHero))
        {
            RestoreCharacterTabMeshVisibility(_currentHero);
        }

        RestoreCharacterPose();
    }

    private void RestoreCharacterTabMeshVisibility(Node node)
    {
        if (node == null) return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            if (mi.HasMeta("ParentCompositeMesh"))
            {
                var parent = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                if (parent != null && GodotObject.IsInstanceValid(parent))
                {
                    bool parentVis = parent.HasMeta("UserVisibility") ? parent.GetMeta("UserVisibility").AsBool() : parent.Visible;
                    mi.Visible = parentVis;
                }
            }
            else if (mi.HasMeta("IsHiddenComposite"))
            {
                // Composite original mesh MUST remain hidden to prevent duplicate rendering with split submeshes
                mi.Visible = false;
                bool compositeUserVis = mi.HasMeta("UserVisibility") ? mi.GetMeta("UserVisibility").AsBool() : true;

                if (mi.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = mi.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    foreach (var sub in genList)
                    {
                        if (sub != null && GodotObject.IsInstanceValid(sub))
                        {
                            sub.Visible = compositeUserVis;
                        }
                    }
                }
            }
            else
            {
                bool userVis = mi.HasMeta("UserVisibility") ? mi.GetMeta("UserVisibility").AsBool() : mi.Visible;
                mi.Visible = userVis;
            }
        }

        foreach (Node child in node.GetChildren())
        {
            RestoreCharacterTabMeshVisibility(child);
        }
    }

    private void CacheCharacterPose()
    {
        if (_currentHero == null) return;

        _cachedBonePoses.Clear();
        _cachedAnimationName = null;
        _cachedAnimationPosition = 0.0;
        _cachedAnimationPlaying = false;
        _hasCachedPose = false;

        var animPlayer = SearchAnimationPlayer(_currentHero);
        if (animPlayer != null && animPlayer.IsPlaying())
        {
            _cachedAnimationName = animPlayer.CurrentAnimation;
            _cachedAnimationPosition = animPlayer.CurrentAnimationPosition;
            _cachedAnimationPlaying = true;
            animPlayer.Pause();
        }
        else if (animPlayer != null && !string.IsNullOrEmpty(animPlayer.AssignedAnimation))
        {
            _cachedAnimationName = animPlayer.AssignedAnimation;
            _cachedAnimationPosition = animPlayer.CurrentAnimationPosition;
            _cachedAnimationPlaying = false;
        }

        var skeleton = SearchSkeleton(_currentHero);
        if (skeleton != null)
        {
            int count = skeleton.GetBoneCount();
            for (int i = 0; i < count; i++)
            {
                _cachedBonePoses[i] = new CachedBoneTransform
                {
                    Position = skeleton.GetBonePosePosition(i),
                    Rotation = skeleton.GetBonePoseRotation(i),
                    Scale = skeleton.GetBonePoseScale(i)
                };
            }
            _hasCachedPose = true;
        }
    }

    private void RestoreCharacterPose()
    {
        if (_currentHero == null || !_hasCachedPose) return;

        var animPlayer = SearchAnimationPlayer(_currentHero);
        if (animPlayer != null && !string.IsNullOrEmpty(_cachedAnimationName) && animPlayer.HasAnimation(_cachedAnimationName))
        {
            animPlayer.Play(_cachedAnimationName);
            animPlayer.Seek(_cachedAnimationPosition, update: true);
            if (!_cachedAnimationPlaying)
            {
                animPlayer.Pause();
            }
        }

        var skeleton = SearchSkeleton(_currentHero);
        if (skeleton != null)
        {
            foreach (var kvp in _cachedBonePoses)
            {
                int boneIdx = kvp.Key;
                if (boneIdx < skeleton.GetBoneCount())
                {
                    skeleton.SetBonePosePosition(boneIdx, kvp.Value.Position);
                    skeleton.SetBonePoseRotation(boneIdx, kvp.Value.Rotation);
                    skeleton.SetBonePoseScale(boneIdx, kvp.Value.Scale);
                }
            }
            skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.Conform(skeleton);

            Node3D heroRoot = skeleton;
            while (heroRoot != null && !heroRoot.Name.ToString().StartsWith("Hero_") && heroRoot.GetParent() is Node3D parent3D)
            {
                heroRoot = parent3D;
            }
            if (!string.IsNullOrEmpty(_cachedAnimationName))
            {
                DeadlockMaterialResolver.OnPoseChanged(heroRoot, heroRoot?.Name.ToString() ?? "", _cachedAnimationName);
            }
        }

        _hasCachedPose = false;
        _cachedBonePoses.Clear();
    }

    private void ResetCharacterPose()
    {
        if (_currentHero == null) return;
        var skeleton = SearchSkeleton(_currentHero);
        if (skeleton != null)
        {
            var ikManager = skeleton.GetNodeOrNull<CharacterIKManager>("CharacterIKManager");
            ikManager?.SetMasterIKEnabled(false);

            for (int i = 0; i < skeleton.GetBoneCount(); i++)
            {
                skeleton.ResetBonePose(i);
            }
            skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.Conform(skeleton);
        }
    }

    private Skeleton3D SearchSkeleton(Node node)
    {
        if (node is Skeleton3D sk) return sk;
        foreach (Node child in node.GetChildren())
        {
            var res = SearchSkeleton(child);
            if (res != null) return res;
        }
        return null;
    }

    private AnimationPlayer SearchAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap) return ap;
        foreach (Node child in node.GetChildren())
        {
            var res = SearchAnimationPlayer(child);
            if (res != null) return res;
        }
        return null;
    }

    private ExportModDialog _modDialogInstance;
    private ExportProgressDialog _progressDialogInstance;

    public void OpenExportModDialog()
    {
        if (_layerManager == null) return;
        var preBaked = _layerManager.BakeCompositeImage();

        string heroCodename = _currentHero != null && _currentHero.HasMeta("HeroCodename")
            ? _currentHero.GetMeta("HeroCodename").AsString()
            : _currentHero?.Name.ToString().Replace("Hero_", "").ToLowerInvariant() ?? "hero";

        string heroDisplayName = _currentHero != null && _currentHero.HasMeta("HeroDisplayName")
            ? _currentHero.GetMeta("HeroDisplayName").AsString()
            : char.ToUpperInvariant(heroCodename[0]) + heroCodename.Substring(1);

        var pathManager = new GamePathManager();
        pathManager.LoadConfig();

        if (_modDialogInstance == null || !GodotObject.IsInstanceValid(_modDialogInstance))
        {
            var dialogScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportModDialog.tscn");
            if (dialogScene != null)
            {
                _modDialogInstance = dialogScene.Instantiate<ExportModDialog>();
                GetTree().Root.AddChild(_modDialogInstance);
                _modDialogInstance.ExportConfirmed += (config) =>
                {
                    ExecuteExportModPipeline(config);
                };
            }
        }

        if (_modDialogInstance != null)
        {
            _modDialogInstance.Setup(heroCodename, heroDisplayName, _meshHierarchy?.Submeshes, preBaked, pathManager);
            _modDialogInstance.Visible = true;
        }
    }

    private async void ExecuteExportModPipeline(ModExportConfig config)
    {
        if (config == null) return;

        if (_progressDialogInstance == null || !GodotObject.IsInstanceValid(_progressDialogInstance))
        {
            var progressScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportProgressDialog.tscn");
            if (progressScene != null)
            {
                _progressDialogInstance = progressScene.Instantiate<ExportProgressDialog>();
                GetTree().Root.AddChild(_progressDialogInstance);
            }
        }

        if (_progressDialogInstance == null) return;

        var cts = new System.Threading.CancellationTokenSource();
        string targetDir = config.InstallDirectlyToGame
            ? Path.Combine(config.DeadlockGamePath, "game", "citadel", "addons")
            : config.CustomExportFolder;

        _progressDialogInstance.StartExport(cts, targetDir);

        try
        {
            _exporter ??= new SkinExporter(_layerManager);
            var res = await _exporter.ExportModAsync(config, _progressDialogInstance, cts.Token);
            _progressDialogInstance.OnExportFinished(res);

            if (_lblExportStatus != null && res != null)
            {
                _lblExportStatus.Text = res.Success
                    ? $"Exported: {Path.GetFileName(res.VpkPath)}"
                    : $"Export Failed: {res.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            _progressDialogInstance.OnExportFinished(new SkinExportResult
            {
                Success = false,
                ErrorMessage = "Operation was canceled by user."
            });
        }
        catch (Exception ex)
        {
            _progressDialogInstance.OnExportFinished(new SkinExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }
}
