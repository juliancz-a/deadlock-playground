using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Materials;
using DeadlockPlayground.UI;
using DeadlockPlayground.Particles;
using DeadlockPlayground.Workspace;
using DeadlockPlayground.Catalog;

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
    private ParticleStage3D _particleStage;
    private VBoxContainer _particleTexturesGroupContainer;

    private Node3D _currentHero;
    private string _currentHeroId = "";
    private string _currentHeroDisplayName = "";
    private bool _currentHeroIsExtra = false;
    private string _currentHeroVmdlPath = "";
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
    public bool IsTabActive { get; private set; } = false;
    private bool _wasShowingParticle = false;

    private class SubmeshRowUI
    {
        public SubmeshNodeInfo Submesh;
        public HBoxContainer RowContainer;
        public CheckBox VisCheck;
        public Button SoloBtn;
        public Button SelectBtn;
    }

    private class MaterialGroupRowUI
    {
        public MaterialGroup Group;
        public VBoxContainer GroupContainer;
        public PanelContainer HeaderPanel;
        public HBoxContainer HeaderRow;
        public Button ToggleBtn;
        public Label TitleLabel;
        public Label BadgeLabel;
        public Button PaintBtn;
        public VBoxContainer ChildrenContainer;
        public List<SubmeshRowUI> SubmeshRows = new();
    }

    private readonly List<MaterialGroupRowUI> _materialGroupRows = new();
    private readonly List<SubmeshRowUI> _submeshRows = new();
    private StyleBoxFlat _groupHeaderStyle;

    private void EnsureGroupHeaderStyle()
    {
        if (_groupHeaderStyle != null) return;
        _groupHeaderStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.17f, 0.20f, 0.85f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
    }

    public SkinLayerManager LayerManager => _layerManager;
    public HeroMeshHierarchy MeshHierarchy => _meshHierarchy;
    public MeshPainter3D Painter => _painter;
    public FloatingBrushPaletteUI BrushPalette => _brushPalette;

    // --- UI Controls ---
    // Header
    private Label _lblHeroName;
    private Label _lblActiveMesh;
    private OptionButton _optTargetMesh;
    private Label _lblCanvasResolution;
    private Texture2D _iconPencil;

    // Project Save / Load (.dptex)
    private Button _btnSaveProject;
    private Button _btnLoadProject;
    private FileDialog _saveProjectDialog;
    private FileDialog _loadProjectDialog;
    private ConfirmationDialog _confirmHeroMismatchDialog;
    private string _pendingLoadProjectPath;

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
    public static bool IsRenameDialogOpen { get; private set; }
    private int _layerIndexToRename = -1;
    private string _activeParticleVtexPath = null;

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

        _painter.StrokeFinished += () =>
        {
            RegisterActiveStrokeMetadata();
            if (_layerManager != null)
            {
                _brushPalette?.UpdateUndoRedoState(_layerManager.CanUndo, _layerManager.CanRedo);
            }
        };
        _decalStamper.DecalBaked += () => RegisterActiveStrokeMetadata();
        _textProjector.TextBaked += () => RegisterActiveStrokeMetadata();

        if (_brushPalette != null)
        {
            _brushPalette.Setup(_painter, _decalStamper, _textProjector);
            _brushPalette.UndoRequested += () =>
            {
                if (_layerManager != null && _layerManager.CanUndo)
                {
                    _layerManager.Undo();
                    RegisterActiveStrokeMetadata();
                }
            };
            _brushPalette.RedoRequested += () =>
            {
                if (_layerManager != null && _layerManager.CanRedo)
                {
                    _layerManager.Redo();
                    RegisterActiveStrokeMetadata();
                }
            };
            _brushPalette.ClearRequested += () =>
            {
                _layerManager?.ClearCurrentLayer();
                RegisterActiveStrokeMetadata();
            };
            _brushPalette.BlendModeChanged += (mode) =>
            {
                _layerManager?.SetOverlayBlendMode(mode);
                _painter?.SyncCameraBrushProperties(force: true);
                RegisterActiveStrokeMetadata();
            };
            _brushPalette.FillRequested += (color) =>
            {
                _layerManager?.FillCurrentSubmesh(color, null, _painter?.MagicWandTool, _painter != null ? _painter.BucketFillTolerance : 0.5f);
                RegisterActiveStrokeMetadata();
            };
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
        _lblCanvasResolution = GetNodeOrNull<Label>("HeaderCard/VBox/HBoxRes/ResolutionLabel");
        _btnSaveProject = GetNodeOrNull<Button>("HeaderCard/VBox/HBoxProject/BtnSaveProject");
        _btnLoadProject = GetNodeOrNull<Button>("HeaderCard/VBox/HBoxProject/BtnLoadProject");
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
        if (_exportFileDialog != null)
        {
            _exportFileDialog.Transient = true;
            _exportFileDialog.Exclusive = true;
        }

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
            _optLayerBlend.AddItem("Color (HSL)", (int)LayerBlendMode.Color);
            _optLayerBlend.Select(0);
        }

        UpdateCanvasResolutionLabel();
    }

    public void UpdateCanvasResolutionLabel()
    {
        if (_lblCanvasResolution != null && _layerManager != null)
        {
            _lblCanvasResolution.Text = $"{_layerManager.CanvasSize.X} x {_layerManager.CanvasSize.Y}";
        }
    }

    private void OnSaveProjectPressed()
    {
        if (_saveProjectDialog == null)
        {
            _saveProjectDialog = new FileDialog
            {
                Title = "Save Deadlock Playground Project (.dpp)",
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new string[] { "*.dpp ; Deadlock Playground Project (*.dpp)" },
                UseNativeDialog = true
            };
            _saveProjectDialog.FileSelected += (path) =>
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!path.EndsWith(".dpp", StringComparison.OrdinalIgnoreCase))
                {
                    path += ".dpp";
                }

                FlushActiveChangesToWorkspace(true);
                bool ok = ModWorkspaceSession.Instance.PackageProject(path);
                if (ok)
                {
                    if (_lblExportStatus != null) _lblExportStatus.Text = $"Saved project: {System.IO.Path.GetFileName(path)}";
                    _studioUI?.ShowToast($"Project Saved: {System.IO.Path.GetFileName(path)}", path);
                }
                else
                {
                    if (_lblExportStatus != null) _lblExportStatus.Text = "Error saving project.";
                }
            };
            AddChild(_saveProjectDialog);
        }

        string defaultHeroName = !string.IsNullOrEmpty(_currentHeroId)
            ? _currentHeroId
            : (_currentHero != null ? _currentHero.Name.ToString().Replace("Hero_", "").ToLowerInvariant() : "workspace");
        _saveProjectDialog.CurrentFile = $"{defaultHeroName}_project.dpp";
        _saveProjectDialog.PopupCentered(new Vector2I(750, 500));
    }

    private void OnLoadProjectPressed()
    {
        if (_loadProjectDialog == null)
        {
            _loadProjectDialog = new FileDialog
            {
                Title = "Load Deadlock Playground Project (.dpp)",
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new string[] { "*.dpp ; Deadlock Playground Project (*.dpp)" },
                UseNativeDialog = true
            };
            _loadProjectDialog.FileSelected += (path) =>
            {
                ExecuteLoadProject(path);
            };
            AddChild(_loadProjectDialog);
        }

        _loadProjectDialog.PopupCentered(new Vector2I(750, 500));
    }

    private void HandleProjectFileSelected(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

        var manifest = ProjectFileManager.PeekManifest(path);
        if (manifest != null && !string.IsNullOrWhiteSpace(manifest.HeroId))
        {
            string currentHeroName = _currentHero != null ? _currentHero.Name.ToString().Replace("Hero_", "").Trim() : "";
            string projHeroName = manifest.HeroId.Replace("Hero_", "").Trim();

            if (!string.IsNullOrEmpty(currentHeroName) && !currentHeroName.Equals(projHeroName, StringComparison.OrdinalIgnoreCase))
            {
                _pendingLoadProjectPath = path;
                ShowHeroMismatchDialog(projHeroName, currentHeroName);
                return;
            }
        }

        ExecuteLoadProject(path);
    }

    private void ShowHeroMismatchDialog(string projHero, string currentHero)
    {
        if (_confirmHeroMismatchDialog == null)
        {
            _confirmHeroMismatchDialog = new ConfirmationDialog
            {
                Title = "Hero Mismatch Warning",
                OkButtonText = "Load Anyway",
                CancelButtonText = "Cancel",
                Transient = true,
                Exclusive = true
            };
            _confirmHeroMismatchDialog.Confirmed += () =>
            {
                if (!string.IsNullOrEmpty(_pendingLoadProjectPath))
                {
                    ExecuteLoadProject(_pendingLoadProjectPath);
                    _pendingLoadProjectPath = null;
                }
            };
            _confirmHeroMismatchDialog.Canceled += () =>
            {
                _pendingLoadProjectPath = null;
            };
            _confirmHeroMismatchDialog.CloseRequested += () =>
            {
                _pendingLoadProjectPath = null;
            };
            AddChild(_confirmHeroMismatchDialog);
        }

        _confirmHeroMismatchDialog.DialogText = $"This project was created for hero '{projHero.ToUpperInvariant()}', but the current hero is '{currentHero.ToUpperInvariant()}'.\n\nLoading it may cause UV and texture misalignments. Do you wish to continue?";
        _confirmHeroMismatchDialog.PopupCentered(new Vector2I(460, 180));
    }

    public bool IsAnyDialogOpen()
    {
        return (_saveProjectDialog != null && _saveProjectDialog.Visible)
            || (_loadProjectDialog != null && _loadProjectDialog.Visible)
            || (_confirmHeroMismatchDialog != null && _confirmHeroMismatchDialog.Visible)
            || (_exportFileDialog != null && _exportFileDialog.Visible)
            || (_renameLayerDialog != null && _renameLayerDialog.Visible)
            || (_modDialogInstance != null && _modDialogInstance.Visible)
            || (_progressDialogInstance != null && _progressDialogInstance.Visible)
            || IsRenameDialogOpen;
    }

    public async void ExecuteLoadProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

        string fileName = System.IO.Path.GetFileName(path);
        _studioUI?.ShowLoading(true, $"Loading project {fileName}...");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        try
        {
            // 1. Purgar estado sucio en memoria ANTES de descomprimir o recargar el héroe,
            _lastRegisteredDirtyMatKey = null;
            _flushedMaterialKeys.Clear();
            if (_meshHierarchy?.Submeshes != null)
            {
                foreach (var sm in _meshHierarchy.Submeshes)
                    sm.IsDirty = false;
            }
            _layerManager?.ClearAllLayers();

            // 2. Descomprimir el proyecto limpio en el workspace
            if (!ModWorkspaceSession.Instance.UnpackProject(path))
            {
                if (_lblExportStatus != null) _lblExportStatus.Text = "Error: Failed to unpack .dpp project";
                return;
            }

            var manifest = ModWorkspaceSession.Instance.Manifest;
            string targetHero = manifest?.LastActiveHeroId;
            string targetPath = manifest?.LastActiveAssetPath;
            string targetType = manifest?.LastActiveAssetType;

            // Si no había LastActiveHeroId, tomar el primer héroe del manifiesto
            if (string.IsNullOrEmpty(targetHero) && manifest?.Heroes != null && manifest.Heroes.Count > 0)
            {
                var firstHero = manifest.Heroes.Values.First();
                targetHero = firstHero.HeroId;
                var firstAsset = firstHero.Assets.FirstOrDefault();
                if (firstAsset != null)
                {
                    targetPath = firstAsset.VpkPath;
                    targetType = firstAsset.Category;
                }
            }

            var vpkLoader = GetVpkLoader();
            if (vpkLoader != null && !string.IsNullOrEmpty(targetHero))
            {
                var stage = GetOrCreateParticleStage();
                if (stage != null && stage.HasActiveParticle)
                {
                    stage.UnloadActiveParticle();
                }

                if (targetType == "ExtraModel" && !string.IsNullOrEmpty(targetPath))
                {
                    await vpkLoader.LoadExtraModelAsync(targetPath, targetHero, System.IO.Path.GetFileNameWithoutExtension(targetPath));
                }
                else
                {
                    var catEntry = DeadlockPlayground.Catalog.DeadlockHeroCatalog.GetByCodename(targetHero)
                                ?? DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero(targetHero);
                    if (catEntry != null)
                    {
                        await vpkLoader.LoadHeroModelAsync(catEntry);
                    }
                    else
                    {
                        await vpkLoader.LoadHeroAsync(targetHero, targetHero);
                    }

                    if (targetType == "ParticleVFX" && !string.IsNullOrEmpty(targetPath))
                    {
                        var charTab = GetTree()?.Root?.FindChild("CharacterTab", true, false) as CharacterTabUI;
                        charTab?.LoadParticleDirectly(targetPath, ownerHeroId: targetHero);
                    }
                }
            }

            ModWorkspaceSession.Instance.HasUnsavedChanges = false;
            if (_lblExportStatus != null) _lblExportStatus.Text = $"Loaded project: {fileName}";
            _studioUI?.ShowToast($"Project Loaded: {fileName}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PaintTabUI] Error loading project: {ex.Message}");
        }
        finally
        {
            _studioUI?.ShowLoading(false);
        }
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
            _layerManager.StackChanged += () =>
            {
                RefreshLayersListUI();
                _brushPalette?.UpdateUndoRedoState(_layerManager.CanUndo, _layerManager.CanRedo);

                if (_layerManager.IsStandaloneTextureMode && !string.IsNullOrEmpty(_activeParticleVtexPath)
                    && _layerManager.HasPaintStrokes())
                {
                    var st = GetOrCreateParticleStage();
                    var baked = _layerManager.BakeCompositeImage();
                    if (st != null && baked != null)
                    {
                        st.UpdateParticleTexture(_activeParticleVtexPath, baked);
                    }
                    var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                                ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
                    uvCanvas?.QueueCanvasRedraw();
                }
            };
            _layerManager.LayerSelected += OnLayerSelected;
        }

        // Workspace session synchronization events
        ModWorkspaceSession.Instance.OnAssetDiscarded += OnWorkspaceAssetDiscarded;
        ModWorkspaceSession.Instance.OnWorkspaceCleared += OnWorkspaceCleared;

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



        if (_btnSaveProject != null)
        {
            _btnSaveProject.Pressed += OnSaveProjectPressed;
        }

        if (_btnLoadProject != null)
        {
            _btnLoadProject.Pressed += OnLoadProjectPressed;
        }

        // Submesh Actions
        if (_btnShowAllMeshes != null)
        {
            _btnShowAllMeshes.Pressed += () =>
            {
                var pStage = GetOrCreateParticleStage();
                if (pStage != null && pStage.HasActiveParticle)
                {
                    for (int i = 0; i < pStage.Renderers.Count; i++)
                    {
                        pStage.SetRendererVisibility(i, true);
                    }
                    UpdateParticleTexturesGroup();
                }
                else
                {
                    _meshHierarchy?.ShowAll();
                }
            };
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
            _btnDeleteLayer.TooltipText = "Delete active layer\nShift+Click: Delete across all materials\nRight-Click: Options";
            _btnDeleteLayer.Pressed += () =>
            {
                if (_layerManager == null) return;
                if (Input.IsKeyPressed(Key.Shift))
                {
                    _layerManager.DeleteActiveLayerAcrossAllMaterials();
                    ModWorkspaceSession.Instance.CleanupTempSession();
                }
                else
                {
                    string matKey = _layerManager.ActiveMaterialKey;
                    _layerManager.DeleteActiveLayer();
                    CheckAndUnregisterIfMaterialClean(matKey);
                }
            };
            _btnDeleteLayer.GuiInput += (ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    if (_layerManager != null)
                    {
                        ShowLayerContextMenu(_layerManager.ActiveLayerIndex, mb.GlobalPosition);
                    }
                }
            };
        }
        if (_btnMoveUp != null)
        {
            _btnMoveUp.Pressed += () =>
            {
                if (_layerManager == null) return;
                int idx = _layerManager.ActiveLayerIndex;
                if (idx >= 0 && idx < _layerManager.Layers.Count - 1) _layerManager.MoveLayer(idx, idx + 1);
            };
        }
        if (_btnMoveDown != null)
        {
            _btnMoveDown.Pressed += () =>
            {
                if (_layerManager == null) return;
                int idx = _layerManager.ActiveLayerIndex;
                if (idx > 0) _layerManager.MoveLayer(idx, idx - 1);
            };
        }

     if (_optLayerBlend != null)
        {
            _optLayerBlend.ItemSelected += (idx) =>
            {
                if (_layerManager == null) return;
                if (_layerManager.ActiveLayer != null && _layerManager.ActiveLayer.BlendMode == (LayerBlendMode)idx) return;
                _layerManager.SetLayerBlendMode(_layerManager.ActiveLayerIndex, (LayerBlendMode)idx);
                _painter?.SyncCameraBrushProperties(force: true);
                RegisterActiveStrokeMetadata();
            };
        }

        if (_sliderLayerOpacity != null)
        {
            _sliderLayerOpacity.ValueChanged += (val) =>
            {
                if (_layerManager == null) return;
                if (_layerManager.ActiveLayer != null && Mathf.Abs(_layerManager.ActiveLayer.Opacity - (float)val) < 0.005f) return;
                _layerManager.SetLayerOpacity(_layerManager.ActiveLayerIndex, (float)val);
                if (_lblLayerOpacity != null) _lblLayerOpacity.Text = $"{Mathf.RoundToInt(val * 100)}%";
                _painter?.SyncCameraBrushProperties(force: true);
                RegisterActiveStrokeMetadata();
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
        if (_currentHero != null && _currentHero != heroNode)
        {
            FlushActiveChangesToWorkspace(true);
        }

        _currentHero = heroNode;
        _hasCachedPaintVisibility = false;
        _cachedPaintSubmeshVisibility = null;
        _cachedPaintPreSoloVisibility = null;
        _cachedPaintSoloedSubmesh = null;
        _lastRegisteredDirtyMatKey = null;
        _flushedMaterialKeys.Clear();

        if (heroNode == null)
        {
            ClearHero();
            return;
        }

        string heroName = heroNode.Name.ToString().Replace("Hero_", "");
        if (_lblHeroName != null) _lblHeroName.Text = $"HERO: {heroName.ToUpperInvariant()}";

        // 1. Scan submesh hierarchy (generates UV2 unwraps)
        _meshHierarchy?.ScanHero(heroNode);

        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _meshHierarchy?.HideAll();
            HideAllMeshesRecursive(heroNode);
        }
        else if (_painter != null && _painter.IsPaintingActive)
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
        _painter?.ResetSession();
        _layerManager?.ClearHistory();
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

        // 5. Update canvas resolution label
        UpdateCanvasResolutionLabel();

        // 6. Refresh submesh list UI
        RefreshSubmeshListUI();

        UpdateControlsState(true);

        if (_brushPalette != null)
        {
            _brushPalette.SyncFromPainter();
        }

        var vpkLoader = GetVpkLoader();
        _currentHeroId = vpkLoader?.LastLoadedHeroEntry?.InternalCodename 
                      ?? vpkLoader?.BaseHeroEntry?.InternalCodename 
                      ?? vpkLoader?.LastLoadedHeroKey 
                      ?? heroName.ToLowerInvariant();
        _currentHeroDisplayName = vpkLoader?.LastLoadedHeroEntry?.DisplayName 
                               ?? vpkLoader?.BaseHeroEntry?.DisplayName 
                               ?? vpkLoader?.LastLoadedDisplayName 
                               ?? _currentHeroId;
        _currentHeroIsExtra = vpkLoader?.IsExtraModelLoaded ?? false;

        string catalogVmdl = vpkLoader?.LastLoadedVmdlPath
                          ?? vpkLoader?.LastLoadedHeroEntry?.VmdlRelativePath
                          ?? vpkLoader?.BaseHeroEntry?.VmdlRelativePath
                          ?? DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero(_currentHeroId)?.VmdlRelativePath;

        if (string.IsNullOrEmpty(catalogVmdl))
        {
            string heroFolder = SkinExporter.ResolveHeroInternalFolder(_currentHeroId, _meshHierarchy?.Submeshes?.ToList());
            catalogVmdl = $"{heroFolder}/{_currentHeroId}.vmdl_c";
        }
        _currentHeroVmdlPath = catalogVmdl;

        RestoreStagedTexturesFromWorkspace(_currentHeroId);
    }


    private VpkLoaderTest _cachedVpkLoader;
    private VpkLoaderTest GetVpkLoader()
    {
        if (_cachedVpkLoader == null || !GodotObject.IsInstanceValid(_cachedVpkLoader))
        {
            _cachedVpkLoader = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }
        return _cachedVpkLoader;
    }
    private string _lastRegisteredDirtyMatKey = null;

    public void RegisterActiveStrokeMetadata()
    {
        try
        {
            if (_layerManager == null) return;
            _layerManager.SetActiveContextHasPaintStrokes(true);

            string matKey = _layerManager.ActiveMaterialKey;
            _flushedMaterialKeys.Remove(matKey ?? "");

            if (_meshHierarchy != null)
            {
                if (!string.IsNullOrEmpty(matKey))
                {
                    _meshHierarchy.MarkMaterialDirty(matKey, _layerManager);
                }
                else if (_layerManager.TargetMesh != null)
                {
                    _meshHierarchy.MarkSubmeshDirty(_layerManager.TargetMesh);
                }
            }

            UpdateSubmeshRowHighlights();

            if (_particleStage != null && _particleStage.HasActiveParticle && !string.IsNullOrEmpty(_activeParticleVtexPath))
            {
                string pHeroId = !string.IsNullOrEmpty(_particleStage.ActiveOwnerHeroId)
                    ? _particleStage.ActiveOwnerHeroId
                    : (!string.IsNullOrEmpty(_currentHeroId) ? _currentHeroId : "particles");
                string pHeroDisplay = !string.IsNullOrEmpty(_currentHeroDisplayName) ? _currentHeroDisplayName : pHeroId;

                ModWorkspaceSession.Instance.RegisterPendingModifiedTexture(
                    pHeroId,
                    pHeroDisplay,
                    "ParticleVFX",
                    _particleStage.ActiveVpcfPath,
                    _activeParticleVtexPath,
                    System.IO.Path.GetFileName(_activeParticleVtexPath),
                    _activeParticleVtexPath);
            }
            else if (_currentHero != null && _meshHierarchy != null)
            {
                string heroId = !string.IsNullOrEmpty(_currentHeroId) ? _currentHeroId : _currentHero.Name.ToString().Replace("Hero_", "").ToLowerInvariant();
                string heroDisplayName = !string.IsNullOrEmpty(_currentHeroDisplayName) ? _currentHeroDisplayName : heroId;
                bool isExtra = _currentHeroIsExtra;
                string category = isExtra ? "ExtraModel" : "CharacterModel";
                string ownerPath = !string.IsNullOrEmpty(_currentHeroVmdlPath) ? _currentHeroVmdlPath : $"models/heroes_staging/{heroId}/{heroId}.vmdl_c";

                foreach (var sm in _meshHierarchy.Submeshes)
                {
                    if (sm.IsDirty && sm.Mesh != null)
                    {
                        string smMatKey = _layerManager.GetMaterialKey(sm.Mesh, sm.SurfaceIndex);
                        if (string.IsNullOrEmpty(matKey) || string.Equals(smMatKey, matKey, StringComparison.OrdinalIgnoreCase))
                        {
                            string vtexPath = sm.OriginalColorVtexCPath ?? sm.OriginalVmatPath;
                            ModWorkspaceSession.Instance.RegisterPendingModifiedTexture(
                                heroId,
                                heroDisplayName,
                                category,
                                ownerPath,
                                sm.OriginalVmatPath,
                                sm.DisplayName,
                                vtexPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PaintTabUI] Error registering active stroke metadata: {ex.Message}");
        }
    }

    public void FlushActiveChangesToWorkspace(bool syncDisk = true)
    {
        try
        {
            if (syncDisk)
            {
                ExecuteWorkspaceFlush();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PaintTabUI] Error triggering workspace flush: {ex.Message}");
        }
    }

    private readonly HashSet<string> _flushedMaterialKeys = new(StringComparer.OrdinalIgnoreCase);

  private void ExecuteWorkspaceFlush()
    {
        try
        {
            int flushedCount = 0;
            string toastSubtitle = "";

            // 1. Flush character model / extra model paint changes
            if (_currentHero != null && _meshHierarchy != null && _layerManager != null)
            {
                _layerManager.SaveActiveContext();
                _meshHierarchy.ReconcileDirtyStates(_layerManager);
                var dirtySubmeshes = _meshHierarchy.Submeshes.Where(s => s.IsDirty).ToList();
                if (dirtySubmeshes.Count > 0)
                {
                    string heroId = !string.IsNullOrEmpty(_currentHeroId)
                        ? _currentHeroId
                        : _currentHero.Name.ToString().Replace("Hero_", "").ToLowerInvariant();
                    string heroDisplayName = !string.IsNullOrEmpty(_currentHeroDisplayName)
                        ? _currentHeroDisplayName
                        : heroId;
                    bool isExtra = _currentHeroIsExtra;
                    string category = isExtra ? "ExtraModel" : "CharacterModel";
                    string ownerPath = !string.IsNullOrEmpty(_currentHeroVmdlPath)
                        ? _currentHeroVmdlPath
                        : $"models/heroes_staging/{heroId}/{heroId}.vmdl_c";
                    toastSubtitle = heroDisplayName;

                    var processedMatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sm in dirtySubmeshes)
                    {
                        string matKey = _layerManager.GetMaterialKey(sm.Mesh, sm.SurfaceIndex);
                        if (!string.IsNullOrEmpty(matKey) && !processedMatKeys.Add(matKey))
                        {
                            continue; // Already baked this material!
                        }
                        if (!string.IsNullOrEmpty(matKey) && _flushedMaterialKeys.Contains(matKey))
                        {
                            continue; // Already flushed to disk and no new strokes since!
                        }

                        var baked = _layerManager.BakeCompositeImageForSubmesh(sm);
                        if (baked != null && !baked.IsEmpty())
                        {
                            string vtexPath = sm.OriginalColorVtexCPath ?? sm.OriginalVmatPath;
                            ModWorkspaceSession.Instance.StageTexture(
                                heroId,
                                heroDisplayName,
                                category,
                                ownerPath,
                                sm.OriginalVmatPath,
                                sm.DisplayName,
                                vtexPath,
                                baked);

                            // Guardar también las capas individuales con su BlendMode, Opacity y Visibilidad
                            var stagedLayers = _layerManager.StageMaterialLayersToWorkspace(matKey, vtexPath);
                            if (stagedLayers.Count > 0 && ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(heroId, out var hE))
                            {
                                var aE = hE.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, ownerPath, StringComparison.OrdinalIgnoreCase));
                                var mE = aE?.Materials.FirstOrDefault(m => string.Equals(m.MaterialPath, sm.OriginalVmatPath, StringComparison.OrdinalIgnoreCase));
                                if (mE != null)
                                {
                                    mE.Layers = stagedLayers;
                                    mE.ActiveLayerIndex = _layerManager.ActiveLayerIndex;
                                    mE.Width = baked.GetWidth();
                                    mE.Height = baked.GetHeight();
                                }
                                ModWorkspaceSession.Instance.SaveManifest();
                            }

                            if (!string.IsNullOrEmpty(matKey)) _flushedMaterialKeys.Add(matKey);
                            flushedCount++;
                        }
                    }
                }
            }

            // 2. Flush particle VFX changes
            if (_particleStage != null && _particleStage.HasActiveParticle)
            {
                _particleStage.FlushOverridesToWorkspace();
            }

            if (flushedCount > 0)
            {
                _studioUI?.ShowToast($"Workspace Saved ({flushedCount} material{(flushedCount > 1 ? "s" : "")})", toastSubtitle);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PaintTabUI] Error executing workspace flush: {ex.Message}");
        }
    }

    private void RestoreStagedTexturesFromWorkspace(string heroId)
    {
        if (string.IsNullOrEmpty(heroId) || _layerManager == null || _meshHierarchy == null) return;
        try
        {
            var session = ModWorkspaceSession.Instance;
            if (!session.Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry == null) return;

            // Ensure all submeshes have their atlas managers allocated so GPU RIDs exist
            _layerManager.EnsureAllSubmeshAtlases(_meshHierarchy);

            bool anyRestored = false;
            foreach (var asset in heroEntry.Assets)
            {
                if (asset.Category == "ParticleVFX") continue;
                foreach (var (vtexPath, relPath) in asset.ModifiedTextures)
                {
                    var stagedImg = session.GetStagedTexture(vtexPath);
                    if (stagedImg == null || stagedImg.IsEmpty()) continue;

                    string normVtex = vtexPath.Replace('\\', '/').Trim('/');
                    string vtexBase = Path.GetFileNameWithoutExtension(normVtex);
                    if (vtexBase.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                        vtexBase = Path.GetFileNameWithoutExtension(vtexBase);

                    var targetSubmesh = _meshHierarchy.Submeshes.FirstOrDefault(s =>
                    {
                        if (string.Equals(s.OriginalColorVtexCPath, vtexPath, StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(s.OriginalVmatPath, vtexPath, StringComparison.OrdinalIgnoreCase)) return true;

                        string sVmat = s.OriginalVmatPath?.Replace('\\', '/')?.Trim('/');
                        if (!string.IsNullOrEmpty(sVmat))
                        {
                            if (sVmat.Equals(normVtex, StringComparison.OrdinalIgnoreCase)) return true;
                            string vmatBase = Path.GetFileNameWithoutExtension(sVmat);
                            if (vmatBase.Equals(vtexBase, StringComparison.OrdinalIgnoreCase) ||
                                vtexBase.StartsWith(vmatBase, StringComparison.OrdinalIgnoreCase) ||
                                vmatBase.StartsWith(vtexBase, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }

                        string sColor = s.OriginalColorVtexCPath?.Replace('\\', '/')?.Trim('/');
                        if (!string.IsNullOrEmpty(sColor))
                        {
                            if (sColor.Equals(normVtex, StringComparison.OrdinalIgnoreCase)) return true;
                            string colorBase = Path.GetFileNameWithoutExtension(sColor);
                            if (colorBase.Equals(vtexBase, StringComparison.OrdinalIgnoreCase)) return true;
                        }

                        if (!string.IsNullOrEmpty(s.DisplayName))
                        {
                            string dispClean = s.DisplayName.Replace(" ", "_").ToLowerInvariant();
                            if (vtexBase.ToLowerInvariant().Contains(dispClean) || dispClean.Contains(vtexBase.ToLowerInvariant()))
                                return true;
                        }

                        return false;
                    });

                    if (targetSubmesh != null && targetSubmesh.Mesh != null)
                    {
                        string matKey = _layerManager.GetMaterialKey(targetSubmesh.Mesh, targetSubmesh.SurfaceIndex);

                        // Ensure all submeshes sharing this material have their atlas linked and are marked dirty
                        foreach (var sub in _meshHierarchy.Submeshes)
                        {
                            if (sub.Mesh != null && string.Equals(_layerManager.GetMaterialKey(sub.Mesh, sub.SurfaceIndex), matKey, StringComparison.OrdinalIgnoreCase))
                            {
                                _layerManager.EnsureSubmeshAtlas(sub.Mesh);
                                sub.IsDirty = true;
                            }
                        }

                        var matEntry = asset.Materials.FirstOrDefault(m =>
                            string.Equals(m.MaterialPath, targetSubmesh.OriginalVmatPath, StringComparison.OrdinalIgnoreCase) ||
                            m.Textures.ContainsKey(vtexPath));

                        int defaultW = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
                        int defaultH = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;
                        int w = (matEntry != null && matEntry.Width > 0) ? matEntry.Width : (stagedImg.GetWidth() > 0 ? stagedImg.GetWidth() : defaultW);
                        int h = (matEntry != null && matEntry.Height > 0) ? matEntry.Height : (stagedImg.GetHeight() > 0 ? stagedImg.GetHeight() : defaultH);

                        var restoredLayers = new List<SkinLayer>();

                        Vector2I resVec = new Vector2I(w, h);
                        if (matEntry != null && matEntry.Layers != null && matEntry.Layers.Count > 0)
                        {
                            for (int lIdx = 0; lIdx < matEntry.Layers.Count; lIdx++)
                            {
                                var le = matEntry.Layers[lIdx];
                                string layerKey = $"{vtexPath}__layer_{lIdx}";
                                var lImg = session.GetStagedTexture(layerKey) ?? stagedImg;

                                var sl = new SkinLayer();
                                sl.Initialize(le.Name ?? $"Paint Layer {lIdx + 1}", resVec, le.Locked, null, null, null);
                                sl.SetOpacity(le.Opacity);
                                sl.SetVisibility(le.Visible);
                                sl.SetBlendMode((LayerBlendMode)le.BlendMode);
                                sl.IsLocked = le.Locked;
                                sl.GpuData = ProjectFileManager.ImageToLayerGpuData(lImg, w, h);
                                sl.IsCpuSynced = true;
                                restoredLayers.Add(sl);
                            }
                        }
                        else
                        {
                            var restoredLayer = new SkinLayer();
                            restoredLayer.Initialize("Paint Layer 1", resVec, false, null, null, null);
                            restoredLayer.GpuData = ProjectFileManager.ImageToLayerGpuData(stagedImg, w, h);
                            restoredLayer.IsCpuSynced = true;
                            restoredLayers.Add(restoredLayer);
                        }

                        bool isActiveTarget = string.Equals(_layerManager.ActiveMaterialKey, matKey, StringComparison.OrdinalIgnoreCase);
                        int activeIdx = matEntry != null ? Math.Clamp(matEntry.ActiveLayerIndex, 0, Math.Max(0, restoredLayers.Count - 1)) : 0;
                        _layerManager.RestoreMaterialContext(matKey, new Vector2I(w, h), restoredLayers, activeIdx, isActiveTarget);
                        _flushedMaterialKeys.Add(matKey);

                        anyRestored = true;
                    }
                }
            }

            if (anyRestored)
            {
                _layerManager.ApplyOverlayParametersToMeshes();
                RefreshSubmeshListUI();
                UpdateSubmeshRowHighlights();
                RefreshLayersListUI();
                var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                            ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
                uvCanvas?.QueueCanvasRedraw();
                GD.Print($"[PaintTabUI] Restored staged textures and layers for '{heroId}' from workspace session.");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PaintTabUI] Error restoring staged textures: {ex.Message}");
        }
    }

    private void OnWorkspaceAssetDiscarded(string heroId, string assetOrMatPath)
    {

        _lastRegisteredDirtyMatKey = null;
        _flushedMaterialKeys.Clear();
        _layerManager?.ResetMaterialContext(assetOrMatPath);

        bool matchesOwner = false;
        var vpkLoader = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        if (string.Equals(vpkLoader?.LastLoadedVmdlPath, assetOrMatPath, StringComparison.OrdinalIgnoreCase))
        {
            matchesOwner = true;
        }

        if (_meshHierarchy != null)
        {
            foreach (var sub in _meshHierarchy.Submeshes)
            {
                if (matchesOwner ||
                    string.Equals(sub.OriginalVmatPath, assetOrMatPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sub.OriginalColorVtexCPath, assetOrMatPath, StringComparison.OrdinalIgnoreCase))
                {
                    _layerManager?.ResetMaterialContext(sub.OriginalVmatPath);
                    if (!string.IsNullOrEmpty(sub.OriginalColorVtexCPath))
                    {
                        _layerManager?.ResetMaterialContext(sub.OriginalColorVtexCPath);
                    }
                    sub.IsDirty = false;
                }
            }
        }

        var pStage = GetOrCreateParticleStage();
        if (pStage != null && pStage.HasActiveParticle)
        {
            pStage.RestoreAllVanillaOverrides();
        }

        RefreshSubmeshListUI();
        UpdateSubmeshRowHighlights();
        RefreshLayersListUI();
    }

    private void OnWorkspaceCleared()
    {
        _lastRegisteredDirtyMatKey = null;
        _flushedMaterialKeys.Clear();
        if (_meshHierarchy != null)
        {
            foreach (var sub in _meshHierarchy.Submeshes)
            {
                _layerManager?.ResetMaterialContext(sub.OriginalVmatPath);
                if (!string.IsNullOrEmpty(sub.OriginalColorVtexCPath))
                {
                    _layerManager?.ResetMaterialContext(sub.OriginalColorVtexCPath);
                }
                sub.IsDirty = false;
            }
        }

        var pStage = GetOrCreateParticleStage();
        if (pStage != null && pStage.HasActiveParticle)
        {
            pStage.RestoreAllVanillaOverrides();
        }

        RefreshSubmeshListUI();
        UpdateSubmeshRowHighlights();
        RefreshLayersListUI();
    }

    private void CheckAndUnregisterIfMaterialClean(string matKey)
    {
        if (string.IsNullOrEmpty(matKey) || _layerManager == null) return;

        bool anyNonBlank = false;
        foreach (var l in _layerManager.Layers)
        {
            if (l != null && !l.IsBlank()) { anyNonBlank = true; break; }
        }

        if (!anyNonBlank)
        {
            _layerManager.SetActiveContextHasPaintStrokes(false);
            _meshHierarchy?.ClearMaterialDirty(matKey, _layerManager);
            _lastRegisteredDirtyMatKey = null;
            _flushedMaterialKeys.Remove(matKey);

            

            string ownerPath = _currentHeroVmdlPath;
            if (_meshHierarchy != null)
            {
                foreach (var sm in _meshHierarchy.Submeshes)
                {
                    string smKey = _layerManager.GetMaterialKey(sm.Mesh, sm.SurfaceIndex);
                    if (string.Equals(smKey, matKey, StringComparison.OrdinalIgnoreCase))
                    {
                        sm.IsDirty = false;
                        ModWorkspaceSession.Instance.RemoveMaterial(_currentHeroId ?? "", ownerPath, sm.OriginalVmatPath);
                    }
                }
            }
            UpdateSubmeshRowHighlights();
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
        FlushActiveChangesToWorkspace(true);
        _currentHero = null;
        _hasCachedPose = false;
        _cachedBonePoses.Clear();
        _cachedAnimationName = null;
        _cachedAnimationPlaying = false;
        _hasCachedPaintVisibility = false;
        _cachedPaintSubmeshVisibility = null;
        _cachedPaintPreSoloVisibility = null;
        _cachedPaintSoloedSubmesh = null;
        _materialGroupRows.Clear();
        _submeshRows.Clear();
        _flushedMaterialKeys.Clear();
        _lastRegisteredDirtyMatKey = null;
        if (_submeshListContainer != null)
        {
            foreach (Node child in _submeshListContainer.GetChildren())
            {
                child.QueueFree();
            }
        }

        if (_lblHeroName != null) _lblHeroName.Text = "HERO: NONE";
        if (_lblActiveMesh != null) _lblActiveMesh.Text = "Target: None";
        if (_optTargetMesh != null) _optTargetMesh.Clear();

        _painter?.ResetSession();
        _painter?.SetTargetMesh(null);
        _layerManager?.ClearHistory();
        _layerManager?.CleanupHeroAtlas();
        _meshHierarchy?.ScanHero(null);
        _decalStamper?.HidePreview();

        UpdateControlsState(false);
        _brushPalette?.UpdateUndoRedoState(false, false);
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
        UpdateCanvasResolutionLabel();
    }

    public void UpdateSubmeshRowHighlights()
    {
        if (_meshHierarchy == null) return;
        var active = _meshHierarchy.ActiveTarget;
        var stage = GetOrCreateParticleStage();
        bool hasParticleActive = stage != null && stage.HasActiveParticle;
        bool isPaintingParticle = hasParticleActive && !string.IsNullOrEmpty(_activeParticleVtexPath);

        if (isPaintingParticle && _lblActiveMesh != null)
        {
            _lblActiveMesh.Text = $"Target: Particle ({System.IO.Path.GetFileName(_activeParticleVtexPath)})";
        }
        else if (hasParticleActive && _lblActiveMesh != null)
        {
            _lblActiveMesh.Text = "Target: Active VFX";
        }

        foreach (var groupRow in _materialGroupRows)
        {
            bool isGroupActive = false;
            if (!hasParticleActive && !isPaintingParticle && groupRow.Group != null && active != null)
            {
                isGroupActive = groupRow.Group.Submeshes.Contains(active);
            }

            if (groupRow.PaintBtn != null && GodotObject.IsInstanceValid(groupRow.PaintBtn))
            {
                if (isGroupActive)
                {
                    groupRow.PaintBtn.Modulate = new Color(1.0f, 0.85f, 0.4f, 1.0f);
                    groupRow.PaintBtn.Text = "Painting";
                }
                else
                {
                    groupRow.PaintBtn.Modulate = Colors.White;
                    groupRow.PaintBtn.Text = "Paint";
                }
            }

            foreach (var row in groupRow.SubmeshRows)
            {
                if (row.SelectBtn != null && GodotObject.IsInstanceValid(row.SelectBtn))
                {
                    row.SelectBtn.Modulate = (!hasParticleActive && !isPaintingParticle && row.Submesh == active)
                        ? new Color(1.0f, 0.85f, 0.4f, 1.0f)
                        : Colors.White;
                }
            }
        }
    }

    public void RefreshSubmeshListUI()
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
            if (!_layerManager.HasPopulatedBaseAtlasBuffer)
            {
                _layerManager.RebuildBaseAtlasBuffer();
            }
        }

        var groups = _meshHierarchy.MaterialGroups;
        if (groups.Count == 0 && _meshHierarchy.Submeshes.Count > 0)
        {
            _meshHierarchy.RebuildMaterialGroups();
            groups = _meshHierarchy.MaterialGroups;
        }

        var stage = GetOrCreateParticleStage();
        bool hasParticle = stage != null && stage.HasActiveParticle;

        // Check if existing rows can be updated in-place (must not be transitioning to/from particle mode)
        bool canUpdateInPlace = (_materialGroupRows.Count == groups.Count) && !hasParticle && !_wasShowingParticle;
        _wasShowingParticle = hasParticle;
        if (canUpdateInPlace)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                var gRow = _materialGroupRows[g];
                var grp = groups[g];
                if (gRow.Group != grp || gRow.SubmeshRows.Count != grp.Submeshes.Count ||
                    !GodotObject.IsInstanceValid(gRow.GroupContainer) ||
                    !GodotObject.IsInstanceValid(gRow.ChildrenContainer))
                {
                    canUpdateInPlace = false;
                    break;
                }
                for (int s = 0; s < grp.Submeshes.Count; s++)
                {
                    if (gRow.SubmeshRows[s].Submesh != grp.Submeshes[s] ||
                        !GodotObject.IsInstanceValid(gRow.SubmeshRows[s].RowContainer))
                    {
                        canUpdateInPlace = false;
                        break;
                    }
                }
                if (!canUpdateInPlace) break;
            }
        }

        if (canUpdateInPlace)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                var gRow = _materialGroupRows[g];
                var grp = groups[g];
                gRow.ToggleBtn.Text = grp.IsExpanded ? "▼" : "▶";
                gRow.ChildrenContainer.Visible = grp.IsExpanded;

                for (int s = 0; s < grp.Submeshes.Count; s++)
                {
                    var sRow = gRow.SubmeshRows[s];
                    var sub = grp.Submeshes[s];
                    bool isVis = sub.Mesh != null && GodotObject.IsInstanceValid(sub.Mesh) && sub.Mesh.Visible;
                    sRow.VisCheck.SetPressedNoSignal(isVis);
                    sRow.SoloBtn.SetPressedNoSignal(sub.IsSoloed);
                    sRow.SoloBtn.Text = sub.IsSoloed ? "[Solo]" : "Solo";
                }
            }
            if (_btnHideAccMeshes != null) _btnHideAccMeshes.Visible = !hasParticle;
            UpdateSubmeshRowHighlights();
            UpdateParticleTexturesGroup();
            return;
        }

        // Full rebuild needed
        _particleTexturesGroupContainer = null;
        foreach (Node child in _submeshListContainer.GetChildren())
        {
            child.QueueFree();
        }
        _materialGroupRows.Clear();
        _submeshRows.Clear();

        EnsureGroupHeaderStyle();

        if (_btnHideAccMeshes != null) _btnHideAccMeshes.Visible = !hasParticle;

        var colusFont = PlaygroundThemeHelper.GetFontColus();

        if (!hasParticle)
        {
            foreach (var group in groups)
            {
                var groupContainer = new VBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                groupContainer.AddThemeConstantOverride("separation", 2);

                var headerPanel = new PanelContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                headerPanel.AddThemeStyleboxOverride("panel", _groupHeaderStyle);

                var headerRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                headerRow.AddThemeConstantOverride("separation", 6);

                var toggleBtn = new Button
                {
                    Text = group.IsExpanded ? "▼" : "▶",
                    Flat = true,
                    CustomMinimumSize = new Vector2(22, 22),
                    TooltipText = "Expand/Collapse material group",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                toggleBtn.AddThemeFontSizeOverride("font_size", 12);

                var titleLabel = new Label
                {
                    Text = group.DisplayName,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    TooltipText = $"Material: {group.MaterialKey}"
                };
                if (colusFont != null) titleLabel.AddThemeFontOverride("font", colusFont);
                titleLabel.AddThemeFontSizeOverride("font_size", 14);

            var badgeLabel = new Label
            {
                Text = $"[{group.Submeshes.Count} {(group.Submeshes.Count == 1 ? "mesh" : "meshes")}]",
                Modulate = new Color(0.72f, 0.76f, 0.82f),
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            badgeLabel.AddThemeFontSizeOverride("font_size", 12);

            var paintBtn = new Button
            {
                Text = "Paint",
                CustomMinimumSize = new Vector2(56, 24),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = $"Paint material group '{group.DisplayName}'"
            };
            paintBtn.AddThemeFontSizeOverride("font_size", 12);

            var localGroup = group;
            paintBtn.Pressed += () =>
            {
                _activeParticleVtexPath = null;
                UpdateParticleTexturesGroup();
                SubmeshNodeInfo target = null;
                foreach (var s in localGroup.Submeshes)
                {
                    if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && s.Mesh.Visible)
                    {
                        target = s;
                        break;
                    }
                }
                target ??= (localGroup.Submeshes.Count > 0 ? localGroup.Submeshes[0] : null);
                if (target != null)
                {
                    _meshHierarchy.SelectTarget(target, target.SurfaceIndex);
                }
            };

            headerRow.AddChild(toggleBtn);
            headerRow.AddChild(titleLabel);
            headerRow.AddChild(badgeLabel);
            headerRow.AddChild(paintBtn);
            headerPanel.AddChild(headerRow);
            groupContainer.AddChild(headerPanel);

            var childrenContainer = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Visible = group.IsExpanded
            };
            childrenContainer.AddThemeConstantOverride("separation", 2);

            var localChildren = childrenContainer;
            var localToggle = toggleBtn;
            toggleBtn.Pressed += () =>
            {
                localGroup.IsExpanded = !localGroup.IsExpanded;
                localChildren.Visible = localGroup.IsExpanded;
                localToggle.Text = localGroup.IsExpanded ? "▼" : "▶";
            };

            var groupSubmeshRows = new List<SubmeshRowUI>();

            for (int i = 0; i < group.Submeshes.Count; i++)
            {
                var submesh = group.Submeshes[i];
                bool isLast = (i == group.Submeshes.Count - 1);

                var row = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                row.AddThemeConstantOverride("separation", 4);

                var branchLabel = new Label
                {
                    Text = isLast ? "  └─" : "  ├─",
                    Modulate = new Color(0.45f, 0.48f, 0.52f),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                branchLabel.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(branchLabel);

                bool isVis = submesh.Mesh != null && GodotObject.IsInstanceValid(submesh.Mesh) && submesh.Mesh.Visible;
                var visCheck = new CheckBox
                {
                    ButtonPressed = isVis,
                    TooltipText = "Toggle submesh visibility",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                var localSub = submesh;
                visCheck.Toggled += (vis) => _meshHierarchy.SetMeshVisibility(localSub, vis);
                row.AddChild(visCheck);

                var soloBtn = new Button
                {
                    Text = submesh.IsSoloed ? "[Solo]" : "Solo",
                    CustomMinimumSize = new Vector2(44, 24),
                    ToggleMode = true,
                    ButtonPressed = submesh.IsSoloed,
                    TooltipText = "Solo submesh (hide others)",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                soloBtn.AddThemeFontSizeOverride("font_size", 10);
                soloBtn.Pressed += () => _meshHierarchy.ToggleSolo(localSub);
                row.AddChild(soloBtn);

                int charLen = submesh.DisplayName?.Length ?? 0;
                float minHeight = charLen > 40 ? 46f : (charLen > 22 ? 34f : 24f);

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
                selectBtn.AddThemeFontSizeOverride("font_size", 10);

                selectBtn.Pressed += () =>
                {
                    _meshHierarchy.SelectTarget(localSub, localSub.SurfaceIndex);
                };

                btnWrapper.AddChild(selectBtn);
                row.AddChild(btnWrapper);

                childrenContainer.AddChild(row);

                var rowUI = new SubmeshRowUI
                {
                    Submesh = submesh,
                    RowContainer = row,
                    VisCheck = visCheck,
                    SoloBtn = soloBtn,
                    SelectBtn = selectBtn
                };
                groupSubmeshRows.Add(rowUI);
                _submeshRows.Add(rowUI);
            }

            groupContainer.AddChild(childrenContainer);
            _submeshListContainer.AddChild(groupContainer);

            _materialGroupRows.Add(new MaterialGroupRowUI
            {
                Group = group,
                GroupContainer = groupContainer,
                HeaderPanel = headerPanel,
                HeaderRow = headerRow,
                ToggleBtn = toggleBtn,
                TitleLabel = titleLabel,
                BadgeLabel = badgeLabel,
                PaintBtn = paintBtn,
                ChildrenContainer = childrenContainer,
                SubmeshRows = groupSubmeshRows
            });
        }
        }

        UpdateParticleTexturesGroup();
        UpdateSubmeshRowHighlights();
    }

    public void SetParticleStage(ParticleStage3D stage)
    {
        if (_particleStage != null)
        {
            _particleStage.ParticleLoaded -= OnParticleStageLoaded;
            _particleStage.ParticleUnloaded -= OnParticleStageUnloaded;
        }

        _particleStage = stage;
        if (_particleStage != null)
        {
            _particleStage.ParticleLoaded += OnParticleStageLoaded;
            _particleStage.ParticleUnloaded += OnParticleStageUnloaded;
        }

        RefreshSubmeshListUI();
        UpdateParticleTexturesGroup();
    }

    private ParticleStage3D GetOrCreateParticleStage()
    {
        if (_particleStage != null && GodotObject.IsInstanceValid(_particleStage)) return _particleStage;
        var worldViewport = GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
        var stage = worldViewport?.FindChild("ParticleStage3D", true, false) as ParticleStage3D;
        if (stage != null)
        {
            SetParticleStage(stage);
        }
        return stage;
    }

    private void OnParticleStageLoaded(string vpcfPath, string variantName, string abilityName)
    {
        _activeParticleVtexPath = null;
        var stage = GetOrCreateParticleStage();
        if (stage != null && stage.GetActiveTextures().Count > 0)
        {
            var firstPair = stage.GetActiveTextures().First();
            _activeParticleVtexPath = firstPair.Key;

            if (IsTabActive && IsVisibleInTree())
            {
                var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                            ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
                if (uvCanvas != null)
                {
                    uvCanvas.OpenParticleTexture(firstPair.Key, firstPair.Value, (updatedImage) =>
                    {
                        stage.UpdateParticleTexture(firstPair.Key, updatedImage);
                    });
                }
            }
        }
        if (_btnHideAccMeshes != null) _btnHideAccMeshes.Visible = false;
        RefreshSubmeshListUI();
        UpdateParticleTexturesGroup();
        UpdateSubmeshRowHighlights();
        UpdateCanvasResolutionLabel();
        if (_lblActiveMesh != null)
        {
            _lblActiveMesh.Text = $"Particle: {System.IO.Path.GetFileName(_activeParticleVtexPath ?? vpcfPath)}";
        }
    }

    private void OnParticleStageUnloaded()
    {
        _particleStage?.FlushOverridesToWorkspace();
        _activeParticleVtexPath = null;
        if (_btnHideAccMeshes != null) _btnHideAccMeshes.Visible = true;

        if (_currentHero != null && GodotObject.IsInstanceValid(_currentHero))
        {
            ShowAllMeshesRecursive(_currentHero);
        }
        _meshHierarchy?.ShowAll();

        //Exit standalone particle texture mode and restore active character material in 2D Canvas & LayerManager
        var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                    ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
        uvCanvas?.CloseParticleTextureMode();

        var activeTarget = _meshHierarchy?.ActiveTarget ?? (_meshHierarchy?.Submeshes.Count > 0 ? _meshHierarchy.Submeshes[0] : null);
        if (activeTarget != null && activeTarget.Mesh != null)
        {
            _meshHierarchy.SelectTarget(activeTarget, activeTarget.SurfaceIndex);
            _layerManager?.SetPaintTargetMesh(activeTarget.Mesh);
            _painter?.SetTargetMesh(activeTarget.Mesh, activeTarget.SurfaceIndex);
        }

        UpdateParticleTexturesGroup();
        RefreshSubmeshListUI();
        UpdateSubmeshRowHighlights();
        RefreshLayersListUI();

        if (_lblActiveMesh != null)
        {
            string displayName = _meshHierarchy?.ActiveTarget?.DisplayName ?? "None";
            _lblActiveMesh.Text = $"Target: {displayName}";
        }
        UpdateCanvasResolutionLabel();
    }

    private static void ShowAllMeshesRecursive(Node node)
    {
        if (node == null) return;
        string nName = node.Name.ToString();
        if (nName == "SkeletonGizmoManager" || nName == "BoneLayerManager" || nName == "CharacterIKManager")
        {
            return;
        }
        if (node is MeshInstance3D mi && !mi.HasMeta("IsHiddenComposite") && HeroMeshHierarchy.IsAuthenticHeroMesh(mi))
        {
            bool userVis = mi.HasMeta("UserVisibility") ? mi.GetMeta("UserVisibility").AsBool() : true;
            mi.Visible = userVis;
        }
        foreach (Node child in node.GetChildren())
        {
            ShowAllMeshesRecursive(child);
        }
    }

    private static bool IsSharedParticleTexture(
        string vtexPath,
        string heroId = null,
        string abilityName = null,
        string activeVpcfPath = null,
        DeadlockHeroEntry heroEntry = null)
    {
        if (string.IsNullOrEmpty(vtexPath)) return true;
        string norm = vtexPath.Replace("\\", "/").ToLowerInvariant();

        // 1. Gather all hero aliases and identifying tokens for the current active hero
        var heroTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (heroEntry != null)
        {
            if (!string.IsNullOrWhiteSpace(heroEntry.InternalCodename)) heroTokens.Add(heroEntry.InternalCodename);
            if (!string.IsNullOrWhiteSpace(heroEntry.DisplayName)) heroTokens.Add(heroEntry.DisplayName);
            if (!string.IsNullOrWhiteSpace(heroEntry.WipAlias)) heroTokens.Add(heroEntry.WipAlias);
        }

        if (!string.IsNullOrWhiteSpace(heroId))
        {
            heroTokens.Add(heroId);
            var resolved = DeadlockHeroCatalog.ResolveHero(heroId);
            if (resolved != null)
            {
                heroTokens.Add(resolved.InternalCodename);
                heroTokens.Add(resolved.DisplayName);
                if (!string.IsNullOrWhiteSpace(resolved.WipAlias)) heroTokens.Add(resolved.WipAlias);
            }
            string display = HeroParticleService.ResolveDisplayName(heroId);
            if (!string.IsNullOrWhiteSpace(display)) heroTokens.Add(display);
        }

        if (!string.IsNullOrWhiteSpace(activeVpcfPath))
        {
            string cleanVpcf = activeVpcfPath.Replace("\\", "/").ToLowerInvariant();
            if (cleanVpcf.StartsWith("particles/abilities/"))
            {
                string rel = cleanVpcf.Substring("particles/abilities/".Length);
                string heroFolder = rel.Split('/')[0];
                if (!string.IsNullOrWhiteSpace(heroFolder))
                {
                    heroTokens.Add(heroFolder);
                    var resolved = DeadlockHeroCatalog.ResolveHero(heroFolder);
                    if (resolved != null)
                    {
                        heroTokens.Add(resolved.InternalCodename);
                        heroTokens.Add(resolved.DisplayName);
                        if (!string.IsNullOrWhiteSpace(resolved.WipAlias)) heroTokens.Add(resolved.WipAlias);
                    }
                    string display = HeroParticleService.ResolveDisplayName(heroFolder);
                    if (!string.IsNullOrWhiteSpace(display)) heroTokens.Add(display);
                }
            }
        }

        // Clean ability name
        if (!string.IsNullOrWhiteSpace(abilityName))
        {
            string cleanAbility = abilityName.ToLowerInvariant();
            if (cleanAbility.StartsWith("citadel_ability_")) cleanAbility = cleanAbility.Substring(16);
            else if (cleanAbility.StartsWith("ability_")) cleanAbility = cleanAbility.Substring(8);
            if (!string.IsNullOrWhiteSpace(cleanAbility))
            {
                heroTokens.Add(cleanAbility);
            }
        }

        // 2. Check if the texture belongs to this hero
        foreach (var token in heroTokens)
        {
            string t = token.Trim().ToLowerInvariant();
            if (t.Length < 3) continue;

            if (norm.Contains($"/{t}/") || norm.Contains($"/{t}_") || norm.Contains($"_{t}_") ||
                norm.Contains($"_{t}.") || norm.Contains($"/{t}."))
            {
                return false; // Property of this hero!
            }

            if (norm.Contains($"/abilities/{t}") || norm.Contains($"/heroes/{t}") ||
                norm.Contains($"/heroes_staging/{t}") || norm.Contains($"/heroes_wip/{t}"))
            {
                return false; // Property of this hero!
            }
        }

        // 3. Any texture that is NOT property of the hero is marked as shared or global
        return true;
    }

    private void UpdateParticleTexturesGroup()
    {
        if (_submeshListContainer == null) return;

        var stage = GetOrCreateParticleStage();
        if (stage == null || !stage.HasActiveParticle)
        {
            if (_particleTexturesGroupContainer != null && GodotObject.IsInstanceValid(_particleTexturesGroupContainer))
            {
                _particleTexturesGroupContainer.QueueFree();
                _particleTexturesGroupContainer = null;
            }
            return;
        }

        var textures = stage.GetActiveTextures();
        if (textures.Count == 0 && stage.Renderers.Count == 0)
        {
            if (_particleTexturesGroupContainer != null && GodotObject.IsInstanceValid(_particleTexturesGroupContainer))
            {
                _particleTexturesGroupContainer.QueueFree();
                _particleTexturesGroupContainer = null;
            }
            return;
        }

        EnsureGroupHeaderStyle();

        if (_particleTexturesGroupContainer == null || !GodotObject.IsInstanceValid(_particleTexturesGroupContainer) || _particleTexturesGroupContainer.GetParent() != _submeshListContainer)
        {
            _particleTexturesGroupContainer = new VBoxContainer
            {
                Name = "ParticleMaterialsGroup",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _particleTexturesGroupContainer.AddThemeConstantOverride("separation", 2);
            _submeshListContainer.AddChild(_particleTexturesGroupContainer);
        }

        foreach (Node child in _particleTexturesGroupContainer.GetChildren())
        {
            child.QueueFree();
        }

        var colus = PlaygroundThemeHelper.GetFontColus();

        // 1. Particle Materials / Textures Section
        var headerPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headerPanel.AddThemeStyleboxOverride("panel", _groupHeaderStyle);

        var headerRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headerRow.AddThemeConstantOverride("separation", 6);

        var iconSparkles = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/icons/sparkles.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 1.0f)
        };
        headerRow.AddChild(iconSparkles);

        var titleLabel = new Label
        {
            Text = "Particle Materials",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        if (colus != null) titleLabel.AddThemeFontOverride("font", colus);
        titleLabel.AddThemeFontSizeOverride("font_size", 14);
        headerRow.AddChild(titleLabel);

        var badgeLabel = new Label
        {
            Text = $"[{textures.Count}]",
            Modulate = new Color(0.72f, 0.76f, 0.82f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        badgeLabel.AddThemeFontSizeOverride("font_size", 12);
        headerRow.AddChild(badgeLabel);

        headerPanel.AddChild(headerRow);
        _particleTexturesGroupContainer.AddChild(headerPanel);

        var childrenContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        childrenContainer.AddThemeConstantOverride("separation", 4);

        var vpkLoader = GetVpkLoader();

        foreach (var (vtexPath, imageTex) in textures)
        {
            bool isShared = IsSharedParticleTexture(
                vtexPath,
                stage.ActiveOwnerHeroId ?? vpkLoader?.HeroName,
                stage.ActiveAbilityName,
                stage.ActiveVpcfPath,
                vpkLoader?.LastLoadedHeroEntry);

            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);

            var texLabel = new Label
            {
                Text = System.IO.Path.GetFileName(vtexPath),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = vtexPath
            };
            if (colus != null) texLabel.AddThemeFontOverride("font", colus);
            texLabel.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(texLabel);

            if (isShared)
            {
                var sharedContainer = new HBoxContainer
                {
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                sharedContainer.AddThemeConstantOverride("separation", 4);

                var sharedBadge = new Label
                {
                    Text = "[SHARED]",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    TooltipText = "⚠️ Shared Global Material\nThis texture is used globally across heroes and environment effects in Deadlock.\nModifying it will affect all heroes and abilities that use it in-game."
                };
                if (colus != null) sharedBadge.AddThemeFontOverride("font", colus);
                sharedBadge.AddThemeFontSizeOverride("font_size", 11);
                sharedBadge.AddThemeColorOverride("font_color", new Color(1.0f, 0.28f, 0.28f, 1.0f)); // Danger red/coral
                sharedContainer.AddChild(sharedBadge);

                var infoBtn = new Button
                {
                    Icon = GD.Load<Texture2D>("res://assets/at-icons/info.svg"),
                    CustomMinimumSize = new Vector2(18, 18),
                    Flat = true,
                    FocusMode = Control.FocusModeEnum.None,
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    Modulate = new Color(1.0f, 0.45f, 0.45f, 1.0f),
                    TooltipText = "⚠️ Shared Global Material\nThis texture is NOT exclusive to this hero.\nClick to learn what shared materials mean in Deadlock."
                };

                string capturedPath = vtexPath;
                infoBtn.Pressed += () =>
                {
                    var dialog = new AcceptDialog
                    {
                        Title = "Shared Texture Warning",
                        DialogText = $"⚠️ SHARED GLOBAL MATERIAL\n\nTexture:\n{capturedPath}\n\nThis texture is a global game effect asset shared across multiple heroes and environmental systems in Deadlock.\n\nModifying or repainting this texture will alter its visual appearance across ALL heroes and abilities that reference it in-game, not just the currently selected hero."
                    };
                    dialog.Confirmed += () => dialog.QueueFree();
                    dialog.Canceled += () => dialog.QueueFree();
                    AddChild(dialog);
                    dialog.PopupCentered();
                };
                sharedContainer.AddChild(infoBtn);

                row.AddChild(sharedContainer);
            }

            bool isThisParticlePainting = !string.IsNullOrEmpty(_activeParticleVtexPath) &&
                string.Equals(_activeParticleVtexPath, vtexPath, StringComparison.OrdinalIgnoreCase);

            var paintBtn = new Button
            {
                Text = isThisParticlePainting ? "Painting" : "Paint",
                CustomMinimumSize = new Vector2(60, 24),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Modulate = isThisParticlePainting ? new Color(1.0f, 0.85f, 0.4f, 1.0f) : Colors.White
            };
            paintBtn.AddThemeFontSizeOverride("font_size", 12);

            string localPath = vtexPath;
            var localTex = imageTex;
            paintBtn.Pressed += () =>
            {
                _activeParticleVtexPath = localPath;
                UpdateSubmeshRowHighlights();
                UpdateParticleTexturesGroup();

                var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                            ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
                if (uvCanvas != null)
                {
                    uvCanvas.OpenParticleTexture(localPath, localTex, (updatedImage) =>
                    {
                        var st = GetOrCreateParticleStage();
                        st?.UpdateParticleTexture(localPath, updatedImage);
                    });
                }
            };
            row.AddChild(paintBtn);

            childrenContainer.AddChild(row);
        }

        _particleTexturesGroupContainer.AddChild(childrenContainer);

        // 2. Individual Particle Emitters Section
        _particleTexturesGroupContainer.AddChild(new HSeparator());

        var paramHeaderPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        paramHeaderPanel.AddThemeStyleboxOverride("panel", _groupHeaderStyle);
        var paramHeaderRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        paramHeaderRow.AddThemeConstantOverride("separation", 6);

        var iconGear = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/at-icons/cog.svg") ?? GD.Load<Texture2D>("res://assets/icons/sparkles.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = new Color(0.40f, 0.75f, 1.0f, 1.0f)
        };
        paramHeaderRow.AddChild(iconGear);

        var paramTitle = new Label
        {
            Text = "Particle Emitters",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        if (colus != null) paramTitle.AddThemeFontOverride("font", colus);
        paramTitle.AddThemeFontSizeOverride("font_size", 13);
        paramHeaderRow.AddChild(paramTitle);

        var countBadge = new Label
        {
            Text = $"[{stage.Renderers.Count}]",
            Modulate = new Color(0.65f, 0.68f, 0.72f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        countBadge.AddThemeFontSizeOverride("font_size", 10);
        paramHeaderRow.AddChild(countBadge);

        paramHeaderPanel.AddChild(paramHeaderRow);
        _particleTexturesGroupContainer.AddChild(paramHeaderPanel);

        var paramVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        paramVBox.AddThemeConstantOverride("separation", 6);

        string rootBase = stage.ActiveRootDefinition?.VpcfPath?.GetFile()?.GetBaseName() ?? "";

        for (int i = 0; i < stage.Renderers.Count; i++)
        {
            int rIdx = i;
            var renderer = stage.Renderers[i];
            if (renderer == null || !GodotObject.IsInstanceValid(renderer)) continue;

            string baseName = renderer.Instance?.Definition?.VpcfPath?.GetFile()?.GetBaseName() ?? renderer.Name.ToString();
            string emitterName;
            if (!string.IsNullOrEmpty(rootBase) && baseName.StartsWith(rootBase, StringComparison.OrdinalIgnoreCase) && baseName.Length > rootBase.Length)
            {
                emitterName = baseName.Substring(rootBase.Length).TrimStart('_');
            }
            else
            {
                emitterName = baseName;
            }

            string rType = renderer.RendererDef?.ClassName?.Replace("C_OP_", "")?.Replace("Render", "") ?? "";
            if (!string.IsNullOrEmpty(rType))
            {
                emitterName += $" ({rType})";
            }
            if (string.IsNullOrWhiteSpace(emitterName))
            {
                emitterName = $"Emitter {i + 1}";
            }

            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.10f, 0.12f, 0.15f, 0.7f),
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 6,
                ContentMarginRight = 6,
                ContentMarginTop = 4,
                ContentMarginBottom = 4
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var cardContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cardContent.AddThemeConstantOverride("separation", 4);

            var headRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            headRow.AddThemeConstantOverride("separation", 6);

            var visCb = new CheckBox
            {
                ButtonPressed = renderer.VisibleAndEmitting,
                TooltipText = "Toggle emitter visibility",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            var localRenderer = renderer;
            visCb.Toggled += (vis) =>
            {
                stage.SetRendererVisibility(rIdx, vis);
            };
            headRow.AddChild(visCb);

            var nameLbl = new Label
            {
                Text = emitterName,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = renderer.Name.ToString()
            };
            if (colus != null) nameLbl.AddThemeFontOverride("font", colus);
            nameLbl.AddThemeFontSizeOverride("font_size", 11);
            headRow.AddChild(nameLbl);

            var tintPicker = new ColorPickerButton
            {
                CustomMinimumSize = new Vector2(36, 20),
                Color = localRenderer.CustomTintColor,
                TooltipText = "Emitter Tint Color",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };

            var toggleDetailsBtn = new Button
            {
                Text = "▼",
                Flat = true,
                CustomMinimumSize = new Vector2(20, 20),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Show/Hide Emitter Controls"
            };
            toggleDetailsBtn.AddThemeFontSizeOverride("font_size", 9);
            headRow.AddChild(toggleDetailsBtn);

            cardContent.AddChild(headRow);

            var detailsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            detailsBox.AddThemeConstantOverride("separation", 4);

            toggleDetailsBtn.Pressed += () =>
            {
                detailsBox.Visible = !detailsBox.Visible;
                toggleDetailsBtn.Text = detailsBox.Visible ? "▼" : "▶";
            };

            var def = localRenderer.Instance?.Definition;
            var randColInit = def?.Initializers.OfType<RandomColorInitializerDef>().FirstOrDefault();
            var colFadeOp = def?.Operators.OfType<ColorInterpolateOperatorDef>().FirstOrDefault();

            var colorsRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            colorsRow.AddThemeConstantOverride("separation", 8);

            if (randColInit != null)
            {
                // Este .vpcf usa C_INIT_RandomColor -> Mostrar Color Min y Color Max
                var lblMin = new Label { Text = "Min:" };
                lblMin.AddThemeFontSizeOverride("font_size", 10);
                var pickerMin = new ColorPickerButton
                {
                    CustomMinimumSize = new Vector2(32, 20),
                    Color = randColInit.ColorMin,
                    EditAlpha = false,
                    TooltipText = "Random Color Min (C_INIT_RandomColor)"
                };

                var lblMax = new Label { Text = "Max:" };
                lblMax.AddThemeFontSizeOverride("font_size", 10);
                var pickerMax = new ColorPickerButton
                {
                    CustomMinimumSize = new Vector2(32, 20),
                    Color = randColInit.ColorMax,
                    EditAlpha = false,
                    TooltipText = "Random Color Max (C_INIT_RandomColor)"
                };

                pickerMin.ColorChanged += (c) => stage.SetRendererRandomColor(rIdx, c, pickerMax.Color);
                pickerMax.ColorChanged += (c) => stage.SetRendererRandomColor(rIdx, pickerMin.Color, c);

                colorsRow.AddChild(lblMin);
                colorsRow.AddChild(pickerMin);
                colorsRow.AddChild(lblMax);
                colorsRow.AddChild(pickerMax);
            }
            else if (def != null)
            {
                // Este .vpcf NO usa C_INIT_RandomColor -> Mostrar Constant Color
                var lblConst = new Label { Text = "Color:" };
                lblConst.AddThemeFontSizeOverride("font_size", 10);
                var pickerConst = new ColorPickerButton
                {
                    CustomMinimumSize = new Vector2(36, 20),
                    Color = def.ConstantColor,
                    EditAlpha = false,
                    TooltipText = "Constant Color (m_ConstantColor)"
                };
                pickerConst.ColorChanged += (c) => stage.SetRendererConstantColor(rIdx, c);

                colorsRow.AddChild(lblConst);
                colorsRow.AddChild(pickerConst);
            }

            if (colFadeOp != null)
            {
                // Este .vpcf tiene C_OP_ColorInterpolate -> Mostrar Fade Color
                var lblFade = new Label { Text = "Fade:" };
                lblFade.AddThemeFontSizeOverride("font_size", 10);
                var pickerFade = new ColorPickerButton
                {
                    CustomMinimumSize = new Vector2(32, 20),
                    Color = colFadeOp.ColorFade,
                    EditAlpha = false,
                    TooltipText = "Interpolate Fade Color (C_OP_ColorInterpolate)"
                };
                pickerFade.ColorChanged += (c) => stage.SetRendererColorFade(rIdx, c);

                colorsRow.AddChild(lblFade);
                colorsRow.AddChild(pickerFade);
            }

            if (colorsRow.GetChildCount() > 0)
            {
                detailsBox.AddChild(colorsRow);
            }

            // Size Slider Row
            var sizeRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            sizeRow.AddThemeConstantOverride("separation", 6);
            var sizeLbl = new Label { Text = "Size:", CustomMinimumSize = new Vector2(40, 0) };
            sizeLbl.AddThemeFontSizeOverride("font_size", 10);
            sizeRow.AddChild(sizeLbl);

            var sizeValLbl = new Label { Text = $"{localRenderer.CustomSizeScale:F1}x", CustomMinimumSize = new Vector2(32, 0) };
            sizeValLbl.AddThemeFontSizeOverride("font_size", 10);

            var sizeSlider = new HSlider
            {
                MinValue = 0.1,
                MaxValue = 5.0,
                Step = 0.05,
                Value = localRenderer.CustomSizeScale,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            sizeSlider.ValueChanged += (v) =>
            {
                float s = (float)v;
                sizeValLbl.Text = $"{s:F1}x";
                stage.SetRendererCustomSizeScale(rIdx, s);
            };
            sizeRow.AddChild(sizeSlider);
            sizeRow.AddChild(sizeValLbl);
            detailsBox.AddChild(sizeRow);

            // Opacity Slider Row
            var alphaRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            alphaRow.AddThemeConstantOverride("separation", 6);
            var alphaLbl = new Label { Text = "Alpha:", CustomMinimumSize = new Vector2(40, 0) };
            alphaLbl.AddThemeFontSizeOverride("font_size", 10);
            alphaRow.AddChild(alphaLbl);

            var alphaValLbl = new Label { Text = $"{Mathf.RoundToInt(localRenderer.CustomAlphaScale * 100)}%", CustomMinimumSize = new Vector2(32, 0) };
            alphaValLbl.AddThemeFontSizeOverride("font_size", 10);

            var alphaSlider = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.02,
                Value = localRenderer.CustomAlphaScale,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            alphaSlider.ValueChanged += (v) =>
            {
                float a = (float)v;
                alphaValLbl.Text = $"{Mathf.RoundToInt(a * 100)}%";
                stage.SetRendererCustomAlphaScale(rIdx, a);
            };
            alphaRow.AddChild(alphaSlider);
            alphaRow.AddChild(alphaValLbl);
            detailsBox.AddChild(alphaRow);

            tintPicker.ColorChanged += (newCol) =>
            {
                stage.SetRendererCustomTint(rIdx, newCol);
            };

            cardContent.AddChild(detailsBox);
            card.AddChild(cardContent);
            paramVBox.AddChild(card);
        }

        var resetAllBtn = new Button
        {
            Text = "Reset All Emitters",
            CustomMinimumSize = new Vector2(0, 24),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        resetAllBtn.AddThemeFontSizeOverride("font_size", 10);
        resetAllBtn.Pressed += () =>
        {
            stage.ResetEmitterParametersOnly();
            UpdateParticleTexturesGroup();
        };
        paramVBox.AddChild(resetAllBtn);

        _particleTexturesGroupContainer.AddChild(paramVBox);
    }

    private void RefreshLayersListUI()
    {
        if (_layersListContainer == null || _layerManager == null) return;

        foreach (Node child in _layersListContainer.GetChildren())
        {
            child.QueueFree();
        }

        var layers = _layerManager.Layers;
        for (int i = layers.Count - 1; i >= 0; i--)
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
            selectBtn.GuiInput += (ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    ShowLayerContextMenu(layerIndex, mb.GlobalPosition);
                }
            };
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

    private PopupMenu _layerContextMenu;

    private void ShowLayerContextMenu(int layerIdx, Vector2 screenPos)
    {
        if (_layerManager == null || layerIdx < 0 || layerIdx >= _layerManager.Layers.Count) return;

        if (_layerContextMenu == null)
        {
            _layerContextMenu = new PopupMenu();
            _layerContextMenu.Name = "LayerContextMenu";
            AddChild(_layerContextMenu);
        }

        _layerContextMenu.Clear();
        _layerContextMenu.AddItem("Rename Layer", 0);
        _layerContextMenu.AddItem("Delete Layer (This Material)", 1);
        _layerContextMenu.AddItem("Delete Layer (Across All Materials)", 2);

        // Clear previous signal connections
        var connections = _layerContextMenu.GetSignalConnectionList("id_pressed");
        foreach (var conn in connections)
        {
            if (conn.TryGetValue("callable", out var cVal) && cVal.VariantType == Variant.Type.Callable)
            {
                _layerContextMenu.Disconnect("id_pressed", cVal.AsCallable());
            }
        }

        int targetIdx = layerIdx;
        _layerContextMenu.IdPressed += (id) =>
        {
            if (id == 0)
            {
                ShowRenameLayerDialog(targetIdx);
            }
            else if (id == 1)
            {
                CheckAndUnregisterIfMaterialClean(_layerManager.ActiveMaterialKey);
                _layerManager.DeleteLayer(targetIdx);
            }
            else if (id == 2)
            {
                _layerManager.DeleteLayerAcrossAllMaterials(targetIdx);
            }
        };

        _layerContextMenu.Position = (Vector2I)screenPos;
        _layerContextMenu.Popup();
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
            Size = new Vector2I(360, 120),
            Transient = true,
            Exclusive = true
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
        IsTabActive = true;
        CacheCharacterPose();
        ResetCharacterPose();
        var pStage = GetOrCreateParticleStage();
        bool hasParticleActive = pStage != null && pStage.HasActiveParticle;

        if (_meshHierarchy != null)
        {
            _meshHierarchy.IsPaintingModeActive = true;
            _meshHierarchy.ApplyToonShading(false);

           if (hasParticleActive)
            {
                _meshHierarchy.HideAll();
                if (_currentHero != null)
                {
                    HideAllMeshesRecursive(_currentHero);
                }
                var activeTextures = pStage.GetActiveTextures();
                if (activeTextures.Count > 0)
                {
                    if (string.IsNullOrEmpty(_activeParticleVtexPath) || !activeTextures.ContainsKey(_activeParticleVtexPath))
                    {
                        _activeParticleVtexPath = activeTextures.Keys.First();
                    }
                    string targetVtex = _activeParticleVtexPath;
                    var targetTex = activeTextures[targetVtex];
                    var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                                ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
                    uvCanvas?.OpenParticleTexture(targetVtex, targetTex, (updatedImage) =>
                    {
                        pStage.UpdateParticleTexture(targetVtex, updatedImage);
                    });
                }
            }
        }
       if (_painter != null)
        {
            _painter.IsPaintingActive = !hasParticleActive;
            if (!hasParticleActive)
            {
                _layerManager?.UpdateActiveLayerBinding();
                _layerManager?.ApplyOverlayParametersToMeshes();
                if (_painter.CameraBrush != null && GodotObject.IsInstanceValid(_painter.CameraBrush))
                {
                    _painter.CameraBrush.Call("get_atlas_textures");
                }
            }
        }
        if (_brushPalette != null)
        {
            _brushPalette.Visible = true;
            _brushPalette.SyncFromPainter();
        }
        if (!hasParticleActive)
        {
            _layerManager?.RebuildBaseAtlasBuffer();
        }
        RefreshSubmeshListUI();
        UpdateParticleTexturesGroup();
        UpdateSubmeshRowHighlights();
        RefreshLayersListUI();
        UpdateCanvasResolutionLabel();
    }

    public void OnTabDeactivated()
    {
        IsTabActive = false;
        _layerManager?.SaveActiveContext();
        var stage = GetOrCreateParticleStage();
        bool hasActiveParticle = stage != null && stage.HasActiveParticle;

        if (_meshHierarchy != null)
        {
            _meshHierarchy.IsPaintingModeActive = false;
            if (UserSettings.ToonEnabled && !hasActiveParticle)
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

        // 2. Restore character tab mesh visibility onto the character model ONLY if no active particle
        if (!hasActiveParticle)
        {
            if (_currentHero != null && GodotObject.IsInstanceValid(_currentHero))
            {
                RestoreCharacterTabMeshVisibility(_currentHero);
            }

            RestoreCharacterPose();
        }
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

    private static void HideAllMeshesRecursive(Node node)
    {
        if (node == null) return;
        if (node is MeshInstance3D mi)
        {
            mi.Visible = false;
        }
        foreach (Node child in node.GetChildren())
        {
            HideAllMeshesRecursive(child);
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

    public void OpenExportModDialog(string overrideHeroCodename = null, string overrideHeroDisplayName = null)
    {
        if (_layerManager == null) return;
        FlushActiveChangesToWorkspace(true);

        string activeHeroCodename = !string.IsNullOrEmpty(_currentHeroId)
            ? _currentHeroId
            : (_currentHero != null && _currentHero.HasMeta("HeroCodename")
                ? _currentHero.GetMeta("HeroCodename").AsString()
                : _currentHero?.Name.ToString().Replace("Hero_", "").ToLowerInvariant() ?? "hero");

        string activeHeroDisplay = !string.IsNullOrEmpty(_currentHeroDisplayName)
            ? _currentHeroDisplayName
            : (_currentHero != null && _currentHero.HasMeta("HeroDisplayName")
                ? _currentHero.GetMeta("HeroDisplayName").AsString()
                : char.ToUpperInvariant(activeHeroCodename[0]) + activeHeroCodename.Substring(1));

        string heroCodename = !string.IsNullOrEmpty(overrideHeroCodename) ? overrideHeroCodename : activeHeroCodename;
        string heroDisplayName = !string.IsNullOrEmpty(overrideHeroDisplayName) ? overrideHeroDisplayName : activeHeroDisplay;

        bool isExportingCurrentActiveHero = string.Equals(heroCodename, activeHeroCodename, StringComparison.OrdinalIgnoreCase);
        var preBaked = isExportingCurrentActiveHero ? _layerManager.BakeCompositeImage() : null;
        var submeshesToPass = isExportingCurrentActiveHero ? _meshHierarchy?.Submeshes : new List<SubmeshNodeInfo>();

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
            _modDialogInstance.Setup(heroCodename, heroDisplayName, submeshesToPass, preBaked, pathManager);
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
