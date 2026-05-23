#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define FLOSS_COMPOSITE_VERSION 1

uint32_t floss_composite_version(void);

void floss_composite_normal_row(uint8_t *dst, const uint8_t *src, int32_t width, uint32_t opacity);

void floss_composite_normal_bgra_region(
    uint8_t *dst,
    int32_t dst_stride,
    const uint8_t *src,
    int32_t src_stride,
    int32_t width,
    int32_t height,
    uint32_t opacity);

void floss_clear_bgra_region(
    uint8_t *dst,
    int32_t dst_stride,
    int32_t width,
    int32_t height,
    uint32_t clear_value);

#ifdef __cplusplus
}
#endif
