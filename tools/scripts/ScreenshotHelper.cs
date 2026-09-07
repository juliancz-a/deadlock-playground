using Godot;
using System;
using System.IO;
using System.Threading.Tasks;

public partial class ScreenshotHelper : Node
{
    [Signal]
    public delegate void ScreenshotSavedEventHandler(string absolutePath);

    private SubViewport _captureViewport;
    private SubViewportContainer _captureWorldContainer;
    private SubViewport _capture3DViewport;
    private Camera3D _captureCamera;

    private CanvasLayer _bgCanvas;
    private ColorRect _bgColorRect;
    private TextureRect _bgRect;

    private CanvasLayer _shaderCanvas;
    private BackBufferCopy _captureToonBuffer;
    private ColorRect _captureToonRect;
    private BackBufferCopy _captureCrtBuffer;
    private ColorRect _captureCrtRect;
    private BackBufferCopy _captureGlitchBuffer;
    private ColorRect _captureGlitchRect;

    public override void _Ready()
    {
        // 1. Root composite capture viewport
        _captureViewport = new SubViewport
        {
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            TransparentBg = true
        };

        // 2. Background CanvasLayer (layer -2)
        _bgCanvas = new CanvasLayer { Layer = -2 };

        _bgColorRect = new ColorRect();
        _bgColorRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bgCanvas.AddChild(_bgColorRect);

        _bgRect = new TextureRect();
        _bgRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bgRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _bgRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        _bgCanvas.AddChild(_bgRect);

        _captureViewport.AddChild(_bgCanvas);

        // 3. 3D Character SubViewport in SubViewportContainer
        _captureWorldContainer = new SubViewportContainer
        {
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _captureWorldContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        _capture3DViewport = new SubViewport
        {
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always
        };

        _captureCamera = new Camera3D();
        _capture3DViewport.AddChild(_captureCamera);
        _captureWorldContainer.AddChild(_capture3DViewport);
        _captureViewport.AddChild(_captureWorldContainer);

        // 4. Shader Overlay CanvasLayer (layer 1)
        _shaderCanvas = new CanvasLayer { Layer = 1 };

        _captureToonBuffer = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport };
        _captureToonRect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        _captureToonRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _shaderCanvas.AddChild(_captureToonBuffer);
        _shaderCanvas.AddChild(_captureToonRect);

        _captureCrtBuffer = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport };
        _captureCrtRect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        _captureCrtRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _shaderCanvas.AddChild(_captureCrtBuffer);
        _shaderCanvas.AddChild(_captureCrtRect);

        _captureGlitchBuffer = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport };
        _captureGlitchRect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        _captureGlitchRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _shaderCanvas.AddChild(_captureGlitchBuffer);
        _shaderCanvas.AddChild(_captureGlitchRect);

        _captureViewport.AddChild(_shaderCanvas);

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
        _captureViewport.Size = targetResolution;
        _captureViewport.TransparentBg = transparent;

        _captureWorldContainer.Size = targetResolution;
        _capture3DViewport.Size = targetResolution;
        _capture3DViewport.World3D = mainCamera.GetWorld3D();

        _captureCamera.GlobalTransform = mainCamera.GlobalTransform;
        _captureCamera.Near = mainCamera.Near;
        _captureCamera.Far = mainCamera.Far;
        _captureCamera.Environment = mainCamera.Environment;
        _captureCamera.Attributes = mainCamera.Attributes;
        _captureCamera.Projection = mainCamera.Projection;
        _captureCamera.Size = mainCamera.Size;
        _captureCamera.CullMask = mainCamera.CullMask; // Exclude Gizmo layer (layer 2)

        _captureCamera.Current = true;

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
            _bgColorRect.Size = targetResolution;
            _bgRect.Size = targetResolution;

            var mainColor = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/ColorRect")
                         ?? GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/BGCanvas/ColorRect")
                         ?? GetNodeOrNull<ColorRect>("/root/Main/BGCanvas/ColorRect")
                         ?? GetTree().Root.FindChild("ColorRect", true, false) as ColorRect;
            if (mainColor != null && _bgColorRect != null)
            {
                _bgColorRect.Color = mainColor.Color;
                _bgColorRect.Visible = mainColor.Visible;
            }

            var mainBg = GetNodeOrNull<TextureRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/BackgroundRect")
                      ?? GetNodeOrNull<TextureRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/BGCanvas/BackgroundRect")
                      ?? GetNodeOrNull<TextureRect>("/root/Main/BGCanvas/BackgroundRect")
                      ?? GetTree().Root.FindChild("BackgroundRect", true, false) as TextureRect;
            if (mainBg != null && _bgRect != null)
            {
                _bgRect.Texture = mainBg.Texture;
                _bgRect.Material = mainBg.Material;
                _bgRect.Visible = mainBg.Visible;
            }
        }

        // 4. Replicate Active 2D Post-Processing Shaders (CRT & Glitch)
        var mainCrt = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/ShaderOverlayStack/CRTRect")
                   ?? GetTree().Root.FindChild("CRTRect", true, false) as ColorRect;
        var mainGlitch = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/ShaderOverlayStack/GlitchRect")
                      ?? GetTree().Root.FindChild("GlitchRect", true, false) as ColorRect;

        bool hasShaders = false;

        _captureToonRect.Visible = false;
        _captureToonBuffer.Visible = false;

        if (mainCrt != null && mainCrt.Visible && mainCrt.Material != null)
        {
            _captureCrtRect.Size = targetResolution;
            _captureCrtRect.Visible = true;
            _captureCrtBuffer.Visible = true;
            _captureCrtRect.Material = (Material)mainCrt.Material.Duplicate();
            hasShaders = true;
        }
        else
        {
            _captureCrtRect.Visible = false;
            _captureCrtBuffer.Visible = false;
        }

        if (mainGlitch != null && mainGlitch.Visible && mainGlitch.Material != null)
        {
            _captureGlitchRect.Size = targetResolution;
            _captureGlitchRect.Visible = true;
            _captureGlitchBuffer.Visible = true;
            _captureGlitchRect.Material = (Material)mainGlitch.Material.Duplicate();
            hasShaders = true;
        }
        else
        {
            _captureGlitchRect.Visible = false;
            _captureGlitchBuffer.Visible = false;
        }

        _shaderCanvas.Visible = hasShaders;

        // 5. Render Pass
        _capture3DViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _captureViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // Wait two frames to ensure render completes in both viewports
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        _capture3DViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _captureViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;

        // 6. Save Image
        Image img = _captureViewport.GetTexture().GetImage();

        await Task.Run(() =>
        {
            if (!saveAsJpg)
                img.SavePng(filePath);
            else
                img.SaveJpg(filePath, 0.95f);
        });

        // 7. Restore Gizmos
        if (gizmoManager != null)
        {
            gizmoManager.Visible = wasGizmoVisible;
        }

        EmitSignal(SignalName.ScreenshotSaved, filePath);
    }
}
