using Godot;
using System;

/// <summary>
/// Minimalist translucent 3x3 D-Pad HUD widget for precision camera control.
/// Matches the Deadlock UI palette: semi-transparent #222021 backdrop, #efdebf icons, #72947f active accents.
/// </summary>
public partial class CameraControlPadUI : PanelContainer
{
    public enum ControlMode
    {
        Pan,
        Orbit
    }

    [Export] public OrbitCamera CameraTarget { get; set; }
    [Export] public VpkLoaderTest VpkLoader { get; set; }

    [ExportGroup("Precision Tuning")]
    [Export] public float PanStep = 0.08f;
    [Export] public float RotateStepDegrees = 5.0f;

    private ControlMode _currentMode = ControlMode.Pan;

    // UI Nodes
    private Button _btnUpLeft;
    private Button _btnUp;
    private Button _btnUpRight;
    private Button _btnLeft;
    private Button _btnCenter;
    private Button _btnRight;
    private Button _btnDownLeft;
    private Button _btnDown;
    private Button _btnDownRight;
    private Button _btnModeToggle;

    public override void _Ready()
    {
        FindReferences();
        BuildUI();
        ApplyCustomStyling();
    }

    private void FindReferences()
    {
        CameraTarget ??= GetNodeOrNull<OrbitCamera>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot")
                      ?? GetNodeOrNull<OrbitCamera>("/root/Main/CameraPivot")
                      ?? GetTree().Root.FindChild("CameraPivot", true, false) as OrbitCamera;

        VpkLoader ??= GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                   ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                   ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
    }

    private void BuildUI()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 3);
        AddChild(vbox);

        // 3x3 Directional Grid
        var grid = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        grid.AddThemeConstantOverride("h_separation", 2);
        grid.AddThemeConstantOverride("v_separation", 2);

        _btnUpLeft = CreatePadButton("↖", "Nudge Up-Left");
        _btnUp = CreatePadButton("↑", "Nudge Up");
        _btnUpRight = CreatePadButton("↗", "Nudge Up-Right");

        _btnLeft = CreatePadButton("←", "Nudge Left");
        _btnCenter = CreatePadButton("⌖", "Recenter Pivot on Model");
        _btnRight = CreatePadButton("→", "Nudge Right");

        _btnDownLeft = CreatePadButton("↙", "Nudge Down-Left");
        _btnDown = CreatePadButton("↓", "Nudge Down");
        _btnDownRight = CreatePadButton("↘", "Nudge Down-Right");

        grid.AddChild(_btnUpLeft);
        grid.AddChild(_btnUp);
        grid.AddChild(_btnUpRight);

        grid.AddChild(_btnLeft);
        grid.AddChild(_btnCenter);
        grid.AddChild(_btnRight);

        grid.AddChild(_btnDownLeft);
        grid.AddChild(_btnDown);
        grid.AddChild(_btnDownRight);

        vbox.AddChild(grid);

        // Compact Mode Toggle Button below grid
        _btnModeToggle = new Button
        {
            Text = "Pan",
            CustomMinimumSize = new Vector2(76, 18),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            TooltipText = "Click to toggle between Pan and Orbit mode"
        };
        _btnModeToggle.AddThemeFontSizeOverride("font_size", 9);
        _btnModeToggle.Pressed += ToggleMode;
        vbox.AddChild(_btnModeToggle);

        // Wire Click Handlers
        _btnUpLeft.Pressed += () => OnDirectionClicked(-1, 1);
        _btnUp.Pressed += () => OnDirectionClicked(0, 1);
        _btnUpRight.Pressed += () => OnDirectionClicked(1, 1);

        _btnLeft.Pressed += () => OnDirectionClicked(-1, 0);
        _btnRight.Pressed += () => OnDirectionClicked(1, 0);

        _btnDownLeft.Pressed += () => OnDirectionClicked(-1, -1);
        _btnDown.Pressed += () => OnDirectionClicked(0, -1);
        _btnDownRight.Pressed += () => OnDirectionClicked(1, -1);

        _btnCenter.Pressed += RecenterOnModel;

        UpdateModeButtonAppearance();
    }

    private Button CreatePadButton(string icon, string tooltip)
    {
        var btn = new Button
        {
            Text = icon,
            CustomMinimumSize = new Vector2(24, 24),
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None
        };
        return btn;
    }

    private void ApplyCustomStyling()
    {
        // Completely transparent backdrop
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

        // Style the buttons
        StylePadButton(_btnUpLeft);
        StylePadButton(_btnUp);
        StylePadButton(_btnUpRight);
        StylePadButton(_btnLeft);
        StylePadButton(_btnRight);
        StylePadButton(_btnDownLeft);
        StylePadButton(_btnDown);
        StylePadButton(_btnDownRight);

        // Center button accent styling
        StyleCenterButton(_btnCenter);
    }

    private void StylePadButton(Button btn)
    {
        if (btn == null) return;

        btn.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_pressed_color", PlaygroundThemeHelper.SageGreen);
        btn.AddThemeFontSizeOverride("font_size", 10);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.1843f, 0.2667f, 0.2588f, 0.65f), // Deep Teal translucent
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.2471f, 0.3647f, 0.3020f, 0.5f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = PlaygroundThemeHelper.PineGreen;
        hover.BorderColor = PlaygroundThemeHelper.SageGreen;

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = PlaygroundThemeHelper.SageGreen;
        pressed.BorderColor = PlaygroundThemeHelper.Parchment;

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", pressed);
    }

    private void StyleCenterButton(Button btn)
    {
        if (btn == null) return;

        btn.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_pressed_color", Colors.White);
        btn.AddThemeFontSizeOverride("font_size", 12);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.2471f, 0.3647f, 0.3020f, 0.85f), // Pine Green
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = PlaygroundThemeHelper.SageGreen,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = PlaygroundThemeHelper.SageGreen;
        hover.BorderColor = PlaygroundThemeHelper.Parchment;

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = PlaygroundThemeHelper.Parchment;
        pressed.BorderColor = Colors.White;

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", pressed);
    }

    public void ToggleMode()
    {
        _currentMode = (_currentMode == ControlMode.Pan) ? ControlMode.Orbit : ControlMode.Pan;
        UpdateModeButtonAppearance();
    }

    private void UpdateModeButtonAppearance()
    {
        if (_btnModeToggle == null) return;

        bool isPan = _currentMode == ControlMode.Pan;
        _btnModeToggle.Text = isPan ? "Pan" : "Orbit";

        var style = new StyleBoxFlat
        {
            BgColor = isPan ? new Color(0.1843f, 0.2667f, 0.2588f, 0.9f) : new Color(0.2471f, 0.3647f, 0.3020f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = isPan ? PlaygroundThemeHelper.PineGreen : PlaygroundThemeHelper.SageGreen,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };

        var hover = (StyleBoxFlat)style.Duplicate();
        hover.BorderColor = PlaygroundThemeHelper.Parchment;

        _btnModeToggle.AddThemeStyleboxOverride("normal", style);
        _btnModeToggle.AddThemeStyleboxOverride("hover", hover);
        _btnModeToggle.AddThemeColorOverride("font_color", isPan ? PlaygroundThemeHelper.Parchment : PlaygroundThemeHelper.SageGreen);
        _btnModeToggle.AddThemeFontSizeOverride("font_size", 11);
    }

    private void OnDirectionClicked(int dirX, int dirY)
    {
        if (CameraTarget == null)
        {
            FindReferences();
            if (CameraTarget == null) return;
        }

        if (_currentMode == ControlMode.Pan)
        {
            // Pan along camera X and Y axes
            CameraTarget.NudgePan(dirX * PanStep, dirY * PanStep);
        }
        else
        {
            // Orbit: dirX controls Yaw (left/right), dirY controls Pitch (up/down)
            CameraTarget.NudgeRotation(dirY * RotateStepDegrees, -dirX * RotateStepDegrees);
        }
    }

    public void RecenterOnModel()
    {
        if (CameraTarget == null)
        {
            FindReferences();
            if (CameraTarget == null) return;
        }

        Vector3 targetCenter = CalculateModelCenter();
        CameraTarget.RecenterOnTarget(targetCenter);
    }

    private Vector3 CalculateModelCenter()
    {
        if (VpkLoader == null) FindReferences();

        Node3D heroNode = VpkLoader?.CurrentHeroNode;
        if (heroNode != null && GodotObject.IsInstanceValid(heroNode))
        {
            // Check for chest or spine bone first for ideal framing
            var skeleton = SearchSkeleton(heroNode);
            if (skeleton != null)
            {
                string[] chestBones = { "chest", "spine_3", "spine_2", "spine_1", "pelvis" };
                foreach (var bName in chestBones)
                {
                    int idx = skeleton.FindBone(bName);
                    if (idx != -1)
                    {
                        return (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(idx)).Origin;
                    }
                }
            }

            // Fallback to bounding box of all mesh instances
            Aabb combinedAabb = new Aabb();
            bool hasAabb = false;
            CollectAabbs(heroNode, ref combinedAabb, ref hasAabb);
            if (hasAabb && combinedAabb.Size.LengthSquared() > 0.01f)
            {
                return combinedAabb.GetCenter();
            }

            return heroNode.GlobalPosition + new Vector3(0, 1.0f, 0);
        }

        return new Vector3(0, 1.0f, 0);
    }

    private void CollectAabbs(Node node, ref Aabb combined, ref bool hasAabb)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            Aabb worldAabb = mi.GlobalTransform * mi.GetAabb();
            if (!hasAabb)
            {
                combined = worldAabb;
                hasAabb = true;
            }
            else
            {
                combined = combined.Merge(worldAabb);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            CollectAabbs(child, ref combined, ref hasAabb);
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
}
