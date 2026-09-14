using Godot;
using System;
using System.IO;
using DeadlockPlayground.Painter;

public partial class PaintModExportPanelUI : PanelContainer
{
    [Export] private Button _btnExportPng;
    [Export] private Button _btnExportVmat;
    [Export] private Button _btnInstallCitadel;
    [Export] private Label _lblStatus;
    [Export] private FileDialog _exportFileDialog;

    private SkinLayerManager _layerManager;
    private HeroMeshHierarchy _meshHierarchy;
    private Node3D _currentHero;
    private SkinExporter _exporter;

    public override void _Ready()
    {
        _btnExportPng ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnExportPng");
        _btnExportVmat ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnExportVmat");
        _btnInstallCitadel ??= GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BtnInstallCitadel");
        _lblStatus ??= GetNodeOrNull<Label>("MarginContainer/VBoxContainer/LblStatus");
        _exportFileDialog ??= GetNodeOrNull<FileDialog>("ExportFileDialog");

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
                string heroName = _currentHero?.Name.ToString().Replace("Hero_", "") ?? "hero";
                string meshName = _meshHierarchy?.ActiveTarget?.RawName ?? "body";
                var pathManager = new GamePathManager();
                var res = _exporter?.InstallModToCitadel(pathManager.CurrentGamePath, heroName, meshName);
                if (res != null)
                {
                    if (_lblStatus != null)
                    {
                        _lblStatus.Text = res.Success
                            ? $"Staged to Addons: {Path.GetFileName(res.ModDirectory)}"
                            : $"Failed: {res.ErrorMessage}";
                        _lblStatus.Modulate = res.Success ? Colors.LightGreen : Colors.Salmon;
                    }
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
                    string heroName = _currentHero?.Name.ToString().Replace("Hero_", "") ?? "hero";
                    string meshName = _meshHierarchy?.ActiveTarget?.RawName ?? "body";
                    string relativeTexPath = $"materials/heroes/{heroName}/{heroName}_{meshName}_color.png";
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
}
