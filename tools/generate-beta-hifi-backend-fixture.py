"""Regenerate the native Beta backend golden using the frozen Python reference.

Development-only oracle. The C# decoder and tests never invoke Python. Supply
--model/--profile/--eq-profile/--notch-profile to reproduce from archived inputs.
The default paths identify the immutable experiment that produced preview 16.
"""
from pathlib import Path
import argparse
import gzip
import hashlib
import importlib.util
import io
import json
import struct
import sys

import numpy as np
import scipy
from scipy import signal
import soundfile as sf

REPO = Path(__file__).resolve().parents[1]
EXPERIMENT = REPO / 'scratch/beta-hifi-20261019'
MODEL = EXPERIMENT / 'residual-noise-20261019/beta-dynamic-nr-20261019/v3-development/model'


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def pcm24(values):
    stream = io.BytesIO()
    sf.write(stream, values, 48000, format='WAV', subtype='PCM_24')
    stream.seek(0)
    return sf.read(stream, dtype='float64', always_2d=True)[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, default=MODEL / 'dynamic_beta_nr_v3.py')
    parser.add_argument('--profile', type=Path, default=MODEL / 'v3-top-training-selected.json')
    parser.add_argument('--eq-profile', type=Path, default=EXPERIMENT / 'noise-analysis/empirical-response-profile-v2.json')
    parser.add_argument('--notch-profile', type=Path, default=EXPERIMENT / 'noise-analysis/notch-profile.json')
    parser.add_argument('--output', type=Path, default=REPO / 'tests/VHSDecode.Tests/Fixtures/beta-hifi-backend-v1.bin.gz')
    args = parser.parse_args()
    assert sha(args.model) == 'bf003a507b96b5fadebd35d980f0352fe949aab72d94add7a7d9ed0ad266e633'
    assert sha(args.profile) == '06c9fc5a90f02b0be408682cf535e25a13c1a47c0776d93d6016bb6c538b21db'
    profile = json.loads(args.profile.read_bytes())['parameters']
    eq = json.loads(args.eq_profile.read_bytes())
    notch = json.loads(args.notch_profile.read_bytes())
    spec = importlib.util.spec_from_file_location('frozen_beta_v3_oracle', args.model)
    model = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = model
    spec.loader.exec_module(model)

    count = 32779
    t = np.arange(count, dtype=np.float64) / 48000
    envelope = np.select([t < .08, t < .18, t < .34, t < .45, t < .54, t < .61],
                         [.00008, .04, .18, .003, .075, .018], default=.00001)
    envelope *= np.where(t < .54, 1., np.exp(-np.maximum(t-.54, 0)/.035))
    rng = np.random.default_rng(20261020)
    noise = rng.standard_normal((count, 2)) * .00002
    left = envelope * (.7*np.sin(2*np.pi*440*t) + .2*np.sin(2*np.pi*3900*t)
                       + .17*np.sin(2*np.pi*11800*t) + .03*np.sin(2*np.pi*notch['frequency_hz']*t))
    right = envelope * (.5*np.sin(2*np.pi*660*t+.3) + .18*np.sin(2*np.pi*6200*t)
                        + .12*np.sin(2*np.pi*13500*t))
    source = (np.column_stack([left, right]) + noise).astype('<f4')
    source[0] = [.000231, -.000173]
    source[[137, 4799, 4800, 4801, 30000]] += np.array([.08, -.06], dtype=np.float32)

    def process(x, deemphasis=True, expander=True, reference_offset=0.):
        # Actual original libsndfile interstage quantization and SciPy filters.
        y = pcm24(x.astype(np.float64))
        if deemphasis:
            b, a = eq['b'], eq['a']
            state = signal.lfilter_zi(b, a)[:, None]*y[0]
            y = pcm24(signal.lfilter(b, a, y, axis=0, zi=state)[0])
            nb, na = signal.iirnotch(notch['frequency_hz'], notch['quality_factor'], fs=48000)
            y = signal.filtfilt(nb, na, y, axis=0, padlen=min(9, len(y)-1))
        if expander:
            parameters = profile.copy()
            parameters['reference_dbfs'] += reference_offset
            parameters['top_reference_dbfs'] += reference_offset
            y = model.DynamicBetaNR(model.Parameters(**parameters)).process(y)
        return y

    cases = [('default', source, True, True, 0.),
             ('reference-plus-six', source, True, True, 6.),
             ('deemphasis-only', source[:961], True, False, 0.),
             ('dynamics-only', source[:961], False, True, 0.),
             ('bypass-quantized', source[:147], False, False, 0.),
             ('silence', np.zeros((217, 2), dtype='<f4'), True, True, 0.)]
    for n in [1, 2, 9, 10, 47, 48, 49]:
        cases.append((f'short-{n}', source[:n], True, True, 0.))
    mono = source[:3001].copy()
    mono[:, 1] = 0
    cases.append(('left-only', mono, True, True, 0.))

    nb, na = signal.iirnotch(notch['frequency_hz'], notch['quality_factor'], fs=48000)
    ub, ua = model.upper_shelf_coefficients(48000, 4500, profile['upper_shelf_db'])
    coefficients = {
        'eq_b': eq['b'], 'eq_a': eq['a'], 'eq_zi': signal.lfilter_zi(eq['b'], eq['a']).tolist(),
        'notch_b': nb.tolist(), 'notch_a': na.tolist(), 'notch_zi': signal.lfilter_zi(nb, na).tolist(),
        'detector_sos': signal.butter(2, [3000, 12000], fs=48000, btype='bandpass', output='sos').tolist(),
        'fallback_sos': signal.butter(2, [100, 15000], fs=48000, btype='bandpass', output='sos').tolist(),
        'top_sos': signal.butter(2, [10000, 15000], fs=48000, btype='bandpass', output='sos').tolist(),
        'low_1k': [x.tolist() for x in signal.butter(1, 1000, fs=48000)],
        'low_10k': [x.tolist() for x in signal.butter(1, 10000, fs=48000)],
        'upper_b': ub.tolist(), 'upper_a': ua.tolist()}
    metadata = {'schema': 'BETA16G1', 'description': 'Independent frozen Python/SciPy/SoundFile oracle; synthetic levels, weak tones, transient, tail and file edges.',
                'model_sha256': sha(args.model), 'profile_sha256': sha(args.profile),
                'eq_profile_sha256': sha(args.eq_profile), 'notch_profile_sha256': sha(args.notch_profile),
                'numpy': np.__version__, 'scipy': scipy.__version__, 'soundfile': sf.__version__,
                'libsndfile': sf.__libsndfile_version__, 'rate': 48000, 'parameters': profile,
                'short_file_policy': 'odd filtfilt padding min(9, frames-1); constant-extension initial state for one frame',
                'coefficients': coefficients}
    output = io.BytesIO()
    output.write(b'BETA16G1')
    meta = json.dumps(metadata, sort_keys=True, separators=(',', ':')).encode()
    output.write(struct.pack('<i', len(meta))); output.write(meta)
    output.write(struct.pack('<i', len(cases)))
    for name, x, deemp, expand, offset in cases:
        name_bytes = name.encode()
        output.write(struct.pack('<i', len(name_bytes))); output.write(name_bytes)
        output.write(struct.pack('<idi', int(deemp)+2*int(expand), offset, len(x)))
        output.write(np.asarray(x, dtype='<f4').tobytes())
        output.write(np.asarray(process(x, deemp, expand, offset), dtype='<f8').tobytes())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(gzip.compress(output.getvalue(), compresslevel=9, mtime=0))
    print(json.dumps({'fixture': str(args.output), 'sha256': sha(args.output), 'cases': len(cases),
                      'coefficients': coefficients}, indent=2))


if __name__ == '__main__':
    main()
