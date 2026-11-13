"""DIS ToneDither.Phase, generalized from binary decisions to palette intervals.

The source-raster index keys every microcell, so previews and exports share the
same stored dot grid. Color fitting replaces the final gray-to-AA/44 decision;
it must not discard hue by feeding the already binary reference to the solver.
"""
import numpy as np


def phase(sample_index):
    value = np.asarray(sample_index, dtype=np.int64).astype(np.uint64) & np.uint64(0xffffffff)
    value = (value * np.uint64(2654435761)) & np.uint64(0xffffffff)
    value ^= value >> np.uint64(16)
    value = (value * np.uint64(0x7FEB352D)) & np.uint64(0xffffffff)
    value ^= value >> np.uint64(15)
    value = (value * np.uint64(0x846CA68B)) & np.uint64(0xffffffff)
    value ^= value >> np.uint64(16)
    return value.astype(np.float64) / 4294967296.


def allocate(chosen, weights, sample_indices, strength):
    # At strength zero, use the central (hard) palette decision. At strength one,
    # hash thresholds cover the whole cumulative mixture, as in Tone.UseWhite.
    # Complement native gray >= 128/255 + strength*(phase-.5) so the
    # binary [dark, white] palette agrees at every source pixel, including
    # the native 128 (rather than 127.5) midpoint.
    thresholds = 1. - 128./255. - float(strength) * (phase(sample_indices) - .5)
    cumulative = np.cumsum(weights)
    return chosen[np.searchsorted(cumulative, thresholds, side='right').clip(max=len(chosen)-1)]
