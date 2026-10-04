@tool
class_name BrushCompositorEffect
extends CompositorEffect

var camera_brush: CameraBrush

var rd: RenderingDevice
var shader: RID
var pipeline: RID

var dummy_texture_rid: RID
var brush_shape_sampler_rid: RID

# can change
var brush_shape_texture_rid: RID
var brush_shape_uniform_set: RID

var atlas_texture_uniform_set: RID


func _init() -> void:
	effect_callback_type = EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	rd = RenderingServer.get_rendering_device()
	if rd:
		var dummy_fmt = RDTextureFormat.new()
		dummy_fmt.width = 1
		dummy_fmt.height = 1
		dummy_fmt.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
		dummy_fmt.texture_type = RenderingDevice.TEXTURE_TYPE_2D
		dummy_fmt.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_STORAGE_BIT
		var dummy_img = Image.create(1, 1, false, Image.FORMAT_RGBAH)
		dummy_texture_rid = rd.texture_create(dummy_fmt, RDTextureView.new(), [dummy_img.get_data()])
	RenderingServer.call_on_render_thread(_initialize_compute)
	RenderingServer.call_on_render_thread(_create_brush_shape_sampler)


# System notifications, we want to react on the notification that
# alerts us we are about to be destroyed.
func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE:
		if shader.is_valid():
			# Freeing our shader will also free any dependents such as the pipeline!
			rd.free_rid(shader)
		
		if brush_shape_texture_rid.is_valid():
			rd.free_rid(brush_shape_texture_rid)
			brush_shape_texture_rid = RID()

		if brush_shape_sampler_rid.is_valid():
			rd.free_rid(brush_shape_sampler_rid)
			brush_shape_sampler_rid = RID()

		if dummy_texture_rid.is_valid():
			rd.free_rid(dummy_texture_rid)
			dummy_texture_rid = RID()


#region Code in this region runs on the rendering thread.
# Compile our shader at initialization.
func _initialize_compute() -> void:
	rd = RenderingServer.get_rendering_device()
	if not rd:
		return

	# Compile our shader.
	var shader_file := load("uid://bwm7j25sbgip3")
	var shader_spirv: RDShaderSPIRV = shader_file.get_spirv()
	var compile_err := shader_spirv.get_stage_compile_error(RenderingDevice.SHADER_STAGE_COMPUTE)
	if not compile_err.is_empty():
		push_error("BrushCompositorEffect: Compute shader compile error: " + compile_err)

	shader = rd.shader_create_from_spirv(shader_spirv)
	if shader.is_valid():
		pipeline = rd.compute_pipeline_create(shader)


func create_brush_shape_texture() -> void:
	if not camera_brush:
		return

	if not camera_brush.brush_shape:
		return
	
	if not rd:
		return

	
	if brush_shape_texture_rid.is_valid():
		rd.free_rid(brush_shape_texture_rid)
		brush_shape_texture_rid = RID()

	print("CameraBrush: Creating brush shape texture")

	# Get image from texture and convert to RGBAF format
	var image := camera_brush.brush_shape
	image.decompress()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)

	# create texture format
	var fmt := RDTextureFormat.new()
	fmt.width = image.get_width()
	fmt.height = image.get_height()
	fmt.format = RenderingDevice.DATA_FORMAT_R8G8B8A8_UNORM
	fmt.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	fmt.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT

	# create texture view
	var view := RDTextureView.new()

	brush_shape_texture_rid = rd.texture_create(fmt, view, [image.get_data()]) 
	if not brush_shape_texture_rid.is_valid() or not rd.texture_is_valid(brush_shape_texture_rid):
		push_error("CameraBrush: Failed to create brush_shape_texture_rid on RenderingDevice.")
		return

	if not brush_shape_sampler_rid.is_valid():
		_create_brush_shape_sampler()

	if not brush_shape_sampler_rid.is_valid():
		push_error("CameraBrush: Failed to create brush_shape_sampler_rid.")
		return

	if not shader.is_valid():
		_initialize_compute()

	if not shader.is_valid():
		push_error("CameraBrush: Compute shader is not valid.")
		return

	# create uniform
	var uniform := RDUniform.new()
	uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
	uniform.binding = 0
	uniform.add_id(brush_shape_sampler_rid)
	uniform.add_id(brush_shape_texture_rid)

	brush_shape_uniform_set = UniformSetCacheRD.get_cache(shader, 1, [uniform])
	if not brush_shape_uniform_set.is_valid():
		brush_shape_uniform_set = rd.uniform_set_create([uniform], shader, 1)


func _create_dummy_texture() -> void:
	if not rd:
		rd = RenderingServer.get_rendering_device()
	if not rd:
		return

	if dummy_texture_rid.is_valid():
		rd.free_rid(dummy_texture_rid)
		dummy_texture_rid = RID()

	var dummy_fmt = RDTextureFormat.new()
	dummy_fmt.width = 1
	dummy_fmt.height = 1
	dummy_fmt.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
	dummy_fmt.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	dummy_fmt.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | \
	                       RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | \
	                       RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT | \
	                       RenderingDevice.TEXTURE_USAGE_CAN_COPY_TO_BIT | \
	                       RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
	var dummy_img = Image.create(1, 1, false, Image.FORMAT_RGBAH)
	dummy_texture_rid = rd.texture_create(dummy_fmt, RDTextureView.new(), [dummy_img.get_data()])


func _create_brush_shape_sampler() -> void:
	if not rd:
		return

	if brush_shape_sampler_rid.is_valid():
		rd.free_rid(brush_shape_sampler_rid)
		brush_shape_sampler_rid = RID()

	var sampler_state := RDSamplerState.new()
	sampler_state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	sampler_state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	sampler_state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	sampler_state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	brush_shape_sampler_rid = rd.sampler_create(sampler_state)


func _get_fallback_dummy_texture_rid() -> RID:
	if not rd:
		rd = RenderingServer.get_rendering_device()
	if not rd:
		return RID()
	if not dummy_texture_rid.is_valid() or not rd.texture_is_valid(dummy_texture_rid):
		_create_dummy_texture()
	return dummy_texture_rid


func get_atlas_textures(all_managers: Array[Node]) -> void:
	if not rd:
		rd = RenderingServer.get_rendering_device()
	if not rd:
		return

	var fallback_rid := _get_fallback_dummy_texture_rid()
	if not (fallback_rid.is_valid() and rd.texture_is_valid(fallback_rid)):
		_create_dummy_texture()
		fallback_rid = dummy_texture_rid
	if not (fallback_rid.is_valid() and rd.texture_is_valid(fallback_rid)):
		push_error("CameraBrush: Failed to create fallback dummy texture.")
		return

	var active_atlases: Array[RID] = []
	for i in range(8):
		active_atlases.append(fallback_rid)

	var base_tex_rid: RID = fallback_rid

	# 1. Identify active target manager (which always maps to slot 0 for brush compute)
	var active_mgr: OverlayAtlasManager = null
	for item in all_managers:
		var manager := item as OverlayAtlasManager
		if manager == null or not is_instance_valid(manager) or manager.is_queued_for_deletion():
			continue
		if manager.get("is_active_target") == true:
			active_mgr = manager
			break

	if active_mgr != null and active_mgr.atlas_texture_rid is RID and active_mgr.atlas_texture_rid.is_valid() and rd.texture_is_valid(active_mgr.atlas_texture_rid):
		active_atlases[0] = active_mgr.atlas_texture_rid
		if active_mgr.base_texture_rid is RID and active_mgr.base_texture_rid.is_valid() and rd.texture_is_valid(active_mgr.base_texture_rid):
			base_tex_rid = active_mgr.base_texture_rid

	# 2. Fill remaining slots with other managers
	for item in all_managers:
		var manager := item as OverlayAtlasManager
		if manager == null or not is_instance_valid(manager) or manager.is_queued_for_deletion():
			continue
		if manager == active_mgr:
			continue
		var idx: int = manager.atlas_index
		if active_mgr != null and idx == 0:
			# Slot 0 is strictly reserved for active_mgr
			continue
		if idx >= 0 and idx < 8:
			if manager.atlas_texture_rid is RID and manager.atlas_texture_rid.is_valid() and rd.texture_is_valid(manager.atlas_texture_rid):
				active_atlases[idx] = manager.atlas_texture_rid
			else:
				active_atlases[idx] = fallback_rid
			if (not base_tex_rid.is_valid() or base_tex_rid == fallback_rid) and manager.base_texture_rid is RID and manager.base_texture_rid.is_valid() and rd.texture_is_valid(manager.base_texture_rid):
				base_tex_rid = manager.base_texture_rid

	var uniforms: Array[RDUniform] = []
	for i in range(8):
		var tex_rid: RID = active_atlases[i]
		if not (tex_rid is RID and tex_rid.is_valid() and rd.texture_is_valid(tex_rid)):
			tex_rid = fallback_rid
		var uniform = RDUniform.new()
		uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
		uniform.binding = i
		uniform.add_id(tex_rid)
		uniforms.append(uniform)

	# Binding 8: companion base texture for real-time blend modes
	var base_uniform = RDUniform.new()
	base_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	base_uniform.binding = 8
	var valid_base_rid: RID = fallback_rid
	if base_tex_rid is RID and base_tex_rid.is_valid() and rd.texture_is_valid(base_tex_rid):
		valid_base_rid = base_tex_rid
	base_uniform.add_id(valid_base_rid)
	uniforms.append(base_uniform)

	# Binding 9: selection mask image for stencil masking
	var mask_uniform = RDUniform.new()
	mask_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	mask_uniform.binding = 9
	var valid_mask_rid: RID = fallback_rid
	if camera_brush and "selection_mask_rid" in camera_brush:
		var cand_rid = camera_brush.selection_mask_rid
		if cand_rid is RID and cand_rid.is_valid() and rd.texture_is_valid(cand_rid):
			valid_mask_rid = cand_rid
	mask_uniform.add_id(valid_mask_rid)
	uniforms.append(mask_uniform)

	# Universal Fallback Sweep: ensure NO uniform points to an invalid RID
	for u in uniforms:
		var ids := u.get_ids()
		for j in range(ids.size()):
			var id: RID = ids[j]
			if not (id is RID and id.is_valid() and rd.texture_is_valid(id)):
				u.clear_ids()
				u.add_id(fallback_rid)
				break

	atlas_texture_uniform_set = RID()

	# Create uniform set using UniformSetCacheRD to manage lifecycle across canvas resizes
	if shader.is_valid() and fallback_rid.is_valid() and rd.texture_is_valid(fallback_rid):
		atlas_texture_uniform_set = UniformSetCacheRD.get_cache(shader, 2, uniforms)
		if not atlas_texture_uniform_set.is_valid():
			atlas_texture_uniform_set = rd.uniform_set_create(uniforms, shader, 2)


func _render_callback(p_effect_callback_type: EffectCallbackType, p_render_data: RenderData) -> void:
	if not rd:
		return
	
	if not p_effect_callback_type == EFFECT_CALLBACK_TYPE_POST_TRANSPARENT:
		return

	if not pipeline.is_valid():
		return
	
	if not brush_shape_uniform_set.is_valid() or not atlas_texture_uniform_set.is_valid():
		return

	if not rd.uniform_set_is_valid(brush_shape_uniform_set) or not rd.uniform_set_is_valid(atlas_texture_uniform_set):
		return
	
	if not camera_brush:
		return

	# Get our render scene buffers object, this gives us access to our render buffers.
	var render_scene_buffers := p_render_data.get_render_scene_buffers()
	if render_scene_buffers:
		var size: Vector2i = render_scene_buffers.get_internal_size()
		if size.x == 0 and size.y == 0:
			return

		var tex_width: int = size.x
		var tex_height: int = size.y
		var center := Vector2(float(tex_width) * 0.5, float(tex_height) * 0.5)
		if "brush_center" in camera_brush and camera_brush.brush_center.x >= 0.0 and camera_brush.brush_center.y >= 0.0:
			center = camera_brush.brush_center

		var brush_radius: float = minf(float(tex_width), float(tex_height)) * 0.5
		if "brush_radius" in camera_brush and camera_brush.brush_radius > 0.0:
			brush_radius = camera_brush.brush_radius
		brush_radius += float(camera_brush.max_bleed)

		var min_x: int = maxi(0, int(center.x - brush_radius))
		var max_x: int = mini(tex_width - 1, int(center.x + brush_radius))
		var min_y: int = maxi(0, int(center.y - brush_radius))
		var max_y: int = mini(tex_height - 1, int(center.y + brush_radius))

		@warning_ignore("integer_division")
		var x_groups: int = clampi(int(ceil(float(max_x - min_x + 1) / 16.0)), 1, 16)
		@warning_ignore("integer_division")
		var y_groups: int = clampi(int(ceil(float(max_y - min_y + 1) / 16.0)), 1, 16)
		var z_groups: int = 1

		# prepare push constant
		var linear_color := camera_brush.color.srgb_to_linear()
		var push_constant : PackedFloat32Array = PackedFloat32Array([
			linear_color.r,
			linear_color.g,
			linear_color.b,
			linear_color.a,
			camera_brush.last_delta * camera_brush.draw_speed,
			camera_brush.max_distance,
			camera_brush.start_distance_fade,
			float(camera_brush.min_bleed),
			float(camera_brush.max_bleed),
			float(1.0 if (camera_brush.is_erase or camera_brush.color.a < 0.0) else 0.0),
			float(camera_brush.blend_mode),
			float(1.0 if camera_brush.use_selection_mask else 0.0),
			float(min_x),
			float(min_y),
			float(max_x),
			float(max_y)
		])


		# Get the RID for our color image, we will be reading from and writing to it.
		var framebuffer_rid: RID = render_scene_buffers.get_color_layer(0)
		if not (framebuffer_rid.is_valid() and rd.texture_is_valid(framebuffer_rid)):
			return

		# Create a uniform set, this will be cached, the cache will be cleared if our viewports configuration is changed.
		var framebuffer_uniform := RDUniform.new()
		framebuffer_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
		framebuffer_uniform.binding = 0
		framebuffer_uniform.add_id(framebuffer_rid)
		var framebuffer_uniform_set := UniformSetCacheRD.get_cache(shader, 0, [framebuffer_uniform])
		if not framebuffer_uniform_set.is_valid():
			framebuffer_uniform_set = rd.uniform_set_create([framebuffer_uniform], shader, 0)
		if not (framebuffer_uniform_set.is_valid() and rd.uniform_set_is_valid(framebuffer_uniform_set)):
			return

		# Run our compute shader.
		var compute_list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute_list, pipeline)
		rd.compute_list_bind_uniform_set(compute_list, framebuffer_uniform_set, 0)
		rd.compute_list_bind_uniform_set(compute_list, brush_shape_uniform_set, 1)
		rd.compute_list_bind_uniform_set(compute_list, atlas_texture_uniform_set, 2)
		rd.compute_list_set_push_constant(compute_list, push_constant.to_byte_array(), push_constant.size() * 4)
		rd.compute_list_dispatch(compute_list, x_groups, y_groups, z_groups)
		rd.compute_list_end()

#endregion
