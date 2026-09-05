using Godot;
using System;
using System.IO;
using System.Threading.Tasks;

public partial class ScreenshotHelper : Node
{
    [Signal]
    public delegate void ScreenshotSavedEventHandler(string absolutePath);

    private SubViewport _captureViewport;
    private Camera3D _captureCamera;
    private CanvasLayer _bgCanvas;
    private ColorRect _bgColorRect;
    private TextureRect _bgRect;

    public override void _Ready()
    {
        // Setup off-screen viewport
        _captureViewport = new SubViewport();
        _captureViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _captureViewport.RenderTargetClearMode = SubViewport.ClearMode.Always;
        _captureViewport.TransparentBg = true;

        _captureCamera = new Camera3D();
        _captureViewport.AddChild(_captureCamera);

        _bgCanvas = new CanvasLayer();
        _bgCanvas.Layer = -1;

        _bgColorRect = new ColorRect();
        _bgColorRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bgCanvas.AddChild(_bgColorRect);

        _bgRect = new TextureRect();
        _bgRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bgRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _bgRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        _bgCanvas.AddChild(_bgRect);

        _captureViewport.AddChild(_bgCanvas);

        AddChild(_captureViewport);
    }

    public Task CaptureAsync(Camera3D mainCamera, int resolutionMultiplier, bool transparent, bool useJpg, string saveDirectory, Node3D gizmoManager = null)
    {
        Vector2I baseSize = (Vector2I)GetViewport().GetVisibleRect().Size;
        Vector2I targetRes = resolutionMultiplier switch
        {
            0 => new Vector2I(854, 480),
            1 => new Vector2I(1280, 720),
            2 => new Vector2I(1920, 1080),
            3 => new Vector2I(2560, 1440),
            4 => new Vector2I(3840, 2160),
            _ => baseSize * Math.Max(1, resolutionMultiplier)
        };

        return CaptureAsync(mainCamera, targetRes, transparent, useJpg, saveDirectory, gizmoManager);
    }

    public async Task CaptureAsync(Camera3D mainCamera, Vector2I targetResolution, bool transparent, bool useJpg, string saveDirectory, Node3D gizmoManager = null)
    {
        if (mainCamera == null)
        {
            GD.PrintErr("[ScreenshotHelper] mainCamera is null!");
            return;
        }

        // 1. Setup paths
        if (string.IsNullOrWhiteSpace(saveDirectory) || !Directory.Exists(saveDirectory))
        {
            saveDirectory = OS.GetSystemDir(OS.SystemDir.Pictures);
            if (string.IsNullOrEmpty(saveDirectory))
            {
                saveDirectory = OS.GetUserDataDir();
            }
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        bool saveAsJpg = transparent ? false : useJpg;
        string extension = saveAsJpg ? ".jpg" : ".png";

        string filePath = Path.Combine(saveDirectory, $"DeadlockCapture_{timestamp}{extension}").Replace("\\", "/");

        // 2. Setup Viewport matching main camera with custom aspect ratio
        _captureViewport.World3D = mainCamera.GetWorld3D();
        _captureViewport.Size = targetResolution;
        _captureViewport.TransparentBg = transparent;

        _captureCamera.GlobalTransform = mainCamera.GlobalTransform;
        _captureCamera.Near = mainCamera.Near;
        _captureCamera.Far = mainCamera.Far;
        _captureCamera.Environment = mainCamera.Environment;
        _captureCamera.Attributes = mainCamera.Attributes;

        // In Deadlock Playground, framing overlay uses Height (fixed vertical FOV)
        _captureCamera.KeepAspect = Camera3D.KeepAspectEnum.Height;

        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        float screenAspect = viewportSize.X / Mathf.Max(1.0f, viewportSize.Y);
        float targetAspect = (float)targetResolution.X / Mathf.Max(1, targetResolution.Y);

        if (screenAspect < targetAspect)
        {
            float frameHeight = viewportSize.X / targetAspect;
            float vFactor = frameHeight / viewportSize.Y;
            float origHalfRad = Mathf.DegToRad(mainCamera.Fov * 0.5f);
            float targetHalfRad = Mathf.Atan(Mathf.Tan(origHalfRad) * vFactor);
            _captureCamera.Fov = Mathf.RadToDeg(targetHalfRad * 2.0f);
        }
        else
        {
            _captureCamera.Fov = mainCamera.Fov;
        }

        // 3. Hide Gizmos temporarily & Sync Background
        bool wasGizmoVisible = true;
        if (gizmoManager != null)
        {
            wasGizmoVisible = gizmoManager.Visible;
            gizmoManager.Visible = false;
        }

        if (transparent)
        {
            _bgCanvas.Visible = false;
        }
        else
        {
            _bgCanvas.Visible = true;

            var mainColor = GetNodeOrNull<ColorRect>("/root/Main/BGCanvas/ColorRect");
            if (mainColor != null && _bgColorRect != null)
            {
                _bgColorRect.Color = mainColor.Color;
                _bgColorRect.Visible = mainColor.Visible;
            }

            var mainBg = GetNodeOrNull<TextureRect>("/root/Main/BGCanvas/BackgroundRect");
            if (mainBg != null && _bgRect != null)
            {
                _bgRect.Texture = mainBg.Texture;
                _bgRect.Material = mainBg.Material;
                _bgRect.Visible = mainBg.Visible;
            }
        }

        // Wait for frame update
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 4. Request Render
        _captureViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

        // Wait two frames to ensure render completes
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 5. Save Image
        Image img = _captureViewport.GetTexture().GetImage();

        await Task.Run(() =>
        {
            if (!saveAsJpg)
                img.SavePng(filePath);
            else
                img.SaveJpg(filePath, 0.95f);
        });

        // 6. Restore Gizmos
        if (gizmoManager != null)
        {
            gizmoManager.Visible = wasGizmoVisible;
        }

        EmitSignal(SignalName.ScreenshotSaved, filePath);
    }
}
