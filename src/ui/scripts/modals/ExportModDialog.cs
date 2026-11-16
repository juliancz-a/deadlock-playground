using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Theming;
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
    private string _detectedSourceModVpkPath = "";
    private List<SubmeshNodeInfo> _allSubmeshes = new();
    private readonly Dictionary<CheckBox, SubmeshNodeInfo> _submeshCheckboxes = new();
    private readonly Dictionary<CheckBox, WorkspaceAssetEntry> _extraModelCheckboxes = new();
    private readonly Dictionary<CheckBox, WorkspaceAssetEntry> _particleCheckboxes = new();
    private readonly Dictionary<CheckBox, WorkspaceAnimationEntry> _animationCheckboxes = new();
    private Image _preBakedAtlas;
    private GamePathManager _pathManager;
    private bool _isBrowsingCsdk = false;

    private CheckBox _chkMergeBaseMod;
    private CheckBox _chkPrioritizeNewMod;
    private CheckBox _chkOverwriteOriginalMod;
    private VBoxContainer _modMergeOptionsBox;
    private ColorRect _backdrop;
    private PanelContainer _dialogPanel;
    private bool _isCompilerFound;
    private string _targetVpkStatusKind = "success";

    public override void _Ready()
    {
        FindNodes();
        ConnectEvents();
        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyThemeStyling();
    }

    public override void _ExitTree()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(ThemeProfile _)
    {
        if (GodotObject.IsInstanceValid(this))
        {
            ApplyThemeStyling();
        }
    }

    private void ApplyThemeStyling()
    {
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
        {
            _backdrop.Color = ThemeManager.GetModalBackdropColor();
        }

        if (_dialogPanel != null && GodotObject.IsInstanceValid(_dialogPanel))
        {
            var modalTheme = ThemeManager.Instance?.GetModalTheme();
            if (modalTheme != null)
            {
                _dialogPanel.Theme = modalTheme;
            }
            _dialogPanel.ThemeTypeVariation = "ModalPanel";
        }

        ApplyCompilerStatusColor();
        ApplyTargetVpkStatusColor();
    }

    private void ApplyCompilerStatusColor()
    {
        if (_lblCompilerStatus == null || !GodotObject.IsInstanceValid(_lblCompilerStatus)) return;
        _lblCompilerStatus.Modulate = Colors.White;
        _lblCompilerStatus.ThemeTypeVariation = _isCompilerFound ? "BadgeLabelSuccess" : "BadgeLabelWarning";
        _lblCompilerStatus.AddThemeColorOverride("font_color", ThemeManager.GetColor(_isCompilerFound
            ? nameof(ThemePaletteData.SemanticSuccess)
            : nameof(ThemePaletteData.SemanticWarning)));
    }

    private void ApplyTargetVpkStatusColor()
    {
        if (_lblTargetVpkInfo == null || !GodotObject.IsInstanceValid(_lblTargetVpkInfo)) return;
        _lblTargetVpkInfo.Modulate = Colors.White;
        (string variation, string token) = _targetVpkStatusKind switch
        {
            "danger" => ("BadgeLabelDanger", nameof(ThemePaletteData.SemanticDanger)),
            "warning" => ("BadgeLabelWarning", nameof(ThemePaletteData.SemanticWarning)),
            _ => ("BadgeLabelSuccess", nameof(ThemePaletteData.SemanticSuccess))
        };
        _lblTargetVpkInfo.ThemeTypeVariation = variation;
        _lblTargetVpkInfo.AddThemeColorOverride("font_color", ThemeManager.GetColor(token));
    }

    private void FindNodes()
    {
        _backdrop ??= GetNodeOrNull<ColorRect>("Backdrop");
        _dialogPanel ??= GetNodeOrNull<PanelContainer>("DialogPanel");
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
                    _folderDialog.Title = "Select CSDK13 Root Directory (Optional)";
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

        if (_btnDownloadCsdk != null)
        {
            _btnDownloadCsdk.Pressed += () =>
                OS.ShellOpen("https://deadlockmodding.pages.dev/modding-tools/csdk-13");
        }
    }

    private string ResolveActiveSourceModVpk()
    {
        if (_allSubmeshes != null)
        {
            foreach (var sm in _allSubmeshes)
            {
                if (!string.IsNullOrWhiteSpace(sm?.SourceAddonVpkPath) && File.Exists(sm.SourceAddonVpkPath))
                    return sm.SourceAddonVpkPath;
                if (sm?.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh) && sm.Mesh.HasMeta("SourceAddonVpkPath"))
                {
                    string meta = sm.Mesh.GetMeta("SourceAddonVpkPath").AsString();
                    if (!string.IsNullOrWhiteSpace(meta) && File.Exists(meta))
                        return meta;
                }
            }
        }

        if (ModWorkspaceSession.Instance != null &&
            !string.IsNullOrWhiteSpace(_heroCodename) &&
            ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(_heroCodename, out var wsHero) &&
            !string.IsNullOrWhiteSpace(wsHero?.SourceAddonVpkPath) &&
            File.Exists(wsHero.SourceAddonVpkPath))
        {
            return wsHero.SourceAddonVpkPath;
        }

        var vpkLoader = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as global::VpkLoaderTest;
        if (vpkLoader != null && !string.IsNullOrWhiteSpace(vpkLoader.ActiveAddonVpkPath) && File.Exists(vpkLoader.ActiveAddonVpkPath))
        {
            return vpkLoader.ActiveAddonVpkPath;
        }

        return "";
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

        _detectedSourceModVpkPath = ResolveActiveSourceModVpk();
        bool hasSourceMod = !string.IsNullOrWhiteSpace(_detectedSourceModVpkPath) && File.Exists(_detectedSourceModVpkPath);

        if (_lblCommunityModWarning != null)
        {
            _lblCommunityModWarning.Visible = isCommunityMod || hasSourceMod;
            if (hasSourceMod)
            {
                _lblCommunityModWarning.Text = $"📦 Base Mod Detected: {Path.GetFileName(_detectedSourceModVpkPath)}";
            }
        }

        // Mount the Mod Merge checkboxes inside VBoxInstall (right below the install destination checkboxes)
        var installBox = _chkInstallCitadel?.GetParent() as Container
                      ?? GetNodeOrNull<Container>("DialogPanel/Margin/VBox/VBoxInstall")
                      ?? GetNodeOrNull<Container>("DialogPanel/Margin/VBox");
        if (installBox != null)
        {
            EnsureModMergeUICreated(installBox);
            if (_modMergeOptionsBox != null)
            {
                _modMergeOptionsBox.Visible = hasSourceMod;
            }
        }

        if (_txtModName != null)
        {
            _txtModName.Text = $"{_heroDisplayName.Replace(" ", "")}_Skin";
        }

        if (_lblHero != null)
        {
            _lblHero.Text = $"{_heroDisplayName} ({_heroCodename})";
        }

        string compilerPath = _pathManager?.ResolveResourceCompilerExe();
        if (_txtCompilerPath != null)
        {
            _txtCompilerPath.Text = compilerPath ?? "";
        }
        UpdateCompilerValidationStatus(compilerPath);

        string csdk = _pathManager?.CurrentCsdkPath;
        if (string.IsNullOrEmpty(csdk) && !string.IsNullOrEmpty(compilerPath))
        {
            csdk = AutoDeriveCsdkRoot(compilerPath);
        }
        if (_txtCsdkPath != null)
        {
            _txtCsdkPath.Text = csdk ?? "";
        }

        PopulateSubmeshChecklist();

        if (_chkInstallCitadel != null) _chkInstallCitadel.ButtonPressed = true;
        if (_chkCustomFolder != null) _chkCustomFolder.ButtonPressed = false;
        UpdateInstallModeUI();
        UpdateTargetVpkInfo();
        ApplyThemeStyling();

        Visible = true;
    }

    private void UpdateCompilerValidationStatus(string path)
    {
        if (_lblCompilerStatus == null) return;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim()))
        {
            _isCompilerFound = true;
            string trimmed = path.Trim();
            string norm = trimmed.Replace('\\', '/');
            bool isLegacy12 = norm.Contains("CSDK12", StringComparison.OrdinalIgnoreCase)
                           || norm.Contains("CSDK_12", StringComparison.OrdinalIgnoreCase)
                           || norm.Contains("/bin_cs2/", StringComparison.OrdinalIgnoreCase);
            _lblCompilerStatus.Text = isLegacy12
                ? $"✓ Found compiler: {Path.GetFileName(trimmed)} (Legacy CSDK12 deprecated — CSDK13 recommended)"
                : $"✓ Found compiler: {Path.GetFileName(trimmed)}";
        }
        else
        {
            _isCompilerFound = false;
            _lblCompilerStatus.Text = "⚠ resourcecompiler.exe not found! Please browse to the executable file.";
        }
        ApplyCompilerStatusColor();
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
        _animationCheckboxes.Clear();

        var session = ModWorkspaceSession.Instance;
        var characterModelAssets = new List<WorkspaceAssetEntry>();
        var extraModelAssets = new List<WorkspaceAssetEntry>();
        var particleAssets = new List<WorkspaceAssetEntry>();
        var animationEntries = new List<WorkspaceAnimationEntry>();

        if (session != null && session.Manifest != null)
        {
            if (session.Manifest.Heroes.TryGetValue(_heroCodename, out var currentHeroEntry) && currentHeroEntry != null)
            {
                foreach (var asset in currentHeroEntry.Assets)
                {
                    if (asset.Category == "CharacterModel") characterModelAssets.Add(asset);
                    else if (asset.Category == "ExtraModel") extraModelAssets.Add(asset);
                    else if (asset.Category == "ParticleVFX") particleAssets.Add(asset);

                    if (asset.Animations != null && asset.Animations.Count > 0)
                    {
                        animationEntries.AddRange(asset.Animations);
                    }
                }
            }
        }

        void AddSectionHeader(string title)
        {
            var header = new Label
            {
                Text = title,
                ThemeTypeVariation = "SectionHeaderLabel"
            };
            _submeshChecklistContainer.AddChild(header);
        }

        HBoxContainer AddChecklistRow(string text, bool isPreChecked, string initialStatusText, string fileBadgeText, out CheckBox chkOut, out Label statusOut)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);

            var chk = new CheckBox
            {
                Text = text,
                ButtonPressed = isPreChecked,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };

            var lblStatusBadge = new Label
            {
                Text = initialStatusText,
                ThemeTypeVariation = isPreChecked ? "BadgeLabelSuccess" : "MutedLabel"
            };

            hbox.AddChild(chk);
            hbox.AddChild(lblStatusBadge);

            if (!string.IsNullOrEmpty(fileBadgeText))
            {
                var lblBadge = new Label
                {
                    Text = fileBadgeText,
                    ThemeTypeVariation = "BadgeLabelInfo"
                };
                hbox.AddChild(lblBadge);
            }

            _submeshChecklistContainer.AddChild(hbox);
            chkOut = chk;
            statusOut = lblStatusBadge;
            return hbox;
        }

        bool hasCustomImportedModel = _allSubmeshes.Any(s =>
            s.IsCustomImportedMesh ||
            (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) &&
             s.Mesh.HasMeta("IsCustomImportedMesh") && s.Mesh.GetMeta("IsCustomImportedMesh").AsBool()));

        void AddSubmeshRow(SubmeshNodeInfo submesh, bool isPreChecked)
        {
            submesh.IsSelected = isPreChecked;
            if (isPreChecked && !hasCustomImportedModel)
            {
                submesh.IsDirty = true;
            }

            bool isOutline = submesh.Mesh != null && GodotObject.IsInstanceValid(submesh.Mesh) &&
                             submesh.Mesh.HasMeta("IsOutlineShell") && submesh.Mesh.GetMeta("IsOutlineShell").AsBool();
            string outlineTag = isOutline ? " [OUTLINE SHELL]" : "";

            string statusText = isPreChecked ? ((hasCustomImportedModel && !submesh.IsCustomImportedMesh && !submesh.IsDirty) ? "[KEPT PROP]" : (submesh.IsDirty ? "[SELECTED]" : "[MODIFIED]")) : "[UNMODIFIED]";
            string fileBadge = !string.IsNullOrEmpty(submesh.OriginalVmatPath) ? Path.GetFileName(submesh.OriginalVmatPath) : null;

            AddChecklistRow($"{submesh.DisplayName} [{submesh.MaterialName ?? "mat"}]{outlineTag}", isPreChecked, statusText, fileBadge, out var chk, out var lblStatusBadge);

            chk.Toggled += (pressed) =>
            {
                submesh.IsSelected = pressed;
                if (pressed)
                {
                    if (!hasCustomImportedModel)
                        submesh.IsDirty = true;
                    lblStatusBadge.Text = (hasCustomImportedModel && !submesh.IsCustomImportedMesh && !submesh.IsDirty) ? "[KEPT PROP]" : "[SELECTED]";
                    lblStatusBadge.ThemeTypeVariation = "BadgeLabelSuccess";
                }
                else
                {
                    lblStatusBadge.Text = "[SKIPPED]";
                    lblStatusBadge.ThemeTypeVariation = "MutedLabel";
                }
            };

            _submeshCheckboxes[chk] = submesh;
        }

        AddSectionHeader("Hero Model Submeshes");
        var dirtySubmeshes = _allSubmeshes.Where(s => s.IsDirty).ToList();
        bool hasOtherWorkspaceMods = extraModelAssets.Count > 0 || particleAssets.Count > 0 || animationEntries.Count > 0;

        if (_allSubmeshes.Count > 1)
        {
            var chkSelectAll = new CheckBox
            {
                Text = "Select All / Deselect All Materials",
                ButtonPressed = hasCustomImportedModel || dirtySubmeshes.Count > 0,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            chkSelectAll.Toggled += (selectAll) =>
            {
                foreach (var kv in _submeshCheckboxes)
                {
                    kv.Key.ButtonPressed = selectAll;
                }
            };
            _submeshChecklistContainer.AddChild(chkSelectAll);
        }

        if (hasCustomImportedModel)
        {
            foreach (var sm in _allSubmeshes)
            {
                bool isVisible = sm.Mesh == null || !GodotObject.IsInstanceValid(sm.Mesh) || sm.Mesh.Visible;
                AddSubmeshRow(sm, isVisible);
            }
        }
        else if (dirtySubmeshes.Count > 0)
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
                int texCount = charAsset.ModifiedTextures.Count;
                AddChecklistRow($"{charAsset.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})", true, "[MODIFIED]", null, out var chk, out _);
                _extraModelCheckboxes[chk] = charAsset;
            }
        }
        else if (hasOtherWorkspaceMods)
        {
            var emptyLbl = new Label
            {
                Text = "   (No character model submeshes modified)",
                ThemeTypeVariation = "MutedLabel"
            };
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
                Text = "   No submeshes found. Default body will be exported.",
                ThemeTypeVariation = "MutedLabel"
            };
            _submeshChecklistContainer.AddChild(emptyLbl);
        }

        if (extraModelAssets.Count > 0)
        {
            _submeshChecklistContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            AddSectionHeader("Extra Models");

            foreach (var extra in extraModelAssets)
            {
                int texCount = extra.ModifiedTextures.Count;
                string modelFile = Path.GetFileName(extra.OwnerAssetPath);
                AddChecklistRow($"{extra.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})", true, "[MODIFIED]", modelFile, out var chk, out var lblStatusBadge);
                chk.Toggled += (pressed) =>
                {
                    lblStatusBadge.Text = pressed ? "[SELECTED]" : "[SKIPPED]";
                    lblStatusBadge.ThemeTypeVariation = pressed ? "BadgeLabelSuccess" : "MutedLabel";
                };
                _extraModelCheckboxes[chk] = extra;
            }
        }

        if (particleAssets.Count > 0)
        {
            _submeshChecklistContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            AddSectionHeader("Particle VFX");

            foreach (var part in particleAssets)
            {
                int texCount = part.ModifiedTextures.Count;
                string vpcfFile = Path.GetFileName(part.OwnerAssetPath);
                AddChecklistRow($"{part.DisplayName} ({texCount} texture{(texCount != 1 ? "s" : "")})", true, "[MODIFIED]", vpcfFile, out var chk, out var lblStatusBadge);
                chk.Toggled += (pressed) =>
                {
                    lblStatusBadge.Text = pressed ? "[SELECTED]" : "[SKIPPED]";
                    lblStatusBadge.ThemeTypeVariation = pressed ? "BadgeLabelSuccess" : "MutedLabel";
                };
                _particleCheckboxes[chk] = part;
            }
        }

        if (animationEntries.Count > 0)
        {
            _submeshChecklistContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            AddSectionHeader("Animations");

            foreach (var animEntry in animationEntries)
            {
                string label = !string.IsNullOrEmpty(animEntry.CleanName) ? animEntry.CleanName : animEntry.AnimName;
                string clipFile = Path.GetFileName(animEntry.TargetVpkPath ?? animEntry.RelativeVnmclipPath ?? "");
                AddChecklistRow($"{label} ({animEntry.Duration:0.00}s)", true, "[MODIFIED]", clipFile, out var chk, out var lblStatusBadge);
                chk.Toggled += (pressed) =>
                {
                    lblStatusBadge.Text = pressed ? "[SELECTED]" : "[SKIPPED]";
                    lblStatusBadge.ThemeTypeVariation = pressed ? "BadgeLabelSuccess" : "MutedLabel";
                };
                _animationCheckboxes[chk] = animEntry;
            }
        }
    }

    private void EnsureModMergeUICreated(Container parentContainer)
    {
        if (_modMergeOptionsBox != null && GodotObject.IsInstanceValid(_modMergeOptionsBox))
            return;

        _modMergeOptionsBox = new VBoxContainer
        {
            Name = "ModMergeOptionsBox",
            Visible = false
        };
        _modMergeOptionsBox.AddThemeConstantOverride("separation", 4);

        _chkMergeBaseMod = new CheckBox
        {
            Text = "Bundle Base Mod Files (Standalone Merged VPK)",
            ButtonPressed = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = "Includes the original mod's 3D model (.vmdl_c), materials (.vmat_c), and base assets alongside your exported changes (animation/recolor/VFX/misc) in the new VPK."
        };
        _chkMergeBaseMod.Toggled += (_) => UpdateTargetVpkInfo();

        _chkPrioritizeNewMod = new CheckBox
        {
            Text = "Give New Export Highest Load Priority (Takes lower pakXX number & shifts original mod up)",
            ButtonPressed = true, // TRUE by default so the new export (animation/recolor/VFX/misc) wins in Deadlock without deleting the original!
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = "In Source 2, lower pakXX numbers have higher priority. This places your new mod (animation/recolor/VFX/misc) in the original mod's slot and moves the original mod to the next pakXX slot so nothing is lost."
        };
        _chkPrioritizeNewMod.Toggled += (_) => UpdateTargetVpkInfo();

        _chkOverwriteOriginalMod = new CheckBox
        {
            Text = "Overwrite Original Mod VPK in addons/ (Replace instead of keeping both)",
            ButtonPressed = false, // FALSE by default: keeps both mods!
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = "If checked, overwrites the original mod file directly instead of keeping both VPK files."
        };
        _chkOverwriteOriginalMod.Toggled += (on) =>
        {
            if (_chkPrioritizeNewMod != null)
            {
                _chkPrioritizeNewMod.Disabled = on;
            }
            UpdateTargetVpkInfo();
        };

        _modMergeOptionsBox.AddChild(_chkMergeBaseMod);
        _modMergeOptionsBox.AddChild(_chkPrioritizeNewMod);
        _modMergeOptionsBox.AddChild(_chkOverwriteOriginalMod);
        parentContainer.AddChild(_modMergeOptionsBox);
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

            bool hasSourceModInAddons = false;
            string srcFileName = "";
            if (!string.IsNullOrWhiteSpace(_detectedSourceModVpkPath) && File.Exists(_detectedSourceModVpkPath) && Directory.Exists(addonsDir))
            {
                string srcDir = Path.GetDirectoryName(Path.GetFullPath(_detectedSourceModVpkPath))?.TrimEnd('\\', '/') ?? "";
                string tgtDir = Path.GetFullPath(addonsDir).TrimEnd('\\', '/');
                hasSourceModInAddons = string.Equals(srcDir, tgtDir, StringComparison.OrdinalIgnoreCase);
                srcFileName = Path.GetFileName(_detectedSourceModVpkPath);
            }

            if (hasSourceModInAddons && _chkOverwriteOriginalMod?.ButtonPressed == true)
            {
                _lblTargetVpkInfo.Text = $"Will overwrite original mod in-place: {srcFileName} (in game/citadel/addons)";
                _targetVpkStatusKind = "warning";
            }
            else if (hasSourceModInAddons && _chkPrioritizeNewMod?.ButtonPressed != false)
            {
                _lblTargetVpkInfo.Text = $"Will take priority slot '{srcFileName}' & shift original mod to '{nextVpk}' (both kept!)";
                _targetVpkStatusKind = "success";
            }
            else
            {
                _lblTargetVpkInfo.Text = $"Will be installed as: {nextVpk} (in game/citadel/addons)";
                _targetVpkStatusKind = "success";
            }
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
            _targetVpkStatusKind = "warning";
        }
        ApplyTargetVpkStatusColor();
    }

    private void OnConfirmPressed()
    {
        var selectedSubmeshes = new List<SubmeshNodeInfo>();
        bool hasCustomImported = _allSubmeshes.Any(s =>
            s.IsCustomImportedMesh ||
            (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) &&
             s.Mesh.HasMeta("IsCustomImportedMesh") && s.Mesh.GetMeta("IsCustomImportedMesh").AsBool()));
        foreach (var kvp in _submeshCheckboxes)
        {
            var sm = kvp.Value;
            sm.IsSelected = kvp.Key.ButtonPressed;
            if (sm.IsSelected)
            {
                if (!hasCustomImported)
                    sm.IsDirty = true;
                selectedSubmeshes.Add(sm);
            }
        }

        int totalSelectedCount = selectedSubmeshes.Count;
        int totalExtraSelected = _extraModelCheckboxes.Count(kvp => kvp.Key.ButtonPressed);
        int totalParticlesSelected = _particleCheckboxes.Count(kvp => kvp.Key.ButtonPressed);
        int totalAnimsSelected = _animationCheckboxes.Count(kvp => kvp.Key.ButtonPressed);
        int totalItems = totalSelectedCount + totalExtraSelected + totalParticlesSelected + totalAnimsSelected;

        if (totalItems == 0 && ModWorkspaceSession.Instance.GetTotalModifiedCount() == 0)
        {
            if (_lblTargetVpkInfo != null)
            {
                _lblTargetVpkInfo.Text = "⚠ No modified assets or submeshes selected! Please check at least one item.";
                _targetVpkStatusKind = "danger";
                ApplyTargetVpkStatusColor();
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
        foreach (var kvp in _animationCheckboxes)
        {
            if (!kvp.Key.ButtonPressed)
            {
                string targetVpk = !string.IsNullOrEmpty(kvp.Value.TargetVpkPath)
                    ? kvp.Value.TargetVpkPath
                    : DeadlockPlayground.Tools.DeadlockAnimLoader.ResolveHeroClipVpkPath(_heroCodename, null, kvp.Value.CleanName);
                if (!string.IsNullOrEmpty(targetVpk))
                {
                    excludedPaths.Add(targetVpk.Replace('\\', '/').TrimStart('/'));
                }
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
            SourceModVpkPath = _detectedSourceModVpkPath,
            MergeWithSourceModVpk = _chkMergeBaseMod == null || _chkMergeBaseMod.ButtonPressed,
            PrioritizeNewModOverSource = _chkPrioritizeNewMod == null || _chkPrioritizeNewMod.ButtonPressed,
            OverwriteSourceModInAddons = _chkOverwriteOriginalMod != null && _chkOverwriteOriginalMod.ButtonPressed,
            PreBakedAtlas = _preBakedAtlas,
            ExcludedAssetVpkPaths = excludedPaths
        };

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