using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Materials;
using ValvePak;

namespace DeadlockPlayground.UI;

/// <summary>
/// Responsive visual gallery card displaying a Deadlock hero model with high-resolution card artwork,
/// category tags, and responsive hover/selected borders matching PlaygroundTheme.tres.
/// </summary>
public partial class HeroCardUI : PanelContainer
{
    [Signal] public delegate void CardSelectedEventHandler(HeroCardUI card);
    [Signal] public delegate void CardDoubleClickedEventHandler(HeroCardUI card);

    public DeadlockHeroEntry Hero { get; private set; }

    private TextureRect _thumbRect;
    private Label _lblName;
    private Label _lblCodename;
    private PanelContainer _thumbPanel;
    private PanelContainer _tagPanel;
    private Label _lblTag;

    private bool _isSelected = false;
    private bool _isHovered = false;

    private StyleBoxFlat _styleNormal;
    private StyleBoxFlat _styleHover;
    private StyleBoxFlat _styleSelected;

    private static readonly Dictionary<string, Texture2D> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> HeroArtworkAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "viper", new[] { "kali", "viper", "vyper" } },
        { "vyper", new[] { "kali", "viper", "vyper" } },
        { "kali", new[] { "kali", "viper", "vyper" } },

        { "mcginnis", new[] { "engineer", "mcginnis", "forge" } },
        { "engineer", new[] { "engineer", "mcginnis", "forge" } },

        { "abrams", new[] { "bull", "abrams", "atlas_detective" } },
        { "bull", new[] { "bull", "abrams", "atlas_detective" } },

        { "seven", new[] { "gigawatt", "gigawatt_prisoner", "seven" } },
        { "gigawatt", new[] { "gigawatt", "gigawatt_prisoner", "seven" } },
        { "gigawatt_prisoner", new[] { "gigawatt", "gigawatt_prisoner", "seven" } },

        { "geist", new[] { "spectre", "geist", "lady_geist", "ghost" } },
        { "lady_geist", new[] { "spectre", "geist", "lady_geist", "ghost" } },
        { "spectre", new[] { "spectre", "geist", "lady_geist", "ghost" } },

        { "dynamo", new[] { "sumo", "dynamo", "prof_dynamo" } },
        { "sumo", new[] { "sumo", "dynamo", "prof_dynamo" } },

        { "werewolf_transform", new[] { "werewolf_wolf", "werewolf_transform", "werewolf", "silver" } },
        { "werewolf_wolf", new[] { "werewolf_wolf", "werewolf_transform", "werewolf", "silver" } },
        { "werewolf", new[] { "werewolf", "werewolf_wolf", "silver" } },
        { "silver", new[] { "werewolf", "werewolf_wolf", "silver" } },

        { "ivy", new[] { "tengu", "ivy" } },
        { "tengu", new[] { "tengu", "ivy" } },

        { "archer", new[] { "archer", "grey_talon", "archer_v2" } },
        { "grey_talon", new[] { "archer", "grey_talon", "archer_v2" } },

        { "inferno", new[] { "inferno", "infernus" } },
        { "infernus", new[] { "inferno", "infernus" } },

        { "astro", new[] { "astro", "holliday" } },
        { "holliday", new[] { "astro", "holliday" } },

        { "digger", new[] { "digger", "mo_and_krill", "krill" } },
        { "mo_and_krill", new[] { "digger", "mo_and_krill", "krill" } },

        { "hornet", new[] { "hornet", "vindicta" } },
        { "vindicta", new[] { "hornet", "vindicta" } },

        { "chrono", new[] { "chrono", "paradox" } },
        { "paradox", new[] { "chrono", "paradox" } },

        { "nano", new[] { "nano", "calico" } },
        { "calico", new[] { "nano", "calico" } },

        { "bookworm", new[] { "bookworm", "paige" } },
        { "paige", new[] { "bookworm", "paige" } },

        { "artist", new[] { "artist", "violet" } },
        { "violet", new[] { "artist", "violet" } },

        { "chessmaster", new[] { "chessmaster", "solomon" } },
        { "solomon", new[] { "chessmaster", "solomon" } },

        { "deadpack", new[] { "deadpack", "deadman_danny" } },
        { "deadman_danny", new[] { "deadpack", "deadman_danny" } },

        { "nurse", new[] { "nurse", "nurse_harrow" } },
        { "nurse_harrow", new[] { "nurse", "nurse_harrow" } },

        { "necro", new[] { "necro", "graves" } },
        { "graves", new[] { "necro", "graves" } },

        { "fencer", new[] { "fencer", "apollo" } },
        { "apollo", new[] { "fencer", "apollo" } },

        { "punkgoat", new[] { "punkgoat", "billy" } },
        { "billy", new[] { "punkgoat", "billy" } },

        { "unicorn", new[] { "unicorn", "celeste" } },
        { "celeste", new[] { "unicorn", "celeste" } },

        { "vampirebat", new[] { "vampirebat", "mina" } },
        { "mina", new[] { "vampirebat", "mina" } },

        { "frank", new[] { "frank", "victor" } },
        { "victor", new[] { "frank", "victor" } },

        { "priest", new[] { "priest", "venator" } },
        { "venator", new[] { "priest", "venator" } },

        { "magician", new[] { "magician", "sinclair" } },
        { "sinclair", new[] { "magician", "sinclair" } },

        { "synth", new[] { "synth", "pocket" } },
        { "pocket", new[] { "pocket", "synth" } }
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            UpdateCardStyle();
        }
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(170, 240);
        MouseFilter = MouseFilterEnum.Stop;

        InitStyles();
        BuildUI();
        UpdateCardStyle();
    }

    public void Setup(DeadlockHeroEntry hero)
    {
        Hero = hero;
        if (_lblName != null && hero != null)
        {
            _lblName.Text = hero.DisplayName;
            _lblCodename.Text = hero.InternalCodename;

            string tagText = hero.Category switch
            {
                HeroCategory.NewUpdate => "Update Hero",
                HeroCategory.UpdatedAndNew => "Standard",
                HeroCategory.LegacyStaging => "Legacy",
                _ => "Hero"
            };
            Color tagColor = hero.Category switch
            {
                HeroCategory.NewUpdate => new Color(0.95f, 0.85f, 0.30f, 1.0f),
                HeroCategory.UpdatedAndNew => new Color(0.40f, 0.75f, 1.0f, 1.0f),
                HeroCategory.LegacyStaging => new Color(0.70f, 0.50f, 0.85f, 1.0f),
                _ => Colors.White
            };

            _lblTag.Text = tagText;
            _lblTag.Modulate = tagColor;
        }
    }

    private void InitStyles()
    {
        _styleNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.16f, 0.95f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.22f, 0.26f, 0.34f, 0.8f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };

        _styleHover = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.17f, 0.22f, 0.98f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.95f, 0.85f, 0.40f, 0.9f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };

        _styleSelected = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.22f, 0.30f, 1.0f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderColor = new Color(0.95f, 0.85f, 0.30f, 1.0f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
    }

    private void BuildUI()
    {
        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        // 1. Thumbnail Container
        _thumbPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(158, 170),
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        var thumbStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 1.0f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        _thumbPanel.AddThemeStyleboxOverride("panel", thumbStyle);
        vbox.AddChild(_thumbPanel);

        _thumbRect = new TextureRect
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered
        };
        _thumbPanel.AddChild(_thumbRect);

        // Tag badge overlay inside thumbnail
        _tagPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        _tagPanel.SetAnchorsPreset(LayoutPreset.TopLeft);
        var tagStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.85f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        _tagPanel.AddThemeStyleboxOverride("panel", tagStyle);

        _lblTag = new Label
        {
            Text = "Hero"
        };
        _lblTag.AddThemeFontSizeOverride("font_size", 10);
        _tagPanel.AddChild(_lblTag);
        _thumbPanel.AddChild(_tagPanel);

        // 2. Hero Name Label
        _lblName = new Label
        {
            Text = Hero?.DisplayName ?? "Hero Name",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        var colusFont = PlaygroundThemeHelper.GetFontColus();
        if (colusFont != null)
        {
            _lblName.AddThemeFontOverride("font", colusFont);
        }
        _lblName.AddThemeFontSizeOverride("font_size", 14);
        _lblName.AddThemeColorOverride("font_color", Colors.White);
        vbox.AddChild(_lblName);

        // 3. Hero Codename / Subtitle
        _lblCodename = new Label
        {
            Text = Hero?.InternalCodename ?? "",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _lblCodename.AddThemeFontSizeOverride("font_size", 12);
        _lblCodename.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f, 1.0f));
        vbox.AddChild(_lblCodename);

        if (Hero != null)
        {
            Setup(Hero);
        }
    }

    private void UpdateCardStyle()
    {
        if (_isSelected)
        {
            AddThemeStyleboxOverride("panel", _styleSelected);
        }
        else if (_isHovered)
        {
            AddThemeStyleboxOverride("panel", _styleHover);
        }
        else
        {
            AddThemeStyleboxOverride("panel", _styleNormal);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter)
        {
            _isHovered = true;
            UpdateCardStyle();
        }
        else if (what == NotificationMouseExit)
        {
            _isHovered = false;
            UpdateCardStyle();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            if (mb.DoubleClick)
            {
                EmitSignal(SignalName.CardDoubleClicked, this);
            }
            else
            {
                EmitSignal(SignalName.CardSelected, this);
            }
            AcceptEvent();
        }
    }

    /// <summary>
    /// Loads the hero thumbnail asynchronously from the VPK archive following the 4-tier fallback chain:
    /// 1. panorama/images/heroes/{hero_codename}_card_psd.vtex_c
    /// 2. panorama/images/heroes/{hero_codename}_vertical_psd.vtex_c
    /// 3. panorama/images/heroes/{hero_codename}_psd.vtex_c or selection_{hero_codename}_psd.vtex_c
    /// 4. Quaternary fallback: themed placeholder card with styled initials.
    /// </summary>
    public async Task LoadThumbnailAsync(Package package)
    {
        if (Hero == null) return;
        string cacheKey = Hero.InternalCodename;

        if (_thumbnailCache.TryGetValue(cacheKey, out var cachedTex) && cachedTex != null)
        {
            _thumbRect.Texture = cachedTex;
            return;
        }

        if (package == null)
        {
            CreateFallbackPlaceholder();
            return;
        }

        Texture2D loadedTexture = await Task.Run(() =>
        {
            try
            {
                List<string> codenamesToCheck = new() { Hero.InternalCodename };
                if (!string.IsNullOrEmpty(Hero.WipAlias) && !codenamesToCheck.Any(c => string.Equals(c, Hero.WipAlias, StringComparison.OrdinalIgnoreCase)))
                {
                    codenamesToCheck.Add(Hero.WipAlias);
                }

                // Check directory name of VMDL as an additional fallback codename (e.g. artist, chessmaster)
                if (!string.IsNullOrEmpty(Hero.VmdlRelativePath))
                {
                    string dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Hero.VmdlRelativePath) ?? "");
                    if (!string.IsNullOrEmpty(dir) && !codenamesToCheck.Any(c => string.Equals(c, dir, StringComparison.OrdinalIgnoreCase)))
                    {
                        codenamesToCheck.Add(dir);
                    }
                }

                // Add DisplayName normalized (e.g. lady_geist, grey_talon, mo_and_krill)
                if (!string.IsNullOrEmpty(Hero.DisplayName))
                {
                    string dClean = Hero.DisplayName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_").Replace("&", "and");
                    if (!codenamesToCheck.Any(c => string.Equals(c, dClean, StringComparison.OrdinalIgnoreCase)))
                    {
                        codenamesToCheck.Add(dClean);
                    }
                }

                // Resolve all known aliases from HeroArtworkAliases dictionary
                var aliasesToAdd = new List<string>();
                foreach (var code in codenamesToCheck)
                {
                    if (HeroArtworkAliases.TryGetValue(code, out var mappedAliases))
                    {
                        foreach (var alias in mappedAliases)
                        {
                            if (!codenamesToCheck.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                                !aliasesToAdd.Contains(alias, StringComparer.OrdinalIgnoreCase))
                            {
                                aliasesToAdd.Add(alias);
                            }
                        }
                    }
                }
                codenamesToCheck.AddRange(aliasesToAdd);

                foreach (var codename in codenamesToCheck)
                {
                    string clean = codename.ToLowerInvariant().Trim();

                    // Candidate 1: Primary card
                    string c1 = $"panorama/images/heroes/{clean}_card_psd.vtex_c";
                    if (package.FindEntry(c1) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c1, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }

                    // Candidate 2: Vertical card (used by Violet/Artist etc.)
                    string c2 = $"panorama/images/heroes/{clean}_vertical_psd.vtex_c";
                    if (package.FindEntry(c2) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c2, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }

                    // Candidate 3: Standard portrait
                    string c3 = $"panorama/images/heroes/{clean}_psd.vtex_c";
                    if (package.FindEntry(c3) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c3, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }

                    // Candidate 4: Selection icon
                    string c4 = $"panorama/images/heroes/selection_{clean}_psd.vtex_c";
                    if (package.FindEntry(c4) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c4, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }

                    // Candidate 5: Generic hero icon
                    string c5 = $"panorama/images/heroes/{clean}_icon_psd.vtex_c";
                    if (package.FindEntry(c5) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c5, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }

                    // Candidate 6: HUD portrait
                    string c6 = $"panorama/images/hud/hero_portraits/{clean}_psd.vtex_c";
                    if (package.FindEntry(c6) != null)
                    {
                        var tex = Source2TextureLoader.ExtractVtexToGodot(package, c6, maxDimension: 256, forceOpaque: false);
                        if (tex != null) return tex;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroCardUI] Error extracting thumbnail for {Hero.DisplayName}: {ex.Message}");
            }
            return null;
        });

        if (loadedTexture != null)
        {
            _thumbnailCache[cacheKey] = loadedTexture;
            _thumbRect.Texture = loadedTexture;
        }
        else
        {
            CreateFallbackPlaceholder();
        }
    }

    private void CreateFallbackPlaceholder()
    {
        // Quaternary Fallback: Styled placeholder icon with hero initials
        var placeholder = Image.CreateEmpty(160, 200, false, Image.Format.Rgba8);
        Color baseCol = Hero?.Category switch
        {
            HeroCategory.NewUpdate => new Color(0.20f, 0.18f, 0.12f, 1.0f),
            HeroCategory.UpdatedAndNew => new Color(0.12f, 0.16f, 0.22f, 1.0f),
            _ => new Color(0.16f, 0.14f, 0.20f, 1.0f)
        };
        placeholder.Fill(baseCol);

        var fallbackTex = ImageTexture.CreateFromImage(placeholder);
        _thumbRect.Texture = fallbackTex;
    }
}
