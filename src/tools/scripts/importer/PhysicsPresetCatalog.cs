using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Single-source-of-truth Valve-backed physics preset catalog calibrated directly from compiled
    /// Deadlock <c>FeModel</c> benchmarks (<c>pak01_dir.vpk</c> via <c>tools/fe_model_dump</c>).
    /// Drives auto-classification (<see cref="RigBoneClassifier"/>), Godot live preview
    /// (<c>CustomJiggleBoneDriver</c>), and Source 2 <c>CSDK13</c> export (<c>CustomModelExporter</c>).
    /// </summary>
    public static class PhysicsPresetCatalog
    {
        public const string CustomId = "custom";
        public const string ShortHairBangsId = "short_hair_bangs";
        public const string MediumHairId = "medium_hair";
        public const string LongFlowingHairId = "long_flowing_hair";
        public const string UltraLongPonytailId = "ultra_long_ponytail";
        public const string HeavySkirtDressId = "heavy_skirt_dress";
        public const string StructuredDressGownId = "structured_dress_gown";
        public const string TrenchcoatLongCoatId = "trenchcoat_long_coat";
        public const string CapeCloakId = "cape_cloak";
        public const string ScarfRibbonSashId = "scarf_ribbon_sash";
        public const string TailFlexibleId = "tail_flexible";
        public const string ChainsKeysJewelryId = "chains_keys_jewelry";
        public const string RigidJiggleId = "rigid_jiggle";

        public sealed class PhysicsPresetDef
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string HeroReference { get; init; } = "";
            public string ShortTag { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "Clothing";
            /// <summary>Custom preview thumbnail path under <c>res://assets/thumbnails/physics_preview/</c>.</summary>
            public string ThumbnailPath { get; init; } = "";
            public string FallbackIconPath { get; init; } = "res://assets/at-icons/signal_wave.svg";

            public SimulationKind Simulation { get; init; } = SimulationKind.ProxyCloth;
            public float StiffnessRoot { get; init; } = 0.55f;
            public float StiffnessTip { get; init; } = 0.05f;
            /// <summary>Exponent for root-to-tip taper <c>Pow(1 - t, GoalTaperPower)</c>. &lt; 1 holds shape down to mid-length (Geist); &gt; 2 drops fast (Yamato).</summary>
            public float GoalTaperPower { get; init; } = 1.85f;
            public float Damping { get; init; } = 0.15f;
            public float Gravity { get; init; } = 1.0f;
            public float CollisionRadiusTipInches { get; init; } = 2.0f;
            public float Friction { get; init; } = 0.15f;
            public float StrayRadiusInches { get; init; } = -1.0f;
            public bool PlanarizeBackBarrier { get; init; } = false;
            public bool FloorCollision { get; init; } = false;
            public bool OpenSheet { get; init; } = true;
            public bool AnchorFreeRotate { get; init; } = false;
            public float AngleLimitDeg { get; init; } = 35.0f;
            public float TwistRelax { get; init; } = 1.0f;
            public bool PinRootBone { get; init; } = false;
        }

        public static readonly IReadOnlyList<PhysicsPresetDef> Presets = new List<PhysicsPresetDef>
        {
            new()
            {
                Id = ShortHairBangsId,
                DisplayName = "Short Hair / Bangs",
                HeroReference = "Paige / Silver / Lash",
                ShortTag = "1D Rope · Paige/Silver · 3.0 in Leash",
                Description = "Structured bangs, short spiky hair, and tufts using a pure Valve 1D rope with pinned scalp root, 1.0g gravity, and 3.0 in leash (calibrated to Paige/Silver/Lash).",
                Category = "Hair",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/short_hair_paige.png",
                FallbackIconPath = "res://assets/at-icons/sparkles.svg",
                Simulation = SimulationKind.Cloth,
                StiffnessRoot = 0.86f,
                StiffnessTip = 0.65f,
                GoalTaperPower = 1.15f,
                Damping = 0.15f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 0.50f,
                Friction = 0.10f,
                StrayRadiusInches = 3.0f,
                PlanarizeBackBarrier = false,
                OpenSheet = true,
                AnchorFreeRotate = true,
                AngleLimitDeg = 35.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = MediumHairId,
                DisplayName = "Medium Hair",
                HeroReference = "Silver",
                ShortTag = "Open Sheet · Mid Hold · Back Barrier",
                Description = "Shoulder-length hair curtain that keeps structured volume through the mid-length with smooth shoulder deflection (calibrated to Silver).",
                Category = "Hair",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/medium_hair_silver.png",
                FallbackIconPath = "res://assets/at-icons/signal_wave.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.72f,
                StiffnessTip = 0.58f,
                GoalTaperPower = 1.00f,
                Damping = 0.10f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 2.6f,
                Friction = 0.35f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = true,
                OpenSheet = true,
                AnchorFreeRotate = true,
                AngleLimitDeg = 45.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = LongFlowingHairId,
                DisplayName = "Long Flowing Hair",
                HeroReference = "Yamato / Vindicta",
                ShortTag = "Open Sheet · Free Tips · Back Barrier",
                Description = "Long back hair or braids that follow the scalp at the root and drape freely down the back with chest-clipping protection.",
                Category = "Hair",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/long_flowing_hair_vindicta.png",
                FallbackIconPath = "res://assets/at-icons/signal_wave.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.70f,
                StiffnessTip = 0.04f,
                GoalTaperPower = 2.10f,
                Damping = 0.08f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 2.4f,
                Friction = 0.10f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = true,
                FloorCollision = true,
                OpenSheet = true,
                AnchorFreeRotate = true,
                AngleLimitDeg = 55.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = UltraLongPonytailId,
                DisplayName = "Ultra-Long Ponytail",
                HeroReference = "Celeste",
                ShortTag = "Cloth Chain · Loose Flow · Back Barrier",
                Description = "Very loose, fluid long ponytail with planarized upper-back collision planes so it never flips into the chest (calibrated to Celeste/Unicorn).",
                Category = "Hair",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/ultra_long_ponytail_celeste.png",
                FallbackIconPath = "res://assets/at-icons/flag_triangular.svg",
                Simulation = SimulationKind.Cloth,
                StiffnessRoot = 0.25f,
                StiffnessTip = 0.02f,
                GoalTaperPower = 2.20f,
                Damping = 0.06f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 1.6f,
                Friction = 0.05f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = true,
                FloorCollision = true,
                OpenSheet = true,
                AnchorFreeRotate = true,
                AngleLimitDeg = 65.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = HeavySkirtDressId,
                DisplayName = "Heavy Skirt / Dress",
                HeroReference = "Yamato",
                ShortTag = "360° Sheet · 3.0× Grav · Thigh Planes",
                Description = "Weighted wrap-around skirt or kimono that hangs naturally down instead of floating, with planarized thigh barriers (calibrated to Yamato).",
                Category = "Clothing",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/heavy_skirt_yamato.png",
                FallbackIconPath = "res://assets/at-icons/person_body.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.72f,
                StiffnessTip = 0.04f,
                GoalTaperPower = 2.60f,
                Damping = 0.08f,
                Gravity = 3.0f,
                CollisionRadiusTipInches = 2.0f,
                Friction = 0.20f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                FloorCollision = true,
                OpenSheet = false,
                AnchorFreeRotate = false,
                AngleLimitDeg = 45.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = StructuredDressGownId,
                DisplayName = "Structured Dress / Gown",
                HeroReference = "Lady Geist",
                ShortTag = "360° Sheet · Late Taper · High Friction",
                Description = "Holds upper-hip dress structure and flows gracefully at the lower hem with high sliding friction (calibrated to Lady Geist).",
                Category = "Clothing",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/dress_lady_geist.png",
                FallbackIconPath = "res://assets/at-icons/layers.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.92f,
                StiffnessTip = 0.14f,
                GoalTaperPower = 0.65f,
                Damping = 0.10f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 2.4f,
                Friction = 0.85f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                FloorCollision = true,
                OpenSheet = false,
                AnchorFreeRotate = false,
                AngleLimitDeg = 40.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = TrenchcoatLongCoatId,
                DisplayName = "Trenchcoat / Long Coat",
                HeroReference = "Wraith / Paradox",
                ShortTag = "Open Sheet · Wide Col · Natural Drape",
                Description = "Open long coat or trenchcoat tails with wide lower collision spheres to clear running legs cleanly (calibrated to Wraith/Paradox/Abrams).",
                Category = "Clothing",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/trenchcoat_wraith.png",
                FallbackIconPath = "res://assets/at-icons/uv_layer.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.68f,
                StiffnessTip = 0.02f,
                GoalTaperPower = 2.20f,
                Damping = 0.08f,
                Gravity = 1.15f,
                CollisionRadiusTipInches = 3.2f,
                Friction = 0.25f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                FloorCollision = true,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 45.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = CapeCloakId,
                DisplayName = "Cape / Cloak",
                HeroReference = "Nurse Harrow / Venator",
                ShortTag = "Open Sheet · Natural Drape · 3.2 in Col",
                Description = "Billowing shoulder cape or split cloak panels with capsule collision and smooth drape (calibrated to Nurse Harrow/Venator — 0 CollisionPlanes).",
                Category = "Clothing",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/cloak_venator.png",
                FallbackIconPath = "res://assets/at-icons/flag_triangular.svg",
                Simulation = SimulationKind.ProxyCloth,
                StiffnessRoot = 0.58f,
                StiffnessTip = 0.04f,
                GoalTaperPower = 1.50f,
                Damping = 0.10f,
                Gravity = 1.0f,
                CollisionRadiusTipInches = 3.2f,
                Friction = 0.20f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                FloorCollision = true,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 55.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = ScarfRibbonSashId,
                DisplayName = "Scarf / Ribbon / Sash",
                HeroReference = "Holliday / Pocket",
                ShortTag = "Cloth Chain · 1.4× Grav · Fluid Drape",
                Description = "Independent flowing scarf, ribbon, sash, or fur stole with brisk drop-off and smooth settle (calibrated to Holliday/Pocket/Geist).",
                Category = "Clothing",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/fur_lady_Geist.png",
                FallbackIconPath = "res://assets/at-icons/line_graph.svg",
                Simulation = SimulationKind.Cloth,
                StiffnessRoot = 0.72f,
                StiffnessTip = 0.05f,
                GoalTaperPower = 2.20f,
                Damping = 0.08f,
                Gravity = 1.40f,
                CollisionRadiusTipInches = 2.0f,
                Friction = 0.30f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                FloorCollision = true,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 55.0f,
                TwistRelax = 1.0f
            },
            new()
            {
                Id = TailFlexibleId,
                DisplayName = "Tail / Flexible Accessory",
                HeroReference = "Abrams / Apollo",
                ShortTag = "Cloth Chain · 0.7× Grav · Gradual Taper",
                Description = "Responsive animated tail or flexible appendage that tracks root animations closely with gentle tip lag (calibrated to Abrams/Apollo).",
                Category = "Accessory",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/tail_abrams.png",
                FallbackIconPath = "res://assets/at-icons/bone.svg",
                Simulation = SimulationKind.Cloth,
                StiffnessRoot = 0.88f,
                StiffnessTip = 0.30f,
                GoalTaperPower = 1.35f,
                Damping = 0.12f,
                Gravity = 0.70f,
                CollisionRadiusTipInches = 1.6f,
                Friction = 0.10f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 45.0f,
                TwistRelax = 1.2f
            },
            new()
            {
                Id = ChainsKeysJewelryId,
                DisplayName = "Chains / Keys / Jewelry",
                HeroReference = "Doorman / Calico",
                ShortTag = "Cloth Chain · Very Loose · 0.4 in Col",
                Description = "Free-swinging metal keys, chains, pendants, or earrings with crisp gravity and minimal collision radius (calibrated to Doorman/Calico).",
                Category = "Accessory",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/keys_doorman.png",
                FallbackIconPath = "res://assets/at-icons/key.svg",
                Simulation = SimulationKind.Cloth,
                StiffnessRoot = 0.12f,
                StiffnessTip = 0.02f,
                GoalTaperPower = 1.50f,
                Damping = 0.05f,
                Gravity = 1.80f,
                CollisionRadiusTipInches = 0.4f,
                Friction = 0.05f,
                StrayRadiusInches = -1.0f,
                PlanarizeBackBarrier = false,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 75.0f,
                TwistRelax = 0.8f
            },
            new()
            {
                Id = RigidJiggleId,
                DisplayName = "Rigid Jiggle (Anatomy / Props)",
                HeroReference = "Soft-Tissue / Badges",
                ShortTag = "JiggleBone · 0× Grav · No Body Collision",
                Description = "Compact rotational spring bounce with zero gravity and zero body-capsule collision (ideal for bust, glutes, ears, holsters, pouches).",
                Category = "Accessory",
                ThumbnailPath = "res://assets/thumbnails/physics_preview/pouches_bebop.png",
                FallbackIconPath = "res://assets/at-icons/circle.svg",
                Simulation = SimulationKind.Jiggle,
                StiffnessRoot = 0.90f,
                StiffnessTip = 0.74f,
                GoalTaperPower = 1.00f,
                Damping = 0.55f,
                Gravity = 0.0f,
                CollisionRadiusTipInches = 0.5f,
                Friction = 0.0f,
                StrayRadiusInches = 2.0f,
                PlanarizeBackBarrier = false,
                OpenSheet = true,
                AnchorFreeRotate = false,
                AngleLimitDeg = 18.0f,
                TwistRelax = 1.0f
            }
        };

        private static readonly Dictionary<string, PhysicsPresetDef> ById;
        private static readonly Dictionary<string, Texture2D> HighDpiSvgCache = new(StringComparer.OrdinalIgnoreCase);

        static PhysicsPresetCatalog()
        {
            ById = new Dictionary<string, PhysicsPresetDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Presets) ById[p.Id] = p;
        }

        public static PhysicsPresetDef Find(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId)) return null;
            return ById.TryGetValue(presetId, out var p) ? p : null;
        }

        public static string GetDisplayLabel(string presetId)
        {
            var p = Find(presetId);
            return p != null ? $"{p.DisplayName} ({p.HeroReference})" : "Custom (Manual Tuning)";
        }

        /// <summary>
        /// Rasterizes SVG icons at 4x scale (96–128 px) instead of Godot's default 1x (16–24 px)
        /// so vector icons like <c>sliders.svg</c> stay razor-sharp inside preset cards.
        /// </summary>
        private static Texture2D LoadHighQualityIcon(string resPath)
        {
            if (string.IsNullOrEmpty(resPath)) return null;
            if (resPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                if (HighDpiSvgCache.TryGetValue(resPath, out var cached) && cached != null)
                    return cached;
                try
                {
                    if (Godot.FileAccess.FileExists(resPath))
                    {
                        byte[] bytes = Godot.FileAccess.GetFileAsBytes(resPath);
                        if (bytes != null && bytes.Length > 0)
                        {
                            var img = new Image();
                            if (img.LoadSvgFromBuffer(bytes, 4.0f) == Error.Ok && !img.IsEmpty())
                            {
                                var tex = ImageTexture.CreateFromImage(img);
                                HighDpiSvgCache[resPath] = tex;
                                return tex;
                            }
                        }
                    }
                }
                catch { /* Fall through to standard ResourceLoader */ }
            }
            return ResourceLoader.Exists(resPath) ? ResourceLoader.Load<Texture2D>(resPath) : null;
        }

        public static Texture2D LoadPresetTexture(PhysicsPresetDef preset)
        {
            if (preset == null)
                return LoadHighQualityIcon("res://assets/at-icons/sliders.svg");

            if (!string.IsNullOrEmpty(preset.ThumbnailPath) && ResourceLoader.Exists(preset.ThumbnailPath))
            {
                var customTex = ResourceLoader.Load<Texture2D>(preset.ThumbnailPath);
                if (customTex != null) return customTex;
            }
            if (!string.IsNullOrEmpty(preset.FallbackIconPath))
            {
                var fallback = LoadHighQualityIcon(preset.FallbackIconPath);
                if (fallback != null) return fallback;
            }
            return null;
        }

        /// <summary>
        /// Applies a Valve-backed preset to <paramref name="ch"/>, populating both raw physics fields
        /// and macro defaults so preview and Source 2 export stay 1:1 synchronized.
        /// </summary>
        public static void ApplyPresetToChain(BoneChainDef ch, string presetId, bool resetMacros = true)
        {
            if (ch == null) return;
            var preset = Find(presetId);
            if (preset == null)
            {
                ch.PresetId = CustomId;
                return;
            }

            ch.PresetId = preset.Id;
            ch.Simulation = preset.Simulation;
            ch.GoalTaperPower = preset.GoalTaperPower;
            ch.Friction = preset.Friction;
            ch.StrayRadiusInches = preset.StrayRadiusInches;
            ch.PlanarizeBackBarrier = preset.PlanarizeBackBarrier;
            ch.FloorCollision = preset.FloorCollision;
            ch.OpenSheet = preset.OpenSheet;
            ch.AnchorFreeRotate = preset.AnchorFreeRotate;
            ch.AngleLimitDeg = preset.AngleLimitDeg;
            ch.TwistRelax = preset.TwistRelax;
            ch.EnableRibbonExtrude = false;

            if (resetMacros)
            {
                ch.MacroLooseness = 0.50f;
                ch.MacroCollisionScale = 1.0f;
                ch.Gravity = preset.Gravity;
                ch.Damping = preset.Damping;
            }

            ApplyMacrosToChain(ch, ch.MacroLooseness, ch.Gravity, ch.Damping, ch.MacroCollisionScale);

            if (preset.PinRootBone && ch.SourceBoneNames != null && ch.SourceBoneNames.Count >= 2)
            {
                var rootOv = ch.GetOrCreateJointOverride(ch.SourceBoneNames[0]);
                rootOv.Simulate = false;
            }
        }

        /// <summary>
        /// Evaluates the 4 intuitive macro sliders (Looseness, Gravity, Damping, Collision Margin)
        /// against the chain's active preset baseline (or current raw values if Custom).
        /// </summary>
        public static void ApplyMacrosToChain(
            BoneChainDef ch,
            float looseness01,
            float gravity,
            float damping01,
            float collisionScale)
        {
            if (ch == null) return;
            ch.MacroLooseness = Mathf.Clamp(looseness01, 0f, 1f);
            ch.Gravity = Mathf.Clamp(gravity, 0f, 3.5f);
            ch.Damping = Mathf.Clamp(damping01, 0f, 1f);
            ch.MacroCollisionScale = Mathf.Clamp(collisionScale, 0.25f, 2.5f);

            var preset = Find(ch.PresetId);
            if (preset == null) return;

            // Looseness 0.50 = exact Valve preset baseline.
            // 0.00 = firmer / tighter hold; 1.00 = looser / more fluid flow.
            float delta = (ch.MacroLooseness - 0.50f) * 2.0f; // -1 (firm) .. +1 (loose)
            if (preset.Simulation == SimulationKind.Jiggle)
            {
                float stiffScale = delta < 0f ? Mathf.Lerp(1.0f, 1.15f, -delta) : Mathf.Lerp(1.0f, 0.55f, delta);
                ch.StiffnessRoot = Mathf.Clamp(preset.StiffnessRoot * stiffScale, 0.25f, 1.0f);
                ch.StiffnessTip = Mathf.Clamp(preset.StiffnessTip * stiffScale, 0.15f, ch.StiffnessRoot);
                ch.AngleLimitDeg = Mathf.Clamp(preset.AngleLimitDeg * (1.0f + delta * 0.45f), 6.0f, 55.0f);
            }
            else
            {
                float rootMul = delta < 0f ? Mathf.Lerp(1.0f, 1.35f, -delta) : Mathf.Lerp(1.0f, 0.45f, delta);
                float tipMul = delta < 0f ? Mathf.Lerp(1.0f, 2.20f, -delta) : Mathf.Lerp(1.0f, 0.15f, delta);
                ch.StiffnessRoot = Mathf.Clamp(preset.StiffnessRoot * rootMul, 0.04f, 0.98f);
                float baseTip = delta < 0f ? MathF.Max(preset.StiffnessTip, 0.08f) : preset.StiffnessTip;
                ch.StiffnessTip = Mathf.Clamp(baseTip * tipMul, 0.0f, ch.StiffnessRoot);
                ch.GoalTaperPower = Mathf.Clamp(preset.GoalTaperPower * (1.0f + delta * 0.35f), 0.35f, 3.80f);
            }

            ch.CollisionRadiusTipInches = Mathf.Clamp(preset.CollisionRadiusTipInches * ch.MacroCollisionScale, 0.1f, 6.0f);
        }
    }
}

