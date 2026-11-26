/*
 * bitstream.c
 * Copyright (C) 2000-2003 Michel Lespinasse <walken@zoy.org>
 * Copyright (C) 1999-2000 Aaron Holtzman <aholtzma@ess.engr.uvic.ca>
 *
 * MISRC changes (2026-11-07): Refill the bit cache from a bounded byte buffer.
 *
 * This file is part of a52dec, a free ATSC A-52 stream decoder.
 * See http://liba52.sourceforge.net/ for updates.
 *
 * a52dec is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * a52dec is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

#include "config.h"

#include <inttypes.h>

#include "a52.h"
#include "a52_internal.h"
#include "bitstream.h"

void a52_bitstream_set_ptr (a52_state_t * state, const uint8_t * buf, size_t bytes)
{
    state->buffer_start = buf;
    state->buffer_remaining = bytes;
    state->bits_left = 0;
    state->current_word = 0;
    state->bitstream_error = 0;
}

/* Refill the left-aligned bit cache without reading outside this frame. */
uint32_t a52_bitstream_get_bh (a52_state_t * state, uint32_t num_bits)
{
    uint32_t prefix = state->bits_left
        ? state->current_word >> (32 - state->bits_left) : 0;
    uint32_t wanted = num_bits - state->bits_left;
    size_t bytes = state->buffer_remaining < 4 ? state->buffer_remaining : 4;
    if (wanted > 8 * bytes) {
        state->bitstream_error = 1;
        return 0;
    }

    const uint8_t * buf = state->buffer_start;
    uint32_t word = 0;
    if (bytes == 4) {
        word = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
               ((uint32_t)buf[2] << 8) | buf[3];
    } else {
        for (size_t i = 0; i < bytes; ++i)
            word |= (uint32_t)buf[i] << (24 - 8 * i);
    }

    state->buffer_start += bytes;
    state->buffer_remaining -= bytes;
    state->bits_left = 8 * bytes - wanted;
    state->current_word = word << wanted;
    return (prefix << wanted) | (word >> (32 - wanted));
}
