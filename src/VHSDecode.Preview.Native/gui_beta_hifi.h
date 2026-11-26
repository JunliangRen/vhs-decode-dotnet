#ifndef GUI_BETA_HIFI_H
#define GUI_BETA_HIFI_H

#include "gui_rf_preview.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define GUI_BETA_HIFI_INPUT_RATE_HZ 20000000u
#define GUI_BETA_HIFI_AUDIO_RATE_HZ 48000u
#define GUI_BETA_HIFI_AUDIO_CHANNELS 2u

typedef struct gui_beta_hifi_decoder gui_beta_hifi_decoder_t;

typedef enum {
    GUI_BETA_HIFI_IQ_AUTO = 0,
    GUI_BETA_HIFI_IQ_SCALAR,
    GUI_BETA_HIFI_IQ_SSE2,
    GUI_BETA_HIFI_IQ_AVX2
} gui_beta_hifi_iq_kernel_t;

typedef struct {
    bool locked;
    uint64_t input_samples;
    uint64_t pcm_frames;
    uint64_t head_switches;
    uint64_t dropouts;
    uint64_t resets;
    char reason[96];
} gui_beta_hifi_status_t;

/* Borrowed interleaved float stereo, 48 kHz. end_sample is the exclusive
 * end of the processed input on the caller's common 20 MSPS sample axis.
 * The callback runs on the caller's thread and must not wait for playback.
 * Frames before lock or during unusable RF are emitted as digital silence. */
typedef void (*gui_beta_hifi_pcm_callback_t)(void *context, const float *stereo,
                                            size_t frames, uint64_t end_sample,
                                            const gui_beta_hifi_status_t *status);

/* Configure once before processing. The caller owns the reserved helper. */
bool gui_beta_hifi_set_runner(gui_beta_hifi_decoder_t *decoder, gui_rf_lane_runner_t runner);
gui_beta_hifi_decoder_t *gui_beta_hifi_create(void);
void gui_beta_hifi_destroy(gui_beta_hifi_decoder_t *decoder);
void gui_beta_hifi_reset(gui_beta_hifi_decoder_t *decoder);
/* Fixed-memory, synchronous processing: no per-call allocation or playback I/O.
 * A non-contiguous first_sample resets all RF, calibration, and audio history. */
void gui_beta_hifi_process(gui_beta_hifi_decoder_t *decoder, const float *samples,
                           size_t count, uint64_t first_sample,
                           gui_beta_hifi_pcm_callback_t callback, void *context);
void gui_beta_hifi_get_status(const gui_beta_hifi_decoder_t *decoder,
                              gui_beta_hifi_status_t *status);
/* Internal diagnostics/fixtures may select the IQ FIR kernel before input
 * begins. Unsupported kernels return false, without changing the selection.
 * AUTO uses runtime CPU/OS capability checks; reset retains the selection.
 * This controls only the four complex IQ FIRs, not other DSP stages. */
bool gui_beta_hifi_set_iq_kernel(gui_beta_hifi_decoder_t *decoder,
                                gui_beta_hifi_iq_kernel_t kernel);
gui_beta_hifi_iq_kernel_t gui_beta_hifi_get_iq_kernel(
    const gui_beta_hifi_decoder_t *decoder);

#endif
