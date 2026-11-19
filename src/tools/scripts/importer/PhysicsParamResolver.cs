using System;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Single source of truth translating UI physics sliders (<see cref="BoneChainDef"/> and
    /// <see cref="BoneJointOverride"/>) into exact Source 2 <c>FeModel</c> export parameters
    /// (<c>ProxyCloth</c>, <c>ClothChain</c>, <c>JiggleBone</c>), Valve-matched global <c>ClothParams</c>,
    /// and Godot live preview parameters (<c>CustomJiggleBoneDriver</c>).
    ///
    /// Calibrated directly against Valve's compiled Deadlock <c>FeModel</c> benchmarks
    /// (yamato, chrono, wraith, hornet, unicorn, abrams, fencer, geist).
    /// </summary>
    public static class PhysicsParamResolver
    {
        public const float StandardGravityInchesPerSecSq = 360.0f;
        public const float StandardGravityMetersPerSecSq = 9.81f;

        /// <summary>
        /// Valve 8/8 hero baseline for global <c>ClothParams</c> (verified via <c>tools/fe_model_dump</c>).
        /// </summary>
        public readonly struct GlobalClothParams
        {
            public readonly int ExtraIterations;
            public readonly float DefaultGravityScale;
            public readonly float DefaultVelAirDrag;
            public readonly float DefaultExpAirDrag;
            public readonly float VelocitySmoothRate;
            public readonly int VelocitySmoothIterations;
            public readonly float QuadBendTolerance;
            public readonly float LocalForce;
            public readonly float LocalRotation;
            public readonly float LocalDrag1;

            public GlobalClothParams(
                int extraIterations,
                float defaultGravityScale,
                float defaultVelAirDrag,
                float defaultExpAirDrag,
                float velocitySmoothRate,
                int velocitySmoothIterations,
                float quadBendTolerance,
                float localForce,
                float localRotation,
                float localDrag1)
            {
                ExtraIterations = extraIterations;
                DefaultGravityScale = defaultGravityScale;
                DefaultVelAirDrag = defaultVelAirDrag;
                DefaultExpAirDrag = defaultExpAirDrag;
                VelocitySmoothRate = velocitySmoothRate;
                VelocitySmoothIterations = velocitySmoothIterations;
                QuadBendTolerance = quadBendTolerance;
                LocalForce = localForce;
                LocalRotation = localRotation;
                LocalDrag1 = localDrag1;
            }
        }

        /// <summary>
        /// Valve's proven vanilla baseline across yamato, chrono, wraith, hornet, unicorn, abrams, fencer, geist.
        /// </summary>
        public static readonly GlobalClothParams ValveBaselineClothParams = new(
            extraIterations: 1,
            defaultGravityScale: 1.0f,
            defaultVelAirDrag: 0.0f,
            defaultExpAirDrag: 0.0f,
            velocitySmoothRate: 0.10f,
            velocitySmoothIterations: 1,
            quadBendTolerance: 0.05f,
            localForce: 1.0f,
            localRotation: 0.0f,
            localDrag1: 0.0f);

        /// <summary>
        /// Resolved per-vertex / per-node parameters for Source 2 <c>ProxyCloth</c> DMX and <c>ClothChain</c>.
        /// </summary>
        public readonly struct ResolvedClothNodeParams
        {
            /// <summary>Effective compiled <c>FeModel.AnimationForceAttraction</c> (0.000 .. 0.900).</summary>
            public readonly float GoalForce;
            /// <summary>Cube-root compensated <c>cloth_goal_strength_v2$0</c> / <c>goal_strength</c> input to CSDK13.</summary>
            public readonly float GoalStrengthInput;
            /// <summary><c>cloth_goal_damping$0</c> / <c>goal_damping</c> (0.0 by default to prevent CSDK13 tip attraction spikes).</summary>
            public readonly float GoalDamping;
            /// <summary>Per-node air drag (<c>cloth_drag$0</c> / <c>drag</c>), driven cleanly by the UI Damping slider.</summary>
            public readonly float Drag;
            /// <summary>Bend stiffness (<c>cloth_bend_stiffness$0</c>).</summary>
            public readonly float BendStiffness;
            /// <summary>Per-node collision radius in Source inches (<c>cloth_collision_radius$0</c>).</summary>
            public readonly float CollisionRadiusInches;
            /// <summary>Per-node gravity in Source inches/s² (<c>cloth_gravity$0</c>).</summary>
            public readonly float GravityInchesPerSecSq;
            /// <summary>Normalized gravity scale for <c>ClothChain</c> (<c>gravity_z</c>).</summary>
            public readonly float GravityScale;
            /// <summary>Stray radius in inches; &lt; 0 means disabled / omitted from DMX.</summary>
            public readonly float StrayRadiusInches;
            /// <summary>Surface sliding friction (<c>cloth_friction$0</c>).</summary>
            public readonly float Friction;
            /// <summary>Free anchor hinge weight (<c>cloth_anchor_free_rotate$0</c>).</summary>
            public readonly float AnchorFreeRotate;
            /// <summary>Torsion relaxation for <c>ClothChain</c>.</summary>
            public readonly float Torsion;

            public ResolvedClothNodeParams(
                float goalForce,
                float goalStrengthInput,
                float goalDamping,
                float drag,
                float bendStiffness,
                float collisionRadiusInches,
                float gravityInchesPerSecSq,
                float gravityScale,
                float strayRadiusInches,
                float friction,
                float anchorFreeRotate,
                float torsion)
            {
                GoalForce = goalForce;
                GoalStrengthInput = goalStrengthInput;
                GoalDamping = goalDamping;
                Drag = drag;
                BendStiffness = bendStiffness;
                CollisionRadiusInches = collisionRadiusInches;
                GravityInchesPerSecSq = gravityInchesPerSecSq;
                GravityScale = gravityScale;
                StrayRadiusInches = strayRadiusInches;
                Friction = friction;
                AnchorFreeRotate = anchorFreeRotate;
                Torsion = torsion;
            }
        }

        public readonly struct ResolvedJiggleNodeParams
        {
            public readonly float Stiffness;
            public readonly float AlongStiffness;
            public readonly float Damping;
            public readonly float AngleLimitDeg;

            public ResolvedJiggleNodeParams(float stiffness, float alongStiffness, float damping, float angleLimitDeg)
            {
                Stiffness = stiffness;
                AlongStiffness = alongStiffness;
                Damping = damping;
                AngleLimitDeg = angleLimitDeg;
            }
        }

        public readonly struct ResolvedPreviewBoneParams
        {
            public readonly float SpringK;
            public readonly float DampK;
            public readonly float GravityMetersPerSecSq;
            public readonly float CollisionRadiusMeters;
            public readonly float StrayRadiusMeters;

            public ResolvedPreviewBoneParams(
                float springK,
                float dampK,
                float gravityMetersPerSecSq,
                float collisionRadiusMeters,
                float strayRadiusMeters)
            {
                SpringK = springK;
                DampK = dampK;
                GravityMetersPerSecSq = gravityMetersPerSecSq;
                CollisionRadiusMeters = collisionRadiusMeters;
                StrayRadiusMeters = strayRadiusMeters;
            }
        }

        /// <summary>
        /// Maps UI <c>StiffnessRoot</c> (0..1) directly to root <c>FeModel.AnimationForceAttraction</c> (0.000..0.900).
        /// Monotonic, unclamped to Valve's full range.
        /// </summary>
        public static float ResolveRootGoalForce(float stiffnessRoot01)
        {
            float s = Mathf.Clamp(stiffnessRoot01, 0f, 1f);
            return MathF.Pow(s, 1.6f) * 0.90f;
        }

        /// <summary>
        /// Maps UI <c>StiffnessTip</c> (0..1) directly to tip <c>FeModel.AnimationForceAttraction</c> (0.000..0.500),
        /// clamped to &lt;= rootGoalForce. Allows true <c>0.000</c> tip pull matching chrono, wraith, and yamato.
        /// </summary>
        public static float ResolveTipGoalForce(float stiffnessTip01, float rootGoalForce)
        {
            float s = Mathf.Clamp(stiffnessTip01, 0f, 1f);
            float tip = MathF.Pow(s, 2.0f) * 0.50f;
            return MathF.Min(tip, rootGoalForce);
        }

        /// <summary>
        /// Maps UI <c>Damping</c> (0..1) to per-node DMX <c>cloth_drag$0</c> (0.000..0.025).
        /// CSDK13 multiplies <c>cloth_drag$0</c> by 30.0 into compiled <c>FeModel.NodeIntegrator.PointDamping</c>
        /// (0.000..0.750; Valve vanilla heroes use 0.000), preventing underwater molasses lag on skirts/hair.
        /// </summary>
        public static float ResolveClothDrag(float damping01)
        {
            float d = Mathf.Clamp(damping01, 0f, 1f);
            return MathF.Pow(d, 1.5f) * 0.025f;
        }

        /// <summary>
        /// Returns the exact compiled Source 2 <c>FeModel.NodeIntegrator.PointDamping</c> (<c>cloth_drag$0 * 30</c>).
        /// </summary>
        public static float ResolveCompiledPointDamping(float damping01)
        {
            return ResolveClothDrag(damping01) * 30.0f;
        }

        /// <summary>
        /// Resolves all Source 2 cloth node parameters for a given chain, normalized depth <paramref name="t"/>
        /// (0 = root, 1 = tip), pinned flag, and optional per-joint override.
        /// </summary>
        public static ResolvedClothNodeParams ResolveClothNode(
            BoneChainDef ch,
            float t,
            bool isPinned,
            BoneJointOverride jov = null)
        {
            t = Mathf.Clamp(t, 0f, 1f);
            float sRoot = Mathf.Clamp(ch?.StiffnessRoot ?? 0.55f, 0f, 1f);
            float sTip = Mathf.Clamp(ch?.StiffnessTip ?? 0.05f, 0f, 1f);
            float damp01 = Mathf.Clamp(ch?.Damping ?? 0.25f, 0f, 1f);
            float gravScale = Mathf.Clamp(ch?.Gravity ?? 1.0f, 0f, 4.0f);
            float colTip = Mathf.Clamp(ch?.CollisionRadiusTipInches ?? 2.0f, 0f, 8.0f);
            float friction = Mathf.Clamp(ch?.Friction ?? 0.15f, 0f, 1.0f);
            float torsion = Mathf.Clamp(ch?.TwistRelax ?? 1.0f, 0f, 2.0f);
            float freeRot = (ch?.AnchorFreeRotate == true) ? 1.0f : 0.0f;

            // Valve-matched exponential goal taper: drops quickly past root so mid/tip drape naturally under gravity.
            float rootForce = ResolveRootGoalForce(sRoot);
            float tipForce = ResolveTipGoalForce(sTip, rootForce);
            float taper = MathF.Pow(1.0f - t, 1.85f);
            float goalForce;
            float goalInput;
            if (isPinned)
            {
                goalForce = 1.0f;
                goalInput = 1.0f;
            }
            else if (jov?.GoalStrength.HasValue == true)
            {
                goalInput = Mathf.Clamp(jov.GoalStrength.Value, 0f, 0.99f);
                goalForce = goalInput * goalInput * goalInput;
            }
            else
            {
                goalForce = Mathf.Lerp(tipForce, rootForce, taper);
                goalInput = goalForce > 1e-6f ? MathF.Cbrt(Mathf.Clamp(goalForce, 0f, 1f)) : 0f;
            }

            // Keep goal_damping = 0 by default (CSDK13 computes AnimationVertexAttraction = goal_damping / sqrt(goal_strength))
            // and route the UI Damping slider cleanly into per-node cloth_drag$0 instead.
            float goalDamp = (isPinned || jov?.GoalDamping.HasValue != true)
                ? 0.0f
                : Mathf.Clamp(jov.GoalDamping.Value, 0f, 0.50f);
            float drag = isPinned ? 0.0f : ResolveClothDrag(damp01);

            float bend = Mathf.Clamp(Mathf.Lerp(sRoot, sTip, t), 0.05f, 1.0f);

            // Valve-matched root->tip collision radius taper: small near root (0.25..0.50 in) so root doesn't
            // fight its own anchor capsule, expanding to colTip toward the hem/tip.
            float colRoot = MathF.Min(0.45f, colTip * 0.35f);
            float colRad = isPinned
                ? 0.0f
                : (jov?.CollisionRadiusInches.HasValue == true
                    ? Mathf.Clamp(jov.CollisionRadiusInches.Value, 0f, 8f)
                    : Mathf.Lerp(colRoot, colTip, t));

            float effStray = jov?.StrayRadiusInches.HasValue == true ? jov.StrayRadiusInches.Value : (ch?.StrayRadiusInches ?? -1f);
            float strayRad;
            if (isPinned)
            {
                strayRad = 0.0f;
            }
            else if (effStray < 0f)
            {
                strayRad = -1.0f;
            }
            else
            {
                float strayMax = Mathf.Clamp(effStray, 0.5f, 240f);
                float strayMin = MathF.Min(strayMax, MathF.Max(0.5f, strayMax * 0.25f));
                strayRad = Mathf.Lerp(strayMin, strayMax, t);
            }

            return new ResolvedClothNodeParams(
                goalForce: goalForce,
                goalStrengthInput: goalInput,
                goalDamping: goalDamp,
                drag: drag,
                bendStiffness: bend,
                collisionRadiusInches: colRad,
                gravityInchesPerSecSq: StandardGravityInchesPerSecSq * gravScale,
                gravityScale: gravScale,
                strayRadiusInches: strayRad,
                friction: friction,
                anchorFreeRotate: freeRot,
                torsion: torsion);
        }

        /// <summary>
        /// Resolves Source 2 <c>JiggleBone</c> parameters for rigid bounce chains (glutes, bust, props).
        /// </summary>
        public static ResolvedJiggleNodeParams ResolveJiggleNode(BoneChainDef ch, float t, int chainBoneCount)
        {
            t = Mathf.Clamp(t, 0f, 1f);
            float s = Mathf.Lerp(Mathf.Clamp(ch?.StiffnessRoot ?? 0.85f, 0f, 1f),
                                 Mathf.Clamp(ch?.StiffnessTip ?? 0.65f, 0f, 1f), t);
            float stiff = Mathf.Clamp(s * 420f, 20f, 800f);
            float damp = Mathf.Clamp((ch?.Damping ?? 0.50f) * 14f, 1.0f, 30f);
            float baseAngle = Mathf.Clamp((ch?.AngleLimitDeg ?? 22f) > 1f ? ch.AngleLimitDeg : 22f, 6f, 60f);
            float angle = chainBoneCount > 1
                ? Mathf.Clamp(baseAngle / MathF.Sqrt(chainBoneCount), 6f, 45f)
                : baseAngle;
            return new ResolvedJiggleNodeParams(stiff, Mathf.Clamp(stiff * 0.85f, 20f, 800f), damp, angle);
        }

        /// <summary>
        /// Resolves Godot live preview (<c>CustomJiggleBoneDriver</c>) spring/damping/gravity parameters
        /// so <c>Stiffness = 0</c> hangs freely under true gravity (<c>9.81 * g</c>) and moving any slider
        /// produces a visible, monotonic change matching Source 2 export semantics.
        /// </summary>
        public static ResolvedPreviewBoneParams ResolvePreviewBone(
            BoneChainDef ch,
            float t,
            BoneJointOverride jov = null)
        {
            const float InchesToMeters = 0.0254f;
            var cloth = ResolveClothNode(ch, t, isPinned: false, jov: jov);

            bool isJiggle = ch?.Simulation == SimulationKind.Jiggle;
            // For Cloth / ProxyCloth: gentle structural drape floor (14f) keeps multi-strand sheets
            // from collapsing flat into a vertical needle (since preview lacks FeModel's 2900+ stretch rods),
            // while still responding visibly to both Stiffness and Gravity sliders.
            float springK = isJiggle
                ? (45f + Mathf.Lerp(ch.StiffnessRoot, ch.StiffnessTip, t) * 240f)
                : (14f + cloth.GoalForce * 220f);
            float dampK = isJiggle
                ? (5.0f + Mathf.Clamp(ch.Damping, 0f, 1f) * 20f)
                : (3.0f + Mathf.Clamp(ch?.Damping ?? 0.25f, 0f, 1f) * 16f);

            // Source 2 FeJiggleBone has ZERO gravity — match that in preview so bust/buttocks don't droop!
            float gravMeters = isJiggle ? 0.0f : (StandardGravityMetersPerSecSq * cloth.GravityScale);
            float colMeters = MathF.Max(0.005f, cloth.CollisionRadiusInches * InchesToMeters);
            float strayMeters = cloth.StrayRadiusInches < 0f ? -1.0f : cloth.StrayRadiusInches * InchesToMeters;

            return new ResolvedPreviewBoneParams(springK, dampK, gravMeters, colMeters, strayMeters);
        }
    }
}

