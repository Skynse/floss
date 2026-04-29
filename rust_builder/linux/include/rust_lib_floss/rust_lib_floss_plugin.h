#ifndef FLUTTER_PLUGIN_RUST_LIB_FLOSS_PLUGIN_H_
#define FLUTTER_PLUGIN_RUST_LIB_FLOSS_PLUGIN_H_

#include <flutter_linux/flutter_linux.h>

G_BEGIN_DECLS

G_DECLARE_FINAL_TYPE(RustLibFlossPlugin,
                     rust_lib_floss_plugin,
                     RUST_LIB,
                     FLOSS_PLUGIN,
                     GObject)

G_DECLARE_FINAL_TYPE(FlossPixelBufferTexture,
                     floss_pixel_buffer_texture,
                     FLOSS,
                     PIXEL_BUFFER_TEXTURE,
                     FlPixelBufferTexture)

void rust_lib_floss_plugin_register_with_registrar(
    FlPluginRegistrar* registrar);

G_END_DECLS

#endif
