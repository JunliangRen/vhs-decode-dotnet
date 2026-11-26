/*
 * bitstream.h
 * Copyright (C) 2000-2003 Michel Lespinasse <walken@zoy.org>
 * Copyright (C) 1999-2000 Aaron Holtzman <aholtzma@ess.engr.uvic.ca>
 *
 * MISRC changes (2026-11-07): Use a left-aligned bounded bit cache with unsigned shifts.
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

void a52_bitstream_set_ptr (a52_state_t * state, const uint8_t * buf, size_t bytes);
uint32_t a52_bitstream_get_bh (a52_state_t * state, uint32_t num_bits);

static inline uint32_t bitstream_get (a52_state_t * state, uint32_t num_bits)
{
    if (!num_bits)
        return 0;
    if (num_bits >= 32) {
        state->bitstream_error = 1;
        return 0;
    }
    if (num_bits <= state->bits_left) {
        uint32_t result = state->current_word >> (32 - num_bits);
        state->current_word <<= num_bits;
        state->bits_left -= num_bits;
        return result;
    }

    return a52_bitstream_get_bh (state, num_bits);
}

static inline int32_t bitstream_get_2 (a52_state_t * state, uint32_t num_bits)
{
    if (!num_bits || num_bits >= 32) {
        state->bitstream_error = 1;
        return 0;
    }
    uint32_t value = bitstream_get (state, num_bits);
    uint32_t sign = UINT32_C(1) << (num_bits - 1);
    return (int32_t)(value ^ sign) - (int32_t)sign;
}
