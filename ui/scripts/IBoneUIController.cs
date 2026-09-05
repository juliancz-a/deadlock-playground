using System;
using Godot;

/// <summary>
/// Interface decoupling 3D bone gizmos and picking from the UI panel.
/// Implemented by BonesTabUI and PoseEditorUI.
/// </summary>
public interface IBoneUIController
{
    event Action<bool> OnXRayToggled;
    event Action<bool> OnLinesToggled;
    event Action<int> OnBoneSelectedFromUI;

    bool IsXRayEnabled { get; }
    bool IsLinesEnabled { get; }

    void SetBoneLayerManager(BoneLayerManager layerManager);
    void SelectBone(int boneIdx);
    void UpdateSlidersFromBone();
}
