using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeadlockPlayground.Importer
{
    public enum BoneImportDisposition
    {
        Mapped,
        Kept,
        Collapsed,
        Dropped,
        Appended,
        Disabled
    }

    public sealed class BoneReportEntry
    {
        public int SourceIndex { get; set; }
        public string SourceName { get; set; } = "";
        public BoneKind Kind { get; set; }
        public string Category { get; set; } = "";
        public BoneImportDisposition Disposition { get; set; }
        public string TargetBone { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool HasWeights { get; set; }
        public bool HasWeightedDescendant { get; set; }
    }

    public sealed class RemapDiffEntry
    {
        public string CanonicalName { get; set; } = "";
        public string AutoMappedSource { get; set; } = "";
        public string UserOverrideSource { get; set; } = "";
        public string EffectiveSource { get; set; } = "";
        public bool IsOverridden { get; set; }
        public bool IsClearedByUser { get; set; }
        public bool IsUnmapped => string.IsNullOrEmpty(EffectiveSource);
    }

    public sealed class BindQualityStats
    {
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public double ElapsedMs { get; set; }
        public int RigidPartCount { get; set; }
        public int UnweightedVertexCount { get; set; }
        public int Over4BoneVertexCount { get; set; }
        public float MeanDonorDistance { get; set; }
        public float MaxDonorDistance { get; set; }
        public int TotalExportBoneCount { get; set; }
        public int ClothBoneCount { get; set; }
        public int AccessoryBoneCount { get; set; }
        public int FingerBoneCount { get; set; }
    }

    /// <summary>
    /// Structured diagnostics report emitted by every rig &amp; export pass.
    /// Feeds the Review tab, bottom log console, and headless verification tests.
    /// </summary>
    public sealed class ImportReport
    {
        public RigFamily DetectedFamily { get; set; } = RigFamily.Unknown;
        public double FamilyConfidence { get; set; }
        public RigFamily EffectiveFamily { get; set; } = RigFamily.Unknown;
        public string MethodName { get; set; } = "";

        public List<BoneReportEntry> Bones { get; } = new();
        public List<RemapDiffEntry> RemapDiffs { get; } = new();
        public List<string> AppendedChains { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Logs { get; } = new();
        public BindQualityStats Stats { get; } = new();

        public int CountByDisposition(BoneImportDisposition disp)
            => Bones.Count(b => b.Disposition == disp);

        public int UnmappedCoreCount
            => RemapDiffs.Count(r => r.IsUnmapped);

        public string BuildSummaryLine()
        {
            return $"[{MethodName}] Family={EffectiveFamily} ({FamilyConfidence:P0}) | " +
                   $"Verts={Stats.VertexCount:N0}, Time={Stats.ElapsedMs:F0}ms, " +
                   $"Rigid={Stats.RigidPartCount}, Unweighted={Stats.UnweightedVertexCount}, " +
                   $"Bones: {CountByDisposition(BoneImportDisposition.Mapped)} mapped, " +
                   $"{CountByDisposition(BoneImportDisposition.Appended)} appended ({Stats.ClothBoneCount} cloth), " +
                   $"{CountByDisposition(BoneImportDisposition.Collapsed)} collapsed, " +
                   $"{CountByDisposition(BoneImportDisposition.Dropped)} dropped, " +
                   $"{CountByDisposition(BoneImportDisposition.Disabled)} disabled";
        }

        public string BuildDetailedConsoleText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildSummaryLine());
            if (Stats.MeanDonorDistance > 0f || Stats.MaxDonorDistance > 0f)
            {
                sb.AppendLine($"  Donor distance: mean={Stats.MeanDonorDistance * 100f:F2} cm, max={Stats.MaxDonorDistance * 100f:F2} cm");
            }
            if (AppendedChains.Count > 0)
            {
                sb.AppendLine($"  Appended chains ({AppendedChains.Count}): {string.Join(", ", AppendedChains)}");
            }
            foreach (var w in Warnings)
            {
                sb.AppendLine($"  [WARN] {w}");
            }
            foreach (var l in Logs)
            {
                sb.AppendLine($"  [LOG] {l}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}

