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
        public const float StandardGravityMetersPerSecSq = 360.0f * 0.0254f; // 9.144 m/s² (exact Source 2 360 in/s²)

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
        /// Valve's proven vanilla baseline across paige, silver, lash, shiv, necro, haze, yamato, chrono, unicorn, geist
        /// (10/11 heroes use ExtraIterations = 0; 9/11 use RodVelocitySmoothRate = 0.0, RodVelocitySmoothIterations = 0).
        /// </summary>
        public static readonly GlobalClothParams ValveBaselineClothParams = new(
            extraIterations: 0,
            defaultGravityScale: 1.0f,
            defaultVelAirDrag: 0.0f,
            defaultExpAirDrag: 0.0f,
            velocitySmoothRate: 0.0f,
            velocitySmoothIterations: 0,
            quadBendTolerance: 0.05f,
            localForce: 1.0f,
            localRotation: 0.0f,
            localDrag1: 0.0f);

        /// <summary>
        /// Exact Source 2 Rubikon <c>FeModel.NodeIntegrator.AnimationVertexAttraction</c> (<c>va</c>) formula
        /// verified against VRF 21.1 <c>ClothReconstruction.Goals.cs</c>:
        /// <c>va = 1 - ((1 - fa) / (sqrt((1 - fa) * fa + d^2) + d))^2 * fa</c>.
        /// </summary>
        public static float ComputeAnimationVertexAttraction(float forceAttraction, float goalDamping)
        {
            float fa = Mathf.Clamp(forceAttraction, 0f, 1f);
            float d = Mathf.Clamp(goalDamping, 0f, 1f);
            if (fa >= 0.9999f) return 1.0f;
            if (fa < 0.0001f) return d;
            if (d <= 1e-6f) return fa;
            float denom = MathF.Sqrt((1.0f - fa) * fa + d * d) + d;
            if (denom <= 1e-6f) return 1.0f;
            float r = (1.0f - fa) / denom;
            return Mathf.Clamp(1.0f - r * r * fa, fa, 1.0f);
        }

        /// <summary>
        /// Resolved per-vertex / per-node parameters for Source 2 <c>ProxyCloth</c> DMX and <c>ClothChain</c>.
        /// </summary>
        public readonly struct ResolvedClothNodeParams
        {
            /// <summary>Effective compiled <c>FeModel.AnimationForceAttraction</c> (0.000 .. 0.900).</summary>
            public readonly float GoalForce;
            /// <summary>Effective compiled <c>FeModel.AnimationVertexAttraction</c> computed via exact Source 2 formula.</summary>
            public readonly float GoalVertexAttraction;
            /// <summary>Cube-root compensated <c>cloth_goal_strength_v2$0</c> / <c>goal_strength</c> input to CSDK13.</summary>
            public readonly float GoalStrengthInput;
            /// <summary><c>cloth_goal_damping$0</c> / <c>goal_damping</c> scaled proportionally to <c>GoalForce</c> for spike-free Valve damping.</summary>
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
                float goalVertexAttraction,
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
                GoalVertexAttraction = goalVertexAttraction;
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
            public readonly float TipMass;
            public readonly float Omega;
            public readonly float DampingRatio;

            public ResolvedJiggleNodeParams(
                float stiffness,
                float alongStiffness,
                float damping,
                float angleLimitDeg,
                float tipMass = 0f,
                float omega = 14f,
                float dampingRatio = 0.55f)
            {
                Stiffness = stiffness;
                AlongStiffness = alongStiffness;
                Damping = damping;
                AngleLimitDeg = angleLimitDeg;
                TipMass = tipMass;
                Omega = omega;
                DampingRatio = dampingRatio;
            }
        }

        public readonly struct ResolvedPreviewBoneParams
        {
            public readonly float SpringK;
            public readonly float DampK;
            public readonly float GravityMetersPerSecSq;
            public readonly float CollisionRadiusMeters;
            public readonly float StrayRadiusMeters;
            /// <summary>Exact Source 2 <c>FeModel.AnimationForceAttraction</c> (0.000..0.900) for Verlet PBD cloth.</summary>
            public readonly float GoalForce;
            /// <summary>Exact Source 2 <c>FeModel.AnimationVertexAttraction</c> computed from <c>GoalForce</c> &amp; <c>GoalDamping</c>.</summary>
            public readonly float GoalVertexAttraction;
            /// <summary>Per-bone <c>goal_damping</c> (0.000..0.500) honored in both export and preview.</summary>
            public readonly float GoalDamping;
            /// <summary>Normalized UI Damping slider (0..1) for perceptible preview damping.</summary>
            public readonly float Damping01;
            /// <summary>Exact Source 2 <c>FeModel.NodeIntegrator.PointDamping</c> (0.000..0.600) for Verlet PBD cloth.</summary>
            public readonly float PointDamping;
            /// <summary>Exact Source 2 <c>cloth_bend_stiffness$0</c> (0.05..1.00) for 2-hop bend constraints.</summary>
            public readonly float BendStiffness;
            /// <summary>Exact Source 2 <c>cloth_friction$0</c> (0.00..1.00) for tangential capsule friction.</summary>
            public readonly float Friction;
            /// <summary>Semi-implicit JiggleBone angular frequency ω (rad/s) derived from <see cref="ResolveJiggleNode"/>.</summary>
            public readonly float JiggleOmega;
            /// <summary>Semi-implicit JiggleBone damping ratio ζ derived from <see cref="ResolveJiggleNode"/>.</summary>
            public readonly float JiggleDampRatio;
            /// <summary>Hard JiggleBone cone limit in degrees from <see cref="ResolveJiggleNode"/>.</summary>
            public readonly float JiggleAngleLimitDeg;

            public ResolvedPreviewBoneParams(
                float springK,
                float dampK,
                float gravityMetersPerSecSq,
                float collisionRadiusMeters,
                float strayRadiusMeters,
                float goalForce,
                float goalVertexAttraction,
                float goalDamping,
                float damping01,
                float pointDamping,
                float bendStiffness,
                float friction,
                float jiggleOmega,
                float jiggleDampRatio,
                float jiggleAngleLimitDeg)
            {
                SpringK = springK;
                DampK = dampK;
                GravityMetersPerSecSq = gravityMetersPerSecSq;
                CollisionRadiusMeters = collisionRadiusMeters;
                StrayRadiusMeters = strayRadiusMeters;
                GoalForce = goalForce;
                GoalVertexAttraction = goalVertexAttraction;
                GoalDamping = goalDamping;
                Damping01 = damping01;
                PointDamping = pointDamping;
                BendStiffness = bendStiffness;
                Friction = friction;
                JiggleOmega = jiggleOmega;
                JiggleDampRatio = jiggleDampRatio;
                JiggleAngleLimitDeg = jiggleAngleLimitDeg;
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
        /// Maps UI <c>Damping</c> (0..1) to per-node DMX <c>cloth_drag$0</c> (0.000..0.020).
        /// CSDK13 multiplies <c>cloth_drag$0</c> by 30.0 into compiled <c>FeModel.NodeIntegrator.PointDamping</c>
        /// (0.000..0.600; Valve vanilla defaults sit near 0.000..0.050 and rely on <c>goal_damping</c> for spring settle).
        /// </summary>
        public static float ResolveClothDrag(float damping01)
        {
            float d = Mathf.Clamp(damping01, 0f, 1f);
            return MathF.Pow(d, 1.5f) * 0.020f;
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

            // Valve-matched configurable goal taper (e.g., 0.65 for Geist's structured dress, 2.6 for Yamato's kimono, 1.85 default).
            float rootForce = ResolveRootGoalForce(sRoot);
            float tipForce = ResolveTipGoalForce(sTip, rootForce);
            float taperPower = Mathf.Clamp(ch?.GoalTaperPower ?? 1.85f, 0.30f, 4.0f);
            float taper = MathF.Pow(1.0f - t, taperPower);
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

            // Valve-matched goal_damping scaled proportionally to goalForce (d = fa is exact critical damping in Rubikon):
            // produces crisp, bounce-controlled settle on structured hair without ever causing small-goalForce tip spikes!
            float goalDamp;
            if (isPinned)
            {
                goalDamp = 0.0f;
            }
            else if (jov?.GoalDamping.HasValue == true)
            {
                goalDamp = Mathf.Clamp(jov.GoalDamping.Value, 0f, 0.50f);
            }
            else
            {
                goalDamp = goalForce > 0.005f ? Mathf.Clamp(damp01 * goalForce * 0.85f, 0f, 0.50f) : 0.0f;
            }
            float goalVertexAttraction = isPinned ? 1.0f : ComputeAnimationVertexAttraction(goalForce, goalDamp);
            float drag = isPinned ? 0.0f : ResolveClothDrag(damp01);

            float bend = Mathf.Clamp(Mathf.Lerp(sRoot, sTip, t), 0.05f, 1.0f);

            // Valve-matched root->tip collision radius taper: small near root (0.25..0.50 in) so root doesn't
            // fight its own anchor capsule, expanding to colTip toward the hem/tip.
            float colRoot = MathF.Min(0.45f, colTip * 0.45f);
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
                goalVertexAttraction: goalVertexAttraction,
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
        /// Calibrates <c>Damping</c> directly to physical critical damping (<c>2 * zeta * sqrt(stiffness)</c>)
        /// so UI Damping (0..1) maps identically to Source 2's symplectic Euler <c>FeJiggleBone</c> runtime
        /// and Godot's live preview without low-damping ringing or 3x preview over-damping.
        /// </summary>
        public static ResolvedJiggleNodeParams ResolveJiggleNode(BoneChainDef ch, float t, int chainBoneCount)
        {
            t = Mathf.Clamp(t, 0f, 1f);
            float s = Mathf.Lerp(Mathf.Clamp(ch?.StiffnessRoot ?? 0.85f, 0f, 1f),
                                 Mathf.Clamp(ch?.StiffnessTip ?? 0.65f, 0f, 1f), t);
            float stiff = Mathf.Clamp(s * 420f, 20f, 800f);
            float omega = MathF.Sqrt(stiff);
            float damp01 = Mathf.Clamp(ch?.Damping ?? 0.50f, 0f, 1f);
            float zeta = Mathf.Lerp(0.18f, 1.05f, damp01);
            float damp = Mathf.Clamp(2.0f * zeta * omega, 1.5f, 45.0f);
            float baseAngle = Mathf.Clamp((ch?.AngleLimitDeg ?? 22f) > 1f ? ch.AngleLimitDeg : 22f, 6f, 60f);
            float angle = chainBoneCount > 1
                ? Mathf.Clamp(baseAngle / MathF.Sqrt(chainBoneCount), 6f, 45f)
                : baseAngle;
            float grav = Mathf.Clamp(ch?.Gravity ?? 0f, 0f, 4f);
            float tipMass = grav <= 0.01f ? 0.0f : grav;
            return new ResolvedJiggleNodeParams(
                stiffness: stiff,
                alongStiffness: Mathf.Clamp(stiff * 0.85f, 20f, 800f),
                damping: damp,
                angleLimitDeg: angle,
                tipMass: tipMass,
                omega: omega,
                dampingRatio: zeta);
        }

        /// <summary>
        /// Resolves Godot live preview (<c>CustomJiggleBoneDriver</c>) parameters directly from
        /// Source 2 <c>FeModel</c> / <c>FeJiggleBone</c> semantics so the 60 Hz Verlet PBD cloth
        /// solver and Source 2-matched Jiggle solver closely match in-game Source 2 behavior.
        /// </summary>
        public static ResolvedPreviewBoneParams ResolvePreviewBone(
            BoneChainDef ch,
            float t,
            BoneJointOverride jov = null,
            int chainBoneCount = 1)
        {
            const float InchesToMeters = 0.0254f;
            var cloth = ResolveClothNode(ch, t, isPinned: false, jov: jov);
            var jig = ResolveJiggleNode(ch, t, Math.Max(1, chainBoneCount));

            bool isJiggle = ch?.Simulation == SimulationKind.Jiggle;
            float pointDamping = ResolveCompiledPointDamping(ch?.Damping ?? 0.25f);

            // Exact 1:1 frequency and damping ratio with Source 2 FeJiggleBone (ResolveJiggleNode)
            float jiggleOmega = jig.Omega;
            float jiggleDampRatio = jig.DampingRatio;

            float springK = isJiggle ? jig.Stiffness : (cloth.GoalForce * 220f);
            float dampK = isJiggle ? jig.Damping : pointDamping;

            // Source 2 FeJiggleBone applies -TipMass (in/s^2) on Z when tip_mass > 0; honor Gravity honestly in preview.
            float gravMeters = isJiggle
                ? (jig.TipMass * InchesToMeters)
                : (StandardGravityMetersPerSecSq * cloth.GravityScale);
            float colMeters = MathF.Max(0.005f, cloth.CollisionRadiusInches * InchesToMeters);
            float strayMeters = cloth.StrayRadiusInches < 0f ? -1.0f : cloth.StrayRadiusInches * InchesToMeters;

            return new ResolvedPreviewBoneParams(
                springK: springK,
                dampK: dampK,
                gravityMetersPerSecSq: gravMeters,
                collisionRadiusMeters: colMeters,
                strayRadiusMeters: strayMeters,
                goalForce: cloth.GoalForce,
                goalVertexAttraction: cloth.GoalVertexAttraction,
                goalDamping: cloth.GoalDamping,
                damping01: Mathf.Clamp(ch?.Damping ?? 0.25f, 0f, 1f),
                pointDamping: pointDamping,
                bendStiffness: cloth.BendStiffness,
                friction: cloth.Friction,
                jiggleOmega: jiggleOmega,
                jiggleDampRatio: jiggleDampRatio,
                jiggleAngleLimitDeg: jig.AngleLimitDeg);
        }
    }
}

