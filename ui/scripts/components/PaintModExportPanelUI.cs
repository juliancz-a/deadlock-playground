using Godot;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeadlockPlayground.Painter;

public partial class PaintModExportPanelUI : PanelContainer
{
    [Export] private Button _btnExportMod;
    [Export] private Button _btnExportPng;
    [Export] private Button _btnExportVmat;
    [Export] private Label _lblStatus;
    [Export] private FileDialog _exportFileDialog;

    private SkinLayerManager _layerManager;
    private HeroMeshHierarchy _meshHierarchy;
    private Node3D _currentHero;
    private SkinExporter _exporter;
    private GamePathManager _pathManager;

    private ExportModDialog _modDialogInstance;
    private ExportProgressDialog _progressDialogInstance;

    public override void _Ready()
    {
        _btnExportMod ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnExportMod");
        _btnExportPng ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnExportPng");
        _btnExportVmat ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnExportVmat");
        _lblStatus ??= GetNodeOrNull<Label>("MarginContainer/VBoxContainer/LblStatus");
        _exportFileDialog ??= GetNodeOrNull<FileDialog>("ExportFileDialog");

        _pathManager = new GamePathManager();
        _pathManager.LoadConfig();

        ConnectEvents();
    }

    public void Setup(SkinLayerManager layerManager, HeroMeshHierarchy meshHierarchy, Node3D heroNode)
    {
        _layerManager = layerManager;
        _meshHierarchy = meshHierarchy;
        _currentHero = heroNode;

        if (_layerManager != null)
        {
            _exporter = new SkinExporter(_layerManager);
        }
    }

    public void SetCurrentHero(Node3D heroNode)
    {
        _currentHero = heroNode;
    }

    private void ConnectEvents()
    {
        if (_btnExportMod != null)
        {
            _btnExportMod.Pressed += OnExportModPressed;
        }

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

        if (_exportFileDialog != null)
        {
            _exportFileDialog.FileSelected += (path) =>
            {
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var res = _exporter?.ExportFlattenedPng(path);
                    if (_lblStatus != null && res != null)
                    {
                        _lblStatus.Text = res.Success ? $"Saved PNG: {Path.GetFileName(path)}" : $"Error: {res.ErrorMessage}";
                        _lblStatus.Modulate = res.Success ? Colors.LightGreen : Colors.Salmon;
                    }
                }
                else if (path.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                {
                    string heroName = ResolveHeroCodename();
                    string meshName = _meshHierarchy?.ActiveTarget?.RawName ?? "body";
                    string relativeTexPath = $"models/heroes_staging/{heroName}/materials/{heroName}_{meshName}_color.png";
                    var res = _exporter?.ExportVmat(path, relativeTexPath);
                    if (_lblStatus != null && res != null)
                    {
                        _lblStatus.Text = res.Success ? $"Saved VMAT: {Path.GetFileName(path)}" : $"Error: {res.ErrorMessage}";
                        _lblStatus.Modulate = res.Success ? Colors.LightGreen : Colors.Salmon;
                    }
                }
            };
        }
    }

    private void EnsureSubsystems()
    {
        if (_layerManager == null || _meshHierarchy == null || _currentHero == null)
        {
            var tabPaint = GetTree().Root.FindChild("PaintTab", true, false) as PaintTabUI;
            if (tabPaint != null)
            {
                _layerManager ??= tabPaint.LayerManager;
                _meshHierarchy ??= tabPaint.MeshHierarchy;
                _currentHero ??= tabPaint.CurrentHero;
            }
        }

        if (_currentHero == null)
        {
            var vpkLoader = GetTree().Root.FindChild("VpkLoaderTest", true, false);
            if (vpkLoader != null)
            {
                var heroProp = vpkLoader.Get("CurrentHeroNode");
                if (heroProp.VariantType == Variant.Type.Object && heroProp.AsGodotObject() is Node3D n)
                {
                    _currentHero = n;
                }
            }
        }

        if (_exporter == null && _layerManager != null)
        {
            _exporter = new SkinExporter(_layerManager);
        }
    }

    private void OnExportModPressed()
    {
        EnsureSubsystems();

        if (_currentHero == null)
        {
            if (_lblStatus != null)
            {
                _lblStatus.Text = "Please load a hero in Character tab first.";
                _lblStatus.Modulate = Colors.Salmon;
            }
            GD.PrintErr("[PaintModExportPanelUI] Cannot export: No hero is loaded.");
            return;
        }

        // Bake composite on main thread before opening modal (if layerManager is active)
        Image preBakedAtlas = null;
        if (_layerManager != null)
        {
            preBakedAtlas = _layerManager.BakeCompositeImage();
        }

        string heroCodename = ResolveHeroCodename();
        string heroDisplayName = ResolveHeroDisplayName();

        // Refresh path manager config
        _pathManager ??= new GamePathManager();
        _pathManager.LoadConfig();

        // Get or instantiate ExportModDialog
        if (_modDialogInstance == null || !GodotObject.IsInstanceValid(_modDialogInstance))
        {
            var dialogScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportModDialog.tscn");
            if (dialogScene != null)
            {
                _modDialogInstance = dialogScene.Instantiate<ExportModDialog>();
                GetTree().Root.AddChild(_modDialogInstance);
                _modDialogInstance.ExportConfirmed += (config) =>
                {
                    ExecuteAsyncExportPipeline(config);
                };
            }
        }

        if (_modDialogInstance != null)
        {
            var submeshes = _meshHierarchy?.Submeshes;
            _modDialogInstance.Setup(heroCodename, heroDisplayName, submeshes, preBakedAtlas, _pathManager);
            _modDialogInstance.Visible = true;
        }
    }

    private async void ExecuteAsyncExportPipeline(ModExportConfig config)
    {
        if (config == null) return;

        // Get or instantiate ExportProgressDialog
        if (_progressDialogInstance == null || !GodotObject.IsInstanceValid(_progressDialogInstance))
        {
            var progressScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportProgressDialog.tscn");
            if (progressScene != null)
            {
                _progressDialogInstance = progressScene.Instantiate<ExportProgressDialog>();
                GetTree().Root.AddChild(_progressDialogInstance);
            }
        }

        if (_progressDialogInstance == null)
        {
            GD.PrintErr("[PaintModExportPanelUI] Failed to instantiate ExportProgressDialog.");
            return;
        }

        var cts = new CancellationTokenSource();
        string targetDir = config.InstallDirectlyToGame
            ? Path.Combine(config.DeadlockGamePath, "game", "citadel", "addons")
            : config.CustomExportFolder;

        _progressDialogInstance.StartExport(cts, targetDir);

        try
        {
            var res = await _exporter.ExportModAsync(config, _progressDialogInstance, cts.Token);
            _progressDialogInstance.OnExportFinished(res);

            if (_lblStatus != null && res != null)
            {
                if (res.Success)
                {
                    _lblStatus.Text = $"Exported: {Path.GetFileName(res.VpkPath)}";
                    _lblStatus.Modulate = Colors.LightGreen;
                }
                else
                {
                    _lblStatus.Text = $"Export Failed: {res.ErrorMessage}";
                    _lblStatus.Modulate = Colors.Salmon;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _progressDialogInstance.OnExportFinished(new SkinExportResult
            {
                Success = false,
                ErrorMessage = "Operation was canceled by user."
            });
            if (_lblStatus != null)
            {
                _lblStatus.Text = "Export canceled.";
                _lblStatus.Modulate = Colors.Gold;
            }
        }
        catch (Exception ex)
        {
            _progressDialogInstance.OnExportFinished(new SkinExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
            if (_lblStatus != null)
            {
                _lblStatus.Text = $"Error: {ex.Message}";
                _lblStatus.Modulate = Colors.Salmon;
            }
        }
    }

    private string ResolveHeroCodename()
    {
        if (_currentHero != null)
        {
            if (_currentHero.HasMeta("HeroCodename"))
            {
                return _currentHero.GetMeta("HeroCodename").AsString();
            }
            return _currentHero.Name.ToString().Replace("Hero_", "").ToLowerInvariant();
        }
        return "hero";
    }

    private string ResolveHeroDisplayName()
    {
        if (_currentHero != null)
        {
            if (_currentHero.HasMeta("HeroDisplayName"))
            {
                return _currentHero.GetMeta("HeroDisplayName").AsString();
            }
            string raw = _currentHero.Name.ToString().Replace("Hero_", "");
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }
        return "Hero";
    }
}
