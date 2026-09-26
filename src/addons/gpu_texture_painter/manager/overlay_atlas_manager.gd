@tool
@icon("uid://c1jgnh1db12t")
class_name OverlayAtlasManager
extends Node3D


const  GROUP_NAME := "overlay_atlas_managers"

## Size of the overlay atlas texture (width and height in pixels).
@export_range(1, 1024 * 4) var atlas_size: int = 1024:
	set(value):
		atlas_size = clampi(value, 1, 1024 * 4)
		
@export_storage var atlas_texture_resource: Texture2DRD = null
## Shader used for overlay materials.
@export var overlay_shader: Shader = preload("uid://qow53ph8eivf")

## Calculates the atlas and applies the overlay materials to all MeshInstance3D children & siblings.
@export_tool_button("Apply") var apply_action = apply

@export var apply_on_ready: bool = false

var atlas_index: int = 0

var rd: RenderingDevice
var atlas_texture_rid: RID = RID()
var base_texture_rid: RID = RID()

@export_category("Storage")
# File load and save
@export_file("*.webp", "*.png", "*.exr") var atlas_texture_path: String
@export_tool_button("Save atlas to file") var save_action = save_atlas_texture_to_file


func _ready() -> void:
	add_to_group(GROUP_NAME)
	_get_atlas_index()

	rd = RenderingServer.get_rendering_device()
	
	if apply_on_ready:
		# create everything from scratch
		apply()
	else:
		# create texture and apply to existing resource
		_create_texture()
		_apply_texture_to_texture_resource()


func _notification(what):
	if what == NOTIFICATION_PREDELETE:
		_cleanup_texture()


func apply() -> void:
	_create_texture()
	_create_texture_resource()
	_apply_texture_to_texture_resource()
	_construct_atlas_and_apply_materials()


func _get_atlas_index() -> void:
		var possible_index: Array[int] = [0, 1, 2, 3, 4, 5, 6, 7]

		var all_managers := get_tree().get_nodes_in_group(GROUP_NAME)
		
		if all_managers.is_empty():
			return

		for manager: OverlayAtlasManager in all_managers:
			if manager == null or manager == self or not is_instance_valid(manager) or manager.is_queued_for_deletion():
				continue
			possible_index.erase(manager.atlas_index)
		
		if possible_index.is_empty():
			push_error("OverlayAtlasManager: No available atlas indices left! Maximum of 8 overlay atlases reached.")
			return
		
		atlas_index = possible_index[0]
		print("OverlayAtlasManager: Assigned atlas index {0}".format([atlas_index]))


func _create_texture() -> void:
	if not rd:
		return

	print("OverlayAtlasManager: Creating overlay texture of size {0}x{0}".format([atlas_size]))

	# create texure format
	var fmt := RDTextureFormat.new()
	fmt.width = atlas_size
	fmt.height = atlas_size
	fmt.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
	fmt.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	fmt.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT + \
					 RenderingDevice.TEXTURE_USAGE_STORAGE_BIT + \
					 RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT + \
					 RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT

	# create texture view
	var view := RDTextureView.new()


	var image: Image

	# try to load texture from file
	if !atlas_texture_path.is_empty() and FileAccess.file_exists(atlas_texture_path):
		var loaded = load(atlas_texture_path)
		if loaded:
			if loaded is Texture2D:
				image = loaded.get_image()
			if loaded is Image:
				image = loaded
			image.decompress()
			if image.get_format() != Image.FORMAT_RGBAH:
				image.convert(Image.FORMAT_RGBAH)
	
	#if not loaded create new image
	if !image:
		image = Image.create(atlas_size, atlas_size, false, Image.FORMAT_RGBAH)

	# Clean up previous texture and resource binding
	if atlas_texture_resource:
		atlas_texture_resource.texture_rd_rid = RID()
	var old_rid := atlas_texture_rid
	atlas_texture_rid = RID()
	if old_rid.is_valid() and rd.texture_is_valid(old_rid):
		rd.free_rid(old_rid)

	# Clean up previous base texture
	var old_base_rid := base_texture_rid
	base_texture_rid = RID()
	if old_base_rid.is_valid() and rd.texture_is_valid(old_base_rid):
		rd.free_rid(old_base_rid)

	# Create new texture on RenderingDevice
	atlas_texture_rid = rd.texture_create(fmt, view, [image.get_data()])
	image = null # Free CPU RAM immediately
	print("OverlayAtlasManager: Created texture RID {0}".format([atlas_texture_rid.get_id()]))

	# Create companion base texture on RenderingDevice
	var base_image := Image.create(atlas_size, atlas_size, false, Image.FORMAT_RGBAH)
	base_texture_rid = rd.texture_create(fmt, view, [base_image.get_data()])
	base_image = null
	print("OverlayAtlasManager: Created base texture RID {0}".format([base_texture_rid.get_id()]))

	# update texture resource and notify brushes immediately
	_apply_texture_to_texture_resource()
	_notify_brushes()


func _cleanup_texture() -> void:
	print("OverlayAtlasManager: Cleaning up overlay texture")
	if atlas_texture_resource:
		atlas_texture_resource.texture_rd_rid = RID()
	if atlas_texture_rid.is_valid() and rd and rd.texture_is_valid(atlas_texture_rid):
		rd.free_rid(atlas_texture_rid)
		atlas_texture_rid = RID()
	if base_texture_rid.is_valid() and rd and rd.texture_is_valid(base_texture_rid):
		rd.free_rid(base_texture_rid)
		base_texture_rid = RID()
	_notify_brushes()


func _notify_brushes() -> void:
	if is_inside_tree():
		get_tree().call_group(CameraBrush.GROUP_NAME, "get_atlas_textures")


func _create_texture_resource() -> void:
	atlas_texture_resource = Texture2DRD.new()


func _apply_texture_to_texture_resource() -> void:
	#create Texture2DRD
	if not atlas_texture_resource:
		_create_texture_resource()
	
	if atlas_texture_rid.is_valid():
		atlas_texture_resource.texture_rd_rid = atlas_texture_rid  # handles cleanup of old RID
		print("OverlayAtlasManager: Bound atlas_texture_rid {0} to Texture2DRD".format([atlas_texture_rid.get_id()]))
	notify_property_list_changed()


func _find_base_texture(mesh_instance: MeshInstance3D) -> Texture2D:
	if not mesh_instance or not mesh_instance.mesh:
		return null
	var s_count: int = mesh_instance.mesh.get_surface_count()
	for s in range(s_count):
		var orig_mat = mesh_instance.get_surface_override_material(s)
		if not orig_mat and mesh_instance.mesh:
			orig_mat = mesh_instance.mesh.surface_get_material(s)
		if not orig_mat:
			orig_mat = mesh_instance.material_override
		if orig_mat is StandardMaterial3D and orig_mat.albedo_texture:
			return orig_mat.albedo_texture
		elif orig_mat is ShaderMaterial:
			for p in ["g_tColor", "g_tColor1", "g_tColorA", "g_tColor0", "g_tColor2", "g_tColorB", "u_texture_color", "texture_albedo", "albedo_texture", "g_tNprTransmissiveColor"]:
				var t = orig_mat.get_shader_parameter(p)
				if t is Texture2D:
					return t
	return null


func _get_submesh_recommended_size(mesh_instance: MeshInstance3D, base_tex: Texture2D) -> Vector2i:
	var name_lower := mesh_instance.name.to_lower()
	var parent_name := mesh_instance.get_parent().name.to_lower() if mesh_instance.get_parent() else ""
	var full_name := parent_name + " " + name_lower

	# Minor / low-detail accessory parts
	var is_minor := false
	for kw in ["teeth", "tooth", "tongue", "eye", "eyeball", "cornea", "pupil", "eyelash"]:
		if kw in full_name:
			is_minor = true
			break

	# Major / high-detail primary parts
	var is_major := false
	if not is_minor:
		for kw in ["head", "face", "body", "torso", "skin", "lower", "upper", "chest", "leg", "arm", "cloth", "coat", "jacket", "pants", "dress"]:
			if kw in full_name:
				is_major = true
				break

	if base_tex:
		var bw := base_tex.get_width()
		var bh := base_tex.get_height()
		if bw > 0 and bh > 0:
			var aspect := float(bw) / float(bh)
			var base_dim := 256
			if is_major:
				base_dim = 512
			elif is_minor:
				base_dim = 128
			else:
				base_dim = 256

			var target_w := base_dim
			var target_h := int(round(float(base_dim) / aspect))
			target_w = clampi(target_w, 64, 1024)
			target_h = clampi(target_h, 64, 1024)
			return Vector2i(target_w, target_h)

	if is_major:
		return Vector2i(512, 512)
	elif is_minor:
		return Vector2i(128, 128)
	else:
		return Vector2i(256, 256)


func _construct_atlas_and_apply_materials() -> void:
	var mesh_instances := _get_child_mesh_instances(get_parent())

	# Pack into atlas with intelligent resolution weighting
	var rects: Array[Vector2] = []
	for i in range(mesh_instances.size() - 1, -1, -1):
		var mesh_instance = mesh_instances[i]
		if mesh_instance.mesh == null or not mesh_instance.visible:
			mesh_instances.erase(mesh_instance)
			if mesh_instance.mesh == null:
				push_warning("MeshInstance3D '{0}' has no mesh assigned, skipping overlay material application.".format([mesh_instance.name]))
		else:
			var base_tex := _find_base_texture(mesh_instance)
			var rec_size := _get_submesh_recommended_size(mesh_instance, base_tex)
			mesh_instance.mesh.lightmap_size_hint = rec_size
			rects.push_back(Vector2(rec_size))

	rects.reverse()

	var base_textures: Array[Texture2D] = []
	for mi in mesh_instances:
		base_textures.push_back(_find_base_texture(mi))

	var packed_rects: Array[Rect2] = MaxRectsPacker.pack_into_square(rects)

	print("OverlayAtlasManager: Packed {0} mesh instances into overlay atlas of size {1}x{1}".format([mesh_instances.size(), atlas_size]))

	var overlay_material := ShaderMaterial.new()
	overlay_material.shader = overlay_shader

	for i in mesh_instances.size():
		var mesh_instance = mesh_instances[i]
		mesh_instance.material_overlay = overlay_material.duplicate()
		mesh_instance.material_overlay.set_shader_parameter("overlay_texture", atlas_texture_resource)
		mesh_instance.material_overlay.set_shader_parameter("position_in_atlas", packed_rects[i].position)
		mesh_instance.material_overlay.set_shader_parameter("size_in_atlas", packed_rects[i].size)
		mesh_instance.material_overlay.set_shader_parameter("atlas_index", atlas_index)

		var base_tex: Texture2D = base_textures[i]
		if base_tex:
			mesh_instance.material_overlay.set_shader_parameter("g_tColor", base_tex)

		mesh_instance.layers |= 1 << 20  # enable overlay layer 21

	print("OverlayAtlasManager: Applied overlay materials to mesh instances")


func _get_self_and_child_mesh_instances(node: Node, children_acc: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D:
		children_acc.push_back(node)
		
	for child in node.get_children():
		_get_self_and_child_mesh_instances(child, children_acc)


func _get_child_mesh_instances(node: Node) -> Array[MeshInstance3D]:
	var result: Array[MeshInstance3D] = []
	if node == null:
		return result
	for child in node.get_children():
		_get_self_and_child_mesh_instances(child, result)

	return result




func save_atlas_texture_to_file() -> void:
	if not Engine.is_editor_hint():
		push_warning("OverlayAtlasManager: Texture saving is only available in the editor.")
		return

	# get image from RenderingDevice
	var image := Image.create_from_data(atlas_size, atlas_size, false, Image.FORMAT_RGBAH, rd.texture_get_data(atlas_texture_rid, 0))

	# if no path is set, search for best option
	if atlas_texture_path.is_empty() or !FileAccess.file_exists(atlas_texture_path):
		var base_path: String = get_tree().edited_scene_root.scene_file_path.get_basename()
		var found := false
		# try paths until available one is found
		for i in range(50):
			var test_path := base_path + "_overlay_atlas_{0}.exr".format([i])
			if !FileAccess.file_exists(test_path):
				atlas_texture_path = test_path
				found = true
				break
		if !found:
			push_warning("OverlayAtlasManager: Could not save atlas texture, already 50 atlases where found belonging to this scene.")
			return

	# save as exr
	image.save_exr(atlas_texture_path)
	print("OverlayAtlasManager: Saved atlas texture to '{0}'".format([atlas_texture_path]))

	# Reimport safe lookup (avoids compile-time parse errors in standalone exported builds)
	if Engine.has_singleton("EditorInterface"):
		var editor_iface = Engine.get_singleton("EditorInterface")
		if editor_iface != null:
			var fs = editor_iface.call("get_resource_filesystem")
			if fs != null:
				fs.call("update_file", atlas_texture_path)
				fs.call("reimport_files", [atlas_texture_path])

	# mark scene as modified
	notify_property_list_changed()
