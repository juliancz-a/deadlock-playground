# Deadlock Playground - Agent Engineering Guide & Technical Architecture

This document serves as your **primary onboarding guide, architectural reference, and operational playbook** for working within the **Deadlock Playground** codebase.

Always consult this guide before implementing new features, modifying shaders, altering UI containers, or refactoring the painting/rendering subsystems.

---

## 1. Core Technology Stack & Project Specifications

| Component | Specification | Technical Notes |
|---|---|---|
| **Game Engine** | **Godot Engine 4.7.2 (.NET / C#)** | Project SDK: `Godot.NET.Sdk/4.7.2` configured in `src/Deadlock_Playground.csproj` (Godot engine root is `src/`). |
| **Runtime Target** | **.NET 10.0 (`net10.0`)** | High-performance C# 13 runtime (with `net9.0` condition for Android). |
| **Language Stack** | **C# 13 + GLSL (`.gdshader` / compute `.glsl`) + GDScript** | GDScript is used only for the `gpu_texture_painter` addon pipeline; all core logic is C#. |
| **Unsafe Blocks** | `AllowUnsafeBlocks = true` | Unmanaged pointers (`ulong*`, `Half*`) used in `SkinLayerManager.cs` for 60 FPS 4K compositing. |
| **Rendering Backend** | **Forward+ (Direct3D 12 on Windows / Vulkan)** | Driver set to `d3d12` via `rendering_device/driver.windows="d3d12"` in `src/project.godot`. |
| **Physics Engine** | **Jolt Physics** | Configured via `3d/physics_engine="Jolt Physics"` in `src/project.godot`. |
| **Physics Layer 32** | `"Bone Picking"` | Reserved for bone gizmo selection raycasts. |
| **Visual Layer 21** | `1 << 20` (Bit 20) | **Strictly reserved for GPU Texture Painter `CameraBrush` culling mask.** |
| **Display Resolution** | 1920 × 1080 | Canvas stretch mode: `canvas_items`, aspect: `expand`. Window mode: `3` (borderless). |
| **UI Theme** | `res://assets/themes/PlaygroundTheme.tres` | Global custom theme applied in `src/project.godot`. |

### Primary NuGet Dependencies
- **`ValveResourceFormat` (`20.0.7461`)**: Reverse-engineered Source 2 parser by SteamDatabase (local nupkg compiled from commit `d53ad1c63`). Ingests compiled `.vmdl_c`, `.vmat_c`, `.vtex_c`, `.vanim_c`, supporting Source 2 KV3 uint32 constants (`datatype: 26 (0x1A) / 27`).
- **`ValvePak` (`6.0.0.182`)**: Reads, indexes, extracts, and packages Source 2 VPK archives (`pak01_dir.vpk` and mod `pak##_dir.vpk` files). Uses namespace `ValvePak`.
- **`ValveKeyValue` (`0.71.0.528`)**: High-speed KeyValues3 (KV3) deserializer/serializer used in material analysis and mod packaging.
- **`nuget.config` Global Cache**: Configured to `globalPackagesFolder="D:\GameDev\.nuget_packages"` to store all packages on Drive D and prevent filling Drive C.

### Active Godot Plugins
- **`Gizmo3DSharp`** (`res://addons/Gizmo3DSharp/`): Interactive 3D bone manipulator for character posing.
- **`gpu_texture_painter`** (`res://addons/gpu_texture_painter/`): Real-time GPU compute texture painting subsystem.

---

## 2. Documentation Sitemap & Knowledge Directory

In-depth technical guides are maintained in the [`docs/`](file:///d:/GameDev/deadlock-playground/docs/) folder:

| Topic / Subsystem | Primary Document | What is Documented |
|---|---|---|
| **Agent Engineering Guide** | [docs/agent_engineering_guide.md](file:///d:/GameDev/deadlock-playground/docs/agent_engineering_guide.md) | In-depth engineering playbook, .NET 10 / Godot 4.7.2 specs, shader gotchas, and operational verification checklist. |
| **GPU Texture Painter System** | [docs/gpu_texture_painter_system.md](file:///d:/GameDev/deadlock-playground/docs/gpu_texture_painter_system.md) | 3D CameraBrush projection, Vulkan compute shader (`brush_compute.glsl`), layer stack (`SkinLayerManager`), 2D UV canvas dock (`UVCanvas2DUI`), Magic Wand stencil mask, and 16+ engineering gotchas. |
| **Project Architecture & Components** | [docs/project_structure_and_components.md](file:///d:/GameDev/deadlock-playground/docs/project_structure_and_components.md) | Comprehensive system architecture diagram, complete directory layout, VPK ingestion pipeline, material archetype builders, and UI layout. |
| **Hero Materials & Shaders Guide** | [docs/hero_materials_and_shaders_guide.md](file:///d:/GameDev/deadlock-playground/docs/hero_materials_and_shaders_guide.md) | Per-hero material configurations (Infernus, Lady Geist, Ivy, Lash, Mirage, Vindicta, Viscous, Wraith), outline masks, CSB color matrices, and past material collision bug fixes. |
| **Mod Export Pipeline** | [docs/mod_export_pipeline.md](file:///d:/GameDev/deadlock-playground/docs/mod_export_pipeline.md) | In-place VTEX interception architecture, headless `resourcecompiler.exe` invocation, automated VPK packaging, and collision-free slot allocation (`VpkIndexResolver`). |
| **Valve / VRF Renderer Reference** | [docs/vrf_deadlock_renderer_reference.md](file:///d:/GameDev/deadlock-playground/docs/vrf_deadlock_renderer_reference.md) | Valve's Deadlock NPR toon lighting math (`citadel.slang`), `NprGain`, `NprToonDiffuse`, `NprSteppedSpecular`, and VRF shader decoding rules. |
| **Hero Particle Editor System** | [docs/hero_particle_editor_system.md](file:///d:/GameDev/deadlock-playground/docs/hero_particle_editor_system.md) | VData indexing, particle picker modal, 3D stage lifecycle, Pose Tab simulation scrubbing, and 2D particle texture repainting. |
| **Hero Animation Editor System** | [docs/hero_animation_editor_system.md](file:///d:/GameDev/deadlock-playground/docs/hero_animation_editor_system.md) | Blender-style bottom dock (Dope Sheet, Graph Editor F-Curves), bone gizmo Auto-Keying, pose mirroring, AnimGraph 2 binary .vnmclip_c patcher, .dpp session schema, and VPK mod export. |
| **Dynamic Changelog & Web Showcase** | [docs/dynamic_changelog_and_website_system.md](file:///d:/GameDev/deadlock-playground/docs/dynamic_changelog_and_website_system.md) | Astro web showcase (`web/`), `@tabler/icons`, custom theme cursor, static `/api/changelog.json` endpoint, Cloudflare Pages hosting, and Godot `ChangelogFeedService.cs` integration. |
| **Release Automation Pipeline** | [docs/release_automation_pipeline.md](file:///d:/GameDev/deadlock-playground/docs/release_automation_pipeline.md) | Release Hub web UI (`tools/release_hub/`), web `changelog.json` sync, GitHub tag/release asset upload, and GameBanana assisted workflow. |

---

## 3. Mandatory Engineering Rules & Best Practices for Agents

> [!IMPORTANT]
> **Read and strictly adhere to these rules before making any code modifications.**

### A. Godot C# / .NET Rules
1. **Never Create Nested Classes Inheriting from `GodotObject`**:
   - Godot's C# source generator (`Godot.SourceGenerators`) strictly disallows nested classes that inherit from `GodotObject`, `Control`, `Node`, etc.
   - Attempting to do so triggers compile error **`GD0002: The class is a nested class and extends GodotObject, but Godot does not support nested classes extending GodotObject`**.
   - Always declare Godot classes at file/namespace scope, or use plain un-subclassed `Control` nodes with standard signal handlers.
2. **Preserve 8-Bone Skinning (Do NOT Split Meshes)**:
   - Deadlock hero models use 8-bone skinning (`FlagUse8BoneWeights`).
   - Attempting to split multi-surface meshes via `SurfaceTool` or `AddSurfaceFromArrays` without exact surface format flags causes Godot to reject the surface (`Condition "array.size() != ..." is true`), creating invisible character parts.
   - Always register GLTF `MeshInstance3D` candidates directly to preserve original bones and skeleton bindings.
3. **Always Verify Compilation with `dotnet build`**:
   - The workspace builds cleanly with zero errors. Run `dotnet build` after any C# edit to ensure type safety, correct signal generation, and no broken bindings.
4. **SplitContainer Multi-Child Limitations**:
   - Godot's native `SplitContainer` is designed for **two children**.
   - An `HSplitContainer` with 3 children (`LeftPanel`, `UVCanvasPanel`, `ViewportArea`) does not natively expose a functional grabber for the second split.
   - Do NOT try to force multi-split dragging by overwriting `splitParent.SplitOffsets` inside `Dragged` or `Resized` handlers—this fights the layout engine and causes freezing or layout collapses.
   - Use dedicated interactive edge grip handles (like `UVCanvasResizeGrip` in `UVCanvas2DUI.cs`) with `CursorShape.Hsize` (↔) and global mouse tracking in `_Input`.
5. **Resilient Node Paths**:
   - Many scripts reference nodes via paths rooted at `/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/...`.
   - Never reparent or rename `ViewportArea` without updating all dependent scripts.
   - Always write fallback discovery code: `GetNodeOrNull<T>(path) ?? FindChild(name, true, false) as T;`.

### B. Shaders & GPU Texture Painter Rules
1. **Layer 21 Isolation**:
   - Visual Layer 21 (bit index 20, `1 << 20`) is used strictly by the `CameraBrush` orthographic culling mask.
   - Any helper geometry (such as selection outline followers `_selectionOutlineMesh`, 3D ring gizmo, decal projectors) **must clear bit 20** (`Layers &= ~(1 << 20)`) so the painting camera does not capture or stamp over them.
2. **Linear vs. sRGB Color Space Discipline**:
   - UI color pickers supply colors in standard sRGB.
   - The texture atlas (`GpuData`) is stored in **Linear HDR half-float format**.
   - Always convert colors to Linear (`Color.SrgbToLinear()`) before storing or dispatching into VRAM.
   - In `BakeCompositeImage()`, convert from Linear back to sRGB (`LinearToSrgb()`) for PNG export to prevent washed-out pastel shifts.
3. **Selection Mask Strict Discard (`< 0.5`)**:
   - In `brush_compute.glsl`, always discard pixels where `mask_val < 0.5f`.
   - Remap $[0.5, 1.0] \to [0.0, 1.0]$:
     ```glsl
     float remap_mask = clamp((mask_val - 0.5f) / 0.5f, 0.0f, 1.0f);
     brush_color.a *= remap_mask;
     ```
   - This delivers smooth anti-aliased edge softening along the selection perimeter without allowing a single pixel of paint to bleed into protected areas.
4. **Shader Render Mode `unshaded` for Overlay Passes**:
   - Overlay materials rendered on character meshes must use `render_mode unshaded`.
   - Forward+ spatial shaders without `unshaded` query Descriptor Set 3 (lighting/SDFGI/decals), which are not bound during headless `SubViewport` passes, triggering Vulkan C++ runtime errors (`draw_list_draw: Uniforms were never supplied for set (3)`).
5. **Texture Sampler Hints (`hint_default_transparent`)**:
   - Always declare overlay samplers with `: hint_default_transparent`. This prevents pink/magenta textures if the atlas RID is being reallocated or is temporarily unassigned during canvas resizing.

### C. UI & Interaction Guidelines
1. **Mouse Input & Scroll Isolation**:
   - Scrolling inside UI docks (such as `UVCanvas2DUI` or sidebar tabs) must consume `InputEventMouseButton` wheel events (`GetViewport().SetInputAsHandled()`) so the 3D viewport camera does not zoom simultaneously.
2. **Mouse Cursor Visibility Stability**:
   - When toggling orbit/pan modes in the 3D viewport, ensure the mouse cursor is returned to `Input.MouseModeEnum.Visible` on release so the user is never left without a cursor.
3. **Tab Persistence**:
   - When introducing panel toggle states (e.g. `_uvCanvasUserWantsOpen`), store the user's explicit preference in `StudioUIManager.cs` so switching tabs does not forcibly reopen closed panels.
4. **Asset Organization**:
   - Icons: `res://assets/at-icons/*.svg`
   - Shaders: `res://assets/shaders/painter/*.gdshader`, `res://assets/shaders/valve/*.gdshader`, and `res://assets/shaders/*.gdshader`
   - Components: `res://ui/scenes/components/*.tscn` and `res://ui/scripts/components/*.cs`

### D. Skeletal Animation, Additive Ingestion & Root Motion Rules
1. **VRF Additive Metadata Single Source of Truth**:
   - In Source 2, `VrfAnimation.IsAdditive` parsed directly from compiled `.vnmclip_c` / `.vanim_c` binary headers (`m_bIsAdditive`) is the authoritative source of truth.
   - **Never** use sequence name substrings (`fire`, `shoot`, `shot`, `burst`, `aimed`, `flinch`) to force additive decoding. Heuristic name-based guessing causes full-body absolute animations to compose over rest rotations twice, splaying limbs and rotating the hero horizontal.
   - In `DecodeAnimationToGodot`, strictly trust `info.VrfAnimation.IsAdditive`. Name matching is reserved solely for UI folder categorization in `ClassifyCategory`.
2. **Non-Destructive Caching (Zero Runtime Track Stripping on Decoded Clips)**:
   - `DecodeAnimationToGodot` constructs authoritative tracks cleanly in a single pass with proper rest elevation.
   - Never call `StripAdditivePositionTracks` or mutate tracks on dynamically decoded animations (`godotAnim.HasMeta("decoded_by_anim_loader")`) during playback or selection in `PlayerTabUI.cs`.
   - Mutating cached tracks permanently strips authored shoulder, IK, and spine translations, turning bones static and tricking subsequent selections into classifying the clip as additive.
   - Tag all decoded clips with `anim_loader_version = 2` and evict older cache entries automatically.
3. **In-Place Root Motion Policy & Center Lock**:
   - For all root-level bones (`root`, `root_motion`, `Scene Root`, `SceneRoot`, `world`, or parentless joints), apply `SourceToGltfRotation` only to the top-level parentless root to align the skeleton upright.
   - When "In-Place Motion" is enabled (`_isInPlaceMotionEnabled = true`), zero out horizontal root translation (`convPos.X = 0`, `convPos.Z = 0`) so running, dashing, and rolling sequences remain centered in the viewport while preserving vertical $Y$ jumping/crouching motion.
4. **Incompatible Skeleton & Sub-Prop Isolation**:
   - Clips authored on external weapon or prop skeletons (e.g. `TargetSkeletonName` containing `weapon`, `props/`, `v_`, or clips with $< 8$ tracks on 180+ bone hero models) are flagged with `IsCompatible = false`.
   - In `PlayerTabUI`, incompatible clips are greyed out (`⚠ {name} [Incompatible]`) with disabled selection and informative tooltips, preventing prop tracks from scrambling character body bones.
5. **Tokenized Category Classification**:
   - `ClassifyCategory` uses tokenized word matching (splitting by `_`, `-`, ` `, `/`) rather than raw substrings (`.Contains("air")`), eliminating false positives (e.g. `hair` -> `air`, `white` -> `hit`, `result` -> `ult`, `window` -> `win`).
6. **Additive Composition Contract (`ComposeOverIdle`)**:
   - When an additive sequence is composed over idle, tag the resulting Godot `Animation` with `composed = true`, `is_additive = false`, and `anim_loader_version = 4`.
   - `IsAdditiveAnimation` must immediately return `false` if `composed == true` or `decoded_by_anim_loader == true`, guaranteeing that additive composition is never applied more than once.
7. **Two-Root Skeleton Architecture (`pelvis` & `root_motion`)**:
   - In Deadlock skeletons, `pelvis` is parentless (`Parent == null`, Godot parent -1) alongside `root_motion`.
   - Never assume a single top root via `.FirstOrDefault(b => b.Parent == null)`. Apply `SourceToGltfRotation` (-90° X) to **all parentless joints** (`isParentless = (vrfBone.Parent == null)`).
8. **AnimGraph Secondary Skeleton Retargeting (`.vnmskel`)**:
   - Locomotion, traversal, and action sequences targeting secondary `.vnmskel` skeletons must be retargeted via `SkeletonRetargeter` against the model skeleton.
   - Decomposing retargeted world matrices $M_{\text{local}} = M_{\text{world}}[b] \times (M_{\text{world}}[p])^{-1}$ delivers an exact match (0.0° error) against VRF's glTF-baked oracle.

### E. Mandatory Documentation Synchronization & Maintenance
> [!IMPORTANT]
> **AGENTS MUST KEEP DOCUMENTATION UPDATED**:
> Whenever you modify texture painting pipelines, shaders, hero materials, animation loaders, or UI components:
> 1. You **MUST** immediately update the relevant documentation in [`docs/`](file:///d:/GameDev/deadlock-playground/docs/) (such as [`hero_materials_and_shaders_guide.md`](file:///d:/GameDev/deadlock-playground/docs/hero_materials_and_shaders_guide.md), [`gpu_texture_painter_system.md`](file:///d:/GameDev/deadlock-playground/docs/gpu_texture_painter_system.md), [`agent_engineering_guide.md`](file:///d:/GameDev/deadlock-playground/docs/agent_engineering_guide.md), or [`texture_pipeline_audit/recent_changes_and_fixes.md`](file:///d:/GameDev/deadlock-playground/docs/texture_pipeline_audit/recent_changes_and_fixes.md)).
> 2. Detail the exact root causes, architectural fixes, shader parameters, and file changes.
> 3. Future agents rely on these technical documents as the architectural source of truth. Never leave code modified without updating its corresponding architectural guide.

---

## 4. Key Workflows & Verification Procedures

### Building & Validating Code
```powershell
dotnet build
```
The build must exit with **0 errors**.

### Running Diagnostics on Painting
If paint strokes do not appear or shaders behave unexpectedly:
1. Verify `OverlayAtlasManager` has a valid texture RID: check for `Created texture RID <ID>`.
2. Verify `CameraBrush` viewport shares `World3D`:
   `viewport.world_3d = _worldViewport.find_world_3d();`
3. Verify target submesh has bit 20 enabled:
   `(mesh_instance.layers & (1 << 20)) != 0`
4. Verify selection mask image binding 9 is not zeroed out or improperly bound.
