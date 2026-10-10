#!/usr/bin/env python3
"""Audit the actual ELF files and IPP ABI, without relying on the build host's distro."""

import argparse
import ctypes
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys


DEFAULT_LIMITS = {"GLIBC": "2.34", "GLIBCXX": "3.4.29", "CXXABI": "1.3.13", "GCC": "4.2.0"}
IPP_ABI = 0x00010004
# The pinned 2026.0.1-51 Linux packages identify the IPP library as 2026.0.0.
# Package and library version numbers are recorded separately in provenance.
IPP_VERSION = (2026, 0, 0)
IPP_SYSTEM_LIBRARIES = {
    "libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "librt.so.1",
    "libstdc++.so.6", "libgcc_s.so.1", "ld-linux-x86-64.so.2",
}


def run_tool(*arguments):
    environment = dict(os.environ, LC_ALL="C")
    return subprocess.run(arguments, check=True, capture_output=True, text=True, env=environment).stdout


def version_key(value):
    if not re.fullmatch(r"\d+(?:\.\d+){1,3}", value):
        raise ValueError(f"Invalid ABI version: {value}")
    parts = tuple(int(part) for part in value.split("."))
    return parts + (0,) * (4 - len(parts))


def parse_version_requirements(text):
    """Only imported versions count; a library's own version definitions do not."""
    requirements = {}
    in_needs = False
    for line in text.splitlines():
        if re.match(r"Version .* section", line):
            in_needs = line.startswith("Version needs section")
        if not in_needs:
            continue
        for namespace, value in re.findall(r"\bName:\s+(GLIBCXX|GLIBC|CXXABI|GCC)_(\S+)", line):
            version_key(value)  # Reject PRIVATE and unrecognized ABI feature versions, too.
            requirements.setdefault(namespace, set()).add(value)
    return {
        namespace: sorted(values, key=version_key)
        for namespace, values in requirements.items()
    }


def enforce_version_limits(requirements, limits):
    for namespace, values in requirements.items():
        if namespace not in limits:
            raise ValueError(f"No supported baseline for {namespace}")
        for value in values:
            if version_key(value) > version_key(limits[namespace]):
                raise ValueError(f"Requires {namespace}_{value}, above {namespace}_{limits[namespace]}")


def parse_dynamic_section(text):
    needed = re.findall(r"\(NEEDED\).*Shared library: \[([^\]]+)\]", text)
    for name in needed:
        if "/" in name or "\\" in name:
            raise ValueError(f"Nonportable DT_NEEDED path: {name}")
    paths = re.findall(r"\((?:RPATH|RUNPATH)\).*\[([^\]]*)\]", text)
    for path_list in paths:
        for path in path_list.split(":"):
            if path not in ("$ORIGIN", "${ORIGIN}"):
                prefix = "$ORIGIN/" if path.startswith("$ORIGIN/") else "${ORIGIN}/"
                if not path.startswith(prefix) or ".." in path[len(prefix):].split("/"):
                    raise ValueError(f"Nonportable RPATH/RUNPATH: {path}")
    return sorted(set(needed)), paths


def assert_elf_x64(header):
    if len(header) < 64 or header[:4] != b"\x7fELF":
        raise ValueError("Not a complete ELF header")
    if header[4:7] != b"\x02\x01\x01":
        raise ValueError("Expected ELF64 little-endian version 1")
    if struct.unpack_from("<H", header, 18)[0] != 62:
        raise ValueError("Expected x86-64 ELF machine")
    if struct.unpack_from("<H", header, 16)[0] not in (2, 3):
        raise ValueError("Expected executable/shared ELF object")


def audit_elf(path, limits):
    with path.open("rb") as stream:
        assert_elf_x64(stream.read(64))
    dynamic = run_tool("readelf", "--wide", "--dynamic", str(path))
    needed, paths = parse_dynamic_section(dynamic)
    requirements = parse_version_requirements(run_tool("readelf", "--wide", "--version-info", str(path)))
    enforce_version_limits(requirements, limits)
    if needed:
        resolved = run_tool("ldd", str(path.resolve()))
        if "not found" in resolved:
            raise ValueError(f"Unresolved ELF dependencies: {resolved.strip()}")
    return {"needed": needed, "search_paths": paths, "required_versions": requirements}


class IppRuntimeInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32), ("abi_version", ctypes.c_uint32),
        ("ipp_init_status", ctypes.c_int32), ("ipp_major", ctypes.c_int32),
        ("ipp_minor", ctypes.c_int32), ("ipp_update", ctypes.c_int32),
        ("ipp_build", ctypes.c_int32), ("reserved0", ctypes.c_uint32),
        ("cpu_features", ctypes.c_uint64), ("enabled_cpu_features", ctypes.c_uint64),
        ("ipp_name", ctypes.c_char * 64), ("ipp_version", ctypes.c_char * 64),
        ("ipp_build_date", ctypes.c_char * 32), ("ipp_target_cpu", ctypes.c_char * 32),
    ]


def expected_exports(contract):
    names = set(re.findall(r"\b(vhsdecode_ipp_\w+)\s*\(", contract.read_text(encoding="utf-8")))
    if len(names) < 30 or "vhsdecode_ipp_get_runtime_info" not in names:
        raise ValueError("Incomplete IPP C ABI export contract")
    return names


def probe_ipp(path, contract):
    required = expected_exports(contract)
    output = run_tool("nm", "-D", "--defined-only", str(path))
    exports = {line.split()[-1].split("@")[0] for line in output.splitlines() if line.split()}
    if exports != required:
        raise ValueError(f"IPP exports differ: missing={sorted(required - exports)}, unexpected={sorted(exports - required)}")
    dependencies, _ = parse_dynamic_section(run_tool("readelf", "--wide", "--dynamic", str(path)))
    unexpected = set(dependencies) - IPP_SYSTEM_LIBRARIES
    if unexpected:
        raise ValueError(f"IPP has non-system dependencies: {sorted(unexpected)}")
    if ctypes.sizeof(IppRuntimeInfo) != 240:
        raise ValueError("IPP runtime-info layout is not 240 bytes")
    library = ctypes.CDLL(str(path.resolve()))
    library.vhsdecode_ipp_get_abi_version.restype = ctypes.c_uint32
    if library.vhsdecode_ipp_get_abi_version() != IPP_ABI:
        raise ValueError("IPP bridge ABI version mismatch")
    library.vhsdecode_ipp_get_runtime_info.argtypes = [ctypes.POINTER(IppRuntimeInfo)]
    library.vhsdecode_ipp_get_runtime_info.restype = ctypes.c_int32
    info = IppRuntimeInfo()
    info.struct_size = ctypes.sizeof(info)
    status = library.vhsdecode_ipp_get_runtime_info(ctypes.byref(info))
    if status != 0 or info.ipp_init_status < 0:
        raise ValueError(f"IPP initialization rejected CPU/runtime: status={status}, init={info.ipp_init_status}")
    if info.struct_size != 240 or info.abi_version != IPP_ABI:
        raise ValueError("IPP runtime-info ABI/layout mismatch")
    version = (info.ipp_major, info.ipp_minor, info.ipp_update)
    if version != IPP_VERSION:
        raise ValueError(f"IPP version {version} differs from pinned {IPP_VERSION}")
    if not info.cpu_features or not info.enabled_cpu_features:
        raise ValueError("IPP did not report CPU dispatch features")
    return {
        "abi": f"0x{info.abi_version:08X}", "struct_size": info.struct_size,
        "version": ".".join(str(part) for part in version), "build": info.ipp_build,
        "target_cpu": info.ipp_target_cpu.decode("utf-8"), "exports": sorted(exports),
        "cpu_features": info.cpu_features, "enabled_cpu_features": info.enabled_cpu_features,
    }


def audit_package(root, limits, contract):
    files = {}
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            is_elf = stream.read(4) == b"\x7fELF"
        if is_elf:
            relative = str(path.relative_to(root))
            if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
                raise ValueError(f"ELF file escapes or aliases the package: {relative}")
            try:
                files[relative] = audit_elf(path, limits)
            except ValueError as error:
                raise ValueError(f"{relative}: {error}") from error
    if not files:
        raise ValueError("Package contains no ELF files")
    for name in ("decode", "libcoreclr.so", "libe_sqlite3.so", "libsndfile.so", "libsoxr.so", "libvhsdecode_ipp.so"):
        if name not in files:
            raise ValueError(f"Package is missing required ELF: {name}")
    return {"limits": limits, "elf_files": files, "ipp": probe_ipp(root / "libvhsdecode_ipp.so", contract)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("ipp", "package"))
    parser.add_argument("path", type=Path)
    parser.add_argument("--max-glibc", default=DEFAULT_LIMITS["GLIBC"])
    parser.add_argument("--contract", type=Path, default=Path(__file__).resolve().parents[1] / "src/VHSDecode.Ipp.Native/include/vhsdecode_ipp.h")
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    limits = dict(DEFAULT_LIMITS, GLIBC=options.max_glibc)
    try:
        if options.mode == "ipp":
            result = {"limits": limits, "elf": audit_elf(options.path, limits), "ipp": probe_ipp(options.path, options.contract)}
        else:
            result = audit_package(options.path, limits, options.contract)
        text = json.dumps(result, indent=2, sort_keys=True) + "\n"
        if options.output:
            options.output.write_bytes(text.replace("\n", "\r\n").encode("utf-8"))
            print(f"Verified {options.path}: IPP {result['ipp']['version']}, ABI {result['ipp']['abi']}; audit: {options.output}")
        else:
            print(text, end="")
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Linux native verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
