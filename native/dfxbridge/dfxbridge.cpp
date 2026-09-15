/*
 * str-amp -- flat C entry points for FxSound's DfxDsp library.
 *
 * Copyright (C) 2025 strong-ery
 * Copyright (C) 2025 Aiden A
 *
 * Links against FxSound (https://github.com/fxsound2/fxsound-app), which is
 * Copyright (C) 2025 FxSound LLC and licensed under the GNU Affero General Public
 * License v3. This file and the library it produces are therefore AGPL-3.0-or-later.
 * See COPYRIGHT in the repository root for build instructions and the exact upstream
 * revision the shipped binary was built from.
 *
 * DfxDsp is a C++ class, and P/Invoke cannot call C++ member functions or pass
 * std::wstring. Everything here is a thin forwarder: no logic lives in this file, so
 * that what str-amp hears is FxSound's processing and not an interpretation of it.
 */

#include "DfxDsp.h"

#include <string>

#define DFXBRIDGE_API extern "C" __declspec(dllexport)

DFXBRIDGE_API DfxDsp* dfx_create()
{
    return new (std::nothrow) DfxDsp();
}

DFXBRIDGE_API void dfx_destroy(DfxDsp* dsp)
{
    delete dsp;
}

/// Bits per sample, channels, sample rate, valid bits. Must be called before processing,
/// and again whenever the stream format changes.
DFXBRIDGE_API int dfx_set_signal_format(DfxDsp* dsp, int bitsPerSample, int channels,
                                        int sampleRate, int validBits)
{
    return dsp ? dsp->setSignalFormat(bitsPerSample, channels, sampleRate, validBits) : -1;
}

/// Processes interleaved 16-bit samples. `sampleSets` counts frames, not samples.
/// Input and output may be the same buffer.
DFXBRIDGE_API int dfx_process(DfxDsp* dsp, short* input, short* output,
                              int sampleSets, int checkForDuplicateBuffers)
{
    return dsp ? dsp->processAudio(input, output, sampleSets, checkForDuplicateBuffers) : -1;
}

/// Processes interleaved 32-bit float samples, full scale at +/-1.0.
///
/// processAudio is declared taking short*, but the buffer is only ever a byte buffer: the
/// bits-per-sample passed to setSignalFormat decides how it is read, and at 32 the library
/// casts it straight to its own realtype (a float) and processes in place, no conversion
/// either way. So a float stream can be handed over as it is, which is what str-amp has --
/// going out to 16-bit and back would cost precision and would clip anything the equalizer
/// lifted above full scale before the DSP ever saw it.
///
/// Pass 32 for bitsPerSample and validBits to setSignalFormat to select this path.
DFXBRIDGE_API int dfx_process_float(DfxDsp* dsp, float* input, float* output,
                                    int sampleSets, int checkForDuplicateBuffers)
{
    return dsp ? dsp->processAudio(reinterpret_cast<short*>(input),
                                   reinterpret_cast<short*>(output),
                                   sampleSets, checkForDuplicateBuffers) : -1;
}

/// Loads a .fac preset. This is the whole reason for the bridge: FxSound's own reader
/// applies its own slot order, scaling, per-effect switches and mode factors.
DFXBRIDGE_API int dfx_load_preset(DfxDsp* dsp, const wchar_t* path)
{
    if (!dsp || !path)
        return -1;
    return dsp->loadPreset(std::wstring(path));
}

DFXBRIDGE_API void dfx_power_on(DfxDsp* dsp, int on)
{
    if (dsp) dsp->powerOn(on != 0);
}

DFXBRIDGE_API int dfx_is_power_on(DfxDsp* dsp)
{
    return dsp && dsp->isPowerOn() ? 1 : 0;
}

/// Effect ids follow DfxDsp::Effect: 0 fidelity, 1 ambience, 2 surround,
/// 3 dynamic boost, 4 bass. Values are on FxSound's own 0-10 scale, the one on its sliders.
/// Setting a value also switches the effect on, or off when it is exactly zero.
DFXBRIDGE_API void dfx_set_effect(DfxDsp* dsp, int effect, float value)
{
    if (dsp) dsp->setEffectValue(static_cast<DfxDsp::Effect>(effect), value);
}

/// Note the asymmetry with dfx_set_effect: the library divides by ten on the way in and hands
/// back what it stored, so this returns 0..1 where the setter took 0..10.
DFXBRIDGE_API float dfx_get_effect(DfxDsp* dsp, int effect)
{
    return dsp ? dsp->getEffectValue(static_cast<DfxDsp::Effect>(effect)) : 0.0f;
}

DFXBRIDGE_API void dfx_eq_on(DfxDsp* dsp, int on)
{
    if (dsp) dsp->eqOn(on != 0);
}

DFXBRIDGE_API int dfx_get_num_eq_bands(DfxDsp* dsp)
{
    return dsp ? dsp->getNumEqBands() : 0;
}

DFXBRIDGE_API void dfx_set_num_bands(DfxDsp* dsp, int bands)
{
    if (dsp) dsp->setNumBands(bands);
}

DFXBRIDGE_API float dfx_get_eq_band_frequency(DfxDsp* dsp, int band)
{
    return dsp ? dsp->getEqBandFrequency(band) : 0.0f;
}

DFXBRIDGE_API void dfx_set_eq_band_frequency(DfxDsp* dsp, int band, float hz)
{
    if (dsp) dsp->setEqBandFrequency(band, hz);
}

DFXBRIDGE_API float dfx_get_eq_band_boost_cut(DfxDsp* dsp, int band)
{
    return dsp ? dsp->getEqBandBoostCut(band) : 0.0f;
}

DFXBRIDGE_API void dfx_set_eq_band_boost_cut(DfxDsp* dsp, int band, float db)
{
    if (dsp) dsp->setEqBandBoostCut(band, db);
}

DFXBRIDGE_API void dfx_set_master_gain(DfxDsp* dsp, float db)
{
    if (dsp) dsp->setMasterGain(db);
}

DFXBRIDGE_API void dfx_set_normalization(DfxDsp* dsp, float db)
{
    if (dsp) dsp->setNormalization(db);
}

DFXBRIDGE_API void dfx_set_volume_leveling(DfxDsp* dsp, float db)
{
    if (dsp) dsp->setVolumeLeveling(db);
}

DFXBRIDGE_API void dfx_set_filter_q(DfxDsp* dsp, float q)
{
    if (dsp) dsp->setFilterQ(q);
}
