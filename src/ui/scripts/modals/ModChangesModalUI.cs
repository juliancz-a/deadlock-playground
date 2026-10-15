using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.UI;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.UI
{
    public partial class ModChangesModalUI : PanelContainer
    {
        private VBoxContainer _contentContainer;
        private Label _lblCountBadge;
        private Label _lblSubtitle;
        private Button _btnClose;
        private Button _btnSaveProject;
        private Button _btnLoadProject;
        private Button _btnExportMod;
        private Button _btnClearAll;

        private FileDialog _fileDialogSave;
        private FileDialog _fileDialogLoad;

        [Signal] public delegate void ModalClosedEventHandler();

        public event Action<string, string> OnLoadModelRequested; // heroId, vpkPath
        public event Action<string, string> OnLoadParticleRequested; // heroId, vpcfPath

        public event Action<string, string> OnExportHeroModRequested; // heroId, heroDisplayName
        public event Action OnExportModRequested;

        public override void _Ready()
        {
            Name = "ModChangesModal";
            Visible = false;

            CustomMinimumSize = new Vector2(820, 580);
            Size = new Vector2(820, 580);
            CenterModal();

            var panelStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.09f, 0.12f, 0.96f),
                BorderWidthBottom = 2,
                BorderWidthLeft = 2,
                BorderWidthRight = 2,
                BorderWidthTop = 2,
                BorderColor = new Color(0.24f, 0.28f, 0.35f, 0.9f),
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                ShadowColor = new Color(0, 0, 0, 0.6f),
                ShadowSize = 16
            };
            AddThemeStyleboxOverride("panel", panelStyle);

            BuildUI();

            ModWorkspaceSession.Instance.OnWorkspaceChanged += RefreshList;
        }

        public override void _ExitTree()
        {
            ModWorkspaceSession.Instance.OnWorkspaceChanged -= RefreshList;
        }

        private void BuildUI()
        {
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 20);
            margin.AddThemeConstantOverride("margin_right", 20);
            margin.AddThemeConstantOverride("margin_top", 16);
            margin.AddThemeConstantOverride("margin_bottom", 16);
            AddChild(margin);

            var rootVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            rootVBox.AddThemeConstantOverride("separation", 12);
            margin.AddChild(rootVBox);

            var colus = PlaygroundThemeHelper.GetFontColus();

            // 1. Header Bar
            var headerBar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            headerBar.AddThemeConstantOverride("separation", 10);

            var titleIcon = new TextureRect
            {
                Texture = GD.Load<Texture2D>("res://assets/at-icons/folder.svg") ?? GD.Load<Texture2D>("res://assets/at-icons/cog.svg"),
                CustomMinimumSize = new Vector2(22, 22),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Modulate = new Color(0.95f, 0.75f, 0.30f)
            };
            headerBar.AddChild(titleIcon);

            var titleLbl = new Label
            {
                Text = "WORKSPACE MOD CHANGES",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            if (colus != null) titleLbl.AddThemeFontOverride("font", colus);
            titleLbl.AddThemeFontSizeOverride("font_size", 16);
            headerBar.AddChild(titleLbl);

            _lblCountBadge = new Label
            {
                Text = "[0]",
                Modulate = new Color(0.95f, 0.75f, 0.30f),
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            _lblCountBadge.AddThemeFontSizeOverride("font_size", 12);
            headerBar.AddChild(_lblCountBadge);

            _btnClose = new Button
            {
                Text = "✕",
                CustomMinimumSize = new Vector2(28, 28),
                Flat = true
            };
            _btnClose.Pressed += CloseModal;
            headerBar.AddChild(_btnClose);
            rootVBox.AddChild(headerBar);

            _lblSubtitle = new Label
            {
                Text = "Centralized overview of all staged character paints, extra model customizations, and particle VFX modifications.",
                Modulate = new Color(0.65f, 0.68f, 0.74f),
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _lblSubtitle.AddThemeFontSizeOverride("font_size", 11);
            rootVBox.AddChild(_lblSubtitle);

            rootVBox.AddChild(new HSeparator());

            // 2. Scrollable Content Area
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            _contentContainer = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            _contentContainer.AddThemeConstantOverride("separation", 14);
            scroll.AddChild(_contentContainer);
            rootVBox.AddChild(scroll);

            rootVBox.AddChild(new HSeparator());

            // 3. Footer Actions Bar
            var footerBar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            footerBar.AddThemeConstantOverride("separation", 10);

            _btnSaveProject = new Button
            {
                Text = "Save Project (.dlproj)",
                CustomMinimumSize = new Vector2(160, 32)
            };
            _btnSaveProject.Pressed += OnSaveProjectClicked;
            footerBar.AddChild(_btnSaveProject);

            _btnLoadProject = new Button
            {
                Text = "Load Project",
                CustomMinimumSize = new Vector2(130, 32)
            };
            _btnLoadProject.Pressed += OnLoadProjectClicked;
            footerBar.AddChild(_btnLoadProject);

            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            footerBar.AddChild(spacer);

            _btnClearAll = new Button
            {
                Text = "Clear Session",
                CustomMinimumSize = new Vector2(110, 32),
                Modulate = new Color(0.9f, 0.4f, 0.4f)
            };
            _btnClearAll.Pressed += OnClearAllClicked;
            footerBar.AddChild(_btnClearAll);

            var btnDone = new Button
            {
                Text = "Close",
                CustomMinimumSize = new Vector2(90, 32)
            };
            btnDone.Pressed += CloseModal;
            footerBar.AddChild(btnDone);

            rootVBox.AddChild(footerBar);

            SetupFileDialogs();
        }

        private void SetupFileDialogs()
        {
            _fileDialogSave = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new string[] { "*.dpp ; Deadlock Playground Project (*.dpp)" },
                UseNativeDialog = true,
                Title = "Save Project (.dpp)"
            };
            _fileDialogSave.FileSelected += (path) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    if (!path.EndsWith(".dpp", StringComparison.OrdinalIgnoreCase))
                        path += ".dpp";
                    ModWorkspaceSession.Instance.PackageProject(path);
                    RefreshList();
                    StudioUIManager.Instance?.ShowToast($"Project Saved: {Path.GetFileName(path)}", path);
                }
            };
            AddChild(_fileDialogSave);

            _fileDialogLoad = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new string[] { "*.dpp ; Deadlock Playground Project (*.dpp)" },
                UseNativeDialog = true,
                Title = "Load Project (.dpp)"
            };
            _fileDialogLoad.FileSelected += (path) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    CloseModal();
                    var paintTab = (GetTree()?.Root?.FindChild("PaintTab", true, false) 
                                 ?? GetTree()?.Root?.FindChild("PaintTabUI", true, false)) as global::PaintTabUI;
                    paintTab?.ExecuteLoadProject(path);
                }
            };
            AddChild(_fileDialogLoad);
        }

        public void CenterModal()
        {
            SetAnchorsPreset(LayoutPreset.Center);
            AnchorLeft = 0.5f;
            AnchorTop = 0.5f;
            AnchorRight = 0.5f;
            AnchorBottom = 0.5f;
            GrowHorizontal = GrowDirection.Both;
            GrowVertical = GrowDirection.Both;
            float halfW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X / 2f : 410f;
            float halfH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y / 2f : 290f;
            OffsetLeft = -halfW;
            OffsetTop = -halfH;
            OffsetRight = halfW;
            OffsetBottom = halfH;
        }

        public void OpenModal()
        {
            CenterModal();
            RefreshList();
            Visible = true;
        }

        public void CloseModal()
        {
            Visible = false;
            EmitSignal(SignalName.ModalClosed);
            var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                        ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
            studioUI?.UpdateModalsState();
        }

        public void RefreshList()
        {
            if (_contentContainer == null) return;

            foreach (Node child in _contentContainer.GetChildren())
            {
                child.QueueFree();
            }

            var session = ModWorkspaceSession.Instance;
            int totalCount = session.GetTotalModifiedCount();
            if (_lblCountBadge != null) _lblCountBadge.Text = $"[{totalCount}]";

            var colus = PlaygroundThemeHelper.GetFontColus();

            if (session.Manifest.Heroes.Count == 0)
            {
                var emptyPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var emptyStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.12f, 0.14f, 0.18f, 0.7f),
                    CornerRadiusBottomLeft = 6,
                    CornerRadiusBottomRight = 6,
                    CornerRadiusTopLeft = 6,
                    CornerRadiusTopRight = 6,
                    ContentMarginLeft = 20,
                    ContentMarginRight = 20,
                    ContentMarginTop = 30,
                    ContentMarginBottom = 30
                };
                emptyPanel.AddThemeStyleboxOverride("panel", emptyStyle);

                var emptyLbl = new Label
                {
                    Text = "No staged modifications in this workspace session.\nPaint character materials or customize VFX parameters to track changes across heroes.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Modulate = new Color(0.65f, 0.68f, 0.74f)
                };
                emptyPanel.AddChild(emptyLbl);
                _contentContainer.AddChild(emptyPanel);
                return;
            }

            // Current 3D stage info
            var vpkLoader = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
            string curHeroId = vpkLoader?.LastLoadedHeroEntry?.InternalCodename ?? "";
            bool isExtraLoaded = vpkLoader?.IsExtraModelLoaded ?? false;

            var pStage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as DeadlockPlayground.Particles.ParticleStage3D;
            string curVpcf = pStage?.ActiveVpcfPath ?? "";
            bool hasActiveParticle = pStage != null && pStage.HasActiveParticle;

            foreach (var hero in session.Manifest.Heroes.Values)
            {
                bool isCurrentActiveHero = string.Equals(hero.HeroId, curHeroId, StringComparison.OrdinalIgnoreCase);

                var heroCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var heroCardStyle = new StyleBoxFlat
                {
                    BgColor = isCurrentActiveHero 
                        ? new Color(0.14f, 0.18f, 0.22f, 0.95f) 
                        : new Color(0.12f, 0.14f, 0.18f, 0.85f),
                    BorderWidthBottom = isCurrentActiveHero ? 2 : 1,
                    BorderWidthLeft = isCurrentActiveHero ? 2 : 1,
                    BorderWidthRight = isCurrentActiveHero ? 2 : 1,
                    BorderWidthTop = isCurrentActiveHero ? 2 : 1,
                    BorderColor = isCurrentActiveHero 
                        ? new Color(0.95f, 0.78f, 0.32f, 0.95f) //Highlight
                        : new Color(0.22f, 0.26f, 0.32f, 0.6f),
                    CornerRadiusBottomLeft = 6,
                    CornerRadiusBottomRight = 6,
                    CornerRadiusTopLeft = 6,
                    CornerRadiusTopRight = 6,
                    ContentMarginLeft = 14,
                    ContentMarginRight = 14,
                    ContentMarginTop = 10,
                    ContentMarginBottom = 10
                };
                heroCard.AddThemeStyleboxOverride("panel", heroCardStyle);

                var cardVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardVBox.AddThemeConstantOverride("separation", 8);

                // Hero Header Row
                var heroHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                heroHeader.AddThemeConstantOverride("separation", 8);

                var heroTitle = new Label
                {
                    Text = isCurrentActiveHero 
                        ? $"★ HERO: {hero.DisplayName.ToUpperInvariant()} ({hero.HeroId}) — [ACTIVE]"
                        : $"HERO: {hero.DisplayName.ToUpperInvariant()} ({hero.HeroId})",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Modulate = isCurrentActiveHero ? new Color(1.0f, 0.88f, 0.45f) : Colors.White
                };
                if (colus != null) heroTitle.AddThemeFontOverride("font", colus);
                heroTitle.AddThemeFontSizeOverride("font_size", 13);
                heroHeader.AddChild(heroTitle);

                var exportHeroBtn = new Button
                {
                    Text = "Export Mod (VPK)",
                    CustomMinimumSize = new Vector2(135, 26),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    Modulate = new Color(0.45f, 0.92f, 0.55f)
                };
                exportHeroBtn.AddThemeFontSizeOverride("font_size", 10);
                string expHeroId = hero.HeroId;
                string expHeroDisplay = hero.DisplayName;
                exportHeroBtn.Pressed += () =>
                {
                    var paintTab = (GetTree()?.Root?.FindChild("PaintTab", true, false) 
                                 ?? GetTree()?.Root?.FindChild("PaintTabUI", true, false)) as global::PaintTabUI;
                    paintTab?.FlushActiveChangesToWorkspace(true);
                    CloseModal();
                    if (OnExportHeroModRequested != null)
                        OnExportHeroModRequested.Invoke(expHeroId, expHeroDisplay);
                    else
                        OnExportModRequested?.Invoke();
                };
                heroHeader.AddChild(exportHeroBtn);

                cardVBox.AddChild(heroHeader);
                cardVBox.AddChild(new HSeparator());

                // Group assets into Character Model, Extra Models, VFX
                var charModels = hero.Assets.Where(a => a.Category == "CharacterModel").ToList();
                var extraModels = hero.Assets.Where(a => a.Category == "ExtraModel").ToList();
                var vfxAssets = hero.Assets.Where(a => a.Category == "ParticleVFX").ToList();

                // 1. Character Model
                if (charModels.Count > 0)
                {
                    var catHead = new Label { Text = "• Character Model", Modulate = new Color(0.85f, 0.88f, 0.95f) };
                    catHead.AddThemeFontSizeOverride("font_size", 11);
                    cardVBox.AddChild(catHead);

                    foreach (var a in charModels)
                    {
                        bool isThisActive = string.Equals(hero.HeroId, curHeroId, StringComparison.OrdinalIgnoreCase) && !isExtraLoaded && !hasActiveParticle;
                        cardVBox.AddChild(CreateAssetRow(hero.HeroId, a, isThisActive, isModel: true));
                    }
                }

                // 2. Extra Models
                if (extraModels.Count > 0)
                {
                    var catHead = new Label { Text = "• Extra Models", Modulate = new Color(0.85f, 0.88f, 0.95f) };
                    catHead.AddThemeFontSizeOverride("font_size", 11);
                    cardVBox.AddChild(catHead);

                    foreach (var a in extraModels)
                    {
                        bool isThisActive = string.Equals(hero.HeroId, curHeroId, StringComparison.OrdinalIgnoreCase) && isExtraLoaded && !hasActiveParticle;
                        cardVBox.AddChild(CreateAssetRow(hero.HeroId, a, isThisActive, isModel: true));
                    }
                }

                // 3. Particle VFX
                if (vfxAssets.Count > 0)
                {
                    var catHead = new Label { Text = "• VFX (Particles)", Modulate = new Color(0.85f, 0.88f, 0.95f) };
                    catHead.AddThemeFontSizeOverride("font_size", 11);
                    cardVBox.AddChild(catHead);

                    foreach (var a in vfxAssets)
                    {
                        bool isThisActive = hasActiveParticle && string.Equals(a.VpkPath, curVpcf, StringComparison.OrdinalIgnoreCase);
                        cardVBox.AddChild(CreateAssetRow(hero.HeroId, a, isThisActive, isModel: false));
                    }
                }

                heroCard.AddChild(cardVBox);
                _contentContainer.AddChild(heroCard);
            }
        }

        private HBoxContainer CreateAssetRow(string heroId, WorkspaceAssetEntry asset, bool isActive, bool isModel)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 10);

            var indent = new Control { CustomMinimumSize = new Vector2(16, 0) };
            row.AddChild(indent);

            string title = !string.IsNullOrEmpty(asset.DisplayName) ? asset.DisplayName : Path.GetFileName(asset.VpkPath);
            int modCount = asset.ModifiedTextures.Count;
            string details = isModel ? $"{title} ({modCount} textures)" : $"{title} (VFX parameters)";

            var nameLbl = new Label
            {
                Text = details,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = asset.VpkPath
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(nameLbl);

            if (isActive)
            {
                var activeBadge = new Label
                {
                    Text = isModel ? "[Active Model]" : "[Active Particle]",
                    Modulate = new Color(0.4f, 0.85f, 0.5f),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                activeBadge.AddThemeFontSizeOverride("font_size", 10);
                row.AddChild(activeBadge);
            }
            else
            {
                var loadBtn = new Button
                {
                    Text = isModel ? "Load Model" : "Load Particle",
                    CustomMinimumSize = new Vector2(100, 24),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                loadBtn.AddThemeFontSizeOverride("font_size", 10);

                string localHeroId = heroId;
                string localPath = asset.VpkPath;
                bool localIsModel = isModel;
                loadBtn.Pressed += () =>
                {
                    var paintTab = (GetTree()?.Root?.FindChild("PaintTab", true, false) 
                                 ?? GetTree()?.Root?.FindChild("PaintTabUI", true, false)) as global::PaintTabUI;
                    paintTab?.FlushActiveChangesToWorkspace(true);
                    CloseModal();
                    if (localIsModel)
                    {
                        OnLoadModelRequested?.Invoke(localHeroId, localPath);
                    }
                    else
                    {
                        OnLoadParticleRequested?.Invoke(localHeroId, localPath);
                    }
                };
                row.AddChild(loadBtn);
            }

            var trashBtn = new Button
            {
                Text = "🗑",
                CustomMinimumSize = new Vector2(26, 24),
                TooltipText = "Discard modifications for this asset",
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Flat = true,
                Modulate = new Color(0.9f, 0.45f, 0.45f)
            };
            string delHero = heroId;
            string delPath = asset.VpkPath;
            trashBtn.Pressed += () =>
            {
                ModWorkspaceSession.Instance.RemoveAsset(delHero, delPath);
                RefreshList();
            };
            row.AddChild(trashBtn);

            return row;
        }

        private void OnSaveProjectClicked()
        {
            var paintTab = (GetTree()?.Root?.FindChild("PaintTab", true, false) 
                         ?? GetTree()?.Root?.FindChild("PaintTabUI", true, false)) as global::PaintTabUI;
            paintTab?.FlushActiveChangesToWorkspace(true);
            _fileDialogSave.Popup();
        }

        private void OnLoadProjectClicked()
        {
            _fileDialogLoad.Popup();
        }

        private void OnClearAllClicked()
        {
            ModWorkspaceSession.Instance.CleanupTempSession();
            RefreshList();
        }
    }
}
