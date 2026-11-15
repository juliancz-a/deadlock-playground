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

            ThemeTypeVariation = "ModalPanel";

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
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ThemeTypeVariation = "ModalTitleLabel"
            };
            headerBar.AddChild(titleLbl);

            _lblCountBadge = new Label
            {
                Text = "[0]",
                ThemeTypeVariation = "BadgeLabelInfo",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
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
                ThemeTypeVariation = "MutedLabel",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
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
                Text = "Save Project (.dpp)",
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
                var emptyPanel = new PanelContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ThemeTypeVariation = "CardPanel"
                };

                var emptyLbl = new Label
                {
                    Text = "No staged modifications in this workspace session.\nPaint character materials or customize VFX parameters to track changes across heroes.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ThemeTypeVariation = "MutedLabel"
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

                var heroCard = new PanelContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ThemeTypeVariation = isCurrentActiveHero ? "InspectorSectionPanel" : "CardPanel"
                };

                var cardVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardVBox.AddThemeConstantOverride("separation", 8);

                // Hero Header Row
                var heroHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                heroHeader.AddThemeConstantOverride("separation", 8);

                var heroTitle = new Label
                {
                    Text = isCurrentActiveHero 
                        ? $"HERO: {hero.DisplayName.ToUpperInvariant()} ({hero.HeroId}) — [ACTIVE]"
                        : $"HERO: {hero.DisplayName.ToUpperInvariant()} ({hero.HeroId})",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ThemeTypeVariation = isCurrentActiveHero ? "SectionHeaderLabel" : "SubheaderLabel"
                };
                heroHeader.AddChild(heroTitle);

                var exportHeroBtn = new Button
                {
                    Text = "Export Mod (VPK)",
                    CustomMinimumSize = new Vector2(155, 30),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    ThemeTypeVariation = "PrimaryActionButton"
                };
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

                // 0. Custom 3D Model Replacement
                if (hero.CustomModel != null)
                {
                    cardVBox.AddChild(new Label { Text = "• Custom 3D Model Swap", ThemeTypeVariation = "HeaderLabel" });

                    var cmBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    cmBox.AddThemeConstantOverride("separation", 10);
                    cmBox.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });

                    var cmBadge = new Label
                    {
                        Text = "[3D MODEL]",
                        ThemeTypeVariation = "BadgeLabelInfo",
                        SizeFlagsVertical = SizeFlags.ShrinkCenter
                    };
                    cmBox.AddChild(cmBadge);

                    int pinCount = hero.CustomModel.LandmarkPins?.Count ?? 0;
                    int weightOverrideCount = hero.CustomModel.CustomSkinWeightOverrides?.Count ?? 0;
                    string cmDetails = $"{hero.CustomModel.OriginalFileName} ({pinCount} pins, {weightOverrideCount} painted weight surf.)";
                    var cmLbl = new Label
                    {
                        Text = cmDetails,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        ClipText = true,
                        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                        TooltipText = hero.CustomModel.RelativeModelPath,
                        ThemeTypeVariation = "BodyLabel"
                    };
                    cmBox.AddChild(cmLbl);

                    bool isCustomActive = isCurrentActiveHero &&
                                          vpkLoader?.CurrentHeroNode?.FindChild("Custom_Rigged_Character", true, false) != null;
                    if (isCustomActive)
                    {
                        cmBox.AddChild(new Label
                        {
                            Text = "[Active Replacement]",
                            ThemeTypeVariation = "BadgeLabelSuccess",
                            SizeFlagsVertical = SizeFlags.ShrinkCenter
                        });
                    }
                    else
                    {
                        var applyCmBtn = new Button
                        {
                            Text = "Apply Model",
                            CustomMinimumSize = new Vector2(120, 28),
                            SizeFlagsVertical = SizeFlags.ShrinkCenter,
                            ThemeTypeVariation = "ToolbarButton"
                        };
                        string localCmHero = hero.HeroId;
                        applyCmBtn.Pressed += async () =>
                        {
                            CloseModal();
                            var charTab = GetTree()?.Root?.FindChild("CharacterTab", true, false) as CharacterTabUI;
                            if (vpkLoader != null)
                            {
                                string cur = vpkLoader.LastLoadedHeroEntry?.InternalCodename ?? "";
                                if (!string.Equals(cur, localCmHero, StringComparison.OrdinalIgnoreCase))
                                {
                                    var catEntry = DeadlockPlayground.Catalog.DeadlockHeroCatalog.GetByCodename(localCmHero)
                                                ?? DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero(localCmHero);
                                    if (catEntry != null) await vpkLoader.LoadHeroModelAsync(catEntry);
                                    else await vpkLoader.LoadHeroAsync(localCmHero, localCmHero);
                                }
                                if (vpkLoader.CurrentHeroNode != null)
                                    charTab?.RestoreCustomModelFromWorkspace(vpkLoader.CurrentHeroNode, localCmHero);
                            }
                        };
                        cmBox.AddChild(applyCmBtn);
                    }

                    var cmTrashBtn = new Button
                    {
                        Text = "🗑",
                        CustomMinimumSize = new Vector2(26, 24),
                        TooltipText = "Discard custom 3D model replacement",
                        SizeFlagsVertical = SizeFlags.ShrinkCenter,
                        Flat = true,
                        Modulate = new Color(0.9f, 0.45f, 0.45f)
                    };
                    string delCmHero = hero.HeroId;
                    cmTrashBtn.Pressed += () =>
                    {
                        ModWorkspaceSession.Instance.DiscardCustomModel(delCmHero);
                        if (isCurrentActiveHero)
                        {
                            var charTab = GetTree()?.Root?.FindChild("CharacterTab", true, false) as CharacterTabUI;
                            charTab?.ResetCustomModel();
                        }
                        RefreshList();
                    };
                    cmBox.AddChild(cmTrashBtn);

                    cardVBox.AddChild(cmBox);
                }

                // Group assets into Character Model, Extra Models, VFX
                var charModels = hero.Assets.Where(a => a.Category == "CharacterModel").ToList();
                var extraModels = hero.Assets.Where(a => a.Category == "ExtraModel").ToList();
                var vfxAssets = hero.Assets.Where(a => a.Category == "ParticleVFX").ToList();

                // 1. Character Model
                if (charModels.Count > 0)
                {
                    cardVBox.AddChild(new Label { Text = "• Character Model", ThemeTypeVariation = "HeaderLabel" });

                    foreach (var a in charModels)
                    {
                        bool isThisActive = string.Equals(hero.HeroId, curHeroId, StringComparison.OrdinalIgnoreCase) && !isExtraLoaded && !hasActiveParticle;
                        cardVBox.AddChild(CreateAssetRow(hero.HeroId, a, isThisActive, isModel: true));
                    }
                }

                // 2. Extra Models
                if (extraModels.Count > 0)
                {
                    cardVBox.AddChild(new Label { Text = "• Extra Models", ThemeTypeVariation = "HeaderLabel" });

                    foreach (var a in extraModels)
                    {
                        bool isThisActive = string.Equals(hero.HeroId, curHeroId, StringComparison.OrdinalIgnoreCase) && isExtraLoaded && !hasActiveParticle;
                        cardVBox.AddChild(CreateAssetRow(hero.HeroId, a, isThisActive, isModel: true));
                    }
                }

                // 3. Particle VFX
                if (vfxAssets.Count > 0)
                {
                    cardVBox.AddChild(new Label { Text = "• VFX (Particles)", ThemeTypeVariation = "HeaderLabel" });

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

        private Control CreateAssetRow(string heroId, WorkspaceAssetEntry asset, bool isActive, bool isModel)
        {
            var assetBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            assetBox.AddThemeConstantOverride("separation", 4);

            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 10);

            var indent = new Control { CustomMinimumSize = new Vector2(16, 0) };
            row.AddChild(indent);

            string title = !string.IsNullOrEmpty(asset.DisplayName) ? asset.DisplayName : Path.GetFileName(asset.VpkPath);
            int modCount = asset.ModifiedTextures.Count;
            int animCount = asset.Animations?.Count ?? 0;
            string details = isModel 
                ? (animCount > 0 ? $"{title} ({modCount} textures, {animCount} animations)" : $"{title} ({modCount} textures)")
                : $"{title} (VFX parameters)";

            var nameLbl = new Label
            {
                Text = details,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = asset.VpkPath,
                ThemeTypeVariation = "BodyLabel"
            };
            row.AddChild(nameLbl);

            if (isActive)
            {
                var activeBadge = new Label
                {
                    Text = isModel ? "[Active Model]" : "[Active Particle]",
                    ThemeTypeVariation = "BadgeLabelSuccess",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                row.AddChild(activeBadge);
            }
            else
            {
                var loadBtn = new Button
                {
                    Text = isModel ? "Load Model" : "Load Particle",
                    CustomMinimumSize = new Vector2(120, 28),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    ThemeTypeVariation = "ToolbarButton"
                };

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

            assetBox.AddChild(row);

            // Sub-rows for animations
            if (asset.Animations != null && asset.Animations.Count > 0)
            {
                foreach (var anim in asset.Animations)
                {
                    var animRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    animRow.AddThemeConstantOverride("separation", 8);

                    var animIndent = new Control { CustomMinimumSize = new Vector2(36, 0) };
                    animRow.AddChild(animIndent);

                    var animBadge = new Label
                    {
                        Text = "[ANIM]",
                        ThemeTypeVariation = "BadgeLabelInfo",
                        SizeFlagsVertical = SizeFlags.ShrinkCenter
                    };
                    animRow.AddChild(animBadge);

                    var animNameLbl = new Label
                    {
                        Text = $"{anim.CleanName} ({anim.Fps} FPS, {anim.Duration:F2}s{(anim.IsAdditive ? ", Additive" : "")})",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        ClipText = true,
                        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                        ThemeTypeVariation = "MutedLabel"
                    };
                    animRow.AddChild(animNameLbl);

                    var animDiscardBtn = new Button
                    {
                        Text = "✕",
                        CustomMinimumSize = new Vector2(24, 22),
                        SizeFlagsVertical = SizeFlags.ShrinkCenter,
                        TooltipText = "Discard modified animation",
                        Flat = true,
                        Modulate = new Color(0.95f, 0.45f, 0.45f)
                    };
                    string aHeroId = heroId;
                    string aCleanName = anim.CleanName;
                    animDiscardBtn.Pressed += () =>
                    {
                        ModWorkspaceSession.Instance.DiscardAnimation(aHeroId, aCleanName);
                        RefreshList();
                    };
                    animRow.AddChild(animDiscardBtn);

                    assetBox.AddChild(animRow);
                }
            }

            return assetBox;
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
