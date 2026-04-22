// license:BSD-3-Clause
// copyright-holders:David Valdeita (Seleuco) & Filipe Paulino (FlykeSpice)
/***************************************************************************

    gles1_renderer.cpp

    GL software renderer based on GLES 1.x for MAME4droid

***************************************************************************/

#include "myosd_core.h"

#include "gles1_renderer.h"
#include "gl_utils.hxx"
#include "rendersw.hxx"

#include <stdexcept>

#include <GLES/gl.h>
#include <GLES/glext.h>

// Función para calcular la potencia de dos más cercana (como en GLRendererES10.java)
static int get_pot_size(int size) {
    int p2Size = 1;
    while (p2Size < size) {
        p2Size <<= 1;
    }
    return p2Size;
}

gles1_renderer::gles1_renderer(int width, int height)
        : m_screenbuff(nullptr)
{
    // Deshabilitar 3D y opciones que no usamos
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_SCISSOR_TEST);
    glDisable(GL_CULL_FACE);
    glDisable(GL_BLEND);
    glDisable(GL_LIGHTING);
    glDisable(GL_DITHER);

    // En GLES 1.x es OBLIGATORIO habilitar las texturas 2D
    glEnable(GL_TEXTURE_2D);

    //glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);

    glActiveTexture(GL_TEXTURE0);
    glGenTextures(1, &m_texture_id);
    glBindTexture(GL_TEXTURE_2D, m_texture_id);

    glTexParameterf(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameterf(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

    glTexEnvf(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, GL_REPLACE);

    glMatrixMode(GL_PROJECTION);
    glLoadIdentity();
    glMatrixMode(GL_MODELVIEW);
    glLoadIdentity();

    on_emulatedsize_change(width, height);
}

void gles1_renderer::render(const render_primitive_list& primlist)
{
    //glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    //glClear(GL_COLOR_BUFFER_BIT);

    software_renderer<uint32_t, 0, 0, 0, 0, 8, 16>::draw_primitives(primlist, m_screenbuff, m_width, m_height, m_pitch);

    glBindTexture(GL_TEXTURE_2D, m_texture_id);

    int current_filter = myosd_get(MYOSD_BITMAP_FILTERING);
    if (m_last_filter_mode != current_filter) {
        m_last_filter_mode = current_filter;
        GLfloat filter_mode = current_filter ? GL_LINEAR : GL_NEAREST;
        glTexParameterf(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter_mode);
        glTexParameterf(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter_mode);
    }

    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, m_width, m_height, GL_RGBA, GL_UNSIGNED_BYTE, m_screenbuff);

    static const GLfloat verts[] = {
            -1.0f,  1.0f,
            -1.0f, -1.0f,
            1.0f,  1.0f,
            1.0f, -1.0f
    };

    glEnableClientState(GL_VERTEX_ARRAY);
    glEnableClientState(GL_TEXTURE_COORD_ARRAY);

    glVertexPointer(2, GL_FLOAT, 0, verts);
    glTexCoordPointer(2, GL_FLOAT, 0, m_texcoords); // Usamos la variable precalculada

    glColor4f(1.0f, 1.0f, 1.0f, 1.0f);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);

    glDisableClientState(GL_TEXTURE_COORD_ARRAY);
    glDisableClientState(GL_VERTEX_ARRAY);
}

void gles1_renderer::on_emulatedsize_change(int width, int height)
{
    m_width = width;
    m_height = height;
    m_pitch = width;

    // Calculamos el tamaño "Power Of Two" exigido por GLES 1.x
    m_tex_width = get_pot_size(width);
    m_tex_height = get_pot_size(height);

    void* new_buff = std::realloc(m_screenbuff, m_pitch * height * 4);
    if (new_buff) {
        m_screenbuff = new_buff;
    } else {
        throw std::runtime_error("GLES1 Software: Out of memory during screen buffer reallocation");
    }

    // Le pasamos a OpenGL el tamaño ajustado a POT (ej: 1024x512 en lugar de 640x480)
    glBindTexture(GL_TEXTURE_2D, m_texture_id);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, m_tex_width, m_tex_height, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);

    //Precalculamos las coordenadas UV solo cuando la resolución cambia
    GLfloat max_u = (float)m_width / m_tex_width;
    GLfloat max_v = (float)m_height / m_tex_height;

    m_texcoords[0] = 0.0f;   m_texcoords[1] = 0.0f;  // Top-left
    m_texcoords[2] = 0.0f;   m_texcoords[3] = max_v; // Bottom-left
    m_texcoords[4] = max_u;  m_texcoords[5] = 0.0f;  // Top-right
    m_texcoords[6] = max_u;  m_texcoords[7] = max_v; // Bottom-right
}
