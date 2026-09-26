using Godot;
using Gizmo3DPlugin;
using System;
using System.Collections.Generic;
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

    public Task CaptureAsync(Camera3D mainCamera, int resolutionMultiplier, bool transparent, bool useJpg, string saveDirectory, Node3D gizmoManager = null, bool includeBonesWireframe = false)
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

        return CaptureAsync(mainCamera, targetRes, transparent, useJpg, saveDirectory, gizmoManager, includeBonesWireframe);
    }

    public async Task CaptureAsync(Camera3D mainCamera, Vector2I targetResolution, bool transparent, bool useJpg, string saveDirectory, Node3D gizmoManager = null, bool includeBonesWireframe = false)
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

        // CullMask: include Layer 2 (Gizmos & Wireframes) only if includeBonesWireframe is true; exclude Layer 21 (CameraBrush)
        if (includeBonesWireframe)
        {
            _captureCamera.CullMask = (mainCamera.CullMask | 2) & ~(uint)(1 << 20);
        }
        else
        {
            _captureCamera.CullMask = (mainCamera.CullMask & ~(uint)2) & ~(uint)(1 << 20);
        }

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

        // 3. Temporarily hide Bone & Wireframe Controls if includeBonesWireframe is false
        var boneVisibilityBackup = new BoneWireframeVisibilityBackup();
        if (!includeBonesWireframe)
        {
            boneVisibilityBackup.CollectAndHide(GetTree().Root, gizmoManager);
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

        try
        {
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
        }
        finally
        {
            // 7. Restore bone and wireframe visibility states immediately after capture
            boneVisibilityBackup.Restore();
        }

        EmitSignal(SignalName.ScreenshotSaved, filePath);
    }

    private sealed class BoneWireframeVisibilityBackup
    {
        private readonly List<(Node3D Node, bool WasVisible)> _states = new();
        private readonly List<(CanvasItem Item, bool WasVisible)> _canvasStates = new();
        private readonly HashSet<ulong> _visitedInstanceIds = new();

        public void CollectAndHide(Node root, Node3D primaryGizmoManager)
        {
            if (root == null) return;

            // 1. Primary Gizmo Manager
            if (primaryGizmoManager != null && GodotObject.IsInstanceValid(primaryGizmoManager))
            {
                SaveAndHide(primaryGizmoManager);

                if (primaryGizmoManager is SkeletonGizmoManager sgm)
                {
                    if (sgm.LayerManager != null && GodotObject.IsInstanceValid(sgm.LayerManager))
                    {
                        SaveAndHide(sgm.LayerManager);
                    }
                    if (sgm.IKManager != null && GodotObject.IsInstanceValid(sgm.IKManager))
                    {
                        SaveAndHide(sgm.IKManager);
                    }
                    if (sgm.TargetSkeleton != null && GodotObject.IsInstanceValid(sgm.TargetSkeleton))
                    {
                        CollectSkeletonDebugNodes(sgm.TargetSkeleton);
                    }
                }
            }

            // 2. Discover any additional SkeletonGizmoManager
            var allSgm = FindNodesOfType<SkeletonGizmoManager>(root);
            foreach (var sgm in allSgm)
            {
                SaveAndHide(sgm);
                if (sgm.LayerManager != null && GodotObject.IsInstanceValid(sgm.LayerManager))
                {
                    SaveAndHide(sgm.LayerManager);
                }
                if (sgm.IKManager != null && GodotObject.IsInstanceValid(sgm.IKManager))
                {
                    SaveAndHide(sgm.IKManager);
                }
                if (sgm.TargetSkeleton != null && GodotObject.IsInstanceValid(sgm.TargetSkeleton))
                {
                    CollectSkeletonDebugNodes(sgm.TargetSkeleton);
                }
            }

            // 3. Discover CharacterIKManager
            var allIK = FindNodesOfType<CharacterIKManager>(root);
            foreach (var ik in allIK)
            {
                SaveAndHide(ik);
                foreach (Node child in ik.GetChildren())
                {
                    if (child is Node3D n3d)
                    {
                        SaveAndHide(n3d);
                    }
                }
            }

            // 4. Discover Gizmo3D
            var allGizmos = FindNodesOfType<Gizmo3D>(root);
            foreach (var g in allGizmos)
            {
                SaveAndHide(g);
            }

            // 5. All Skeletons in the tree: hide wireframe mesh instances & bone attachments
            var allSkeletons = FindNodesOfType<Skeleton3D>(root);
            foreach (var skel in allSkeletons)
            {
                CollectSkeletonDebugNodes(skel);
            }

            // 6. UI Gizmo overlay
            var gizmoViewportContainer = root.FindChild("GizmoViewportContainer", true, false) as CanvasItem;
            if (gizmoViewportContainer != null)
            {
                SaveAndHide(gizmoViewportContainer);
            }
        }

        private void CollectSkeletonDebugNodes(Skeleton3D skeleton)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;

            int childCount = skeleton.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                Node child = skeleton.GetChild(i);
                if (child is MeshInstance3D mesh)
                {
                    // Layer 2 is used exclusively for bone wireframes & gizmos
                    if ((mesh.Layers & 2) != 0 || mesh.Name.ToString().Contains("Wireframe") || mesh.Name.ToString().Contains("Skeleton"))
                    {
                        SaveAndHide(mesh);
                    }
                }
                else if (child is BoneAttachment3D attachment)
                {
                    SaveAndHide(attachment);
                    int attCount = attachment.GetChildCount();
                    for (int j = 0; j < attCount; j++)
                    {
                        if (attachment.GetChild(j) is Node3D attN3d)
                        {
                            SaveAndHide(attN3d);
                        }
                    }
                }
            }
        }

        public void SaveAndHide(Node3D node)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                ulong id = node.GetInstanceId();
                if (_visitedInstanceIds.Add(id))
                {
                    _states.Add((node, node.Visible));
                    node.Visible = false;
                }
            }
        }

        public void SaveAndHide(CanvasItem item)
        {
            if (item != null && GodotObject.IsInstanceValid(item))
            {
                ulong id = item.GetInstanceId();
                if (_visitedInstanceIds.Add(id))
                {
                    _canvasStates.Add((item, item.Visible));
                    item.Visible = false;
                }
            }
        }

        public void Restore()
        {
            foreach (var (node, wasVisible) in _states)
            {
                if (node != null && GodotObject.IsInstanceValid(node))
                {
                    node.Visible = wasVisible;
                }
            }
            _states.Clear();

            foreach (var (item, wasVisible) in _canvasStates)
            {
                if (item != null && GodotObject.IsInstanceValid(item))
                {
                    item.Visible = wasVisible;
                }
            }
            _canvasStates.Clear();
            _visitedInstanceIds.Clear();
        }

        private static List<T> FindNodesOfType<T>(Node root) where T : class
        {
            var list = new List<T>();
            Traverse(root, list);
            return list;

            static void Traverse(Node parent, List<T> results)
            {
                if (parent == null) return;
                if (parent is T match) results.Add(match);
                int count = parent.GetChildCount();
                for (int i = 0; i < count; i++)
                {
                    Traverse(parent.GetChild(i), results);
                }
            }
        }
    }
}
