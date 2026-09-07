shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_nearest;
uniform float pixel_size : hint_range(1.0, 10.0) = 3.0;
uniform float vhs_intensity : hint_range(0.0, 10.0) = 3.0;
uniform float opacity : hint_range(0.0, 1.0) = 0.5; 
uniform float double_vision_split : hint_range(0.0, 0.05) = 0.01;

float random(vec2 uv) {
    return fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453123);
}

void fragment() {
    vec4 original_color = texture(SCREEN_TEXTURE, SCREEN_UV);
    vec2 screen_res = vec2(textureSize(SCREEN_TEXTURE, 0));
    vec2 grid_uv = round(SCREEN_UV * (screen_res / pixel_size)) / (screen_res / pixel_size);
    
    float time_step = floor(TIME * 15.0); 
    float glitch = (random(vec2(time_step, floor(grid_uv.y * 30.0))) - 0.5) * 0.005 * vhs_intensity;
    
    vec2 uv = grid_uv;
    uv.x += glitch;

    vec2 uv_red = uv + vec2(double_vision_split, 0.0);
    vec2 uv_blue = uv - vec2(double_vision_split, 0.0);
    
    vec4 tex_red = texture(SCREEN_TEXTURE, uv_red);
    vec4 tex_blue = texture(SCREEN_TEXTURE, uv_blue);
    
    vec3 red_vision = vec3(tex_red.r, 0.0, 0.0);
    vec3 blue_vision = vec3(0.0, tex_blue.g, tex_blue.b); 
    vec3 vhs_color = red_vision + blue_vision;

    vec3 final_color = mix(original_color.rgb, vhs_color, opacity);

    COLOR = vec4(final_color, 1.0);
}