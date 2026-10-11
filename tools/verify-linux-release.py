#!/usr/bin/env python3
"""Exercise real VHS/LD IPP from an extracted, self-contained Linux package."""

import argparse
from array import array
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys

# Frozen integer carrier table: signal generation is independent of libm.
CARRIER = (
    12000, 11996, 11986, 11967, 11942, 11910, 11870, 11823, 11769, 11708, 11640, 11565, 11483, 11394, 11299, 11196, 11087, 10971, 10848, 10719, 10583, 10441, 10293, 10138, 9978, 9811, 9638, 9460, 9276, 9087, 8891, 8691, 8485, 8274, 8059, 7838, 7613, 7383, 7148, 6910, 6667, 6420, 6169, 5915, 5657, 5395, 5131, 4863, 4592, 4319, 4043, 3764, 3483, 3201, 2916, 2629, 2341, 2052, 1761, 1469, 1176, 883, 589, 294, 0, -294, -589, -883, -1176, -1469, -1761, -2052, -2341, -2629, -2916, -3201, -3483, -3764, -4043, -4319, -4592, -4863, -5131, -5395, -5657, -5915, -6169, -6420, -6667, -6910, -7148, -7383, -7613, -7838, -8059, -8274, -8485, -8691, -8891, -9087, -9276, -9460, -9638, -9811, -9978, -10138, -10293, -10441, -10583, -10719, -10848, -10971, -11087, -11196, -11299, -11394, -11483, -11565, -11640, -11708, -11769, -11823, -11870, -11910, -11942, -11967, -11986, -11996, -12000, -11996, -11986, -11967, -11942, -11910, -11870, -11823, -11769, -11708, -11640, -11565, -11483, -11394, -11299, -11196, -11087, -10971, -10848, -10719, -10583, -10441, -10293, -10138, -9978, -9811, -9638, -9460, -9276, -9087, -8891, -8691, -8485, -8274, -8059, -7838, -7613, -7383, -7148, -6910, -6667, -6420, -6169, -5915, -5657, -5395, -5131, -4863, -4592, -4319, -4043, -3764, -3483, -3201, -2916, -2629, -2341, -2052, -1761, -1469, -1176, -883, -589, -294, 0, 294, 589, 883, 1176, 1469, 1761, 2052, 2341, 2629, 2916, 3201, 3483, 3764, 4043, 4319, 4592, 4863, 5131, 5395, 5657, 5915, 6169, 6420, 6667, 6910, 7148, 7383, 7613, 7838, 8059, 8274, 8485, 8691, 8891, 9087, 9276, 9460, 9638, 9811, 9978, 10138, 10293, 10441, 10583, 10719, 10848, 10971, 11087, 11196, 11299, 11394, 11483, 11565, 11640, 11708, 11769, 11823, 11870, 11910, 11942, 11967, 11986, 11996
)
LINE_SAMPLES = 2560
TOTAL_LINES = 2003


def pal_line(line_number):
    samples = [10000] * LINE_SAMPLES
    field_line = (line_number - 10) % 313 if line_number >= 10 else -1
    if 0 <= field_line <= 7:
        first = 1092 if field_line in (3, 4, 5) else 94
        second = 1092 if field_line in (3, 4) else 94
        samples[:first] = [2000] * first
        samples[1280:1280 + second] = [2000] * second
    else:
        samples[:188] = [2000] * 188
        for index in range(400, LINE_SAMPLES):
            phase = (index - 400) % 320
            horizontal = -900 + phase * 1800 // 160 if phase < 160 else 900 - (phase - 160) * 1800 // 160
            samples[index] = 13500 + horizontal - 300 + line_number % 47 * 600 // 46
    return samples


def generate_pal_rf(path, mode):
    # PAL VHS: ire0=4.1 MHz, sync=3.8 MHz; PAL LD: ire0=7.1 MHz, hz/IRE=8000.
    # Values follow the repository's format snapshot. Integer DDS avoids OS-specific math.
    zero_hz = 4_100_000 if mode == "vhs" else 7_100_000
    deviation = 300_000 if mode == "vhs" else 342_857
    phase = 0
    with path.open("wb") as stream:
        for line in range(TOTAL_LINES):
            output = []
            for baseband in pal_line(line):
                frequency = zero_hz + (baseband - 10000) * deviation // 8000
                phase = (phase + frequency * (1 << 32) // 40_000_000) & 0xFFFFFFFF
                output.append(CARRIER[phase >> 24])
            stream.write(struct.pack("<" + "h" * len(output), *output))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def clean_environment():
    environment = dict(os.environ)
    for key in tuple(environment):
        if key.startswith(("DOTNET_", "COREHOST_", "ONEAPI_", "IPP_", "I_MPI_")) or key in (
            "LD_LIBRARY_PATH", "LD_PRELOAD", "MKLROOT", "TBBROOT", "CMPLR_ROOT"):
            environment.pop(key)
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    return environment


def run(arguments, cwd, environment):
    result = subprocess.run([str(argument) for argument in arguments], cwd=cwd, env=environment,
                            capture_output=True, text=True, timeout=180)
    if result.returncode:
        raise ValueError(f"Command failed ({result.returncode}): {arguments}\n{result.stdout}\n{result.stderr}")
    return result.stdout + result.stderr


def read_result(prefix, backend, provider=None, mode="vhs"):
    # The production writer uses .tbc.db for both VHS --write_db and LD output.
    suffixes = (".tbc", ".tbc.json", ".log", ".tbc.db")
    for suffix in suffixes:
        path = Path(str(prefix) + suffix)
        if not path.is_file() or not path.stat().st_size:
            raise ValueError(f"Decode omitted nonempty output {path}")
    metadata = json.loads(Path(str(prefix) + ".tbc.json").read_text())
    video = metadata["videoParameters"]
    if video["numberOfSequentialFields"] != 2 or len(metadata["fields"]) != 2:
        raise ValueError(f"Decode did not complete one frame: {prefix}")
    # Exact and IPP deliberately retain the compatible upstream metadata schema.
    # IPP selection is checked against the runtime diagnostic by the caller.
    if provider and video.get("approxProvider") != provider:
        raise ValueError(f"Decode metadata did not select provider {provider}: {video}")
    if mode == "vhs":
        verify_chroma(prefix, video)
    return metadata


def verify_chroma(prefix, video):
    path = Path(str(prefix) + "_chroma.tbc")
    expected = video["fieldWidth"] * video["fieldHeight"] * video["numberOfSequentialFields"] * 2
    if not path.is_file() or path.stat().st_size != expected or expected == 0:
        raise ValueError(f"VHS chroma output does not match metadata field geometry: {path}")
    return sha256(path)


def compare_tbc(reference, candidate, video=None):
    left = array("H", Path(str(reference) + ".tbc").read_bytes())
    right = array("H", Path(str(candidate) + ".tbc").read_bytes())
    if len(left) != len(right) or not left:
        raise ValueError("Exact/IPP TBC geometry differs or is empty")
    deltas = [abs(a - b) for a, b in zip(left, right)]
    maximum = max(deltas)
    rms = (sum(value * value for value in deltas) / len(deltas)) ** 0.5
    metrics = {"samples": len(left), "maximum_delta": maximum, "rms_delta": rms}
    if video is None:
        if maximum > 8 or rms > 1:
            raise ValueError(f"Exact/IPP TBC mismatch: maximum={maximum}, rms={rms}")
        return metrics
    width = video["fieldWidth"]
    expected_samples = width * video["fieldHeight"] * video["numberOfSequentialFields"]
    if len(left) != expected_samples:
        raise ValueError("TBC byte count does not match metadata field geometry")
    start, end = video["activeVideoStart"], video["activeVideoEnd"]
    if not 0 <= start < end <= width:
        raise ValueError("Invalid active video geometry")
    active = [value for index, value in enumerate(deltas) if start <= index % width < end]
    ire_scale = (video["white16bIre"] - video["black16bIre"]) / 100
    if ire_scale <= 0:
        raise ValueError("Invalid IRE calibration")
    metrics.update(active_maximum_ire=max(active) / ire_scale,
                   active_rms_ire=(sum(value * value for value in active) / len(active)) ** 0.5 / ire_scale)
    # Full decode can make discrete sync/interpolation decisions at RF block edges.
    # Gate picture quality in calibrated IRE, while bounding the whole sync-bearing raster.
    if maximum > 8192 or rms > ire_scale * 0.1 or metrics["active_maximum_ire"] > 2 or metrics["active_rms_ire"] > 0.05:
        raise ValueError(f"Exact/IPP TBC compatibility gate failed: {metrics}")
    return metrics


def decode_arguments(root, mode, backend, input_path, prefix):
    # LaserDisc does not accept the VHS overwrite/write_db command surface.
    mode_options = ["--overwrite", "--write_db"] if mode == "vhs" else ["--noEFM", "--disable_analog_audio"]
    return [root / f"{mode}-decode", "--pal", "--length", "1", "--threads", "2",
            *mode_options, "--dsp-backend", backend, input_path, prefix]


def verify(root, work):
    root = root.resolve()
    work.mkdir(parents=True, exist_ok=True)
    # Commands intentionally run outside the package, in a directory containing spaces.
    cwd = work / "unrelated working directory"
    cwd.mkdir(exist_ok=True)
    environment = clean_environment()
    for name in ("decode", "vhs-decode", "ld-decode", "cvbs-decode", "hifi-decode"):
        run([root / name, "--help"], cwd, environment)
    results = {}
    for mode in ("vhs", "ld"):
        input_path = (work / f"pal-{mode}.s16").resolve()
        generate_pal_rf(input_path, mode)
        prefixes = {}
        metadata_by_label = {}
        for label, backend, extra, provider in (
            ("exact", "exact", [], None),
            ("ipp", "ipp-fast", [], None),
            ("ipp-repeat", "ipp-fast", [], None),
        ):
            prefix = (work / f"{mode}-{label}").resolve()
            output = run(decode_arguments(root, mode, backend, input_path, prefix), cwd, environment)
            if backend == "ipp-fast" and "Intel IPP DSP backend: 2026.0.0" not in Path(str(prefix) + ".log").read_text():
                raise ValueError(f"Decode did not initialize the pinned Linux IPP runtime: {prefix}")
            (work / f"{mode}-{label}.console.txt").write_bytes(output.replace("\r\n", "\n").replace("\n", "\r\n").encode())
            metadata_by_label[label] = read_result(prefix, backend, provider, mode)
            prefixes[label] = prefix
        repeated_suffixes = (".tbc", ".tbc.db", ".tbc.json") + (("_chroma.tbc",) if mode == "vhs" else ())
        for suffix in repeated_suffixes:
            if sha256(Path(str(prefixes["ipp"]) + suffix)) != sha256(Path(str(prefixes["ipp-repeat"]) + suffix)):
                raise ValueError(f"IPP repeated decode is nondeterministic: {mode}{suffix}")
        if metadata_by_label["exact"] != metadata_by_label["ipp"]:
            raise ValueError(f"Exact/IPP field timing, geometry or metadata differs: {mode}")
        results[mode] = {"input_sha256": sha256(input_path),
                         "exact_vs_ipp": compare_tbc(prefixes["exact"], prefixes["ipp"], metadata_by_label["exact"]["videoParameters"]),
                         "ipp_tbc_sha256": sha256(Path(str(prefixes["ipp"]) + ".tbc"))}
        if mode == "vhs":
            results[mode]["ipp_chroma_sha256"] = verify_chroma(prefixes["ipp"], metadata_by_label["ipp"]["videoParameters"])
    # Automatic and explicit float32 providers must both work with the packaged runtime.
    for label, extra, provider in (
        ("auto", [], "ipp"), ("managed", ["--approx-provider", "managed"], "managed"),
        ("explicit-ipp", ["--approx-provider", "ipp"], "ipp"),
    ):
        prefix = (work / f"vhs-approx-{label}").resolve()
        run([root / "vhs-decode", "--pal", "--length", "1", "--threads", "2", "--overwrite",
             "--write_db", "--dsp-backend", "approx-fast", *extra,
             (work / "pal-vhs.s16").resolve(), prefix], cwd, environment)
        read_result(prefix, "approx-fast", provider)
    results["approx_providers"] = ["auto:ipp", "explicit:ipp", "explicit:managed"]
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("work", type=Path)
    options = parser.parse_args()
    try:
        result = verify(options.package, options.work.resolve())
        text = json.dumps(result, indent=2, sort_keys=True) + "\n"
        (options.work / "IPP-SMOKE.json").write_bytes(text.replace("\n", "\r\n").encode())
        print(text, end="")
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(f"Linux IPP package smoke failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

