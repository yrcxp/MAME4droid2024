// license:BSD-3-Clause
// copyright-holders:David Valdeita (Seleuco) & Filipe Paulino (FlykeSpice)
/***************************************************************************

    shader_sources.hxx

    Shader sources for GLES2 renderer

***************************************************************************/


// license:BSD-3-Clause
// copyright-holders:David Valdeita (Seleuco) & Filipe Paulino (FlykeSpice)
/***************************************************************************

    shader_sources.hxx

    Shader sources for GLES renderer

***************************************************************************/


//=================================================
// Quad primitive program (TRUE HDR READY)
// ================================================

static const char* quad_vertex_shader_src = 
    "precision highp float;\n"
    "in float a_corner;\n"
    "in vec4 i_p0p1;\n"
    "in vec4 i_p2p3;\n"
    "in vec4 i_uv0uv1;\n"
    "in vec4 i_uv2uv3;\n"
    "in vec4 i_color;\n"
    "out vec2 v_texuv;\n"
    "out vec4 v_color;\n"
    "uniform mat4 u_ortho;\n"
    "void main() {\n"
    "    vec2 pos;\n"
    "    vec2 uv;\n"
    "    int corner = int(a_corner);\n"
    "    if (corner == 0)      { pos = i_p0p1.xy; uv = i_uv0uv1.xy; }\n"
    "    else if (corner == 1) { pos = i_p0p1.zw; uv = i_uv0uv1.zw; }\n"
    "    else if (corner == 2) { pos = i_p2p3.xy; uv = i_uv2uv3.xy; }\n"
    "    else                  { pos = i_p2p3.zw; uv = i_uv2uv3.zw; }\n"
    "    gl_Position = u_ortho * vec4(pos, 0.0, 1.0);\n"
    "    v_texuv = uv;\n"
    "    v_color = i_color;\n"
    "}\n";

static const char* quad_frag_shader_src = 
    "precision highp float;\n" 
    "in vec2 v_texuv;\n"
    "in vec4 v_color;\n"
    "uniform sampler2D s_texture;\n"
    "uniform int u_use_hdr_display;\n"
    "uniform int u_is_vector;\n"
    "out vec4 fragColor;\n"
    "void main() {\n"
    "    vec4 texColor = texture(s_texture, v_texuv);\n"
    "    if (u_use_hdr_display == 1) {\n"
    "        if (u_is_vector == 1) {\n"
    "            // TRUE LINEAR PATH: Vectors and glow are already mathematically linear\n"
    "            fragColor = vec4(texColor.rgb * v_color.rgb, texColor.a * v_color.a);\n"
    "        } else {\n"
    "            // SRGB PATH: Convert UI/Artworks textures to linear space\n"
    "            texColor.rgb = pow(texColor.rgb, vec3(2.2));\n"
    "            vec3 linear_vcolor = pow(v_color.rgb, vec3(2.2));\n"
    "            fragColor = vec4(texColor.rgb * linear_vcolor, texColor.a * v_color.a);\n"
    "        }\n"
    "    } else {\n"
    "        // CLASSIC SDR PATH\n"
    "        fragColor = texColor * v_color;\n"
    "    }\n"
    "}\n";

static const char* hdr_frag_shader_src = 
    "precision highp float;\n"
    "in vec2 v_texuv;\n"
    "in vec4 v_color;\n"
    "uniform sampler2D s_texture;\n"
    "uniform float u_exposure;\n"
    "uniform int u_use_hdr_display;\n" 
    "\n"
    "// --- DYNAMIC PARAMETERS (In Physical Nits) ---\n"
    "uniform float u_base_nits;\n"         // The light of a standard vector (e.g., 300.0)\n"
    "uniform float u_max_nits;\n"          // The arcade monitor's physical limit (e.g., 400.0)\n"
    "uniform float u_device_peak_nits;\n"  // The phone's maximum peak brightness (e.g., 1000.0 or 320.0)\n"
    "\n"
    "out vec4 fragColor;\n"
    "void main() {\n"
    "    vec4 hdrColor = texture(s_texture, v_texuv);\n"
    "    vec3 mapped;\n"
    "\n"
    "    if (u_use_hdr_display == 1) {\n"
    "        // 1. SAFE PHYSICAL LIMITER\n"
    "        // Compares emulator's target limit with the physical device's capabilities.\n"
    "        // We use the lower of the two to prevent clipping details and blinding the user.\n"
    "        float safe_peak_nits = min(u_max_nits, u_device_peak_nits);\n"
    "\n"
    "        // 2. scRGB CONVERSION\n"
    "        // In the Windows/Android scRGB standard, 1.0 float equals 80 nits.\n"
    "        float base_mult = u_base_nits / 80.0;\n"
    "        float peak_mult = safe_peak_nits / 80.0;\n"
    "\n"
    "        // 3. BASE CRT CALIBRATION\n"
    "        // Scale the pure linear energy read to its real target value.\n"
    "        vec3 crt_energy = hdrColor.rgb * u_exposure * base_mult;\n"
    "\n"
    "        // 4. PHOSPHOR SATURATION (Asymptotic Tone Mapping)\n"
    "        // Smoothly compresses the light as it approaches the physical limit (peak_mult).\n"
    "        // This physically simulates how CRT phosphor stops emitting light once saturated.\n"
    "        mapped = peak_mult * (vec3(1.0) - exp(-crt_energy / peak_mult));\n"
    "    } else {\n"
    "        // Classic SDR Path: Clamped Exponential Tone Mapping\n"
    "        mapped = vec3(1.0) - exp(-hdrColor.rgb * u_exposure);\n"
    "    }\n"
    "\n"
    "    fragColor = vec4(mapped, hdrColor.a) * v_color;\n"
    "}\n";
	

/*
static const char* hdr_frag_shader_src = 
    "precision highp float;\n"
    "in vec2 v_texuv;\n"
    "in vec4 v_color;\n"
    "uniform sampler2D s_texture;\n"
    "uniform float u_exposure;\n"
    "uniform float u_peak_multiplier;\n" 
    "uniform int u_use_hdr_display;\n" 
    "out vec4 fragColor;\n"
    "void main() {\n"
    "    vec4 hdrColor = texture(s_texture, v_texuv);\n"
    "    vec3 mapped;\n"
    "\n"
    "    if (u_use_hdr_display == 1) {\n"
    "        // TRUE FP16 PATH (scRGB linear space)\n"
    "        vec3 linear_light = hdrColor.rgb * u_exposure;\n"
    "\n"
    "        // --- HDR CONTRAST PINCH (Anti-Fattening) ---\n"
    "        // By raising the light to a power, we crush the faint halos so they don't\n"
    "        // turn into solid white walls when multiplied by the display's peak brightness.\n"
    "        // - 1.0 = No correction (Fat, washed-out glowing blobs)\n"
    "        // - 1.5 = Balanced (Bright laser core, smooth gradient preserved)\n"
    "        // - 1.8+ = Extremely sharp (Very thin and dark lines)\n"
    "        vec3 pinched_light = pow(max(linear_light, vec3(0.0)), vec3(1.6));\n"
    "\n"
    "        // Now we safely scale it to the hardware's physical maximum\n"
    "        mapped = pinched_light * u_peak_multiplier;\n" 
    "    } else {\n"
    "        // Classic SDR Path: Standard Exponential Tone Mapping\n"
    "        mapped = vec3(1.0) - exp(-hdrColor.rgb * u_exposure);\n"
    "    }\n"
    "\n"
    "    fragColor = vec4(mapped, hdrColor.a) * v_color;\n"
    "}\n";
*/

