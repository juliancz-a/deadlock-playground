using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Central resolver for Deadlock hero submesh quirks, material overrides, visibility rules,
/// and selective Toon outline eligibility matching Source2Viewer and Deadlock in-game rendering.
/// Delegates hero-specific material configurations to modular IHeroMaterialConfig implementations.
/// </summary>
public static class DeadlockMaterialResolver
{
    /// <summary>
    /// Evaluates if a given mesh surface is eligible for the inverted hull outline pass.
    /// Excludes eyes, mouth interior, teeth, decals, glasses/lenses, fur layers, flames,
    /// sparkles, jitter meshes, and additive VFX to prevent facial collapse and visual artifacts.
    /// </summary>
    public static bool ShouldApplyOutline(string meshName, string materialPath, bool isAdditive, bool isTranslucent)
    {
        if (isAdditive || isTranslucent) return false;

        string mLower = meshName?.ToLowerInvariant() ?? "";
        string matLower = materialPath?.ToLowerInvariant() ?? "";

        // 0. Sparkles and billboard quads (Lash sparkles)
        if (mLower.Contains("sparkle") || matLower.Contains("sparkle") ||
            mLower.Contains("lash_sparkles") || matLower.Contains("lash_sparkles")) return false;

        // 1. Existing built-in outline meshes
        if (mLower.Contains("outline") || matLower.Contains("outline")) return false;

        // 2. Jitter and boundary layers (Billy / punkgoat, etc. F_DISABLE_NPR_OUTLINE = 1)
        if (mLower.Contains("punkgoat") || matLower.Contains("punkgoat") ||
            mLower.Contains("jitter") || matLower.Contains("jitter")) return false;

        // 3. Eyes, pupils, corneas, irises, sclera
        if (mLower.Contains("eye") || mLower.Contains("pupil") || mLower.Contains("cornea") ||
            mLower.Contains("iris") || mLower.Contains("sclera") ||
            mLower.Contains("ivy_eyes") || matLower.Contains("ivy_eyes") ||
            matLower.Contains("eye") || matLower.Contains("cornea") ||
            matLower.Contains("iris") || matLower.Contains("sclera") ||
            matLower.Contains("tengu_eye") || matLower.Contains("gargoyle_eye") ||
            (matLower.Contains("ivy_head") && (mLower.Contains("eye") || mLower.Contains("pupil")))) return false;

        // 4. Mouth interior, teeth, tongue, facial decals
        if (mLower.Contains("teeth") || mLower.Contains("mouth") || mLower.Contains("tongue") ||
            matLower.Contains("teeth") || matLower.Contains("mouth") || matLower.Contains("tongue") ||
            matLower.Contains("decal") || mLower.Contains("decal") || mLower.Contains("patch")) return false;

        // 5. Glasses, lenses, spectacles, translucent glass
        if (mLower.Contains("glass") || mLower.Contains("lens") || mLower.Contains("spectacle") ||
            matLower.Contains("glass") || matLower.Contains("lens") || matLower.Contains("spectacle")) return false;

        // 6. Hair and fur layers (Lady Geist shawl fur01-fur05, etc.)
        if (mLower.Contains("fur") || matLower.Contains("fur") || mLower.Contains("shawl_fur")) return false;

        // 7. Flame, fire, volumetric particle VFX, sparkles and hero outline shells (Infernus armglow/flames, Lash sparkles, Celeste hornglow, etc.)
        if (mLower.Contains("flame") || matLower.Contains("flame") ||
            mLower.Contains("sparkle") || matLower.Contains("sparkle") ||
            mLower.Contains("sparkles") || matLower.Contains("sparkles") ||
            mLower.Contains("lash_sparkles") || matLower.Contains("lash_sparkles") ||
            mLower.Contains("billboard") || matLower.Contains("billboard") ||
            mLower.Contains("particle") || matLower.Contains("particle") ||
            mLower.Contains("quad") || matLower.Contains("quad") ||
            mLower.Contains("armglow") || matLower.Contains("armglow") ||
            mLower.Contains("hornglow") || matLower.Contains("keyglow") ||
            matLower.Contains("armglow") || matLower.Contains("headglow") || matLower.Contains("keyglow") || matLower.Contains("hornglow") ||
            (matLower.Contains("glow") && (matLower.Contains("inferno") || matLower.Contains("vindicta") || matLower.Contains("fx") || matLower.Contains("vfx")))) return false;

        // 8. Glowing internal cores & hourglass (Paradox headhourglass, etc.) matching Valve's F_DISABLE_NPR_OUTLINE=1
        if (mLower.Contains("hourglass") || matLower.Contains("hourglass")) return false;

        // 9. Viscous interior organs and core
        if (matLower.Contains("viscous_swatches") || matLower.Contains("viscous_glass")) return false;

        // 10. Static UI / debug / expression meshes
        if (mLower.Contains("static_ui") || mLower.Contains("ui_") || mLower.Contains("head_ui") ||
            matLower.Contains("vertcolor_pbr_basic") || matLower.Contains("materials/dev/")) return false;

        // 11. Cards / psychic energy playing cards (Wraith cards)
        if (mLower.Contains("card") || matLower.Contains("card")) return false;

        return true;
    }

    /// <summary>
    /// Configures initial visibility for dynamic, debug, or secondary submeshes upon model instantiation.
    /// </summary>
    public static void ApplyInitialSubmeshVisibility(Node3D heroScene, string heroName)
    {
        if (heroScene == null) return;
        string hLower = heroName?.ToLowerInvariant() ?? "";

        ApplySubmeshVisibilityRecursively(heroScene, hLower);
    }

    private static void ApplySubmeshVisibilityRecursively(Node node, string heroLower)
    {
        if (node is MeshInstance3D mi)
        {
            string mName = mi.Name.ToString().TrimStart('.', '_').ToLowerInvariant();

            // Built-in inverted hulls (e.g. Viscous bodyoutline): hidden by default
            if (mName.Equals("bodyoutline") || mName.Equals("outline"))
            {
                mi.Visible = false;
            }

            // Billy / punkgoat: Ensure main model and primary jitter meshes are visible
            if (heroLower.Contains("punkgoat") || heroLower.Contains("billy"))
            {
                if (mName.Contains("jitter02"))
                {
                    mi.Visible = false;
                }
                else if (mName.Contains("jitter") || mName.Equals("punkgoat_model"))
                {
                    mi.Visible = true;
                }
            }

            // Rem: hide static UI/debug meshes by default
            if (heroLower.Contains("familiar") || heroLower.Contains("rem"))
            {
                if (mName.Contains("static_ui_shirt") || mName.StartsWith("8inf_"))
                {
                    mi.Visible = false;
                }
            }

            // Doorman: keep dynamic ability door meshes visible so the user can inspect the door in poses
            if (heroLower.Contains("doorman"))
            {
                // Default: show door mesh (parry_door / reload_door are visible)
                if (mName.Contains("parry_door"))
                {
                    mi.Visible = true;
                }
            }

            // Infernus: ensure flame hair and flask on hip are visible, hide ability flame bursts and hand-held flasks
            if (heroLower.Contains("inferno"))
            {
                if (mName.Contains("flame_hair") || mName.Contains("headglow") || (mName.Contains("hair") && !mName.Contains("base")))
                {
                    mi.Visible = true;
                }
                else if (mName.Contains("flask_on_hip"))
                {
                    mi.Visible = true;
                }
                else if (mName.Contains("flask_in_hand"))
                {
                    mi.Visible = false;
                }
                else if (mName.Contains("inferno_flames"))
                {
                    mi.Visible = false;
                }
            }

            // Generic: hide secondary holstered/hip weapons when a primary weapon in hand exists (do not hide body parts like hips/legs)
            bool isHipWeapon = (mName.Contains("weapon") || mName.Contains("gun") || mName.Contains("sword") ||
                                mName.Contains("pistol") || mName.Contains("dagger") || mName.Contains("bow") ||
                                mName.Contains("sheath") || mName.Contains("revolver")) &&
                               (mName.Contains("hip") || mName.Contains("holster"));

            if ((!heroLower.Contains("inferno") && (isHipWeapon || mName.Contains("_on_hip"))) || mName.Contains("sheath_sword"))
            {
                mi.Visible = false;
            }
        }

        foreach (Node child in node.GetChildren())
        {
            ApplySubmeshVisibilityRecursively(child, heroLower);
        }
    }

    /// <summary>
    /// Updates submesh visibility dynamically when an animation pose is applied.
    /// E.g. For Doorman, switches between parry_door and reload_door based on the pose.
    /// </summary>
    public static void OnPoseChanged(Node3D heroScene, string heroName, string animName)
    {
        if (heroScene == null || string.IsNullOrEmpty(animName)) return;

        string hLower = heroName?.ToLowerInvariant() ?? "";
        string aLower = animName.ToLowerInvariant();

        if (hLower.Contains("doorman"))
        {
            bool isParry = aLower.Contains("parry");
            bool isReload = aLower.Contains("reload");

            SetMeshVisibilityByName(heroScene, "parry_door", isParry || (!isReload));
            SetMeshVisibilityByName(heroScene, "reload_door", isReload);
        }
    }

    private static void SetMeshVisibilityByName(Node node, string partialName, bool visible)
    {
        if (node is MeshInstance3D mi)
        {
            string mName = mi.Name.ToString().TrimStart('.', '_').ToLowerInvariant();
            if (mName.Contains(partialName))
            {
                mi.Visible = visible;
            }
        }

        foreach (Node child in node.GetChildren())
        {
            SetMeshVisibilityByName(child, partialName, visible);
        }
    }

    /// <summary>
    /// Applies hero-specific material overrides and VRF parameter corrections.
    /// Delegates directly to the modular IHeroMaterialConfig registry in HeroMaterialManager.
    /// </summary>
    public static void ResolveHeroMaterial(Package package, string heroName, string meshName, int surfaceIndex, string vmatPath, Godot.Material material)
    {
        if (material is StandardMaterial3D stdMat)
        {
            var config = HeroMaterialManager.GetConfigForHero(heroName);
            config?.ConfigureBaseMaterial(meshName, surfaceIndex, vmatPath, stdMat);
        }
    }
}
