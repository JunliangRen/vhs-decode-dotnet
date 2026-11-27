#ifndef VHSDECODE_CVBS_FRONTEND_H
#define VHSDECODE_CVBS_FRONTEND_H
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

typedef struct {
    float scout[4096], blank, gain;
    unsigned length;
} cvbs_levels_t;

typedef struct {
    float history[128], y_tap[25], i_tap[25], q_tap[25];
    cvbs_levels_t picture, sync;
    float span_ire;
    double osc_i, osc_q, step_i, step_q, fsc, integral, luma_delay, chroma_delay;
    unsigned cursor;
    bool sync_restore;
    bool restore, started;
} cvbs_frontend_t;

void cvbs_frontend_reset(cvbs_frontend_t *s, bool pal, bool restore);
size_t cvbs_frontend_process(cvbs_frontend_t *s, const float *input, size_t count,
    uint64_t first, float *luma, float *chroma_i, float *chroma_q,float *sync);
#endif
