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
    "uniform float u_base_nits;         // Target nits for standard 1.0 intensity (e.g., 100.0)\n"
    "uniform float u_max_nits;          // Physical limit of the CRT phosphor (e.g., 600.0)\n"
    "uniform float u_device_peak_nits;  // Monitor max physical capability (e.g., 1000.0)\n"
    "\n"
    "out vec4 fragColor;\n"
    "\n"
    "void main() {\n"
    "    // 1. BEAM ENERGY FETCH (Linear Accumulation Buffer)\n"
    "    // 'beam' represents the accumulated linear light from the rasterization phase.\n"
    "    vec3 beam = texture(s_texture, v_texuv).rgb * v_color.rgb;\n"
    "\n"
    "    vec3 mapped;\n"
    "\n"
    "    if (u_use_hdr_display == 1) {\n"
    "        // --- HDR PATH (scRGB Linear Space) ---\n"
    "        // In scRGB, a float value of 1.0 exactly equals 80 physical nits.\n"
    "\n"
    "        // Ensure we don't request more nits than the actual display can handle\n"
    "        float safe_peak_nits = min(u_max_nits, u_device_peak_nits);\n"
    "\n"
    "        // Convert target nits to scRGB float multipliers\n"
    "        float base_mult = u_base_nits / 80.0;\n"
    "        float peak_mult = safe_peak_nits / 80.0;\n"
    "\n"
    "        // 2. PRE-COMPENSATION GAIN (Calibration)\n"
    "        // We want an input beam of 1.0 to output exactly 'base_mult' after the saturation curve.\n"
    "        // The mathematical inverse of y = P * (1 - exp(-E/P)) is E = -P * log(1 - y/P).\n"
    "        // We clamp base_mult to 99% of peak_mult to prevent math errors (log of <= 0).\n"
    "        float safe_base_mult = min(base_mult, peak_mult * 0.99);\n"
    "        float gain = -peak_mult * log(1.0 - (safe_base_mult / peak_mult));\n"
    "\n"
    "        // Apply exposure and the calibration gain\n"
    "        vec3 energy = beam * u_exposure * gain;\n"
    "\n"
    "        // 3. PHYSICAL PHOSPHOR SATURATION (Exponential Model)\n"
    "        // Asymptotically compresses highlights. The more energy, the closer it gets to peak_mult\n"
    "        // without ever exceeding it, just like real CRT phosphor.\n"
    "        mapped = peak_mult * (vec3(1.0) - exp(-energy / peak_mult));\n"
    "\n"
    "    } else {\n"
    "        // --- SDR PATH (8-bit Standard Displays) ---\n"
    "\n"
    "        // Simple exponential tone mapping (keeps values strictly below 1.0)\n"
    "        vec3 raw = vec3(1.0) - exp(-beam * u_exposure);\n"
    "\n"
    "        // 4. DISPLAY GAMMA CORRECTION (OETF)\n"
    "        // Encodes the linear light into a Gamma 2.2 space so the SDR monitor displays it correctly.\n"
    "        mapped = pow(clamp(raw, 0.0, 1.0), vec3(1.0 / 2.2));\n"
    "    }\n"
    "\n"
    "    // Multiply the final mapped color by the alpha (which acts as a spatial mask/opacity)\n"
    "    fragColor = vec4(mapped, texture(s_texture, v_texuv).a * v_color.a);\n"
    "}\n";
		
