using System;
using System.Collections.Generic;
using System.Numerics;
using Godot;
using ValveResourceFormat.IO;
using ValveResourceFormat.Particles;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Bridges an active Godot hero Node3D + Skeleton3D + optional AnimationPlayer with VRF's
/// IParticleModelObject so bone/hitbox/skinned-snapshot particle operators read live animated poses.
/// </summary>
public sealed class GodotParticleModelObject : IParticleModelObject
{
    private readonly Model _model;
    private readonly Node3D _heroNode;
    private readonly Skeleton3D _skeleton;
    private readonly AnimationPlayer _animPlayer;
    private readonly int[] _vrfToGodotBoneMap;
    private readonly Dictionary<string, int> _sequenceIndices;

    public GodotParticleModelObject(
        Model model,
        Node3D heroNode,
        Skeleton3D skeleton = null,
        AnimationPlayer animPlayer = null,
        IFileLoader fileLoader = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _heroNode = heroNode;
        _skeleton = skeleton ?? FindChildOfType<Skeleton3D>(heroNode);
        _animPlayer = animPlayer ?? FindChildOfType<AnimationPlayer>(heroNode);

        var vrfBones = _model.Skeleton?.Bones ?? [];
        _vrfToGodotBoneMap = new int[vrfBones.Length];
        for (int i = 0; i < vrfBones.Length; i++)
        {
            int gIdx = -1;
            if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton))
            {
                string boneName = vrfBones[i].Name;
                gIdx = _skeleton.FindBone(boneName);
                if (gIdx < 0)
                {
                    int total = _skeleton.GetBoneCount();
                    for (int b = 0; b < total; b++)
                    {
                        if (string.Equals(_skeleton.GetBoneName(b), boneName, StringComparison.OrdinalIgnoreCase))
                        {
                            gIdx = b;
                            break;
                        }
                    }
                }
            }
            _vrfToGodotBoneMap[i] = gIdx;
        }

        _sequenceIndices = BuildSequenceLookup(_model, fileLoader);
    }

    public Model Model => _model;

    public Matrix4x4 Transform
    {
        get
        {
            if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton))
            {
                return ParticleCoordUtils.GodotWorldTransformToSource2Matrix(_skeleton.GlobalTransform);
            }

            if (_heroNode != null && GodotObject.IsInstanceValid(_heroNode))
            {
                return ParticleCoordUtils.GodotWorldTransformToSource2Matrix(_heroNode.GlobalTransform);
            }

            return Matrix4x4.Identity;
        }
    }

    public void GetBonePose(Span<Matrix4x4> bones)
    {
        if (bones.IsEmpty)
        {
            return;
        }

        // Fill bind pose baseline for any unmapped bones first
        BindPoseModelObject.GetBindPose(_model, bones);

        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
        {
            return;
        }

        int count = Math.Min(bones.Length, _vrfToGodotBoneMap.Length);
        for (int i = 0; i < count; i++)
        {
            int godotIdx = _vrfToGodotBoneMap[i];
            if (godotIdx >= 0)
            {
                bones[i] = ParticleCoordUtils.GodotBonePoseToSource2Matrix(_skeleton.GetBoneGlobalPose(godotIdx));
            }
        }
    }

    public int SequenceIndex
    {
        get
        {
            if (_animPlayer == null || !GodotObject.IsInstanceValid(_animPlayer))
            {
                return -1;
            }

            string current = _animPlayer.CurrentAnimation;
            if (string.IsNullOrEmpty(current))
            {
                return -1;
            }

            return _sequenceIndices.TryGetValue(current, out int idx) ? idx : -1;
        }
    }

    public float SequenceRate
    {
        get
        {
            if (_animPlayer == null || !GodotObject.IsInstanceValid(_animPlayer))
            {
                return 1f;
            }

            if (string.IsNullOrEmpty(_animPlayer.CurrentAnimation))
            {
                return 1f;
            }

            return _animPlayer.IsPlaying() ? (float)_animPlayer.SpeedScale : 0f;
        }
    }

    private static Dictionary<string, int> BuildSequenceLookup(Model model, IFileLoader fileLoader)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;

        void AddNames(string[] names)
        {
            if (names == null) return;
            foreach (var name in names)
            {
                if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                {
                    map[name] = idx;
                }
                idx++;
            }
        }

        try
        {
            AddNames(model.SequenceGroup?.SequenceData?.GetArray<string>("m_localSequenceNameArray"));

            if (fileLoader != null)
            {
                foreach (var groupPath in model.GetReferencedAnimationGroupNames() ?? [])
                {
                    if (string.IsNullOrEmpty(groupPath)) continue;
                    var groupRes = fileLoader.LoadFileCompiled(groupPath);
                    var group = groupRes?.DataBlock?.AsKeyValueCollection();
                    if (group == null) continue;

                    var seqGroupPath = group.GetStringProperty("m_directHSeqGroup_Handle");
                    if (string.IsNullOrEmpty(seqGroupPath)) continue;

                    var seqRes = fileLoader.LoadFileCompiled(seqGroupPath);
                    var seqGroup = seqRes?.DataBlock?.AsKeyValueCollection();
                    if (seqGroup != null)
                    {
                        AddNames(seqGroup.GetArray<string>("m_localSequenceNameArray"));
                    }
                }
            }
        }
        catch
        {
            // Safe fallback if model has no sequences
        }

        return map;
    }

    private static T FindChildOfType<T>(Node root) where T : Node
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return null;
        if (root is T match) return match;
        foreach (Node child in root.GetChildren())
        {
            var found = FindChildOfType<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}

