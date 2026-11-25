/*
 * SPDX-License-Identifier: MIT
 *
 * MIT License
 *
 * Copyright (c) 2026 Herbert Yeung
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

#ifndef HTPIANO_H
#define HTPIANO_H

#include <stdint.h>
#ifdef _WIN32
#define HP_CALL __cdecl
#ifdef HTPIANO_BUILD
#define HP_API __declspec(dllexport)
#else
#define HP_API __declspec(dllimport)
#endif
#else
#define HP_CALL
#define HP_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C"
{
#endif

#define HP_ABI_VERSION 1u
#define HP_SAMPLE_RATE 48000u
    typedef struct hp_session hp_session;
    typedef struct hp_take hp_take;
    typedef int32_t hp_result;
    enum
    {
        HP_OK = 0,
        HP_INVALID_ARGUMENT = 1,
        HP_BUSY = 2,
        HP_REENTRANT = 3,
        HP_QUEUE_FULL = 4,
        HP_INTERNAL_ERROR = 5,
        HP_BANK_ERROR = 6,
        HP_NOT_FOUND = 7,
        HP_LIMIT = 8
    };
    enum
    {
        HP_MAIN = 0,
        HP_RIGHT = 1
    };
    enum
    {
        HP_PIANO = 0,
        HP_HARPSICHORD = 1,
        HP_MARIMBA = 2
    };
    enum
    {
        HP_KEY_DOWN = 1,
        HP_KEY_UP = 2,
        HP_SOUND_OFF = 3,
        HP_PEDAL = 4,
        HP_INSTRUMENT = 5,
        HP_TEMPO = 6,
        HP_METER = 7,
        HP_ALL_OFF = 8
    };

    /* v1 fixed layout, immutable through a callback. frame is a 48 kHz sample-frame index,
       not the delivery time. sequence is monotonically increasing for live events. */
    typedef struct hp_event
    {
        uint64_t frame, sequence, note_id;
        uint32_t type, zone, midi_note, velocity, value, value2;
    } hp_event;
    typedef void(HP_CALL *hp_callback)(void *user, const hp_event *events, uint32_t count);

    HP_API uint32_t HP_CALL hp_abi_version(void);
    /* UTF-8 bank path. NULL creates an event-only session (advance it by rendering silence). */
    HP_API hp_result HP_CALL hp_create(const char *bank_path, hp_session **out);
    /* Call only after other host calls have stopped; never destroy from callback. */
    HP_API hp_result HP_CALL hp_destroy(hp_session *s);
    HP_API hp_result HP_CALL hp_subscribe(hp_session *s, hp_callback fn, void *user);
    HP_API hp_result HP_CALL hp_unsubscribe(hp_session *s);
    HP_API hp_result HP_CALL hp_note_on(hp_session *s, uint32_t zone, uint32_t midi_note, uint32_t velocity,
                                        uint64_t *out_id);
    HP_API hp_result HP_CALL hp_note_off(hp_session *s, uint64_t note_id);
    HP_API hp_result HP_CALL hp_pedal(hp_session *s, uint32_t down);
    HP_API hp_result HP_CALL hp_set_instrument(hp_session *s, uint32_t zone, uint32_t instrument);
    HP_API hp_result HP_CALL hp_set_tempo(hp_session *s, uint32_t milli_bpm);
    HP_API hp_result HP_CALL hp_set_meter(hp_session *s, uint32_t numerator, uint32_t denominator);
    HP_API hp_result HP_CALL hp_all_off(hp_session *s);
    /* One render consumer per session. 48 kHz, signed PCM16 stereo, 2*frames samples.
       NULL output advances an event-only session without allocating a silence buffer. */
    HP_API hp_result HP_CALL hp_render_i16(hp_session *s, int16_t *out, uint32_t frames);
    HP_API hp_result HP_CALL hp_begin_take(hp_session *s);
    HP_API hp_result HP_CALL hp_end_take(hp_session *s, hp_take **out);
    HP_API uint64_t HP_CALL hp_take_duration(const hp_take *take);
    HP_API uint32_t HP_CALL hp_take_count(const hp_take *take);
    /* Returns number of events copied into caller storage. Take frames start at zero. */
    HP_API uint32_t HP_CALL hp_take_read(const hp_take *take, uint32_t first, hp_event *out, uint32_t capacity);
    HP_API void HP_CALL hp_take_free(hp_take *take);
    /* Session retains an immutable take independently of the handle. Only live events are dispatched. */
    HP_API hp_result HP_CALL hp_play_take(hp_session *s, const hp_take *take);
    HP_API hp_result HP_CALL hp_stop_playback(hp_session *s);
    HP_API hp_result HP_CALL hp_is_playing(hp_session *s, uint32_t *out);

#ifdef __cplusplus
}
#endif
#endif
