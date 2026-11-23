# Golden Importer Test Samples

Small synthetic glTF Binary (`.glb`) fixtures for headless regression and Phase 0–6 verification of the Custom 3D Model Importer & Auto-Rigger:

| File | Description | What It Covers |
|---|---|---|
| `arp_magician_sample.glb` | Rigged humanoid with Auto-Rig Pro naming (`root.x`, `spine_01..03.x`, `arm_stretch.l/.r`, `c_pos`, `c_traj`, `c_arm_twist_offset.l`) + weighted cape chain (`cape_01.x`, `cape_02.x`). | `RigBoneClassifier` ARP detection, weighted `c_*` vs unweighted control pruning, cape extra chain extraction. |
| `rigify_sample.glb` | Rigged humanoid with Blender Rigify naming (`DEF-spine`, `DEF-upper_arm.L/.R`, `DEF-thigh.L/.R`, `MCH-torso`, `WGT-root`). | `RigBoneClassifier` Rigify detection, `MCH-*`/`WGT-*` helper filtering, canonical limb mapping. |
| `cape_chain_sample.glb` | Rigged humanoid with long multi-segment cape chain (`cape_center_01..03`) + unweighted helpers (`cs_grp`, `IK_Foot_L`). | `BoneChainDef` preservation, anchor resolution (`spine_3`), `CustomJiggleBoneDriver`, Source 2 `.vmdl` bone preservation & jiggle export. |
| `static_humanoid_sample.glb` | Unrigged humanoid mesh including 5 protruding finger prongs per hand and a rear cape drape. | `CustomLandmarkWeightSolver`, `CustomFingerAutoRigger` fingertip landmark detection + per-finger chain generation + pose presets, `AddClothingChainTool` synthetic chains. |

## Additional Large Community Fixtures (`scratch_out/models/`)

If present locally in `scratch_out/models/` (gitignored):
- `ambush.glb` — ValveBiped 29-bone rig
- `gordon_freeman_half-life_1.glb` — GoldSrc/Source humanoid rig
- `sasha_-_cyberpunk_edgerunners.glb` — Stylized multi-material character
- `zarya_from_stalker.glb` — S.T.A.L.K.E.R. armored rig

