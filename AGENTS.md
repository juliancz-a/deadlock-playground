# Deadlock Playground - Agent Engineering Guide & Technical Architecture

This document serves as your **primary onboarding guide, architectural reference, and operational playbook** for working within the **Deadlock Playground** codebase.

Always consult this guide before implementing new features, modifying shaders, altering UI containers, or refactoring the painting/rendering subsystems.

---

## 1. Core Technology Stack & Project Specifications

| Component | Specification | Technical Notes |
|---|---|---|
| **Game Engine** | **Godot Engine 4.7.2 (.NET / C#)** | Project SDK: `Godot.NET.Sdk/4.7.2` configured in `Deadlock_Playground.csproj`. |
| **Runtime Target** | **.NET 10.0 (`net10.0`)** | High-performance C# 13 runtime (with `net9.0` condition for Android). |
| **Language Stack** | **C# 13 + GLSL (`.gdshader` / compute `.glsl`) + GDScript** | GDScript is used only for the `gpu_texture_painter` addon pipeline; all core logic is C#. |
| **Unsafe Blocks** | `AllowUnsafeBlocks = true` | Unmanaged pointers (`ulong*`, `Half*`) used in `SkinLayerManager.cs` for 60 FPS 4K compositing. |
| **Rendering Backend** | **Forward+ (Direct3D 12 on Windows / Vulkan)** | Driver set to `d3d12` via `rendering_device/driver.windows="d3d12"` in `project.godot`. |
| **Physics Engine** | **Jolt Physics** | Configured via `3d/physics_engine="Jolt Physics"` in `project.godot`. |
| **Physics Layer 32** | `"Bone Picking"` | Reserved for bone gizmo selection raycasts. |
| **Visual Layer 21** | `1 << 20` (Bit 20) | **Strictly reserved for GPU Texture Painter `CameraBrush` culling mask.** |
| **Display Resolution** | 1920 × 1080 | Canvas stretch mode: `canvas_items`, aspect: `expand`. Window mode: `3` (borderless). |
| **UI Theme** | `res://assets/themes/PlaygroundTheme.tres` | Global custom theme applied in `project.godot`. |

### Primary NuGet Dependencies
- **`ValveResourceFormat` (`20.0.6980`)**: Reverse-engineered Source 2 parser by SteamDatabase. Ingests compiled `.vmdl_c` (models), `.vmat_c` (materials), `.vtex_c` (textures), and `.vanim_c` (animations) from Deadlock's `pak01_dir.vpk`.
- **`ValvePak` (`5.0.2.177`)**: Reads, indexes, extracts, and packages Source 2 VPK archives (`pak01_dir.vpk` and mod `pak##_dir.vpk` files).
- **`ValveKeyValue` (`0.70.0.499`)**: High-speed KeyValues3 (KV3) deserializer/serializer used in material analysis and mod packaging.

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
   - Shaders: `res://assets/shaders/painter/*.gdshader` and `res://shaders/*.gdshader`
   - Components: `res://ui/scenes/components/*.tscn` and `res://ui/scripts/components/*.cs`

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
