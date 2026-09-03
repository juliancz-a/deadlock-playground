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
        
        _bgRect = new TextureRect();
        _bgRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bgRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _bgRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        _bgCanvas.AddChild(_bgRect);
        
        _captureViewport.AddChild(_bgCanvas);
        
        AddChild(_captureViewport);
    }

    public async Task CaptureAsync(Camera3D mainCamera, int resolutionMultiplier, bool transparent, bool useJpg, string saveDirectory, Node3D gizmoManager = null)
    {
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
        
        // If transparent is true, we MUST use PNG. Otherwise, respect the useJpg flag.
        bool saveAsJpg = transparent ? false : useJpg;
        string extension = saveAsJpg ? ".jpg" : ".png";
        
        // Ensure path uses appropriate separators and normalize
        string filePath = Path.Combine(saveDirectory, $"DeadlockCapture_{timestamp}{extension}");
        filePath = filePath.Replace("\\", "/");

        // 2. Setup Viewport matching main camera
        _captureViewport.World3D = mainCamera.GetWorld3D();
        _captureCamera.GlobalTransform = mainCamera.GlobalTransform;
        _captureCamera.Fov = mainCamera.Fov;
        _captureCamera.Near = mainCamera.Near;
        _captureCamera.Far = mainCamera.Far;
        _captureCamera.Environment = mainCamera.Environment;
        _captureCamera.Attributes = mainCamera.Attributes;
        
        Vector2I baseSize = (Vector2I)GetViewport().GetVisibleRect().Size;
        // Apply multiplier (assuming 1x = 1080p roughly, or just scale window size)
        // If the user selects e.g. 1440p but their window is 720p, the multiplier logic might be relative to the window size.
        // Or we can set absolute resolutions. But based on the UI `1080p, 1440p, 2160p`, let's just parse those.
        // Actually, the UIManager sends the item ID as multiplier (0=480p, 1=720p, 2=1080p, 3=1440p, 4=2160p etc).
        // Let's implement a clean resolution table:
        Vector2I targetRes = baseSize;
        switch (resolutionMultiplier)
        {
            case 0: targetRes = new Vector2I(854, 480); break;
            case 1: targetRes = new Vector2I(1280, 720); break;
            case 2: targetRes = new Vector2I(1920, 1080); break;
            case 3: targetRes = new Vector2I(2560, 1440); break;
            case 4: targetRes = new Vector2I(3840, 2160); break;
            default: targetRes = baseSize * Math.Max(1, resolutionMultiplier); break;
        }
        
        _captureViewport.Size = targetRes;
        _captureViewport.TransparentBg = transparent;

        // 3. Hide Gizmos temporarily & Set Background
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
            // Fetch current background texture from main scene
            var mainBg = GetNodeOrNull<TextureRect>("/root/Main/BGCanvas/BackgroundRect");
            if (mainBg != null)
            {
                _bgRect.Texture = mainBg.Texture;
            }
        }

        // Wait a frame for visibility to update
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 4. Request Render
        _captureViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

        // Wait two frames to ensure render completes
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 5. Save Image (Run in Task to avoid blocking main thread on disk IO)
        Image img = _captureViewport.GetTexture().GetImage();
        
        await Task.Run(() =>
        {
            if (!saveAsJpg)
                img.SavePng(filePath);
            else
                img.SaveJpg(filePath, 0.95f); // Godot expects a float between 0.01 and 1.0
        });

        // 6. Restore Gizmos
        if (gizmoManager != null)
        {
            gizmoManager.Visible = wasGizmoVisible;
        }

        EmitSignal(SignalName.ScreenshotSaved, filePath);
    }
}
