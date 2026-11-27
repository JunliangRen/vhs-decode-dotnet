/* Direct 20 MSPS composite to the common 5 MSPS picture and burst planes. */
#include "cvbs_frontend.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define PI 3.14159265358979323846
#define SAMPLE_RATE 20000000.0
enum { HALF = 24 };

static double tap(double cutoff, int k) {
    double window = 0.42 + 0.5*cos(PI*k/HALF) + 0.08*cos(2*PI*k/HALF);
    return window * (k ? sin(2*PI*cutoff*k/SAMPLE_RATE)/(PI*k) : 2*cutoff/SAMPLE_RATE);
}

void cvbs_frontend_reset(cvbs_frontend_t *s, bool pal, bool restore) {
    memset(s,0,sizeof(*s));
    s->restore=restore;
    s->span_ire=pal ? 300.0f/7.0f : 40.0f;
    s->picture.gain=s->sync.gain=120;
    s->fsc=pal ? 4433618.75 : 315000000.0/88.0;
    s->luma_delay=s->chroma_delay=HALF/4.0;
    double omega=2*PI*s->fsc/SAMPLE_RATE;
    s->step_i=cos(4*omega);
    s->step_q=-sin(4*omega);
    double y_sum=0, c_sum=0;
    for (int k=0;k<=HALF;k++) {
        double y=tap(1800000,k), c=tap(650000,k);
        y_sum+=(k ? 2 : 1)*y;
        c_sum+=(k ? 2 : 1)*c;
        s->y_tap[k]=(float)y;
        s->i_tap[k]=(float)(c*cos(k*omega));
        s->q_tap[k]=(float)(c*sin(k*omega));
    }
    for (int k=0;k<=HALF;k++) {
        s->y_tap[k]/=(float)y_sum;
        s->i_tap[k]/=(float)c_sum;
        s->q_tap[k]/=(float)c_sum;
    }
}

static int compare_float(const void *a,const void *b) {
    float x=*(const float*)a,y=*(const float*)b;
    return (x>y)-(x<y);
}

/* Use picture quantiles only to scout a slice. Verify H widths and cadence,
 * then measure actual sync tips and back porches for the IRE conversion. */
static bool measure_levels(cvbs_levels_t *s, float threshold, float span_ire) {
    float tips[16],porches[16];
    int start=0,previous=-1000,accepted=0;
    for (int i=1;i<4096-48;i++) {
        if (s->scout[i]<threshold && s->scout[i-1]>=threshold) start=i;
        if (s->scout[i]>=threshold && s->scout[i-1]<threshold && i-start>=16 && i-start<=34) {
            if (start-previous>=302 && start-previous<=335 && accepted<16) {
                float tip=0,porch=0;
                for (int j=0;j<5;j++) {
                    tip+=s->scout[start+9+j];
                    porch+=s->scout[start+41+j];
                }
                tips[accepted]=tip*0.2f;
                porches[accepted++]=porch*0.2f;
            }
            previous=start;
        }
    }
    if (accepted<5) return false;
    qsort(tips,accepted,sizeof(float),compare_float);
    qsort(porches,accepted,sizeof(float),compare_float);
    float blank=porches[accepted/2],span=blank-tips[accepted/2];
    if (span>0.002f) {
        s->gain=span_ire/span;
        s->blank=blank;
        return true;
    }
    return false;
}

static float normalize(cvbs_levels_t *s,float y,float span_ire) {
    s->scout[s->length++]=y;
    if (s->length==4096) {
        float levels[128];
        for (int i=0;i<128;i++) levels[i]=s->scout[32*i+i%7];
        qsort(levels,128,sizeof(float),compare_float);
        if (!measure_levels(s,(levels[4]+levels[30])*0.5f,span_ire))
            measure_levels(s,(levels[4]+levels[16])*0.5f,span_ire);
        s->length=0;
    }
    return (y-s->blank)*s->gain;
}

size_t cvbs_frontend_process(cvbs_frontend_t *s,const float *input,size_t count,
    uint64_t first,float *luma,float *chroma_i,float *chroma_q,float *sync) {
    size_t out=0;
    for (size_t n=0;n<count;n++) {
        unsigned at=s->cursor;
        s->history[at]=s->history[at+64]=input[n];
        s->cursor=(at+1)&63;
        uint64_t tick=first+n;
        if (tick&3u) continue;
        if (!s->started) {
            /* Burst lock supplies absolute phase. Restart on any input grid. */
            s->osc_i=1;
            s->started=true;
        }
        const float *center=s->history+at+64-HALF;
        float y=center[0]*s->y_tap[0],i=center[0]*s->i_tap[0],q=0;
        for (int k=1;k<=HALF;k++) {
            float sum=center[-k]+center[k];
            y+=sum*s->y_tap[k];
            i+=sum*s->i_tap[k];
            q+=(center[-k]-center[k])*s->q_tap[k];
        }
        float timing=y;
        if (s->restore || s->sync_restore) {
            /* Leaky inverse high-pass. In plain composite acquisition only the
             * sync slicer sees the compensated signal; picture levels stay raw. */
            s->integral+=0.00753982*y-0.00010053*s->integral;
            timing+=(float)s->integral;
        }
        luma[out]=normalize(&s->picture,s->restore ? timing : y,s->span_ire);
        sync[out]=s->sync_restore ? normalize(&s->sync,timing,s->span_ire) : luma[out];
        float scale=0.02f*s->picture.gain;
        chroma_i[out]=(float)((i*s->osc_i-q*s->osc_q)*scale);
        chroma_q[out++]=(float)((i*s->osc_q+q*s->osc_i)*scale);
        double next=s->osc_i*s->step_i-s->osc_q*s->step_q;
        s->osc_q=s->osc_q*s->step_i+s->osc_i*s->step_q;
        s->osc_i=next;
    }
    return out;
}
