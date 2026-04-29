#include "include/rust_lib_floss/rust_lib_floss_plugin.h"

#include <flutter_linux/flutter_linux.h>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

// Double-buffered pixel texture to avoid use-after-free races:
// Flutter's raster thread reads from `front_buffer` while the platform
// thread writes into the back buffer and then swaps.
struct _FlossPixelBufferTexture {
  FlPixelBufferTexture parent_instance;
  std::mutex mutex;
  std::vector<uint8_t> pixels[2];
  int front_buffer = 0;
  uint32_t width = 1;
  uint32_t height = 1;
};

G_DEFINE_TYPE(FlossPixelBufferTexture,
              floss_pixel_buffer_texture,
              fl_pixel_buffer_texture_get_type())

static gboolean floss_pixel_buffer_texture_copy_pixels(
    FlPixelBufferTexture* texture,
    const uint8_t** out_buffer,
    uint32_t* width,
    uint32_t* height,
    GError** error) {
  auto self = reinterpret_cast<FlossPixelBufferTexture*>(texture);
  std::lock_guard<std::mutex> lock(self->mutex);
  *out_buffer = self->pixels[self->front_buffer].data();
  *width = self->width;
  *height = self->height;
  return TRUE;
}

static void floss_pixel_buffer_texture_finalize(GObject* object) {
  auto self = FLOSS_PIXEL_BUFFER_TEXTURE(object);
  self->pixels[0].~vector();
  self->pixels[1].~vector();
  self->mutex.~mutex();
  G_OBJECT_CLASS(floss_pixel_buffer_texture_parent_class)->finalize(object);
}

static void floss_pixel_buffer_texture_class_init(
    FlossPixelBufferTextureClass* klass) {
  FL_PIXEL_BUFFER_TEXTURE_CLASS(klass)->copy_pixels =
      floss_pixel_buffer_texture_copy_pixels;
  G_OBJECT_CLASS(klass)->finalize = floss_pixel_buffer_texture_finalize;
}

static void floss_pixel_buffer_texture_init(FlossPixelBufferTexture* self) {
  // G_DEFINE_TYPE allocates with g_malloc0, which does not call C++
  // constructors. Use placement-new for non-trivial members.
  new (&self->mutex) std::mutex();
  new (&self->pixels[0]) std::vector<uint8_t>();
  new (&self->pixels[1]) std::vector<uint8_t>();
  self->pixels[0].assign(4, 0xff);
  self->pixels[1].assign(4, 0xff);
}

struct _RustLibFlossPlugin {
  GObject parent_instance;
  FlTextureRegistrar* texture_registrar;
  FlMethodChannel* channel;
  std::unordered_map<int64_t, FlossPixelBufferTexture*> textures;
};

G_DEFINE_TYPE(RustLibFlossPlugin, rust_lib_floss_plugin, g_object_get_type())

static void rust_lib_floss_plugin_dispose(GObject* object) {
  auto self = RUST_LIB_FLOSS_PLUGIN(object);
  for (auto& entry : self->textures) {
    fl_texture_registrar_unregister_texture(self->texture_registrar,
                                            FL_TEXTURE(entry.second));
    g_object_unref(entry.second);
  }
  self->textures.clear();
  g_clear_object(&self->channel);
  g_clear_object(&self->texture_registrar);
  G_OBJECT_CLASS(rust_lib_floss_plugin_parent_class)->dispose(object);
}

static void rust_lib_floss_plugin_finalize(GObject* object) {
  auto self = RUST_LIB_FLOSS_PLUGIN(object);
  self->textures.~unordered_map();
  G_OBJECT_CLASS(rust_lib_floss_plugin_parent_class)->finalize(object);
}

static void rust_lib_floss_plugin_class_init(RustLibFlossPluginClass* klass) {
  G_OBJECT_CLASS(klass)->dispose = rust_lib_floss_plugin_dispose;
  G_OBJECT_CLASS(klass)->finalize = rust_lib_floss_plugin_finalize;
}

static void rust_lib_floss_plugin_init(RustLibFlossPlugin* self) {
  new (&self->textures) std::unordered_map<int64_t, FlossPixelBufferTexture*>();
}

static int64_t get_int_arg(FlValue* args, const char* key, int64_t fallback) {
  FlValue* value = fl_value_lookup_string(args, key);
  return value == nullptr ? fallback : fl_value_get_int(value);
}

static void create_texture(RustLibFlossPlugin* self, FlMethodCall* method_call) {
  FlValue* args = fl_method_call_get_args(method_call);
  auto texture = reinterpret_cast<FlossPixelBufferTexture*>(
      g_object_new(floss_pixel_buffer_texture_get_type(), nullptr));
  {
    std::lock_guard<std::mutex> lock(texture->mutex);
    texture->width = static_cast<uint32_t>(get_int_arg(args, "width", 1));
    texture->height = static_cast<uint32_t>(get_int_arg(args, "height", 1));
    const size_t size = static_cast<size_t>(texture->width) *
                        static_cast<size_t>(texture->height) * 4;
    texture->pixels[0].assign(size, 0xff);
    texture->pixels[1].assign(size, 0xff);
  }

  if (!fl_texture_registrar_register_texture(self->texture_registrar,
                                             FL_TEXTURE(texture))) {
    g_object_unref(texture);
    fl_method_call_respond_error(method_call, "texture_registration_failed",
                                 "Could not register Flutter texture.", nullptr,
                                 nullptr);
    return;
  }

  const int64_t texture_id = fl_texture_get_id(FL_TEXTURE(texture));
  self->textures[texture_id] = texture;
  g_autoptr(FlValue) result = fl_value_new_int(texture_id);
  fl_method_call_respond_success(method_call, result, nullptr);
}

static void update_texture(RustLibFlossPlugin* self, FlMethodCall* method_call) {
  FlValue* args = fl_method_call_get_args(method_call);
  const int64_t texture_id = get_int_arg(args, "textureId", -1);
  auto it = self->textures.find(texture_id);
  if (it == self->textures.end()) {
    fl_method_call_respond_error(method_call, "unknown_texture",
                                 "Texture ID is not registered.", nullptr,
                                 nullptr);
    return;
  }

  FlValue* pixels_value = fl_value_lookup_string(args, "pixels");
  if (pixels_value == nullptr) {
    fl_method_call_respond_error(method_call, "missing_pixels",
                                 "Texture update requires RGBA pixels.", nullptr,
                                 nullptr);
    return;
  }

  auto texture = it->second;
  {
    std::lock_guard<std::mutex> lock(texture->mutex);
    texture->width = static_cast<uint32_t>(get_int_arg(args, "width", 1));
    texture->height = static_cast<uint32_t>(get_int_arg(args, "height", 1));
    const auto* pixels = fl_value_get_uint8_list(pixels_value);
    const size_t length = fl_value_get_length(pixels_value);

    const int back = 1 - texture->front_buffer;
    texture->pixels[back].assign(pixels, pixels + length);
    texture->front_buffer = back;
  }
  fl_texture_registrar_mark_texture_frame_available(self->texture_registrar,
                                                    FL_TEXTURE(texture));
  fl_method_call_respond_success(method_call, nullptr, nullptr);
}

static void dispose_texture(RustLibFlossPlugin* self,
                            FlMethodCall* method_call) {
  FlValue* args = fl_method_call_get_args(method_call);
  const int64_t texture_id = get_int_arg(args, "textureId", -1);
  auto it = self->textures.find(texture_id);
  if (it != self->textures.end()) {
    fl_texture_registrar_unregister_texture(self->texture_registrar,
                                            FL_TEXTURE(it->second));
    g_object_unref(it->second);
    self->textures.erase(it);
  }
  fl_method_call_respond_success(method_call, nullptr, nullptr);
}

static void method_call_cb(FlMethodChannel* channel,
                           FlMethodCall* method_call,
                           gpointer user_data) {
  auto self = RUST_LIB_FLOSS_PLUGIN(user_data);
  const gchar* method = fl_method_call_get_name(method_call);
  if (strcmp(method, "createTexture") == 0) {
    create_texture(self, method_call);
  } else if (strcmp(method, "updateTexture") == 0) {
    update_texture(self, method_call);
  } else if (strcmp(method, "disposeTexture") == 0) {
    dispose_texture(self, method_call);
  } else {
    fl_method_call_respond_not_implemented(method_call, nullptr);
  }
}

void rust_lib_floss_plugin_register_with_registrar(
    FlPluginRegistrar* registrar) {
  auto self = RUST_LIB_FLOSS_PLUGIN(
      g_object_new(rust_lib_floss_plugin_get_type(), nullptr));
  self->texture_registrar =
      FL_TEXTURE_REGISTRAR(g_object_ref(fl_plugin_registrar_get_texture_registrar(registrar)));
  g_autoptr(FlStandardMethodCodec) codec = fl_standard_method_codec_new();
  self->channel = fl_method_channel_new(
      fl_plugin_registrar_get_messenger(registrar), "floss/texture",
      FL_METHOD_CODEC(codec));
  // Pass self directly without an extra ref to avoid a circular reference:
  // plugin owns channel, channel's handler owns plugin → never freed.
  fl_method_channel_set_method_call_handler(self->channel, method_call_cb,
                                            self, nullptr);
}
