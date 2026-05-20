// license:BSD-3-Clause
// copyright-holders: David Valdeita (Seleuco) & Filipe Paulino (FlykeSpice)
/***************************************************************************

    gles3_renderer.cpp

    GL MAME Native renderer based on GLES 3.x for MAME4droid

***************************************************************************/

#include "gles3_renderer.h"
#include "gl_utils.hxx"

#include "shader_sources.hxx"

#include "modules/render/copyutil.h"

#include <android/log.h>

#include <string>
#include <stdexcept>
#include <algorithm>
#include <cmath>
#include <utility>

#define ANDROID_LOG(...) __android_log_print(ANDROID_LOG_DEBUG, "gles3_renderer", __VA_ARGS__)

/* =======================================================================
 * MAME CORE VECTOR PIPELINE ARCHITECTURE NOTES
 * =======================================================================
 * Based on analysis of MAME's core vector.cpp. These rules define how 
 * MAME generates and submits vector primitives to this GLES3 renderer.
 * * 1. INITIAL VECTOR BUFFER QUAD (THE CLEARING QUAD):
 * Before drawing any vectors, MAME always submits a full-screen black 
 * rectangle (0.0 to 1.0) with flags: BLENDMODE_ALPHA and VECTORBUF(1).
 * - Purpose: In MAME's software renderer, this acts as the "clear screen" 
 * command for the vector layer. 
 * - Relevance: When using our custom Dual-FBO pipeline and Painter's 
 * Algorithm over background artworks, we must intercept or properly 
 * handle this quad so it doesn't accidentally erase the artwork 
 * beneath the FBOs with an opaque black layer.
 * * 2. ADDITIVE BLENDING & INTENSITY (ALPHA CHANNEL):
 * Vector lines are ALWAYS submitted with BLENDMODE_ADD (GL_SRC_ALPHA, GL_ONE).
 * - Relevance: The Alpha channel (prim.color.a) does NOT represent 
 * standard opacity. It represents the true RAW INTENSITY of the 
 * electron beam (mapped from 0 to 255). MAME relies on pure additive 
 * blending to overlap lines and naturally build up brightness at vertices.
 * * 3. PRE-CALCULATED BEAM PHYSICS (CPU SIDE):
 * Be aware that MAME already applies several analog physics calculations on 
 * the CPU before the primitives reach our renderer. The 'prim.width' and 
 * 'prim.color.a' we receive have already been mutated by:
 * - FLICKER: Random intensity drops based on the user's flicker setting.
 * - SIGMOID CURVE: Intensity is passed through a normalized tunable Sigmoid 
 * function to realistically simulate phosphor response non-linearity.
 * - DYNAMIC WIDTH: Physical width interpolates between min/max based on intensity.
 * - POINT SCALING: Zero-length lines (dots) are pre-scaled by 'beam_dot_size'.
 * * Conclusion: Our custom HDR/Bloom physics must act as an *optical extension* * of these values, as the CPU has already baked the baseline electrical 
 * imperfections into the primitive's geometry and alpha channel.
 * ======================================================================= */



// -----------------------------------------------------------------------
// RENDER TARGET OPTIMIZATION (FILLRATE SAVINGS)
// -----------------------------------------------------------------------
// Resolution scale for the FBOs. 0.5f (Half-res) saves 75% of GPU fillrate 
// and provides a natural anti-aliasing optical softness to the glowing vectors.
constexpr float BLOOM_FBO_SCALE = 0.5f; 
//constexpr float BLOOM_FBO_SCALE = 1.0f; 

// The absolute minimum vertical resolution the FBO can drop to.
// Prevents the FBO from becoming a pixelated mess on older low-res screens.
constexpr float BLOOM_FBO_MIN_HEIGHT = 720.0f;

// =======================================================================
// VECTOR BLOOM EFFECT CONFIGURATION
// =======================================================================

// Defines the light falloff curve (Gaussian) for the CRT Phosphor.
// - Purpose: Controls how sharp or soft the core of the line is compared to its glowing halo.
// - Suggested Range: [3.0f - 6.0f] 
//   -> 3.0f = Modern HDR bloom (very soft, nebulous).
//   -> 5.0f = Sweet spot (solid core, smooth natural decay).
//   -> 6.0f = Pure 80s Arcade (extremely tight core, abrupt halo).
//constexpr float BLOOM_PHOSPHOR_FALLOFF = 5.0f;
constexpr float BLOOM_PHOSPHOR_FALLOFF = 4.5f;

// --- Lines (Standard vectors) ---
// - Purpose: Base physical width (in pixels) and base opacity for drawing standard lines.
// - Width Range: [3.0f - 6.0f] (4.0f is generally safe).
// - Alpha Range: [0.50f - 1.0f] (0.75f allows some transparency before HDR kicks in).
//constexpr float BLOOM_LINE_WIDTH_MULT = 3.5f;  
//constexpr float BLOOM_LINE_ALPHA      = 0.75f;

constexpr float BLOOM_LINE_WIDTH_MULT = 5.5f;  
constexpr float BLOOM_LINE_ALPHA      = 0.55f;

// --- Points (Stars / Shots / Explosions) ---
// - Purpose: Base physical width and opacity for drawing single points (vertices).
// - Width Range: [2.0f - 4.0f] (Keep it smaller than lines so stars look sharp).
// - Alpha Range: [0.40f - 0.85f] (Points naturally overlap less, 0.55f is a good baseline).
//constexpr float BLOOM_POINT_WIDTH_MULT = 2.5f; 
//constexpr float BLOOM_POINT_ALPHA      = 0.55f;
constexpr float BLOOM_POINT_WIDTH_MULT = 4.5f; 
constexpr float BLOOM_POINT_ALPHA      = 0.85f;

// =======================================================================
// DUAL-LOBE PHOSPHOR CONFIGURATION (CRT OPTICS)
// =======================================================================

// 1. Core Lobe Sharpness (Laser impact)
// - Purpose: Defines how sharp and bright the pure core of the vector is.
// - Suggested Range: [8.0f - 16.0f]. Higher value = thinner and harder laser. (12.0f recommended)
//constexpr float BLOOM_CORE_SHARPNESS = 12.0f;
constexpr float BLOOM_CORE_SHARPNESS = 14.0f;

// 2. Secondary Lobe Spread (Scattering halo)
// - Purpose: How far the light travels inside the CRT tube glass.
// - Suggested Range: [1.5f - 4.0f]. Lower value = wider halo. (2.5f recommended)
constexpr float BLOOM_GLOW_SPREAD = 2.5f;
//constexpr float BLOOM_GLOW_SPREAD = 1.2f;

// 3. Secondary Lobe Weight (Halo opacity)
// - Purpose: The intensity of the light fog surrounding the laser.
// - Suggested Range: [0.15f - 0.50f]. Higher value = thicker light fog. (0.35f recommended)
constexpr float BLOOM_GLOW_WEIGHT = 0.35f;
//constexpr float BLOOM_GLOW_WEIGHT = 0.65f;


// -----------------------------------------------------------------------
// EXCESS LIGHT PHYSICS (OVERBRIGHT / HDR)
// -----------------------------------------------------------------------

// The absolute maximum limit for extra HDR energy a vector can accumulate.
// - Purpose: Acts as a safety ceiling to prevent the bloom from completely white-washing the screen.
// - Suggested Range: [1.5f - 3.0f] (2.5f allows bright flashes without blinding the player).
//constexpr float BLOOM_OVERBRIGHT_MAX = 2.5f;
constexpr float BLOOM_OVERBRIGHT_MAX = 3.0f;

// How much lines and points physically expand their radius when overloaded with energy.
// - Purpose: Simulates the phosphor bleeding light into adjacent areas when saturated.
// - Suggested Range: [0.30f - 0.70f] (Above 0.8f, the lines will look like fat neon tubes).
//constexpr float BLOOM_OVERBRIGHT_LINE_MULT  = 0.55f; 
//constexpr float BLOOM_OVERBRIGHT_POINT_MULT = 0.45f;
constexpr float BLOOM_OVERBRIGHT_LINE_MULT  = 1.25f; 
constexpr float BLOOM_OVERBRIGHT_POINT_MULT = 2.35f;

// How much excess energy bleeds into other channels to create white highlights (Crosstalk).
// - Suggested Range: [0.10f - 0.50f] (0.25f creates a natural white core for bright vectors).
constexpr float BLOOM_OVERBRIGHT_CROSSTALK = 0.50f;


// -----------------------------------------------------------------------
// CRT GLOBAL DRIVE (MONITOR VOLTAGE / BRIGHTNESS)
// -----------------------------------------------------------------------

// Global energy multiplier applied to the raw alpha value provided by MAME.
// - Purpose: Simulates turning the "Brightness" or "Drive" knob on the back of the arcade monitor.
// - Suggested Range: [1.0f - 2.0f]
//   -> 1.0f = Dark, accurate, strictly follows MAME's alpha.
//   -> 1.35f = Recommended (Arcade monitor running slightly overdriven).
//   -> 1.8f+ = Extremely bright, almost everything will generate bloom.
//constexpr float BLOOM_GLOBAL_DRIVE_MULTIPLIER = 1.35f;
constexpr float BLOOM_GLOBAL_DRIVE_MULTIPLIER = 1.35f;
//constexpr float BLOOM_GLOBAL_DRIVE_MULTIPLIER = 2.2f;

// =======================================================================
// AUTO-EXPOSURE (HDR EYE ADAPTATION)
// =======================================================================
// Global multiplier to boost the overall brightness of the auto-exposure.
// - 1.0f = Standard dynamic range (1.6f down to 0.7f).
// - 1.20f = Boosts the entire dynamic range by 20% (brighter overall).
// - 0.80f = Dims the entire dynamic range by 20%.
constexpr float BLOOM_AUTO_EXPOSURE_MULT = 1.1f;

// The maximum percentage of the screen area that can be covered by full-intensity 
// vectors before the auto-exposure hits its maximum dimming limit (0.7f).
// - Suggested Range: [0.03f - 0.10f] 
//   -> 0.05f = 5% of screen area (Good baseline for fast reaction without over-dimming).
constexpr float BLOOM_AUTO_EXPOSURE_THRESHOLD = 0.05f;

// -----------------------------------------------------------------------
// BEAM SPEED PHYSICS (INTENSITY DYNAMICS)
// -----------------------------------------------------------------------

// What percentage of the screen height is considered a "short" line.
// - Purpose: Identifies high-energy strokes like text characters, ships, or small details.
// - Suggested Range: [0.02f - 0.10f] (0.04f = 4% of screen height, perfect for standard text).
constexpr float BLOOM_SHORT_LINE_THRESHOLD_PCT = 0.04f;

// How much extra light energy a tiny line receives.
// - Purpose: Simulates the electron beam burning the phosphor harder because it's moving less distance.
// - Suggested Range: [0.50f - 2.0f] (1.0f = +100% extra energy, makes text highly legible).
//constexpr float BLOOM_SHORT_LINE_INTENSITY_BOOST = 1.0f;
constexpr float BLOOM_SHORT_LINE_INTENSITY_BOOST = 0.5f;

// How much the core physically widens when burning the phosphor harder.
// - Purpose: Simulates thermal expansion of the dot on the screen.
// - Suggested Range: [0.10f - 0.40f] (0.20f = +20% thicker core for short lines).
//constexpr float BLOOM_SHORT_LINE_WIDTH_BOOST = 0.20f;
constexpr float BLOOM_SHORT_LINE_WIDTH_BOOST = 0.10f;

// What percentage of the screen height dictates a "short" line for halo compression.
// - Purpose: Shrinks the halo of small elements (like text) so they don't become blurry blobs.
// - Purpose: Shrinks the halo of small elements (like text) so they don't become blurry blobs.
constexpr float BLOOM_HALO_LENGTH_THRESHOLD_PCT = 0.15f;

// =======================================================================
// BEAM INERTIA & DWELL TIME (CORNER BURN)
// =======================================================================

// Master toggle to enable or disable the corner burn effect entirely.
constexpr bool BLOOM_CORNER_BURN_ENABLED = true;

// 1. Angular Threshold (Dot Product)
// - Purpose: How sharp a turn must be to cause the beam to decelerate and burn the corner.
// - Suggested Range: [0.30f - 0.70f]. 
//   -> 0.50f (60 degrees) triggers burns on sharp polygons like the Asteroids ship.
constexpr float BLOOM_CORNER_DOT_THRESHOLD = 0.50f;

// 2. Corner Burn Intensity Boost
// - Purpose: How much extra energy is dumped into the phosphor during the dwell time.
// - Suggested Range: [1.0f - 3.0f]. (1.5f provides a beautiful glowing weld effect at vertices).
//constexpr float BLOOM_CORNER_BURN_BOOST = 1.5f;
constexpr float BLOOM_CORNER_BURN_BOOST = 1.5;

// 3. Corner Burn Physical Size
// - Purpose: Confines the extra light inside the vector path to prevent spherical blobs at vertices.
// - Suggested Range: [0.15f - 0.30f]. (0.20f keeps the burn intense but visually sharp).
//constexpr float BLOOM_CORNER_BURN_WIDTH_MULT = 0.20f;
constexpr float BLOOM_CORNER_BURN_WIDTH_MULT = 0.20f;

// -----------------------------------------------------------------------
// ANALOG IMPERFECTIONS (NOISE & MAGNETIC JITTER)
// -----------------------------------------------------------------------

// Master toggle to enable or disable the magnetic jitter and thermal hash effect.
constexpr bool BLOOM_BEAM_JITTER_ENABLED = true;

// Maximum physical deviation of the beam due to magnetic coil noise/heat (in pixels).
// - Purpose: Adds a subtle, living vibration to the vectors, breaking the "perfect digital" look.
// - Suggested Range: [0.0f - 0.60f] 
//   -> 0.0f = Off (Perfectly stable lines).
//   -> 0.15f = Recommended (Subtle electric hum).
//   -> 0.60f+ = Heavy wear/damaged yoke (Looks like a broken monitor).
constexpr float BLOOM_BEAM_JITTER_AMOUNT = 0.15f;

// Maximum intensity drop caused by voltage fluctuation (Flicker).
// - Purpose: Works with magnetic jitter to create an electrical buzz visible at ANY resolution.
// - Suggested Range: [0.0f - 0.30f] (0.15f = up to 15% brightness drop).
constexpr float BLOOM_BEAM_FLICKER_AMOUNT = 0.15f;

// =======================================================================
// PHOSPHOR COLOR RESPONSE (LUMINANCE & BLEED)
// =======================================================================

// Master toggle to enable or disable the phosphor color response (luminance calculation).
// - Purpose: Disabling this treats all colors (Red, Green, Blue) equally with 100% efficiency.
//   Turn to 'false' if you feel certain colors (like pure Blue) are too dim.
constexpr bool BLOOM_PHOSPHOR_RESPONSE_ENABLED = false;

// 1. Perceptual Color Weights (Rec.601 / NTSC standard)
// - Purpose: Defines how strongly each color excites the CRT phosphor.
//   Green is highly efficient and bleeds heavily. Blue is inefficient and tight.
constexpr float BLOOM_PHOSPHOR_WEIGHT_R = 0.299f;
constexpr float BLOOM_PHOSPHOR_WEIGHT_G = 0.587f;
constexpr float BLOOM_PHOSPHOR_WEIGHT_B = 0.114f;

// 2. Base Phosphor Response (Floor)
// - Purpose: The minimum energy retained by the darkest/least efficient color (Blue).
// - Suggested Range: [0.30f - 0.50f]. (0.40f ensures blue vectors remain visible).
constexpr float BLOOM_PHOSPHOR_BASE_RESPONSE = 0.40f;

// 3. Luminance Multiplier
// - Purpose: How much the calculated color luminance boosts the final beam energy.
// - Suggested Range: [0.40f - 0.80f]. (0.60f combined with a 0.40f base perfectly caps at 1.0).
constexpr float BLOOM_PHOSPHOR_LUMA_BOOST = 0.60f;


struct line_aa_step {
	float xoffs, yoffs;
	float weight;
};

static const line_aa_step line_aa_1step[] = {
	{  0.00f,  0.00f,  1.00f },
	{ 0 }
};

static const line_aa_step line_aa_4step[] = {
	{ -0.25f,  0.00f,  0.25f },
	{  0.25f,  0.00f,  0.25f },
	{  0.00f, -0.25f,  0.25f },
	{  0.00f,  0.25f,  0.25f },
	{ 0 }
};

using gles_texture = gles3_renderer::gles_texture;

static std::pair<render_bounds, render_bounds> render_line_to_quad(const render_bounds& bounds, float width, float extension)
{
    render_bounds b0, b1;
    float dx = bounds.x1 - bounds.x0;
    float dy = bounds.y1 - bounds.y0;
    float length = std::sqrt(dx * dx + dy * dy);

    if (length > 0.0001f)
    {
        float half_width = width * 0.5f;
        float nx = -dy / length * half_width;
        float ny =  dx / length * half_width;

        b0.x0 = bounds.x0 + nx;  b0.y0 = bounds.y0 + ny;
        b0.x1 = bounds.x0 - nx;  b0.y1 = bounds.y0 - ny;

        b1.x0 = bounds.x1 + nx;  b1.y0 = bounds.y1 + ny;
        b1.x1 = bounds.x1 - nx;  b1.y1 = bounds.y1 - ny;
    }
    else
    {
        b0.x0 = b0.x1 = bounds.x0; b0.y0 = b0.y1 = bounds.y0;
        b1.x0 = b1.x1 = bounds.x1; b1.y0 = b1.y1 = bounds.y1;
    }
    return std::make_pair(b0, b1);
}

//Prototypes
static HashT texture_compute_hash(const render_texinfo& texture, const u32 flags);
static void texture_copy_data(void* dest, const render_texinfo& texinfo, u32 texformat);
static bool compare_texture_primitive(const gles_texture& texture, const render_primitive& prim);

void gles3_renderer::set_shader(const char* shader_name)
{
    ANDROID_LOG("set_shader %s...", shader_name);
    if (shader_name)
    {
        if (m_lastfilter != shader_name)
        {
            auto it = std::find_if(s_filters.begin(), s_filters.end(),
                                   [&](const std::pair<std::string, filter_data>& p) { return p.first == shader_name; });

            if (it != s_filters.end())
            {
                m_filter.load_filter(it->second.source, it->second.linear);
                m_lastfilter = shader_name;
            }
            else
            {
                ANDROID_LOG("shader not found!");
                return;
            }
        }
        m_usefilter = true;
    }
    else
    {
        m_lastfilter = "";
        m_usefilter = false;
    }
}

std::vector<std::string> gles3_renderer::get_shaders_supported()
{
    std::vector<std::string> key_list;
    for (const auto& [key, value] : s_filters)
        key_list.push_back(key);

    return key_list;
}

gles3_renderer::gles3_renderer(int width, int height, bool use_hdr_display, float peak_multiplier)
{
	m_use_hdr_display = use_hdr_display; // Store the display mode path
	m_peak_multiplier = peak_multiplier;
	
	__android_log_print(ANDROID_LOG_DEBUG, "gles3_renderer", 
        "=== C++ PIPELINE VERIFICATION: SCREEN MODE IS %s ===", 
        m_use_hdr_display ? "REAL HDR (10-BIT)" : "STANDARD SDR (8-BIT)");
	
	//First and foremost, let's check whether a shader compiler is supported.
	//Unfortunately, GLES 2 specification doesn't demand that every implementation bundle a shader compiler on the graphics driver
	GLboolean supported;
	glGetBooleanv(GL_SHADER_COMPILER, &supported);
	if (supported == GL_FALSE)
	{
		throw std::runtime_error("GLES2: Shader compilation isn't supported by your phone graphics driver");
	}

	//Disable some 3D stuff we don't use
	glDisable(GL_DEPTH_TEST);
	glDisable(GL_SCISSOR_TEST);
	glDisable(GL_CULL_FACE);
	glDisable(GL_POLYGON_OFFSET_FILL);

	glDisable(GL_BLEND);
	/* Init quad shader program*/
	GLuint quad_vertex_shader = gl_utils::load_shader(quad_vertex_shader_src, GL_VERTEX_SHADER);
	GLuint quad_frag_shader   = gl_utils::load_shader(quad_frag_shader_src,   GL_FRAGMENT_SHADER);

	m_quad_program = gl_utils::create_program(quad_vertex_shader, quad_frag_shader, {
        {0, "a_corner"}, {1, "i_p0p1"}, {2, "i_p2p3"}, 
        {3, "i_uv0uv1"}, {4, "i_uv2uv3"}, {5, "i_color"}
    });
	
	GLuint hdr_frag_shader = gl_utils::load_shader(hdr_frag_shader_src, GL_FRAGMENT_SHADER);
	m_hdr_program = gl_utils::create_program(quad_vertex_shader, hdr_frag_shader, {
        {0, "a_corner"}, {1, "i_p0p1"}, {2, "i_p2p3"}, 
        {3, "i_uv0uv1"}, {4, "i_uv2uv3"}, {5, "i_color"}
    });
	glDeleteShader(hdr_frag_shader);
	m_uniform_ortho_hdr = glGetUniformLocation(m_hdr_program, "u_ortho");
	m_uniform_exposure_hdr = glGetUniformLocation(m_hdr_program, "u_exposure");
	m_uniform_use_hdr_display = glGetUniformLocation(m_hdr_program, "u_use_hdr_display");
	m_uniform_peak_multiplier = glGetUniformLocation(m_hdr_program, "u_peak_multiplier");	
	
	glUseProgram(m_hdr_program);
	glUniform1i(m_uniform_use_hdr_display, m_use_hdr_display ? 1 : 0);
	glUniform1f(m_uniform_peak_multiplier, m_peak_multiplier);	
	glUniform1i(glGetUniformLocation(m_hdr_program, "s_texture"), 0);
	glUseProgram(0);	
	
	//Flag the shader objects for deletion, so they don't leak when the user is switching renderers
	glDeleteShader(quad_vertex_shader);
	glDeleteShader(quad_frag_shader);

	//We're not gonna be compiling shaders anymore, release up the shader compiler resources
	glReleaseShaderCompiler();

	m_uniform_ortho_quad = glGetUniformLocation(m_quad_program, "u_ortho");

	auto sampler_uniform = glGetUniformLocation(m_quad_program, "s_texture");
	glUniform1i(sampler_uniform, 0); //set sampler2D texture unit to 0
	
	glGenTextures(1, &m_white_texture);
	glActiveTexture(GL_TEXTURE0);
	glBindTexture(GL_TEXTURE_2D, m_white_texture);
	uint32_t white_pixel = 0xFFFFFFFF; // RGBA white
	glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, &white_pixel);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);

	glGenTextures(1, &m_glow_texture); // GLOW texture
	glBindTexture(GL_TEXTURE_2D, m_glow_texture);
	
	uint32_t glow_pixels[128 * 128];
	
	for (int y = 0; y < 128; y++) {
		for (int x = 0; x < 128; x++) {
			// Instead of treating x and y equally, we apply "Astigmatism" to the glass.
			// Center is now 63.5f for a 128x128 texture.
			// By compressing X, the light reaches further (wider halo horizontally).
			// By expanding Y, the light cuts off sooner (narrower halo vertically).
			float dx = ((x - 63.5f) / 63.5f) * 0.85f; // Travels easier horizontally
			float dy = ((y - 63.5f) / 63.5f) * 1.15f; // Has more resistance vertically
			float dist = std::sqrt(dx*dx + dy*dy);
			
			// --- DUAL-LOBE PHOSPHOR OPTICS ---
			// 1. Dense and sharp core (Beam impact)
			float core = std::exp(-(dist * dist) * BLOOM_CORE_SHARPNESS); 
			
			// 2. Soft and expansive halo (Optical scattering)
			float glow = std::exp(-(dist * dist) * BLOOM_GLOW_SPREAD) * BLOOM_GLOW_WEIGHT; 
			
			// Add both lobes and clamp to 1.0 for mathematical safety
			float intensity = std::min(core + glow, 1.0f);
			// ---------------------------------
			
			// --- PRE-MULTIPLIED ALPHA FOR PURE ADDITIVE BLENDING ---
			// Bake the intensity directly into the RGB channels.
			// Alpha is hardcoded to 1.0 (255) to prevent math issues in pure additive passes.
			uint8_t c = (uint8_t)(intensity * 255.0f);
			
			// Pack into RGBA (Little-endian: A is highest byte, RGB are lowest)
			// Format: 0xAABBGGRR -> (255 << 24) | (B << 16) | (G << 8) | R
			glow_pixels[y * 128 + x] = (255 << 24) | (c << 16) | (c << 8) | c; 
		}
	}

	// Upload the new high-res 128x128 texture
	glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 128, 128, 0, GL_RGBA, GL_UNSIGNED_BYTE, glow_pixels);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);	
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
	// Static base geometry (6 indices to form 2 triangles)
    glGenBuffers(1, &m_corner_vbo);
    glBindBuffer(GL_ARRAY_BUFFER, m_corner_vbo);
    float corners[6] = {0.0f, 1.0f, 2.0f, 0.0f, 2.0f, 3.0f};
    glBufferData(GL_ARRAY_BUFFER, sizeof(corners), corners, GL_STATIC_DRAW);

    // Dynamic buffer for instance data
    glGenBuffers(1, &m_instance_vbo);

    m_batch_instances.reserve(4096);
	
	glPixelStorei(GL_UNPACK_ALIGNMENT, 1);

	on_emulatedsize_change(width, height);
}

void gles3_renderer::end_renderer()
{
    m_flush_textures = true;
}

void gles3_renderer::on_emulatedsize_change(int width, int height)
{
    std::lock_guard<std::mutex> lock(m_render_mutex);	
	
	m_ortho = gl_utils::make_ortho(0.0f, width, height, 0.0f, -1.0f, 1.0f);
	m_width = width; m_height = height;

    m_last_filter_mode = myosd_get(MYOSD_BITMAP_FILTERING);

    m_flush_textures = true;
    m_filter.set_ortho(m_ortho);
	
	m_fbo_dirty = true;
	
	m_init = true;
}

void gles3_renderer::create_fbos(int width, int height, bool need_hdr, bool need_sdr) {
    // LAZY INITIALIZATION: We only request GPU memory if the buffer doesn't exist yet.
    // (We no longer blindly delete FBOs here)
    
    if (need_hdr && m_fbo_hdr == 0) {
        glGenTextures(1, &m_fbo_texture_hdr);
        glBindTexture(GL_TEXTURE_2D, m_fbo_texture_hdr);
		glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA16F, width, height, 0, GL_RGBA, GL_HALF_FLOAT, nullptr);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);

        glGenFramebuffers(1, &m_fbo_hdr);
        glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_hdr);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, m_fbo_texture_hdr, 0);
    }
    
    if (need_sdr && m_fbo_sdr == 0) {
        glGenTextures(1, &m_fbo_texture_sdr);
        glBindTexture(GL_TEXTURE_2D, m_fbo_texture_sdr);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);

        glGenFramebuffers(1, &m_fbo_sdr);
        glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_sdr);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, m_fbo_texture_sdr, 0);
    }
	GLenum status = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    if (status != GL_FRAMEBUFFER_COMPLETE) 
		ANDROID_LOG("Error creando FBO: %d", status);	    
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
}


void gles3_renderer::delete_fbos() {
    if (m_fbo_hdr) glDeleteFramebuffers(1, &m_fbo_hdr);
    if (m_fbo_texture_hdr) glDeleteTextures(1, &m_fbo_texture_hdr);
    
    if (m_fbo_sdr) glDeleteFramebuffers(1, &m_fbo_sdr);
    if (m_fbo_texture_sdr) glDeleteTextures(1, &m_fbo_texture_sdr);
    
    m_fbo_hdr = 0; m_fbo_texture_hdr = 0;
    m_fbo_sdr = 0; m_fbo_texture_sdr = 0;
}

void gles3_renderer::set_blendmode(int blendmode)
{
	// try to minimize texture state changes
	if (blendmode != m_last_blendmode)
	{
		switch (blendmode)
		{
			case BLENDMODE_NONE:
				glDisable(GL_BLEND);
				break;
			case BLENDMODE_ALPHA:
				glEnable(GL_BLEND);
				glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
				break;
			case BLENDMODE_RGB_MULTIPLY:
				glEnable(GL_BLEND);
				glBlendFunc(GL_DST_COLOR, GL_ZERO);
				break;
			case BLENDMODE_ADD:
				glEnable(GL_BLEND);
				glBlendFunc(GL_SRC_ALPHA, GL_ONE);
				break;
		}

		m_last_blendmode = blendmode;
	}
}

void gles3_renderer::sync_state(const render_primitive_list* primlist)
{
	//clean old textures
	cleanup_texture_cache();
	
	std::vector<local_primitive> temp_prims;

    // Deep copy
    for (const render_primitive& prim : *primlist) 
	{
        local_primitive lp;
        lp.type = prim.type;
		lp.bounds = prim.bounds;
		lp.color = prim.color;
        lp.texcoords = prim.texcoords;
		lp.flags = prim.flags;
		lp.width = prim.width;
		lp.texture = nullptr;

        if (prim.type == render_primitive::QUAD && prim.texture.base != nullptr)
        {
            update_texture_cache(prim, lp.texture);
        }
		temp_prims.push_back(lp);				
    }
	
	{
		std::lock_guard<std::mutex> lock(m_render_mutex);
        
		for (auto& lp : temp_prims) {
			if (lp.texture) {
			
				if (lp.texture->needs_gl_update) {
					std::swap(lp.texture->base, lp.texture->base_back);                
					lp.needs_texture_upload = true;
					lp.texture->needs_gl_update = false; 
				}

				lp.upload_ptr = lp.texture->base; 
			}
        }	

        m_render_prims = std::move(temp_prims);
        
        m_render_textures_to_delete.insert(m_render_textures_to_delete.end(), 
                                           m_textures_to_delete.begin(), 
                                           m_textures_to_delete.end());
        m_textures_to_delete.clear();
/*		
		std::string traza = "";
		int estaticas = 0, dinamicas = 0;

		for (const auto& tex : m_texlist) {
			bool es_dinamica = (tex->base_back != nullptr);
			es_dinamica ? dinamicas++ : estaticas++;
			char buf[64];
			snprintf(buf, sizeof(buf), "[ID:%u %dx%d %s]", 
					 tex->texture_id, tex->texinfo.width, tex->texinfo.height, 
					 es_dinamica ? "DIN" : "EST");
			traza += buf;
    }	
		ANDROID_LOG("CACHE TOTAL -> Elementos: %zu (Estaticas: %d | Dinamicas: %d) Info: %s", m_texlist.size(), estaticas, dinamicas, traza.c_str());
*/
	}

}

void gles3_renderer::push_quad(const float* verts, const float* uv, const render_color& color) 
{
    // Flush if we reach a safe batch size (around 10k quads at once)
    if (m_batch_instances.size() >= 10000) { 
        flush_batch();
    }
    
    static const float default_uv[8] = {0.0f};
    const float* actual_uv = uv ? uv : default_uv;
    
    // Pack everything into a single contiguous data block
    m_batch_instances.push_back({
        verts[0], verts[1], verts[2], verts[3],
        verts[4], verts[5], verts[6], verts[7],
        actual_uv[0], actual_uv[1], actual_uv[2], actual_uv[3],
        actual_uv[4], actual_uv[5], actual_uv[6], actual_uv[7],
        color.r, color.g, color.b, color.a
    });
}

void gles3_renderer::flush_batch() 
{
    if (m_batch_instances.empty()) return;
	
    // Explicitly protect external VAOs before binding our buffers
    glBindVertexArray(0);	

    // 1. Send the master block of instance data to the GPU
    glBindBuffer(GL_ARRAY_BUFFER, m_instance_vbo);
    
    // Explicit Orphaning: We pass 'nullptr' to force the driver to allocate a 
    // new memory block, preventing it from waiting for the GPU to finish reading the old one (stall).
    size_t data_size = m_batch_instances.size() * sizeof(instance_data);
    glBufferData(GL_ARRAY_BUFFER, data_size, nullptr, GL_DYNAMIC_DRAW);
    
    // Upload the new data to the newly allocated, stall-free memory block
    glBufferSubData(GL_ARRAY_BUFFER, 0, data_size, m_batch_instances.data());

    // 2. Configure the corner buffer (Advances once per VERTEX)
    glBindBuffer(GL_ARRAY_BUFFER, m_corner_vbo);
    glVertexAttribPointer(0, 1, GL_FLOAT, GL_FALSE, 0, nullptr);
    glEnableVertexAttribArray(0);
    glVertexAttribDivisor(0, 0); 

    // 3. Configure the instance buffer (Advances once per INSTANCE)
    glBindBuffer(GL_ARRAY_BUFFER, m_instance_vbo);
    int stride = sizeof(instance_data);
    
    glVertexAttribPointer(1, 4, GL_FLOAT, GL_FALSE, stride, (void*)0);
    glVertexAttribPointer(2, 4, GL_FLOAT, GL_FALSE, stride, (void*)(4 * sizeof(float)));
    glVertexAttribPointer(3, 4, GL_FLOAT, GL_FALSE, stride, (void*)(8 * sizeof(float)));
    glVertexAttribPointer(4, 4, GL_FLOAT, GL_FALSE, stride, (void*)(12 * sizeof(float)));
    glVertexAttribPointer(5, 4, GL_FLOAT, GL_FALSE, stride, (void*)(16 * sizeof(float)));

    for (int i = 1; i <= 5; i++) {
        glEnableVertexAttribArray(i);
        glVertexAttribDivisor(i, 1); // Instancing instruction
    }

    // 4. DRAW EVERYTHING AT ONCE! (6 corners per instance)
    glDrawArraysInstanced(GL_TRIANGLES, 0, 6, m_batch_instances.size());

    // 5. CLEANUP: Absolute State Isolation
    // Explicitly close all pipelines (Attributes 0 to 5)
    for (int i = 0; i <= 5; i++) {
        glDisableVertexAttribArray(i);
    }
    // Reset the instancing divisor
    for (int i = 1; i <= 5; i++) {
        glVertexAttribDivisor(i, 0); 
    }
    // Unbind the buffer so MAME doesn't read it by mistake
    glBindBuffer(GL_ARRAY_BUFFER, 0);

    m_batch_instances.clear();
}

void gles3_renderer::upload_pending_textures(std::vector<local_primitive>& draw_prims)
{
	for (local_primitive& prim : draw_prims) {
		if (prim.texture && (prim.texture->needs_gl_init || prim.needs_texture_upload)) {
			if (prim.texture->needs_gl_init) {
				glGenTextures(1, &prim.texture->texture_id);
				glBindTexture(GL_TEXTURE_2D, prim.texture->texture_id);

				//GLint internal_format = m_use_hdr_display ? GL_SRGB8_ALPHA8 : GL_RGBA;				
				//glTexImage2D(GL_TEXTURE_2D, 0, internal_format, prim.texture->texinfo.width, prim.texture->texinfo.height, 0, GL_RGBA, GL_UNSIGNED_BYTE, prim.upload_ptr);
				
				glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, prim.texture->texinfo.width, prim.texture->texinfo.height, 0, GL_RGBA, GL_UNSIGNED_BYTE, prim.upload_ptr);
				
				GLint filter_mode = myosd_get(MYOSD_BITMAP_FILTERING) ? GL_LINEAR : GL_NEAREST;
				if (PRIMFLAG_GET_SCREENTEX(prim.flags) && m_usefilter) {
					filter_mode = m_filter.is_linear() ? GL_LINEAR : GL_NEAREST;
				}
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter_mode);
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter_mode);
				GLint wrapmode = PRIMFLAG_GET_TEXWRAP(prim.flags) ? GL_REPEAT : GL_CLAMP_TO_EDGE;
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, wrapmode);
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, wrapmode);
				prim.texture->needs_gl_init = false;
			} else if (prim.needs_texture_upload) {
				glBindTexture(GL_TEXTURE_2D, prim.texture->texture_id);
				glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, prim.texture->texinfo.width, prim.texture->texinfo.height, GL_RGBA, GL_UNSIGNED_BYTE, prim.upload_ptr);
			}
			prim.needs_texture_upload = false; 
		}
	}
}

bool gles3_renderer::calculate_auto_exposure(const std::vector<local_primitive>& draw_prims)
{
    bool has_vectors = false;
    float scene_energy = 0.0f;
	
    for (const auto& prim : draw_prims) {
        if (PRIMFLAG_GET_VECTOR(prim.flags)) {
            has_vectors = true;
            
            float dx = prim.bounds.x1 - prim.bounds.x0;
            float dy = prim.bounds.y1 - prim.bounds.y0;
            float len = std::sqrt(dx*dx + dy*dy);
            
			if (len < 0.001f) continue;

            float ideal_width = std::max(prim.width, 0.01f);
            float simulated_energy = prim.color.a * BLOOM_GLOBAL_DRIVE_MULTIPLIER;
            
            // 2. Approximate the "Short Line Boost" (Crucial for flashes, text and debris)
            float threshold = m_height * BLOOM_SHORT_LINE_THRESHOLD_PCT;
            if (len < threshold && len > 0.1f) {
                float shortness = 1.0f - (len / threshold);
                simulated_energy *= (1.0f + shortness * BLOOM_SHORT_LINE_INTENSITY_BOOST);
                ideal_width *= (1.0f + shortness * BLOOM_SHORT_LINE_WIDTH_BOOST);
            }
            
            // 3. Calculate the physical bloom expansion due to Overbright
            float overbright_raw = std::max(simulated_energy - 1.0f, 0.0f);
            float overbright = std::min(std::pow(overbright_raw, 0.7f), BLOOM_OVERBRIGHT_MAX);
            
            float dynamic_width = ideal_width * (BLOOM_LINE_WIDTH_MULT + (overbright * BLOOM_OVERBRIGHT_LINE_MULT));
            
            // 4. THE REAL CALCULATION: Total emitted area * Energy of that area
            float emitted_area = len * dynamic_width;
            scene_energy += emitted_area * simulated_energy;
        }
    }

    // --- AUTO-EXPOSURE (Eye Adaptation Logic) ---
    if (has_vectors) {
        // 5. RESOLUTION-INDEPENDENT 2D NORMALIZATION
        // Normalize energy against the total screen area.
        float screen_area = (float)(m_width * std::max(m_height, 1));

        float normalized_energy = scene_energy / (screen_area * BLOOM_AUTO_EXPOSURE_THRESHOLD); 

        // Base exposure is 1.6f for dark games (Asteroids).
        // As energy rises (Tempest/Explosions), target exposure drops to a minimum of 0.7f.
        float target_exposure = std::clamp(1.6f - normalized_energy, 0.7f, 1.6f);

        target_exposure *= BLOOM_AUTO_EXPOSURE_MULT;

        // Temporal Smoothing (Moving Average)
        // The eye reacts quickly to bright flashes (explosions), but recovers slowly in the dark.
        float adaptation_speed = (target_exposure < m_current_exposure) ? 0.3f : 0.02f;
        m_current_exposure += (target_exposure - m_current_exposure) * adaptation_speed;
		
		// --- DEBUG LOGGING (Traza cada 1 segundo) ---
		static osd_ticks_t last_log_time = 0;
        osd_ticks_t current_ticks = osd_ticks();
        
        if (current_ticks - last_log_time >= osd_ticks_per_second()) {
            ANDROID_LOG("HDR Auto-Exposure -> Energy (Norm): %.4f | Target Exp: %.3f | Current Exp: %.3f", 
                        normalized_energy, target_exposure, m_current_exposure);
            last_log_time = current_ticks;
        }
    }
	
	 //m_current_exposure = 1.5f;
    
    return has_vectors;
}

void gles3_renderer::resolve_hdr(GLuint target_fbo, float layout_w, float layout_h, 
	const render_bounds& layout_bounds, const std::array<float, 16>& vector_ortho)
{
    // Bind destination (Screen or SDR FBO)
    glBindFramebuffer(GL_FRAMEBUFFER, target_fbo);
    
    float fbo_verts[8] = { layout_bounds.x0, layout_bounds.y0, layout_bounds.x0, layout_bounds.y1, layout_bounds.x1, layout_bounds.y1, layout_bounds.x1, layout_bounds.y0 };
    float fbo_uv[8] = { 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f };

    glUseProgram(m_hdr_program);

    if (target_fbo == 0) {
        // Resolving directly to Screen: Use global mobile viewports and screen ortho matrix
        glViewport(0, 0, m_view_width, m_view_height);
        glUniformMatrix4fv(m_uniform_ortho_hdr, 1, GL_FALSE, m_ortho.data());
		
		// Update display path uniform based on target surface capabilities
        glUniform1i(m_uniform_use_hdr_display, m_use_hdr_display ? 1 : 0);	
    } else {
        // Resolving to SDR FBO: Use layout dimensions and specialized vector layout ortho matrix
        glViewport(0, 0, (GLsizei)layout_w, (GLsizei)layout_h);
        glUniformMatrix4fv(m_uniform_ortho_hdr, 1, GL_FALSE, vector_ortho.data());
		
		glUniform1i(m_uniform_use_hdr_display, 0);
    }

    // CRITICAL: Vectors are pure light. Add light to the background, never overwrite it.
    glEnable(GL_BLEND);
    glBlendFunc(GL_ONE, GL_ONE); 

    glUniform1f(m_uniform_exposure_hdr, m_current_exposure); // Tone Mapping exposure

    m_current_texture = m_fbo_texture_hdr;
    glBindTexture(GL_TEXTURE_2D, m_current_texture);

    render_color white = { 1.0f, 1.0f, 1.0f, 1.0f }; 
    push_quad(fbo_verts, fbo_uv, white);
    flush_batch();

    // Clear HDR FBO immediately so it's ready for the next vector batch
    glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_hdr);
    glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    
    // Restore state
    glUseProgram(m_quad_program);
    m_current_texture = 0; 
    m_last_blendmode = -1;
}

void gles3_renderer::switch_fbo_target(int target_fbo, int& current_fbo, bool require_sdr, 
	float layout_w, float layout_h, const render_bounds& layout_bounds, const std::array<float, 16>& vector_ortho)
{
    // If we are already in the correct FBO, do nothing
    if (current_fbo == target_fbo) return;

    // Flush any pending geometry before switching contexts
    flush_batch();
    
    // --- PIPELINE UNWINDING (Cascading Resolve) ---
    
    // Step 1: If leaving HDR FBO, resolve its light down the chain safely via Tone Mapper
    if (current_fbo == 2) {
        GLuint resolve_target = require_sdr ? m_fbo_sdr : 0;
        resolve_hdr(resolve_target, layout_w, layout_h, layout_bounds, vector_ortho);
        current_fbo = require_sdr ? 1 : 0; // State drops to SDR (1) or Screen (0)
    }
    
    // Step 2: If we are now in SDR, and the target is Screen, apply CRT filter
    if (current_fbo == 1 && target_fbo == 0) {
        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        glViewport(0, 0, m_view_width, m_view_height);
        
        // PIXEL-PERFECT RASTER EQUIVALENT: Render the CRT filter exactly on the layout box area
        // using pure normalized UV coordinates (0.0 to 1.0) because the FBO is now a clean texture.
        float fbo_verts[8] = { layout_bounds.x0, layout_bounds.y0, layout_bounds.x0, layout_bounds.y1, layout_bounds.x1, layout_bounds.y1, layout_bounds.x1, layout_bounds.y0 };
        float fbo_uv[8] = { 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f };

        // Pass layout dimensions as the clean native texture size
        int tex_w = (int)layout_w;
        int tex_h = (int)layout_h;	
        
        // CRITICAL: Pure additive blend (GL_ONE, GL_ONE) to add filtered vectors over the background artwork
        glEnable(GL_BLEND);
        glBlendFunc(GL_ONE, GL_ONE); 
        
        m_filter.draw_quad(m_fbo_texture_sdr, fbo_verts, fbo_uv, tex_w, tex_h, m_view_width, m_view_height);
        
        glUseProgram(m_quad_program);
        glUniformMatrix4fv(m_uniform_ortho_quad, 1, GL_FALSE, m_ortho.data()); // Restore main ortho
        m_current_texture = 0; m_last_blendmode = -1;
		
		// Clear SDR FBO immediately so it's clean for the next batch of vectors
        glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_sdr);
        glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        glClear(GL_COLOR_BUFFER_BIT);		
    }
    
    // --- PIPELINE SETUP ---
    
    // Step 3: Bind the newly requested target and update projection matrices dynamically.
    // CRITICAL: glUniformMatrix4fv only affects the CURRENTLY bound shader program. 
    // We must explicitly bind m_quad_program before uploading the ortho matrix to ensure 
    // state isolation and prevent uniform leakage into external filter shaders.
    if (target_fbo == 2) {
        glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_hdr);
        glViewport(0, 0, (GLsizei)layout_w, (GLsizei)layout_h);
        glUseProgram(m_quad_program); // Ensure base shader is active
        glUniformMatrix4fv(m_uniform_ortho_quad, 1, GL_FALSE, vector_ortho.data()); // Apply vector layout matrix
    } else if (target_fbo == 1) {
        glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_sdr);
        glViewport(0, 0, (GLsizei)layout_w, (GLsizei)layout_h);
        glUseProgram(m_quad_program);
        glUniformMatrix4fv(m_uniform_ortho_quad, 1, GL_FALSE, vector_ortho.data());
    } else if (target_fbo == 0) {
        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        glViewport(0, 0, m_view_width, m_view_height);
        glUseProgram(m_quad_program);
        glUniformMatrix4fv(m_uniform_ortho_quad, 1, GL_FALSE, m_ortho.data()); // Restore global screen matrix
    }
    
    // Save the new state
    current_fbo = target_fbo;
}

void gles3_renderer::process_dwell_point(const local_primitive& prim, bool is_vector, bool enable_bloom, float current_time, float& prev_x, float& prev_y, float& prev_dx_norm, float& prev_dy_norm)
{
    // Early exit if the effect is disabled globally or not applicable
    if (!BLOOM_CORNER_BURN_ENABLED || !is_vector || !enable_bloom) {
        return; 
    }

    float px0 = prim.bounds.x0; float py0 = prim.bounds.y0;
    float px1 = prim.bounds.x1; float py1 = prim.bounds.y1;
    
    float dx = px1 - px0;
    float dy = py1 - py0;
    float len = std::sqrt(dx*dx + dy*dy);
    
    if (len > 0.001f) {
        float dx_norm = dx / len;
        float dy_norm = dy / len;
        
        // --- RESOLUTION INDEPENDENCE (Connection Threshold) ---
        // At 480p, 1.0 pixel is a safe threshold to consider two lines connected.
        // On modern high-DPI screens (1080p+), floating point math can cause connected
        // vectors to be slightly further apart. We scale the threshold to match!
        float scale_factor = (float)std::max(m_height, 1) / 480.0f;
        float connect_threshold = 1.0f * scale_factor;
        
        // If the current line starts where the previous one ended (continuous stroke)
        if (std::abs(px0 - prev_x) < connect_threshold && std::abs(py0 - prev_y) < connect_threshold) {
            
            // Dot Product to find the turn angle
            float dot = (prev_dx_norm * dx_norm) + (prev_dy_norm * dy_norm);
            
            // If the turn is sharper than our threshold
            if (dot < BLOOM_CORNER_DOT_THRESHOLD) {
                
                // Calculate brake aggressiveness (0.0 to 1.0)
                float sharpness = (BLOOM_CORNER_DOT_THRESHOLD - dot) / (BLOOM_CORNER_DOT_THRESHOLD + 1.0f);
                
                // INJECT A "DWELL POINT" (Corner Burn)
                local_primitive corner_prim = prim;
                corner_prim.bounds.x0 = px0; corner_prim.bounds.x1 = px0;
                corner_prim.bounds.y0 = py0; corner_prim.bounds.y1 = py0;
                
                // Confine the light physically inside the vector
                corner_prim.width = prim.width * BLOOM_CORNER_BURN_WIDTH_MULT;
                
				// Boost the point's energy based on turn sharpness
				float energy_boost = 1.0f + (sharpness * BLOOM_CORNER_BURN_BOOST);

				// --- TRUE HDR PHYSICS ---
				// We DO NOT clamp to 1.0f anymore. The 16-bit FBO needs to receive 
				// values way above 1.0 to trigger the physical 'Overbright' bloom expansion!
				corner_prim.color.a = prim.color.a * energy_boost;
                
                // Draw the bright point before drawing the actual line
                process_line_primitive(corner_prim, is_vector, enable_bloom, current_time);
            }
        }
        
        // Save the beam state to compare with the next line
        prev_x = px1;
        prev_y = py1;
        prev_dx_norm = dx_norm;
        prev_dy_norm = dy_norm;
        
    } else {
        // If length is 0 (it was an isolated point), break the continuous stroke
        prev_x = -9999.0f; 
    }
}

void gles3_renderer::apply_magnetic_jitter(float& px0, float& py0, float& px1, float& py1, bool is_vector, bool enable_bloom, float current_time)
{
    // Early exit if the effect is disabled globally or not applicable
    if (!BLOOM_BEAM_JITTER_ENABLED || !is_vector || !enable_bloom || BLOOM_BEAM_JITTER_AMOUNT <= 0.0f) {
        return;
    }

    float center_x = (px0 + px1) * 0.5f; 
    float center_y = (py0 + py1) * 0.5f;
    
    // 1. RESOLUTION INDEPENDENCE (Normalize to 0.0 - 1.0)
    float nx = center_x / (float)std::max(m_width, 1);
    float ny = center_y / (float)std::max(m_height, 1);
    float safe_time = std::fmod(current_time, 100.0f); // Wraps every 100 seconds

    // 2. LOW-FREQUENCY DRIFT (Thermal and magnetic drift)
    float drift_x = std::sin(safe_time * 2.1f + ny * 3.14f);
    float drift_y = std::cos(safe_time * 1.8f + nx * 3.14f);

    // 3. AC MAINS HUM (Coil electromagnetic interference)
    float ac_x = std::sin(safe_time * 45.0f + ny * 15.0f);
    float ac_y = std::cos(safe_time * 55.0f + nx * 15.0f);

    // 4. THERMAL HASH (Approximate blue noise)
    float noise_x = std::sin((nx * 12.989f + ny * 78.233f + safe_time) * 437.58f);
    float noise_y = std::cos((nx * 39.346f + ny * 11.135f + safe_time) * 437.58f);

    // FINAL MIX: 40% Slow Drift | 40% AC Hum | 20% Thermal Noise
    float final_jx = (drift_x * 0.40f) + (ac_x * 0.40f) + (noise_x * 0.20f);
    float final_jy = (drift_y * 0.40f) + (ac_y * 0.40f) + (noise_y * 0.20f);

    // --- MASSIVE DPI OVERRIDE ---
    // At 1080p+, a linear pixel displacement is absorbed by anti-aliasing.
    // By raising the scale to the power of 2.5, we force the vertices to jump 
    // far enough to physically break through the high pixel density.
    float linear_scale = (float)std::max(m_height, 1) / 480.0f;
    float scale_factor = linear_scale;
    if (linear_scale > 1.05f) {
        scale_factor = std::pow(linear_scale, 2.5f);
    }

    float jx = final_jx * BLOOM_BEAM_JITTER_AMOUNT * scale_factor;
    float jy = final_jy * BLOOM_BEAM_JITTER_AMOUNT * scale_factor;

    // Apply the offset to the vertices
    px0 += jx; 
    py0 += jy; 
    px1 += jx; 
    py1 += jy;
}

void gles3_renderer::process_line_primitive(const local_primitive& prim, bool is_vector, bool enable_bloom, float current_time)
{
    //float effwidth = std::max(prim.width, 1.0f);
	// --- SUB-PIXEL FIX: Do not force width to 1.0f here. We rescue the real (ideal) width requested by MAME. ---
    float ideal_width = std::max(prim.width, 0.01f);

    // Extract base coordinates
    float px0 = prim.bounds.x0; float py0 = prim.bounds.y0;
    float px1 = prim.bounds.x1; float py1 = prim.bounds.y1;
	
    // --- MAGNETIC WOBBLE & THERMAL HASH (Analog Jitter) ---
	apply_magnetic_jitter(px0, py0, px1, py1, is_vector, enable_bloom, current_time);
    
    // Calculate final line distances
    float dx = px1 - px0; 
    float dy = py1 - py0;
    bool is_point = (std::abs(dx) < 0.001f && std::abs(dy) < 0.001f);
    float length = is_point ? 0.0f : std::sqrt(dx*dx + dy*dy);
    
    // 1. Get raw MAME intensity (0.0 to 1.0)
    float base_intensity = prim.color.a;

	// --- VOLTAGE FLICKER (The ultimate anti-AA jitter) ---
    // High DPI screens hide positional jumps with anti-aliasing.
    // By injecting high-frequency noise directly into the brightness of the beam,
    // we guarantee the "electric buzz" is visible at ANY resolution.
    if (is_vector && enable_bloom && BLOOM_BEAM_JITTER_ENABLED) {
        float safe_time = std::fmod(current_time, 100.0f);
        float hash = std::sin((px0 * 12.989f + py0 * 78.233f + safe_time) * 437.58f);
        
        // Dims the beam randomly by up to 15% to simulate voltage drops
        float flicker = 1.0f - (std::abs(hash) * BLOOM_BEAM_FLICKER_AMOUNT);
        base_intensity *= flicker;
    }

    // --- PHOSPHOR COLOR RESPONSE (Luminance & Bleed) ---
    float phosphor_response = 1.0f;
    float drive = enable_bloom ? BLOOM_GLOBAL_DRIVE_MULTIPLIER : 1.0f; 
    
    if (enable_bloom && BLOOM_PHOSPHOR_RESPONSE_ENABLED) {
        float luminance = (prim.color.r * BLOOM_PHOSPHOR_WEIGHT_R) + (prim.color.g * BLOOM_PHOSPHOR_WEIGHT_G) + (prim.color.b * BLOOM_PHOSPHOR_WEIGHT_B);
        phosphor_response = BLOOM_PHOSPHOR_BASE_RESPONSE + (luminance * BLOOM_PHOSPHOR_LUMA_BOOST);
    }

    // 2. Apply simulated energy physics
    float simulated_energy = base_intensity * drive * phosphor_response;

    // 3. BEAM SPEED DYNAMICS (Electron gun physics)
    if (is_vector && !is_point) {
        float threshold = m_height * BLOOM_SHORT_LINE_THRESHOLD_PCT; 
        if (length < threshold && length > 0.1f) {
            float shortness = 1.0f - (length / threshold);
            simulated_energy *= (1.0f + shortness * BLOOM_SHORT_LINE_INTENSITY_BOOST);
            ideal_width *= (1.0f + shortness * BLOOM_SHORT_LINE_WIDTH_BOOST);
        }
    }
	
    // ====================================================================
    // 4 & 5. ROUTE ENERGY (HDR vs SDR)
    // ====================================================================
    float col_r, col_g, col_b;
    float core_alpha = 1.0f;
    float overbright = 0.0f;

    if (enable_bloom) {
        // --- TRUE HDR PATH (16-Bit Float) ---		
        // Pre-multiply intensity directly into RGB
        col_r = prim.color.r * simulated_energy;
        col_g = prim.color.g * simulated_energy;
        col_b = prim.color.b * simulated_energy;
		
        core_alpha = 1.0f; // Alpha acts strictly as a spatial mask (1.0)
		
        // Calculate overbright for physical expansion (bloom radius)		
        float overbright_raw = std::max(simulated_energy - 1.0f, 0.0f);
        overbright = std::min(std::pow(overbright_raw, 0.7f), BLOOM_OVERBRIGHT_MAX);
        
        // CROSSTALK (Color Desaturation / Highlight Simulation)
        // If the light intensity exceeds 1.0, the excess energy "spills over" 
        // into the other color channels, pushing the core towards pure white.		
        if (overbright > 0.0f) {
            float crosstalk = overbright * BLOOM_OVERBRIGHT_CROSSTALK; 
            col_r += crosstalk; col_g += crosstalk; col_b += crosstalk;
        }       
        
    } else {
        // --- NO BLOOM PATH (8-Bit SDR Standard) ---
        // We CANNOT pre-multiply the alpha into RGB here because MAME's 
        // 8-bit pipeline and classic glBlendFunc expect traditional RGBA.
        col_r = prim.color.r;
        col_g = prim.color.g;
        col_b = prim.color.b;
        
        // In 8-bit, Alpha controls the intensity. We clamp it safely.
        core_alpha = base_intensity;
    }
    
    float bloom_scale = 1.0f;

    // ====================================================================
    // 6. DRAW PRIMITIVES
    // ====================================================================
    if (is_point) {
        if (is_vector && enable_bloom) {
            float dynamic_width = BLOOM_POINT_WIDTH_MULT + (overbright * BLOOM_OVERBRIGHT_POINT_MULT);
            float ideal_bloom_w = ideal_width * dynamic_width * bloom_scale;
            
            // --- SUB-PIXEL ENERGY CONSERVATION (Point Bloom) ---
            // Prevent geometry from dropping below 2.0 pixels to avoid rasterization drop (flickering).
            // If forced to scale up, we proportionally dim the energy to conserve total light emitted.
            float safe_bloom_w = std::max(ideal_bloom_w, 2.0f);
            float comp = ideal_bloom_w / safe_bloom_w;
            
            m_quad_verts[0] = px0 - safe_bloom_w; m_quad_verts[1] = py0 - safe_bloom_w; 
            m_quad_verts[2] = px0 - safe_bloom_w; m_quad_verts[3] = py0 + safe_bloom_w; 
            m_quad_verts[4] = px0 + safe_bloom_w; m_quad_verts[5] = py0 + safe_bloom_w; 
            m_quad_verts[6] = px0 + safe_bloom_w; m_quad_verts[7] = py0 - safe_bloom_w; 
            float bloom_uv[8] = { 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.0f };
            
            // --- PRE-MULTIPLIED ALPHA (Point Bloom - HDR ONLY) ---
            render_color c_bloom = { 1.0f, col_r * BLOOM_POINT_ALPHA * comp, col_g * BLOOM_POINT_ALPHA * comp, col_b * BLOOM_POINT_ALPHA * comp };
            push_quad(m_quad_verts, bloom_uv, c_bloom);
        }

        float ideal_half_w = ideal_width * 0.5f;
        
        // --- SUB-PIXEL ENERGY CONSERVATION (Point Core) ---
        float safe_half_w = std::max(ideal_half_w, 0.5f); // Total geometry width = 1.0
        float comp_core = ideal_half_w / safe_half_w;
        
        m_quad_verts[0] = px0 - safe_half_w; m_quad_verts[1] = py0 - safe_half_w; 
        m_quad_verts[2] = px0 - safe_half_w; m_quad_verts[3] = py0 + safe_half_w; 
        m_quad_verts[4] = px0 + safe_half_w; m_quad_verts[5] = py0 + safe_half_w; 
        m_quad_verts[6] = px0 + safe_half_w; m_quad_verts[7] = py0 - safe_half_w; 
        float core_uv[8] = { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
        
        // POINT CORE (Discriminación dinámica)
        render_color c_core;
        if (enable_bloom) {
            c_core = { 1.0f, col_r * comp_core, col_g * comp_core, col_b * comp_core }; // HDR Pre-multiplied
        } else {
            c_core = { core_alpha * comp_core, col_r, col_g, col_b }; // SDR Standard Alpha
        }
        push_quad(m_quad_verts, is_vector ? core_uv : nullptr, c_core);
        
    } else {
        if (is_vector && enable_bloom) {
            // HALO (BLOOM) WIDTH DYNAMICS ---
            // Short lines get a massive core energy boost. To preserve shape and readability, 
            // we compress their halo spread by up to 50% compared to long lines.			
            float halo_threshold = m_height * BLOOM_HALO_LENGTH_THRESHOLD_PCT;
            float length_factor = std::min(length / halo_threshold, 1.0f); 

            // Base multiplier scales from 0.5x (point/tiny line) to 1.0x (long line)            
            float dynamic_bloom_mult = (BLOOM_LINE_WIDTH_MULT * (0.5f + 0.5f * length_factor)) + (overbright * BLOOM_OVERBRIGHT_LINE_MULT);
            float ideal_bloom_w = ideal_width * dynamic_bloom_mult * bloom_scale;
            
            // --- SUB-PIXEL ENERGY CONSERVATION (Line Bloom) ---
            // Prevent geometry from dropping below 2.0 pixels.
            float safe_bloom_w = std::max(ideal_bloom_w, 2.0f);
            float comp = ideal_bloom_w / safe_bloom_w;
            
            float half_w = safe_bloom_w * 0.5f;

            float nx = (-dy / length) * half_w; float ny = ( dx / length) * half_w;
            float dx_ext = (dx / length) * half_w; float dy_ext = (dy / length) * half_w;

            float ax0 = px0 + nx; float ay0 = py0 + ny;
            float ax1 = px0 - nx; float ay1 = py0 - ny;
            float bx0 = px1 + nx; float by0 = py1 + ny;
            float bx1 = px1 - nx; float by1 = py1 - ny;

            // --- PRE-MULTIPLIED ALPHA (Line Bloom - HDR ONLY) ---
            render_color c_bloom = { 1.0f, col_r * BLOOM_LINE_ALPHA * comp, col_g * BLOOM_LINE_ALPHA * comp, col_b * BLOOM_LINE_ALPHA * comp };

            // 1. START CAP
            m_quad_verts[0] = ax0 - dx_ext; m_quad_verts[1] = ay0 - dy_ext; 
            m_quad_verts[2] = ax1 - dx_ext; m_quad_verts[3] = ay1 - dy_ext; 
            m_quad_verts[4] = ax1;          m_quad_verts[5] = ay1; 
            m_quad_verts[6] = ax0;          m_quad_verts[7] = ay0; 
            float cap1_uv[8] = { 0.0f, 0.0f,  1.0f, 0.0f,  1.0f, 0.5f,  0.0f, 0.5f };
            push_quad(m_quad_verts, cap1_uv, c_bloom);

            // 2. BODY
            m_quad_verts[0] = ax0; m_quad_verts[1] = ay0; 
            m_quad_verts[2] = ax1; m_quad_verts[3] = ay1; 
            m_quad_verts[4] = bx1; m_quad_verts[5] = by1; 
            m_quad_verts[6] = bx0; m_quad_verts[7] = by0; 
            float body_uv[8] = { 0.0f, 0.5f,  1.0f, 0.5f,  1.0f, 0.5f,  0.0f, 0.5f };
            push_quad(m_quad_verts, body_uv, c_bloom);

            // 3. END CAP
            m_quad_verts[0] = bx0;          m_quad_verts[1] = by0; 
            m_quad_verts[2] = bx1;          m_quad_verts[3] = by1; 
            m_quad_verts[4] = bx1 + dx_ext; m_quad_verts[5] = by1 + dy_ext; 
            m_quad_verts[6] = bx0 + dx_ext; m_quad_verts[7] = by0 + dy_ext; 
            float cap2_uv[8] = { 0.0f, 0.5f,  1.0f, 0.5f,  1.0f, 1.0f,  0.0f, 1.0f };
            push_quad(m_quad_verts, cap2_uv, c_bloom);
        }

        // --- CORE ---
        // --- SUB-PIXEL ENERGY CONSERVATION (Line Core) ---
        float safe_core_w = std::max(ideal_width, 1.0f);
        float comp_core = ideal_width / safe_core_w;

        render_bounds jittered_bounds = { px0, py0, px1, py1 };
        auto [b0, b1] = render_line_to_quad(jittered_bounds, safe_core_w, 0.0f);
        
        bool use_aa = PRIMFLAG_GET_ANTIALIAS(prim.flags) && !enable_bloom;
        const line_aa_step* step = use_aa ? line_aa_4step : line_aa_1step;
        
        for (; step->weight != 0.0f; step++) {
            render_color c;
            
            // LINE CORE (Dynamic AA discrimination)
            if (enable_bloom) {
                // --- HDR PATH: Pre-multiply AA weight into RGB ---
                c.a = 1.0f; 
                c.r = col_r * step->weight * comp_core;
                c.g = col_g * step->weight * comp_core;
                c.b = col_b * step->weight * comp_core;
            } else {
                // --- SDR PATH: Apply AA weight to Alpha only ---
                c.a = core_alpha * step->weight * comp_core; 
                c.r = col_r;
                c.g = col_g;
                c.b = col_b;
            }

            m_quad_verts[0] = b0.x0 + step->xoffs; m_quad_verts[1] = b0.y0 + step->yoffs; 
            m_quad_verts[2] = b0.x1 + step->xoffs; m_quad_verts[3] = b0.y1 + step->yoffs; 
            m_quad_verts[4] = b1.x1 + step->xoffs; m_quad_verts[5] = b1.y1 + step->yoffs; 
            m_quad_verts[6] = b1.x0 + step->xoffs; m_quad_verts[7] = b1.y0 + step->yoffs; 

            float core_uv[8] = { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
            push_quad(m_quad_verts, is_vector ? core_uv : nullptr, c);
        }
    }	
}

void gles3_renderer::process_quad_primitive(const local_primitive& prim, bool is_screen, int needed_blend)
{
    m_quad_verts[0] = prim.bounds.x0; m_quad_verts[1] = prim.bounds.y0; 
    m_quad_verts[2] = prim.bounds.x0; m_quad_verts[3] = prim.bounds.y1; 
    m_quad_verts[4] = prim.bounds.x1; m_quad_verts[5] = prim.bounds.y1; 
    m_quad_verts[6] = prim.bounds.x1; m_quad_verts[7] = prim.bounds.y0; 

    if (prim.texture) {
        const render_quad_texuv& texuv = prim.texcoords;
        m_quad_uv[0] = texuv.tl.u; m_quad_uv[1] = texuv.tl.v;
        m_quad_uv[2] = texuv.bl.u; m_quad_uv[3] = texuv.bl.v;
        m_quad_uv[4] = texuv.br.u; m_quad_uv[5] = texuv.br.v;
        m_quad_uv[6] = texuv.tr.u; m_quad_uv[7] = texuv.tr.v;

        if (m_usefilter && is_screen) {

            flush_batch();
            set_blendmode(needed_blend);
            m_filter.draw_quad(m_current_texture, m_quad_verts, m_quad_uv, prim.texture->texinfo.width, prim.texture->texinfo.height, m_view_width, m_view_height);
            glUseProgram(m_quad_program);
            m_current_texture = 0; m_last_blendmode = -1;
        } else {

            push_quad(m_quad_verts, m_quad_uv, prim.color);
        }
    } else {

        push_quad(m_quad_verts, nullptr, prim.color);
    }
}

void gles3_renderer::render()
{
	float current_time = (float)osd_ticks() / (float)osd_ticks_per_second();	
	
	// ELECTRON GUN STATE (To calculate Dwell Time at the corners) ---
    float prev_x = -9999.0f;
    float prev_y = -9999.0f;
    float prev_dx_norm = 0.0f;
    float prev_dy_norm = 0.0f;	
	
	std::vector<local_primitive> draw_prims;
    std::vector<GLuint> delete_texs;

    {
        std::lock_guard<std::mutex> lock(m_render_mutex);
        draw_prims = m_render_prims; 
        delete_texs = std::move(m_render_textures_to_delete);
        m_render_textures_to_delete.clear();
    }
	
	// Unbind any VAO or Buffer left open by the shader during screen rotation
    glBindVertexArray(0);
    glBindBuffer(GL_ARRAY_BUFFER, 0);
    glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
			
	if (m_init) { m_init = false; }

	glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
	
	GLint viewport[4];
    glGetIntegerv(GL_VIEWPORT, viewport);
    if (viewport[2] > 0 && viewport[3] > 0)
    {
        m_view_width = viewport[2];
        m_view_height = viewport[3];
    }

	upload_pending_textures(draw_prims);

	glUseProgram(m_quad_program);
	glUniformMatrix4fv(m_uniform_ortho_quad, 1, GL_FALSE, m_ortho.data());

	m_current_texture = 0; 
    m_last_blendmode = -1; 
	
	// ------------------------------------------------------------------
    // PRE-PASS: Fast check for vectors & SCENE ENERGY RADAR (Auto-Exposure)
    // ------------------------------------------------------------------	
    bool has_vectors = calculate_auto_exposure(draw_prims);

    bool enable_bloom = myosd_get(MYOSD_VECTOR_BLOOM) ? true : false;
    bool require_hdr = has_vectors && enable_bloom;
    bool require_sdr = has_vectors && m_usefilter;

	// FBO state tracking
    static float last_fbo_w = 0.0f;
    static float last_fbo_h = 0.0f;
    bool fbo_initialized = false;
    
    render_bounds layout_bounds = { 0.0f, 0.0f, (float)m_width, (float)m_height };
    float layout_w = (float)m_width;
    float layout_h = (float)m_height;
    float fbo_w = layout_w;
    float fbo_h = layout_h;
	
    // Create a specialized ortho matrix for the vector pass.
    // This forces OpenGL to stretch and fit coordinates inside the smaller FBO seamlessly.	
    auto vector_ortho = gl_utils::make_ortho(layout_bounds.x0, layout_bounds.x1, layout_bounds.y1, layout_bounds.y0, -1.0f, 1.0f);

    // Always start targeted at the Screen (0) for background artworks
    int current_fbo = 0; 
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glViewport(0, 0, m_view_width, m_view_height);

    for (const local_primitive& prim : draw_prims)
    {
        // ==================================================================
        // MULTI-MONITOR / COCKTAIL HANDLING (VECTORBUF)
        // ==================================================================
        // MAME sends a VECTORBUF primitive (a black quad) to mark the 
        // beginning of a new vector screen layout.
        if (PRIMFLAG_GET_VECTORBUF(prim.flags)) {
            
            // 1. Flush any pending vectors from the PREVIOUS monitor to the screen
            if (fbo_initialized) {
                switch_fbo_target(0, current_fbo, require_sdr, fbo_w, fbo_h, layout_bounds, vector_ortho);
            }

            // 2. Capture the exact layout boundaries of the NEW monitor
            layout_bounds = prim.bounds;
            layout_w = layout_bounds.x1 - layout_bounds.x0;
            layout_h = layout_bounds.y1 - layout_bounds.y0;
            if (layout_w <= 0.0f) layout_w = (float)m_width;
            if (layout_h <= 0.0f) layout_h = (float)m_height;

            // --- DOWN-SAMPLING (FILLRATE OPTIMIZATION) ---
            fbo_w = layout_w; 
            fbo_h = layout_h;
            if (fbo_h * BLOOM_FBO_SCALE >= BLOOM_FBO_MIN_HEIGHT) {
                fbo_w *= BLOOM_FBO_SCALE; 
                fbo_h *= BLOOM_FBO_SCALE;
            } else if (fbo_h > BLOOM_FBO_MIN_HEIGHT) {
                float ratio = BLOOM_FBO_MIN_HEIGHT / fbo_h;
                fbo_w *= ratio; 
                fbo_h *= ratio;
            }
            // ---------------------------------------------

            // 3. Reallocate FBO memory ONLY if the layout dimensions changed
            if (fbo_w != last_fbo_w || fbo_h != last_fbo_h || m_fbo_dirty) {
                delete_fbos();
                m_fbo_dirty = false;
                last_fbo_w = fbo_w;
                last_fbo_h = fbo_h;
            }

            // 4. Create FBOs (if they were deleted or didn't exist yet)
            create_fbos((int)fbo_w, (int)fbo_h, require_hdr, require_sdr);

            // 5. Clear ACTIVE FBOs to OPAQUE BLACK (Alpha 1.0f) for the new monitor
            if (require_hdr) {
                glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_hdr);
                glClearColor(0.0f, 0.0f, 0.0f, 1.0f); glClear(GL_COLOR_BUFFER_BIT);
            }
            if (require_sdr) {
                glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_sdr);
                glClearColor(0.0f, 0.0f, 0.0f, 1.0f); glClear(GL_COLOR_BUFFER_BIT);
            }

            // Return binding to Screen (0) to maintain state consistency
            glBindFramebuffer(GL_FRAMEBUFFER, 0);

            // 6. Generate the specific ortho matrix for this layout
            vector_ortho = gl_utils::make_ortho(layout_bounds.x0, layout_bounds.x1, layout_bounds.y1, layout_bounds.y0, -1.0f, 1.0f);
            
            fbo_initialized = true;

            // Skip drawing this black quad (our FBOs are already cleared)
            continue; 
        }

        bool is_screen = PRIMFLAG_GET_SCREENTEX(prim.flags);
        bool is_vector = PRIMFLAG_GET_VECTOR(prim.flags);
        
        // --- FALLBACK FOR GAMES WITHOUT VECTORBUF ---
        // If MAME submits vectors without a VECTORBUF quad (extremely rare), initialize fallback FBOs
        if (is_vector && !fbo_initialized) {
            
            layout_bounds = { 0.0f, 0.0f, (float)m_width, (float)m_height };
            layout_w = (float)m_width; 
            layout_h = (float)m_height;
            
            // --- DOWN-SAMPLING ---
            fbo_w = layout_w; 
            fbo_h = layout_h;
            if (fbo_h * BLOOM_FBO_SCALE >= BLOOM_FBO_MIN_HEIGHT) {
                fbo_w *= BLOOM_FBO_SCALE; 
                fbo_h *= BLOOM_FBO_SCALE;
            } else if (fbo_h > BLOOM_FBO_MIN_HEIGHT) {
                float ratio = BLOOM_FBO_MIN_HEIGHT / fbo_h;
                fbo_w *= ratio; 
                fbo_h *= ratio;
            }
            // ---------------------
            
            if (fbo_w != last_fbo_w || fbo_h != last_fbo_h || m_fbo_dirty) {
                delete_fbos(); m_fbo_dirty = false; last_fbo_w = fbo_w; last_fbo_h = fbo_h;
            }
            
            create_fbos((int)fbo_w, (int)fbo_h, require_hdr, require_sdr);
            
            if (require_hdr) { glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_hdr); glClearColor(0,0,0,1); glClear(GL_COLOR_BUFFER_BIT); }
            if (require_sdr) { glBindFramebuffer(GL_FRAMEBUFFER, m_fbo_sdr); glClearColor(0,0,0,1); glClear(GL_COLOR_BUFFER_BIT); }
            
            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            vector_ortho = gl_utils::make_ortho(layout_bounds.x0, layout_bounds.x1, layout_bounds.y1, layout_bounds.y0, -1.0f, 1.0f);
            fbo_initialized = true;
        }       
        
        // Target: Vectors go to FBOs. UI, Backgrounds, and Raster games stay on Screen (0).
        int target_fbo = 0; 
        if (is_vector) {
            if (require_hdr) target_fbo = 2;
            else if (require_sdr) target_fbo = 1;
        }

        // --- PIPELINE FLUSH ---
        // Context switcher automatically flushes and binds correct shaders/matrices
        switch_fbo_target(target_fbo, current_fbo, require_sdr, fbo_w, fbo_h, layout_bounds, vector_ortho);

        GLuint needed_tex = (prim.texture != nullptr) ? prim.texture->texture_id : (is_vector ? m_glow_texture : m_white_texture);
        int needed_blend = PRIMFLAG_GET_BLENDMODE(prim.flags);

        if (m_current_texture != needed_tex || m_last_blendmode != needed_blend) {
            flush_batch();
            m_current_texture = needed_tex; set_blendmode(needed_blend);
            glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, m_current_texture);
        }

        switch (prim.type)
        {
            case render_primitive::LINE:
                process_dwell_point(prim, is_vector, enable_bloom, current_time, prev_x, prev_y, prev_dx_norm, prev_dy_norm);                    
                process_line_primitive(prim, is_vector, enable_bloom, current_time);
                break;
            case render_primitive::QUAD:
                process_quad_primitive(prim, is_screen, needed_blend);
                break;
            case render_primitive::INVALID: break;
        }
    }

    flush_batch();
    
	// --- TRAILING PIPELINE FLUSH ---
    // Force context switch back to 0 to trigger resolve/filter cascading logic for the LAST monitor processed
    if (fbo_initialized) {
        switch_fbo_target(0, current_fbo, require_sdr, fbo_w, fbo_h, layout_bounds, vector_ortho);
    }

    if (!delete_texs.empty()) 
        glDeleteTextures(delete_texs.size(), delete_texs.data());
}


static void texture_copy_data(void* dest, const render_texinfo& texinfo, u32 texformat)
{
	for (int y=0; y<texinfo.height; y++)
	{
		uint32_t *dst = (u32*)dest + (texinfo.width * y);
		#define src(T) (T*)texinfo.base + (texinfo.rowpixels * y)

		switch (texformat)
		{
			case TEXFORMAT_RGB32: copy_util::copyline_rgb32(dst, src(u32), texinfo.width, texinfo.palette); break;
			case TEXFORMAT_ARGB32: copy_util::copyline_argb32(dst, src(u32), texinfo.width, texinfo.palette); break;
			case TEXFORMAT_PALETTE16: copy_util::copyline_palette16(dst, src(u16), texinfo.width, texinfo.palette); break;
			case TEXFORMAT_YUY16: copy_util::copyline_yuy16_to_argb(dst, src(u16), texinfo.width, texinfo.palette, 1); break;
		}
		#undef src
	}
}

void gles3_renderer::update_texture_cache(const render_primitive& prim, std::shared_ptr<gles_texture>& out_tex)
{
	std::shared_ptr<gles_texture> texture = texture_find(prim, osd_ticks());

	if (texture == nullptr) {
		out_tex = texture_create(prim);
    }
	else
	{
		if (texture->texinfo.seqid != prim.texture.seqid)
		{
			texture->texinfo.seqid = prim.texture.seqid;
			if (texture->base_back == nullptr) {
				texture->base_back = std::malloc((texture->texinfo.width * 4) * texture->texinfo.height);
			}
			texture_copy_data(texture->base_back, prim.texture, PRIMFLAG_GET_TEXFORMAT(prim.flags));
            texture->needs_gl_update = true;
		}
        out_tex = texture;
	}
}

std::shared_ptr<gles3_renderer::gles_texture> gles3_renderer::texture_create(const render_primitive& prim)
{
	const render_texinfo& texinfo = prim.texture;
    std::shared_ptr<gles_texture> texture = std::make_shared<gles_texture>();
	m_texlist.push_front(texture);

	texture->hash = texture_compute_hash(texinfo, prim.flags);
	texture->texinfo = texinfo;
	texture->prim_flags = prim.flags;

	texture->base = std::malloc((texinfo.width * 4) * texinfo.height);
	texture->owned = true;

	texture_copy_data(texture->base, texinfo, PRIMFLAG_GET_TEXFORMAT(prim.flags));

    texture->needs_gl_init = true;
	texture->last_access = osd_ticks();

	return texture;
}

std::shared_ptr<gles3_renderer::gles_texture> gles3_renderer::texture_find(const render_primitive& prim, osd_ticks_t now)
{
	for (auto& tex : m_texlist)
	{
		if (compare_texture_primitive(*tex, prim))
		{
			tex->last_access = now;
			return tex;
		}
	}
	return nullptr;
}

void gles3_renderer::cleanup_texture_cache()
{
    if (m_flush_textures || m_last_filter_mode != myosd_get(MYOSD_BITMAP_FILTERING))
	{
        m_last_filter_mode = myosd_get(MYOSD_BITMAP_FILTERING);
		for (auto& tex : m_texlist) {
            if (tex->texture_id != 0) m_textures_to_delete.push_back(tex->texture_id);
        }		
        m_texlist.clear();
		m_flush_textures = false;
    }
	
	//clean old textures
	osd_ticks_t now = osd_ticks();
	for (auto it = m_texlist.begin(); it != m_texlist.end(); )
	{
		if ((now - (*it)->last_access) > osd_ticks_per_second())
		{
			if ((*it)->texture_id > 0) {
				m_textures_to_delete.push_back((*it)->texture_id);
			}				
			it = m_texlist.erase(it);
	    }	
		else
		{
			++it;
		}
	}	
}

//=========================================================
// Texture hashing utilities
//=========================================================

//Copy-pasted from osd/module/render/drawsdl3accel.cpp
static inline HashT texture_compute_hash(const render_texinfo &texture, const u32 flags)
{
	return (HashT)texture.base ^ (flags & (PRIMFLAG_BLENDMODE_MASK | PRIMFLAG_TEXFORMAT_MASK));
}

static bool compare_texture_primitive(const gles_texture& texture, const render_primitive& prim)
{
	//Just compare if the dimensions are the same, we can update the pixel data if they changed
	return texture.texinfo.base == prim.texture.base
		&& texture.texinfo.width     == prim.texture.width
		&& texture.texinfo.height    == prim.texture.height
		&& texture.texinfo.rowpixels == prim.texture.rowpixels
		&& texture.texinfo.palette   == prim.texture.palette
		&& ((texture.prim_flags ^ prim.flags) & (PRIMFLAG_BLENDMODE_MASK | PRIMFLAG_TEXFORMAT_MASK)) == 0;
}

