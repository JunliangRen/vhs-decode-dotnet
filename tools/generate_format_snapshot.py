#!/usr/bin/env python3
"""Generate the embedded format parameter snapshot from upstream vhs-decode.

The snapshot captures the pure-Python format parameter layer without requiring
SciPy or Numba to be installed.  It installs tiny import-time stubs for those
packages because this script only calls parameter construction functions, not
the DSP functions that need the real packages.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import types
from pathlib import Path
from types import SimpleNamespace
from typing import Any


SYSTEMS = ["PAL", "PAL_M", "NTSC", "MESECAM", "405", "819", "NLINHA"]
TAPE_FORMATS = [
    "VHS",
    "VHSHQ",
    "SVHS",
    "SVHS_ET",
    "UMATIC",
    "UMATIC_HI",
    "UMATIC_SP",
    "BETAMAX",
    "BETAMAX_HIFI",
    "SUPERBETA",
    "VIDEO8",
    "HI8",
    "EIAJ",
    "QUADRUPLEX",
    "VCR",
    "VCR_LP",
    "TYPEC",
    "TYPEB",
    "VHD",
    "VIDEO2000",
]
TAPE_SPEEDS = {"sp": 0, "lp": 1, "ep": 2, "vp": 3}


class DummyModule(types.ModuleType):
    def __getattr__(self, name: str):
        def dummy(*_: Any, **__: Any):
            raise RuntimeError(f"Dummy function {self.__name__}.{name} called")

        return dummy


class FakeNumbaType:
    def __getitem__(self, _: Any) -> "FakeNumbaType":
        return self

    def __call__(self, *_: Any, **__: Any) -> "FakeNumbaType":
        return self


class CapturingLogger:
    def __init__(self) -> None:
        self.warnings: list[str] = []

    def warning(self, fmt: str, *args: Any) -> None:
        try:
            self.warnings.append(fmt % args)
        except Exception:
            self.warnings.append(" ".join([str(fmt), *map(str, args)]))


def identity_decorator(*dargs: Any, **dkwargs: Any):
    if dargs and callable(dargs[0]) and len(dargs) == 1 and not dkwargs:
        return dargs[0]

    def deco(fn: Any) -> Any:
        return fn

    return deco


def install_import_stubs() -> None:
    scipy = DummyModule("scipy")
    scipy.signal = DummyModule("scipy.signal")
    scipy.interpolate = DummyModule("scipy.interpolate")
    scipy.fft = DummyModule("scipy.fft")
    sys.modules["scipy"] = scipy
    sys.modules["scipy.signal"] = scipy.signal
    sys.modules["scipy.interpolate"] = scipy.interpolate
    sys.modules["scipy.fft"] = scipy.fft

    numba = types.ModuleType("numba")
    numba.njit = identity_decorator
    numba.jit = identity_decorator
    numba.jitclass = identity_decorator
    numba.version_info = SimpleNamespace(major=0, minor=59)
    for name in [
        "int8",
        "int16",
        "int32",
        "int64",
        "uint8",
        "uint16",
        "uint32",
        "uint64",
        "uintp",
        "float32",
        "float64",
        "boolean",
    ]:
        setattr(numba, name, FakeNumbaType())

    experimental = types.ModuleType("numba.experimental")
    experimental.jitclass = identity_decorator
    numba.experimental = experimental
    sys.modules["numba"] = numba
    sys.modules["numba.experimental"] = experimental


def normalize(value: Any, np: Any) -> Any:
    if isinstance(value, dict):
        return {str(k): normalize(v, np) for k, v in sorted(value.items(), key=lambda item: str(item[0]))}
    if isinstance(value, tuple):
        return [normalize(v, np) for v in value]
    if isinstance(value, list):
        return [normalize(v, np) for v in value]
    if isinstance(value, np.ndarray):
        return normalize(value.tolist(), np)
    if isinstance(value, np.generic):
        return normalize(value.item(), np)
    if isinstance(value, float):
        return value if math.isfinite(value) else str(value)
    return value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--upstream",
        type=Path,
        default=Path("upstream-vhs-decode"),
        help="Path to a checkout of oyvindln/vhs-decode.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("src/VHSDecode.Core/Formats/format-params.snapshot.json"),
        help="Snapshot JSON output path.",
    )
    parser.add_argument("--commit", default="43155200", help="Expected upstream commit label.")
    args = parser.parse_args()

    sys.path.insert(0, str(args.upstream.resolve()))
    install_import_stubs()

    import numpy as np
    import vhsdecode.formats as formats
    from lddecode.core import (
        FilterParams_NTSC,
        FilterParams_NTSC_lowband,
        FilterParams_PAL,
        FilterParams_PAL_lowband,
        SysParams_NTSC,
        SysParams_PAL,
    )

    cases = []
    for system in SYSTEMS:
        for tape_format in TAPE_FORMATS:
            for speed_name, speed_value in TAPE_SPEEDS.items():
                logger = CapturingLogger()
                entry: dict[str, Any] = {
                    "system": system,
                    "tape_format": tape_format,
                    "tape_speed": speed_name,
                    "tape_speed_value": speed_value,
                }
                try:
                    sysparams, rfparams = formats.get_format_params(
                        system, tape_format, speed_value, logger
                    )
                    entry["status"] = "ok"
                    entry["warnings"] = logger.warnings
                    entry["sysparams"] = normalize(sysparams, np)
                    entry["rfparams"] = normalize(rfparams, np)
                except Exception as exc:
                    entry["status"] = "error"
                    entry["warnings"] = logger.warnings
                    entry["error_type"] = type(exc).__name__
                    entry["error"] = str(exc)
                cases.append(entry)

    cvbs_cases = []
    for system in SYSTEMS:
        entry = {"system": system}
        try:
            sysparams, rfparams = formats.get_cvbs_params(system)
            entry["status"] = "ok"
            entry["sysparams"] = normalize(sysparams, np)
            entry["rfparams"] = normalize(rfparams, np)
        except Exception as exc:
            entry["status"] = "error"
            entry["error_type"] = type(exc).__name__
            entry["error"] = str(exc)
        cvbs_cases.append(entry)

    ld_cases = [
        {
            "system": "PAL",
            "lowband": False,
            "sysparams": normalize(SysParams_PAL, np),
            "rfparams": normalize(FilterParams_PAL, np),
        },
        {
            "system": "PAL",
            "lowband": True,
            "sysparams": normalize(SysParams_PAL, np),
            "rfparams": normalize(FilterParams_PAL_lowband, np),
        },
        {
            "system": "NTSC",
            "lowband": False,
            "sysparams": normalize(SysParams_NTSC, np),
            "rfparams": normalize(FilterParams_NTSC, np),
        },
        {
            "system": "NTSC",
            "lowband": True,
            "sysparams": normalize(SysParams_NTSC, np),
            "rfparams": normalize(FilterParams_NTSC_lowband, np),
        },
    ]

    snapshot = {
        "source": "oyvindln/vhs-decode",
        "commit": args.commit,
        "systems": SYSTEMS,
        "tape_formats": TAPE_FORMATS,
        "tape_speeds": TAPE_SPEEDS,
        "cases": cases,
        "cvbs_cases": cvbs_cases,
        "ld_cases": ld_cases,
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(snapshot, indent=2, sort_keys=True), encoding="utf-8")
    print(f"Wrote {args.output} ({len(cases)} tape cases, {len(cvbs_cases)} CVBS cases, {len(ld_cases)} LD cases)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
