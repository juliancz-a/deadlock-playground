using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.Painter;

public partial class ExportModDialog : CanvasLayer
{
    [Signal] public delegate void ExportConfirmedEventHandler(ModExportConfig config);
    [Signal] public delegate void ExportCancelledEventHandler();

    [Export] private LineEdit _txtModName;
    [Export] private LineEdit _txtAuthor;
    [Export] private LineEdit _txtDescription;
    [Export] private Label _lblHero;
    [Export] private VBoxContainer _submeshChecklistContainer;
    [Export] private OptionButton _optResolution;
    [Export] private LineEdit _txtCompilerPath;
    [Export] private Button _btnBrowseCompilerPath;
    [Export] private Label _lblCompilerStatus;
    [Export] private LineEdit _txtCsdkPath;
    [Export] private Button _btnBrowseCsdkPath;
    [Export] private CheckBox _chkInstallCitadel;
    [Export] private CheckBox _chkCustomFolder;
    [Export] private HBoxContainer _customFolderContainer;
    [Export] private LineEdit _txtCustomFolder;
    [Export] private Button _btnBrowseCustomFolder;
    [Export] private Label _lblTargetVpkInfo;
    [Export] private Button _btnConfirmExport;
    [Export] private Button _btnCancel;
    [Export] private Button _btnDownloadCsdk;
    [Export] private FileDialog _folderDialog;
    [Export] private FileDialog _compilerFileDialog;

    private string _heroCodename = "hero";
    private string _heroDisplayName = "Hero";
    private List<SubmeshNodeInfo> _allSubmeshes = new();
    private readonly Dictionary<CheckBox, SubmeshNodeInfo> _submeshCheckboxes = new();
    private Image _preBakedAtlas;
    private GamePathManager _pathManager;
    private bool _isBrowsingCsdk = false;

    public override void _Ready()
    {
        FindNodes();
        ConnectEvents();
    }

    private void FindNodes()
    {
        _txtModName ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/GridMeta/TxtModName");
        _txtAuthor ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/GridMeta/TxtAuthor");
        _txtDescription ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/GridMeta/TxtDescription");
        _lblHero ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/HBoxHero/LblHeroValue");
        _submeshChecklistContainer ??= GetNodeOrNull<VBoxContainer>("DialogPanel/Margin/VBox/ScrollSubmeshes/SubmeshChecklist");
        _optResolution ??= GetNodeOrNull<OptionButton>("DialogPanel/Margin/VBox/HBoxRes/OptResolution");

        _txtCompilerPath ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/VBoxCompiler/HBoxCompiler/TxtCompilerPath");
        _btnBrowseCompilerPath ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/VBoxCompiler/HBoxCompiler/BtnBrowseCompilerPath");
        _lblCompilerStatus ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/VBoxCompiler/LblCompilerStatus");

        _txtCsdkPath ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/HBoxCsdk/TxtCsdkPath");
        _btnBrowseCsdkPath ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxCsdk/BtnBrowseCsdkPath");

        _chkInstallCitadel ??= GetNodeOrNull<CheckBox>("DialogPanel/Margin/VBox/VBoxInstall/ChkInstallCitadel");
        _chkCustomFolder ??= GetNodeOrNull<CheckBox>("DialogPanel/Margin/VBox/VBoxInstall/ChkCustomFolder");
        _customFolderContainer ??= GetNodeOrNull<HBoxContainer>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer");
        _txtCustomFolder ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer/TxtCustomFolder");
        _btnBrowseCustomFolder ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer/BtnBrowseCustomFolder");

        _lblTargetVpkInfo ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/LblTargetVpkInfo");
        _btnConfirmExport ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnConfirmExport");
        _btnCancel ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCancel");
        _btnDownloadCsdk ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxCsdkDownload/BtnDownloadCsdk");

        _folderDialog ??= GetNodeOrNull<FileDialog>("FolderDialog");
        _compilerFileDialog ??= GetNodeOrNull<FileDialog>("CompilerFileDialog");

        // Populate resolution options
        if (_optResolution != null && _optResolution.ItemCount == 0)
        {
            _optResolution.AddItem("1024 x 1024", 1024);
            _optResolution.AddItem("2048 x 2048 (Recommended)", 2048);
            _optResolution.AddItem("4096 x 4096 (Ultra)", 4096);
            _optResolution.Select(1); // Default to 2048
        }
    }

    private void ConnectEvents()
    {
        if (_chkInstallCitadel != null)
        {
            _chkInstallCitadel.Toggled += (on) =>
            {
                if (on && _chkCustomFolder != null) _chkCustomFolder.ButtonPressed = false;
                UpdateInstallModeUI();
                UpdateTargetVpkInfo();
            };
        }

        if (_chkCustomFolder != null)
        {
            _chkCustomFolder.Toggled += (on) =>
            {
                if (on && _chkInstallCitadel != null) _chkInstallCitadel.ButtonPressed = false;
                UpdateInstallModeUI();
                UpdateTargetVpkInfo();
            };
        }

        if (_btnBrowseCompilerPath != null)
        {
            _btnBrowseCompilerPath.Pressed += () =>
            {
                if (_compilerFileDialog != null)
                {
                    string existingPath = _txtCompilerPath?.Text?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
                    {
                        _compilerFileDialog.CurrentDir = Path.GetDirectoryName(existingPath);
                        _compilerFileDialog.CurrentFile = Path.GetFileName(existingPath);
                    }
                    else
                    {
                        _compilerFileDialog.CurrentDir = "C:/";
                    }
                    _compilerFileDialog.PopupCentered(new Vector2I(750, 500));
                }
            };
        }

        if (_compilerFileDialog != null)
        {
            _compilerFileDialog.FileSelected += (file) =>
            {
                if (_txtCompilerPath != null)
                {
                    _txtCompilerPath.Text = file;
                }
                UpdateCompilerValidationStatus(file);
                _pathManager?.SaveResourceCompilerPath(file);

                // Try to auto-populate CSDK Root if currently blank
                if (string.IsNullOrEmpty(_txtCsdkPath?.Text))
                {
                    string autoRoot = AutoDeriveCsdkRoot(file);
                    if (!string.IsNullOrEmpty(autoRoot) && _txtCsdkPath != null)
                    {
                        _txtCsdkPath.Text = autoRoot;
                        _pathManager?.SaveCsdkPath(autoRoot);
                    }
                }
            };
        }

        if (_txtCompilerPath != null)
        {
            _txtCompilerPath.TextChanged += (text) =>
            {
                UpdateCompilerValidationStatus(text);
                if (File.Exists(text))
                {
                    _pathManager?.SaveResourceCompilerPath(text);
                }
            };
        }

        if (_btnBrowseCustomFolder != null)
        {
            _btnBrowseCustomFolder.Pressed += () =>
            {
                _isBrowsingCsdk = false;
                if (_folderDialog != null)
                {
                    _folderDialog.Title = "Select Custom Export Folder";
                    _folderDialog.CurrentDir = !string.IsNullOrEmpty(_txtCustomFolder?.Text) && Directory.Exists(_txtCustomFolder.Text)
                        ? _txtCustomFolder.Text
                        : OS.GetSystemDir(OS.SystemDir.Desktop);
                    _folderDialog.PopupCentered(new Vector2I(750, 500));
                }
            };
        }

        if (_btnBrowseCsdkPath != null)
        {
            _btnBrowseCsdkPath.Pressed += () =>
            {
                _isBrowsingCsdk = true;
                if (_folderDialog != null)
                {
                    _folderDialog.Title = "Select CSDK12 Root Directory (Optional)";
                    _folderDialog.CurrentDir = !string.IsNullOrEmpty(_txtCsdkPath?.Text) && Directory.Exists(_txtCsdkPath.Text)
                        ? _txtCsdkPath.Text
                        : "C:/";
                    _folderDialog.PopupCentered(new Vector2I(750, 500));
                }
            };
        }

        if (_folderDialog != null)
        {
            _folderDialog.DirSelected += (dir) =>
            {
                if (_isBrowsingCsdk)
                {
                    if (_txtCsdkPath != null) _txtCsdkPath.Text = dir;
                    _pathManager?.SaveCsdkPath(dir);
                }
                else
                {
                    if (_txtCustomFolder != null) _txtCustomFolder.Text = dir;
                    UpdateTargetVpkInfo();
                }
            };
        }

        if (_txtModName != null)
        {
            _txtModName.TextChanged += (_) => UpdateTargetVpkInfo();
        }

        if (_btnConfirmExport != null)
        {
            _btnConfirmExport.Pressed += OnConfirmPressed;
        }

        if (_btnCancel != null)
        {
            _btnCancel.Pressed += () =>
            {
                Visible = false;
                EmitSignal(SignalName.ExportCancelled);
            };
        }

        // Open the CSDK12 download page in the system default browser.
        if (_btnDownloadCsdk != null)
        {
            _btnDownloadCsdk.Pressed += () =>
                OS.ShellOpen("https://deadlockmodding.pages.dev/modding-tools/csdk-12");
        }
    }

    public void Setup(
        string heroCodename,
        string heroDisplayName,
        IReadOnlyList<SubmeshNodeInfo> submeshes,
        Image preBakedAtlas,
        GamePathManager pathManager)
    {
        FindNodes();

        _heroCodename = !string.IsNullOrEmpty(heroCodename) ? heroCodename : "hero";
        _heroDisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : _heroCodename;
        _allSubmeshes = submeshes != null ? submeshes.ToList() : new List<SubmeshNodeInfo>();
        _preBakedAtlas = preBakedAtlas;
        _pathManager = pathManager;

        if (_txtModName != null)
        {
            _txtModName.Text = $"{_heroDisplayName.Replace(" ", "")}_CustomSkin";
        }

        if (_lblHero != null)
        {
            _lblHero.Text = $"{_heroDisplayName} ({_heroCodename})";
        }

        // Resolve Resource Compiler Path
        string compilerPath = _pathManager?.ResolveResourceCompilerExe();
        if (_txtCompilerPath != null)
        {
            _txtCompilerPath.Text = compilerPath ?? "";
        }
        UpdateCompilerValidationStatus(compilerPath);

        // Populate CSDK path if available
        string csdk = _pathManager?.CurrentCsdkPath;
        if (string.IsNullOrEmpty(csdk) && !string.IsNullOrEmpty(compilerPath))
        {
            csdk = AutoDeriveCsdkRoot(compilerPath);
        }
        if (_txtCsdkPath != null)
        {
            _txtCsdkPath.Text = csdk ?? "";
        }

        // Setup submeshes checklist
        PopulateSubmeshChecklist();

        // Default install mode
        if (_chkInstallCitadel != null) _chkInstallCitadel.ButtonPressed = true;
        if (_chkCustomFolder != null) _chkCustomFolder.ButtonPressed = false;
        UpdateInstallModeUI();
        UpdateTargetVpkInfo();

        Visible = true;
    }

    private void UpdateCompilerValidationStatus(string path)
    {
        if (_lblCompilerStatus == null) return;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim()))
        {
            _lblCompilerStatus.Text = $"✓ Found compiler: {Path.GetFileName(path.Trim())}";
            _lblCompilerStatus.Modulate = new Color(0.4f, 0.95f, 0.5f);
        }
        else
        {
            _lblCompilerStatus.Text = "⚠ resourcecompiler.exe not found! Please browse to the executable file.";
            _lblCompilerStatus.Modulate = new Color(1.0f, 0.65f, 0.4f);
        }
    }

    private static string AutoDeriveCsdkRoot(string compilerExePath)
    {
        if (string.IsNullOrEmpty(compilerExePath)) return "";
        try
        {
            string norm = compilerExePath.Replace('\\', '/');
            int idx = norm.IndexOf("/game/bin/win64", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return norm.Substring(0, idx);
            }
            return Path.GetDirectoryName(compilerExePath)?.Replace('\\', '/') ?? "";
        }
        catch
        {
            return "";
        }
    }

    private void PopulateSubmeshChecklist()
    {
        if (_submeshChecklistContainer == null) return;

        foreach (Node child in _submeshChecklistContainer.GetChildren())
        {
            child.QueueFree();
        }
        _submeshCheckboxes.Clear();

        if (_allSubmeshes.Count == 0)
        {
            var emptyLbl = new Label
            {
                Text = "No submeshes found. Default body will be exported."
            };
            emptyLbl.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
            _submeshChecklistContainer.AddChild(emptyLbl);
            return;
        }

        foreach (var submesh in _allSubmeshes)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);

            bool isPreChecked = submesh.IsDirty;
            submesh.IsSelected = isPreChecked;

            var chk = new CheckBox
            {
                Text = $"{submesh.DisplayName} [{submesh.MaterialName ?? "mat"}]",
                ButtonPressed = isPreChecked,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };

            // Badge indicator for dirty state
            var lblStatusBadge = new Label
            {
                Text = submesh.IsDirty ? "[MODIFIED]" : "[UNMODIFIED]"
            };
            lblStatusBadge.AddThemeFontSizeOverride("font_size", 10);
            lblStatusBadge.AddThemeColorOverride("font_color", submesh.IsDirty ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.5f, 0.55f, 0.65f));

            chk.Toggled += (pressed) =>
            {
                submesh.IsSelected = pressed;
                if (pressed)
                {
                    submesh.IsDirty = true;
                    lblStatusBadge.Text = "[SELECTED]";
                    lblStatusBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
                }
                else
                {
                    lblStatusBadge.Text = "[SKIPPED]";
                    lblStatusBadge.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.65f));
                }
            };

            _submeshCheckboxes[chk] = submesh;
            hbox.AddChild(chk);
            hbox.AddChild(lblStatusBadge);

            // Badge indicator if it has authentic original material
            if (!string.IsNullOrEmpty(submesh.OriginalVmatPath))
            {
                var lblBadge = new Label
                {
                    Text = Path.GetFileName(submesh.OriginalVmatPath)
                };
                lblBadge.AddThemeFontSizeOverride("font_size", 10);
                lblBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.65f, 0.85f));
                hbox.AddChild(lblBadge);
            }

            _submeshChecklistContainer.AddChild(hbox);
        }
    }

    private void UpdateInstallModeUI()
    {
        bool isCustom = _chkCustomFolder?.ButtonPressed == true;
        if (_customFolderContainer != null)
        {
            _customFolderContainer.Visible = isCustom;
        }
    }

    private void UpdateTargetVpkInfo()
    {
        if (_lblTargetVpkInfo == null) return;

        bool isDirect = _chkInstallCitadel?.ButtonPressed == true;
        if (isDirect)
        {
            string deadlockPath = _pathManager?.CurrentGamePath ?? "";
            string addonsDir = Path.Combine(deadlockPath, "game", "citadel", "addons");
            string nextVpk = VpkIndexResolver.ResolveNextFileName(addonsDir);
            _lblTargetVpkInfo.Text = $"Will be installed as: {nextVpk} (in game/citadel/addons)";
            _lblTargetVpkInfo.Modulate = new Color(0.4f, 0.9f, 0.5f);
        }
        else
        {
            string modName = _txtModName?.Text.Trim();
            if (string.IsNullOrEmpty(modName)) modName = "CustomSkin";
            string folder = _txtCustomFolder?.Text.Trim();
            if (string.IsNullOrEmpty(folder)) folder = "Custom Folder";
            _lblTargetVpkInfo.Text = $"Will be exported as: {modName}.vpk (in {folder})";
            _lblTargetVpkInfo.Modulate = new Color(0.9f, 0.8f, 0.4f);
        }
    }

    private void OnConfirmPressed()
    {
        var selectedSubmeshes = new List<SubmeshNodeInfo>();
        foreach (var kvp in _submeshCheckboxes)
        {
            var sm = kvp.Value;
            sm.IsSelected = kvp.Key.ButtonPressed;
            if (sm.IsSelected && sm.IsDirty)
            {
                selectedSubmeshes.Add(sm);
            }
        }

        if (selectedSubmeshes.Count == 0)
        {
            if (_lblTargetVpkInfo != null)
            {
                _lblTargetVpkInfo.Text = "⚠ No modified submeshes selected! Please check at least one submesh.";
                _lblTargetVpkInfo.Modulate = Colors.Salmon;
            }
            return;
        }

        int resolution = 2048;
        if (_optResolution != null)
        {
            int selectedId = _optResolution.GetSelectedId();
            if (selectedId > 0) resolution = selectedId;
        }

        string modName = _txtModName?.Text.Trim();
        if (string.IsNullOrEmpty(modName)) modName = $"{_heroDisplayName}_CustomSkin";

        string compilerPath = _txtCompilerPath?.Text.Trim() ?? "";
        string csdkPath = _txtCsdkPath?.Text.Trim() ?? "";

        var config = new ModExportConfig
        {
            ModName = modName,
            Author = _txtAuthor?.Text.Trim() ?? "",
            Description = _txtDescription?.Text.Trim() ?? "",
            HeroCodename = _heroCodename,
            HeroDisplayName = _heroDisplayName,
            TargetSubmeshes = selectedSubmeshes,
            Resolution = resolution,
            InstallDirectlyToGame = _chkInstallCitadel?.ButtonPressed == true,
            CustomExportFolder = _txtCustomFolder?.Text.Trim() ?? "",
            DeadlockGamePath = _pathManager?.CurrentGamePath ?? "",
            ResourceCompilerPath = compilerPath,
            CsdkPath = csdkPath,
            PreBakedAtlas = _preBakedAtlas
        };

        // Persist paths to user config
        if (!string.IsNullOrEmpty(compilerPath))
        {
            _pathManager?.SaveResourceCompilerPath(compilerPath);
        }
        if (!string.IsNullOrEmpty(csdkPath))
        {
            _pathManager?.SaveCsdkPath(csdkPath);
        }

        Visible = false;
        EmitSignal(SignalName.ExportConfirmed, config);
    }
}
