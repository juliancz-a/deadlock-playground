using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Workspace;

public partial class ExportModDialog : CanvasLayer
{
    [Signal] public delegate void ExportConfirmedEventHandler(ModExportConfig config);
    [Signal] public delegate void ExportCancelledEventHandler();

    [Export] private LineEdit _txtModName;
    [Export] private Label _lblHero;
    [Export] private VBoxContainer _submeshChecklistContainer;
    [Export] private LineEdit _txtCompilerPath;
    [Export] private Button _btnBrowseCompilerPath;
    [Export] private Label _lblCompilerStatus;
    [Export] private LineEdit _txtCsdkPath;
    [Export] private Button _btnBrowseCsdkPath;
    [Export] private CheckBox _chkInstallCitadel;
    [Export] private CheckBox _chkCustomFolder;
    [Export] private Control _customFolderContainer;
    [Export] private LineEdit _txtCustomFolder;
    [Export] private Button _btnBrowseCustomFolder;
    [Export] private Label _lblTargetVpkInfo;
    [Export] private Button _btnConfirmExport;
    [Export] private Button _btnCancel;
    [Export] private Label _lblCommunityModWarning;
    [Export] private Button _btnDownloadCsdk;
    [Export] private FileDialog _folderDialog;
    [Export] private FileDialog _compilerFileDialog;

    private string _heroCodename = "hero";
    private string _heroDisplayName = "Hero";
    private List<SubmeshNodeInfo> _allSubmeshes = new();
    private readonly Dictionary<CheckBox, SubmeshNodeInfo> _submeshCheckboxes = new();
    private readonly Dictionary<CheckBox, WorkspaceAssetEntry> _extraModelCheckboxes = new();
    private readonly Dictionary<CheckBox, WorkspaceAssetEntry> _particleCheckboxes = new();
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
        _txtModName ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer/HBoxFileName/TxtModName")
                     ?? FindChild("TxtModName", true, false) as LineEdit;
        _lblHero ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/HBoxHero/LblHeroValue");
        _submeshChecklistContainer ??= GetNodeOrNull<VBoxContainer>("DialogPanel/Margin/VBox/ScrollSubmeshes/SubmeshChecklist");

        _txtCompilerPath ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/VBoxCompiler/HBoxCompiler/TxtCompilerPath");
        _btnBrowseCompilerPath ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/VBoxCompiler/HBoxCompiler/BtnBrowseCompilerPath");
        _lblCompilerStatus ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/VBoxCompiler/LblCompilerStatus");

        _txtCsdkPath ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/HBoxCsdk/TxtCsdkPath");
        _btnBrowseCsdkPath ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxCsdk/BtnBrowseCsdkPath");

        _chkInstallCitadel ??= GetNodeOrNull<CheckBox>("DialogPanel/Margin/VBox/VBoxInstall/ChkInstallCitadel");
        _chkCustomFolder ??= GetNodeOrNull<CheckBox>("DialogPanel/Margin/VBox/VBoxInstall/ChkCustomFolder");
        _customFolderContainer ??= GetNodeOrNull<Control>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer")
                                ?? FindChild("CustomFolderContainer", true, false) as Control;
        _txtCustomFolder ??= GetNodeOrNull<LineEdit>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer/HBoxFolder/TxtCustomFolder")
                          ?? FindChild("TxtCustomFolder", true, false) as LineEdit;
        _btnBrowseCustomFolder ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/VBoxInstall/CustomFolderContainer/HBoxFolder/BtnBrowseCustomFolder")
                                ?? FindChild("BtnBrowseCustomFolder", true, false) as Button;

        _lblTargetVpkInfo ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/LblTargetVpkInfo");
        _btnConfirmExport ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnConfirmExport");
        _btnCancel ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCancel");
        _lblCommunityModWarning ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/LblCommunityModWarning");
        _btnDownloadCsdk ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxCsdkDownload/BtnDownloadCsdk");

        _folderDialog ??= GetNodeOrNull<FileDialog>("FolderDialog");
        _compilerFileDialog ??= GetNodeOrNull<FileDialog>("CompilerFileDialog");
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
        GamePathManager pathManager,
        bool isCommunityMod = false)
    {
        FindNodes();

        _heroCodename = !string.IsNullOrEmpty(heroCodename) ? heroCodename : "hero";
        _heroDisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : _heroCodename;
        _allSubmeshes = submeshes != null ? submeshes.ToList() : new List<SubmeshNodeInfo>();
        _preBakedAtlas = preBakedAtlas;
        _pathManager = pathManager;

        if (_lblCommunityModWarning != null)
        {
            _lblCommunityModWarning.Visible = isCommunityMod;
        }

        if (_txtModName != null)
        {
            _txtModName.Text = $"{_heroDisplayName.Replace(" ", "")}_Skin";
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
        _extraModelCheckboxes.Clear();
        _particleCheckboxes.Clear();

        var session = ModWorkspaceSession.Instance;
        var characterModelAssets = new List<WorkspaceAssetEntry>();
        var extraModelAssets = new List<WorkspaceAssetEntry>();
        var particleAssets = new List<WorkspaceAssetEntry>();

        if (session != null && session.Manifest != null)
        {
            if (session.Manifest.Heroes.TryGetValue(_heroCodename, out var currentHeroEntry) && currentHeroEntry != null)
            {
                foreach (var asset in currentHeroEntry.Assets)
                {
                    if (asset.Category == "CharacterModel") characterModelAssets.Add(asset);
                    else if (asset.Category == "ExtraModel") extraModelAssets.Add(asset);
                    else if (asset.Category == "ParticleVFX") particleAssets.Add(asset);
                }
            }
        }

        void AddSectionHeader(string title, Color color)
        {
            var header = new Label
            {
                Text = title
            };
            header.AddThemeFontSizeOverride("font_size", 12);
            header.AddThemeColorOverride("font_color", color);
            _submeshChecklistContainer.AddChild(header);
        }

        void AddSubmeshRow(SubmeshNodeInfo submesh, bool isPreChecked)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);

            submesh.IsSelected = isPreChecked;

            var chk = new CheckBox
            {
                Text = $"{submesh.DisplayName} [{submesh.MaterialName ?? "mat"}]",
                ButtonPressed = isPreChecked,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };

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

        // Section 1: Hero Model Submeshes
        AddSectionHeader("👤 Hero Model Submeshes", new Color(0.9f, 0.8f, 0.45f));
        var dirtySubmeshes = _allSubmeshes.Where(s => s.IsDirty).ToList();
        bool hasOtherWorkspaceMods = extraModelAssets.Count > 0 || particleAssets.Count > 0;

        if (dirtySubmeshes.Count > 0)
        {
            foreach (var sm in dirtySubmeshes)
            {
                AddSubmeshRow(sm, true);
            }
        }
        else if (characterModelAssets.Count > 0)
        {
            foreach (var charAsset in characterModelAssets)
            {
                var hbox = new HBoxContainer();
                hbox.AddThemeConstantOverride("separation", 8);
                int texCount = charAsset.ModifiedTextures.Count;
                var chk = new CheckBox
                {
                    Text = $"{charAsset.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})",
                    ButtonPressed = true,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                var lblStatusBadge = new Label { Text = "[MODIFIED]" };
                lblStatusBadge.AddThemeFontSizeOverride("font_size", 10);
                lblStatusBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
                _extraModelCheckboxes[chk] = charAsset;
                hbox.AddChild(chk);
                hbox.AddChild(lblStatusBadge);
                _submeshChecklistContainer.AddChild(hbox);
            }
        }
        else if (hasOtherWorkspaceMods)
        {
            var emptyLbl = new Label
            {
                Text = "   (No character model submeshes modified)"
            };
            emptyLbl.AddThemeFontSizeOverride("font_size", 11);
            emptyLbl.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f));
            _submeshChecklistContainer.AddChild(emptyLbl);
        }
        else if (_allSubmeshes.Count > 0)
        {
            foreach (var sm in _allSubmeshes)
            {
                AddSubmeshRow(sm, false);
            }
        }
        else
        {
            var emptyLbl = new Label
            {
                Text = "   No submeshes found. Default body will be exported."
            };
            emptyLbl.AddThemeFontSizeOverride("font_size", 11);
            emptyLbl.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f));
            _submeshChecklistContainer.AddChild(emptyLbl);
        }

        // Section 2: Extra Models
        if (extraModelAssets.Count > 0)
        {
            var spacer = new Control { CustomMinimumSize = new Vector2(0, 6) };
            _submeshChecklistContainer.AddChild(spacer);

            AddSectionHeader("🗡️ Extra Models", new Color(0.4f, 0.75f, 0.95f));

            foreach (var extra in extraModelAssets)
            {
                var hbox = new HBoxContainer();
                hbox.AddThemeConstantOverride("separation", 8);

                int texCount = extra.ModifiedTextures.Count;
                var chk = new CheckBox
                {
                    Text = $"{extra.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})",
                    ButtonPressed = true,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };

                var lblStatusBadge = new Label { Text = "[MODIFIED]" };
                lblStatusBadge.AddThemeFontSizeOverride("font_size", 10);
                lblStatusBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));

                chk.Toggled += (pressed) =>
                {
                    lblStatusBadge.Text = pressed ? "[SELECTED]" : "[SKIPPED]";
                    lblStatusBadge.AddThemeColorOverride("font_color", pressed ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.5f, 0.55f, 0.65f));
                };

                _extraModelCheckboxes[chk] = extra;
                hbox.AddChild(chk);
                hbox.AddChild(lblStatusBadge);

                string modelFile = Path.GetFileName(extra.OwnerAssetPath);
                if (!string.IsNullOrEmpty(modelFile))
                {
                    var lblBadge = new Label { Text = modelFile };
                    lblBadge.AddThemeFontSizeOverride("font_size", 10);
                    lblBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.65f, 0.85f));
                    hbox.AddChild(lblBadge);
                }

                _submeshChecklistContainer.AddChild(hbox);
            }
        }

        // Section 3: Particle VFX
        if (particleAssets.Count > 0)
        {
            var spacer = new Control { CustomMinimumSize = new Vector2(0, 6) };
            _submeshChecklistContainer.AddChild(spacer);

            AddSectionHeader("✨ Particle VFX", new Color(0.85f, 0.5f, 0.95f));

            foreach (var part in particleAssets)
            {
                var hbox = new HBoxContainer();
                hbox.AddThemeConstantOverride("separation", 8);

                int texCount = part.ModifiedTextures.Count;
                var chk = new CheckBox
                {
                    Text = $"{part.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})",
                    ButtonPressed = true,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };

                var lblStatusBadge = new Label { Text = "[MODIFIED]" };
                lblStatusBadge.AddThemeFontSizeOverride("font_size", 10);
                lblStatusBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));

                chk.Toggled += (pressed) =>
                {
                    lblStatusBadge.Text = pressed ? "[SELECTED]" : "[SKIPPED]";
                    lblStatusBadge.AddThemeColorOverride("font_color", pressed ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.5f, 0.55f, 0.65f));
                };

                _particleCheckboxes[chk] = part;
                hbox.AddChild(chk);
                hbox.AddChild(lblStatusBadge);

                string vpcfFile = Path.GetFileName(part.OwnerAssetPath);
                if (!string.IsNullOrEmpty(vpcfFile))
                {
                    var lblBadge = new Label { Text = vpcfFile };
                    lblBadge.AddThemeFontSizeOverride("font_size", 10);
                    lblBadge.AddThemeColorOverride("font_color", new Color(0.4f, 0.65f, 0.85f));
                    hbox.AddChild(lblBadge);
                }

                _submeshChecklistContainer.AddChild(hbox);
            }
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
            if (string.IsNullOrEmpty(modName)) modName = $"{_heroDisplayName.Replace(" ", "")}_Skin";
            if (modName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                modName = modName.Substring(0, modName.Length - 4);
            }
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

        int totalSelectedCount = selectedSubmeshes.Count;
        int totalExtraSelected = _extraModelCheckboxes.Count(kvp => kvp.Key.ButtonPressed);
        int totalParticlesSelected = _particleCheckboxes.Count(kvp => kvp.Key.ButtonPressed);
        int totalItems = totalSelectedCount + totalExtraSelected + totalParticlesSelected;

        if (totalItems == 0 && ModWorkspaceSession.Instance.GetTotalModifiedCount() == 0)
        {
            if (_lblTargetVpkInfo != null)
            {
                _lblTargetVpkInfo.Text = "⚠ No modified assets or submeshes selected! Please check at least one item.";
                _lblTargetVpkInfo.Modulate = Colors.Salmon;
            }
            return;
        }

        var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _extraModelCheckboxes)
        {
            if (!kvp.Key.ButtonPressed)
            {
                if (!string.IsNullOrEmpty(kvp.Value.VpkPath)) excludedPaths.Add(kvp.Value.VpkPath);
                if (!string.IsNullOrEmpty(kvp.Value.OwnerAssetPath)) excludedPaths.Add(kvp.Value.OwnerAssetPath);
            }
        }
        foreach (var kvp in _particleCheckboxes)
        {
            if (!kvp.Key.ButtonPressed)
            {
                if (!string.IsNullOrEmpty(kvp.Value.VpkPath)) excludedPaths.Add(kvp.Value.VpkPath);
                if (!string.IsNullOrEmpty(kvp.Value.OwnerAssetPath)) excludedPaths.Add(kvp.Value.OwnerAssetPath);
            }
        }

        int resolution = 2048;
        if (selectedSubmeshes.Count > 0 && selectedSubmeshes[0].Mesh != null)
        {
            var baseTex = SkinLayerManager.ExtractBaseTexture(
                HeroMeshHierarchy.GetAuthenticMaterial(selectedSubmeshes[0].Mesh, selectedSubmeshes[0].SurfaceIndex)
                ?? selectedSubmeshes[0].Mesh.GetSurfaceOverrideMaterial(selectedSubmeshes[0].SurfaceIndex)
                ?? selectedSubmeshes[0].Mesh.MaterialOverride
            );
            if (baseTex != null)
            {
                resolution = Mathf.Max(baseTex.GetWidth(), baseTex.GetHeight());
            }
        }

        string modName = _txtModName?.Text.Trim();
        if (string.IsNullOrEmpty(modName)) modName = $"{_heroDisplayName.Replace(" ", "")}_Skin";
        if (modName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            modName = modName.Substring(0, modName.Length - 4);
        }

        string compilerPath = _txtCompilerPath?.Text.Trim() ?? "";
        string csdkPath = _txtCsdkPath?.Text.Trim() ?? "";

        var config = new ModExportConfig
        {
            ModName = modName,
            HeroCodename = _heroCodename,
            HeroDisplayName = _heroDisplayName,
            TargetSubmeshes = selectedSubmeshes,
            Resolution = resolution,
            InstallDirectlyToGame = _chkInstallCitadel?.ButtonPressed == true,
            CustomExportFolder = _txtCustomFolder?.Text.Trim() ?? "",
            DeadlockGamePath = _pathManager?.CurrentGamePath ?? "",
            ResourceCompilerPath = compilerPath,
            CsdkPath = csdkPath,
            PreBakedAtlas = _preBakedAtlas,
            ExcludedAssetVpkPaths = excludedPaths
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
