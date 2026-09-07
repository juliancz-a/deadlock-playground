using Godot;
using System;
using System.IO;

public partial class ExportPanelUI : PanelContainer
{
    [Signal] public delegate void ExportRequestedEventHandler(Vector2I resolution, bool includeBg);
    [Signal] public delegate void ExportCompletedEventHandler(string filePath);

    [ExportCategory("Format / Aspect Ratio Buttons")]
    [Export] private Button _btnFormat1x1;
    [Export] private Button _btnFormat4x3;
    [Export] private Button _btnFormat16x9;
    [Export] private Button _btnFormat9x16;

    [ExportCategory("Resolution Buttons")]
    [Export] private Button _btnRes1080p;
    [Export] private Button _btnRes1440p;
    [Export] private Button _btnRes4K;

    [ExportCategory("Options & Export")]
    [Export] private CheckBox _checkIncludeBg;
    [Export] private Button _btnExport;
    [Export] private Label _lblDimensionPreview;

    [ExportCategory("Viewport Framing Overlay")]
    [Export] private Control _framingOverlay;
    [Export] private ColorRect _overlayBarTop;
    [Export] private ColorRect _overlayBarBottom;
    [Export] private ColorRect _overlayBarLeft;
    [Export] private ColorRect _overlayBarRight;

    public enum AspectRatioMode
    {
        Ratio1x1,
        Ratio4x3,
        Ratio16x9,
        Ratio9x16
    }

    public enum ResolutionTier
    {
        Res1080p,
        Res1440p,
        Res4K
    }

    private AspectRatioMode _currentRatio = AspectRatioMode.Ratio16x9;
    private ResolutionTier _currentRes = ResolutionTier.Res1080p;

    private ScreenshotHelper _screenshotHelper;

    public override void _Ready()
    {
        _framingOverlay ??= GetNodeOrNull<Control>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/FramingOverlay")
                         ?? GetNodeOrNull<Control>("/root/Main/UIRoot/MainHUD/FramingOverlay") 
                         ?? GetNodeOrNull<Control>("../FramingOverlay")
                         ?? GetTree().Root.FindChild("FramingOverlay", true, false) as Control;

        if (_framingOverlay != null)
        {
            _overlayBarTop ??= _framingOverlay.GetNodeOrNull<ColorRect>("BarTop");
            _overlayBarBottom ??= _framingOverlay.GetNodeOrNull<ColorRect>("BarBottom");
            _overlayBarLeft ??= _framingOverlay.GetNodeOrNull<ColorRect>("BarLeft");
            _overlayBarRight ??= _framingOverlay.GetNodeOrNull<ColorRect>("BarRight");
            _framingOverlay.Resized += UpdateFramingOverlay;
        }

        _screenshotHelper = new ScreenshotHelper();
        AddChild(_screenshotHelper);
        _screenshotHelper.ScreenshotSaved += OnScreenshotSaved;

        ConnectEvents();
        UpdateSelectionUI();
        UpdateFramingOverlay();
    }

    private void ConnectEvents()
    {
        if (_btnFormat1x1 != null) _btnFormat1x1.Pressed += () => SelectFormat(AspectRatioMode.Ratio1x1);
        if (_btnFormat4x3 != null) _btnFormat4x3.Pressed += () => SelectFormat(AspectRatioMode.Ratio4x3);
        if (_btnFormat16x9 != null) _btnFormat16x9.Pressed += () => SelectFormat(AspectRatioMode.Ratio16x9);
        if (_btnFormat9x16 != null) _btnFormat9x16.Pressed += () => SelectFormat(AspectRatioMode.Ratio9x16);

        if (_btnRes1080p != null) _btnRes1080p.Pressed += () => SelectResolution(ResolutionTier.Res1080p);
        if (_btnRes1440p != null) _btnRes1440p.Pressed += () => SelectResolution(ResolutionTier.Res1440p);
        if (_btnRes4K != null) _btnRes4K.Pressed += () => SelectResolution(ResolutionTier.Res4K);

        if (_btnExport != null)
        {
            _btnExport.Pressed += TriggerExport;
        }

        GetTree().Root.SizeChanged += UpdateFramingOverlay;
    }

    public void SelectFormat(AspectRatioMode mode)
    {
        _currentRatio = mode;
        UpdateSelectionUI();
        UpdateFramingOverlay();
    }

    public void SelectResolution(ResolutionTier tier)
    {
        _currentRes = tier;
        UpdateSelectionUI();
    }

    private void UpdateSelectionUI()
    {
        HighlightButton(_btnFormat1x1, _currentRatio == AspectRatioMode.Ratio1x1);
        HighlightButton(_btnFormat4x3, _currentRatio == AspectRatioMode.Ratio4x3);
        HighlightButton(_btnFormat16x9, _currentRatio == AspectRatioMode.Ratio16x9);
        HighlightButton(_btnFormat9x16, _currentRatio == AspectRatioMode.Ratio9x16);

        HighlightButton(_btnRes1080p, _currentRes == ResolutionTier.Res1080p);
        HighlightButton(_btnRes1440p, _currentRes == ResolutionTier.Res1440p);
        HighlightButton(_btnRes4K, _currentRes == ResolutionTier.Res4K);

        Vector2I res = CalculateTargetResolution();
        if (_lblDimensionPreview != null)
        {
            _lblDimensionPreview.Text = $"{res.X} × {res.Y} px";
        }
    }

    private void HighlightButton(Button btn, bool active)
    {
        if (btn == null) return;
        btn.ButtonPressed = active;
        btn.Modulate = active ? new Color(1.1f, 1.0f, 0.7f) : new Color(0.8f, 0.8f, 0.8f);
    }

    public Vector2I CalculateTargetResolution()
    {
        return (_currentRatio, _currentRes) switch
        {
            (AspectRatioMode.Ratio1x1, ResolutionTier.Res1080p) => new Vector2I(1080, 1080),
            (AspectRatioMode.Ratio1x1, ResolutionTier.Res1440p) => new Vector2I(1440, 1440),
            (AspectRatioMode.Ratio1x1, ResolutionTier.Res4K)     => new Vector2I(2160, 2160),

            (AspectRatioMode.Ratio4x3, ResolutionTier.Res1080p) => new Vector2I(1440, 1080),
            (AspectRatioMode.Ratio4x3, ResolutionTier.Res1440p) => new Vector2I(1920, 1440),
            (AspectRatioMode.Ratio4x3, ResolutionTier.Res4K)     => new Vector2I(2880, 2160),

            (AspectRatioMode.Ratio16x9, ResolutionTier.Res1080p) => new Vector2I(1920, 1080),
            (AspectRatioMode.Ratio16x9, ResolutionTier.Res1440p) => new Vector2I(2560, 1440),
            (AspectRatioMode.Ratio16x9, ResolutionTier.Res4K)    => new Vector2I(3840, 2160),

            (AspectRatioMode.Ratio9x16, ResolutionTier.Res1080p) => new Vector2I(1080, 1920),
            (AspectRatioMode.Ratio9x16, ResolutionTier.Res1440p) => new Vector2I(1440, 2560),
            (AspectRatioMode.Ratio9x16, ResolutionTier.Res4K)    => new Vector2I(2160, 3840),

            _ => new Vector2I(1920, 1080)
        };
    }

    public void UpdateFramingOverlay()
    {
        if (_framingOverlay == null) return;

        Vector2 viewportSize = (_framingOverlay != null && _framingOverlay.Size.X > 10 && _framingOverlay.Size.Y > 10) 
            ? _framingOverlay.Size 
            : GetViewport().GetVisibleRect().Size;
        float targetAspect = _currentRatio switch
        {
            AspectRatioMode.Ratio1x1 => 1.0f,
            AspectRatioMode.Ratio4x3 => 4.0f / 3.0f,
            AspectRatioMode.Ratio16x9 => 16.0f / 9.0f,
            AspectRatioMode.Ratio9x16 => 9.0f / 16.0f,
            _ => 16.0f / 9.0f
        };

        float screenAspect = viewportSize.X / Mathf.Max(1.0f, viewportSize.Y);

        // Reset bars
        if (_overlayBarTop != null) _overlayBarTop.Size = Vector2.Zero;
        if (_overlayBarBottom != null) _overlayBarBottom.Size = Vector2.Zero;
        if (_overlayBarLeft != null) _overlayBarLeft.Size = Vector2.Zero;
        if (_overlayBarRight != null) _overlayBarRight.Size = Vector2.Zero;

        if (screenAspect > targetAspect)
        {
            // Screen is wider than target: pillarbox bars on left and right
            float frameWidth = viewportSize.Y * targetAspect;
            float barWidth = (viewportSize.X - frameWidth) * 0.5f;

            if (_overlayBarLeft != null)
            {
                _overlayBarLeft.Position = Vector2.Zero;
                _overlayBarLeft.Size = new Vector2(barWidth, viewportSize.Y);
                _overlayBarLeft.Visible = barWidth > 2;
            }
            if (_overlayBarRight != null)
            {
                _overlayBarRight.Position = new Vector2(viewportSize.X - barWidth, 0);
                _overlayBarRight.Size = new Vector2(barWidth, viewportSize.Y);
                _overlayBarRight.Visible = barWidth > 2;
            }
            if (_overlayBarTop != null) _overlayBarTop.Visible = false;
            if (_overlayBarBottom != null) _overlayBarBottom.Visible = false;
        }
        else
        {
            // Screen is taller than target: letterbox bars on top and bottom
            float frameHeight = viewportSize.X / targetAspect;
            float barHeight = (viewportSize.Y - frameHeight) * 0.5f;

            if (_overlayBarTop != null)
            {
                _overlayBarTop.Position = Vector2.Zero;
                _overlayBarTop.Size = new Vector2(viewportSize.X, barHeight);
                _overlayBarTop.Visible = barHeight > 2;
            }
            if (_overlayBarBottom != null)
            {
                _overlayBarBottom.Position = new Vector2(0, viewportSize.Y - barHeight);
                _overlayBarBottom.Size = new Vector2(viewportSize.X, barHeight);
                _overlayBarBottom.Visible = barHeight > 2;
            }
            if (_overlayBarLeft != null) _overlayBarLeft.Visible = false;
            if (_overlayBarRight != null) _overlayBarRight.Visible = false;
        }
    }

    private async void TriggerExport()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            camera = GetNodeOrNull<Camera3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot/Camera3D")
                  ?? GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D")
                  ?? GetTree().Root.FindChild("Camera3D", true, false) as Camera3D;
        }

        if (camera == null)
        {
            GD.PrintErr("[ExportPanel] Camera not found!");
            return;
        }

        Vector2I res = CalculateTargetResolution();
        bool includeBg = _checkIncludeBg == null || _checkIncludeBg.ButtonPressed;
        bool transparent = !includeBg;

        if (_btnExport != null) _btnExport.Disabled = true;

        var studioUI = GetNodeOrNull<StudioUIManager>("/root/Main/UIRoot")
                    ?? GetTree().Root.FindChild("UIRoot", true, false) as StudioUIManager;
        string saveDir = studioUI?.SavePath ?? OS.GetSystemDir(OS.SystemDir.Pictures);

        Node3D gizmo = GetNodeOrNull<Node3D>("/root/Main/VpkLoaderTest/SkeletonGizmoManager")
                    ?? GetNodeOrNull<Node3D>("/root/Main/SkeletonGizmoManager")
                    ?? GetTree().Root.FindChild("SkeletonGizmoManager", true, false) as Node3D;

        EmitSignal(SignalName.ExportRequested, res, includeBg);

        await _screenshotHelper.CaptureAsync(camera, res, transparent, false, saveDir, gizmo);

        if (_btnExport != null) _btnExport.Disabled = false;
    }

    private void OnScreenshotSaved(string absolutePath)
    {
        GD.Print($"[ExportPanel] Screenshot exported to: {absolutePath}");
        EmitSignal(SignalName.ExportCompleted, absolutePath);

        var studioUI = GetNodeOrNull<StudioUIManager>("/root/Main/UIRoot");
        if (studioUI != null)
        {
            studioUI.ShowToast("Screenshot Exported!", absolutePath);
        }
    }
}
