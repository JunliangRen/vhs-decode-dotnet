import ctypes
import importlib.util
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location("linux_native", Path(__file__).resolve().parents[1] / "verify-linux-native.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class LinuxNativeAuditTests(unittest.TestCase):
    def test_runtime_info_layout_matches_real_c_abi(self):
        self.assertEqual(240, ctypes.sizeof(native.IppRuntimeInfo))
        self.assertEqual(32, native.IppRuntimeInfo.cpu_features.offset)
        self.assertEqual(48, native.IppRuntimeInfo.ipp_name.offset)
        self.assertEqual(208, native.IppRuntimeInfo.ipp_target_cpu.offset)

    def test_version_definitions_do_not_raise_import_baseline(self):
        text = """Version definition section '.gnu.version_d' contains 1 entry:
  Name: GLIBC_2.43
Version needs section '.gnu.version_r' contains 3 entries:
  Name: GLIBC_2.2.5 Flags: none
  Name: GLIBC_2.34 Flags: none
  Name: GLIBCXX_3.4.11 Flags: none
  Name: CXXABI_1.3.9 Flags: none
"""
        actual = native.parse_version_requirements(text)
        self.assertEqual(["2.2.5", "2.34"], actual["GLIBC"])
        native.enforce_version_limits(actual, native.DEFAULT_LIMITS)

    def test_all_abi_namespaces_have_enforced_limits(self):
        for namespace, newer in (("GLIBC", "2.35"), ("GLIBCXX", "3.4.30"), ("CXXABI", "1.3.14"), ("GCC", "4.3.0")):
            with self.subTest(namespace=namespace), self.assertRaises(ValueError):
                native.enforce_version_limits({namespace: [newer]}, native.DEFAULT_LIMITS)

    def test_unsupported_and_private_abi_requirements_are_rejected(self):
        for value in ("PRIVATE", "ABI_DT_RELR"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                native.parse_version_requirements("Version needs section '.gnu.version_r':\n Name: GLIBC_" + value)

    def test_numeric_version_order_and_trailing_zero_normalization(self):
        self.assertGreater(native.version_key("2.10"), native.version_key("2.9"))
        self.assertEqual(native.version_key("2.34"), native.version_key("2.34.0"))

    def test_cwd_absolute_and_escaping_loader_paths_are_rejected(self):
        for value in ("", "/opt/intel/oneapi/lib", ".", "$ORIGIN/../outside", "${ORIGIN}/../../", "$ORIGIN:"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                native.parse_dynamic_section(f" 0x (RUNPATH) Library runpath: [{value}]")

    def test_origin_relative_loader_paths_are_allowed(self):
        needed, paths = native.parse_dynamic_section(""" 0x (NEEDED) Shared library: [libstdc++.so.6]
 0x (NEEDED) Shared library: [libc.so.6]
 0x (RUNPATH) Library runpath: [$ORIGIN:${ORIGIN}/native]
""")
        self.assertEqual(["libc.so.6", "libstdc++.so.6"], needed)
        self.assertEqual(["$ORIGIN:${ORIGIN}/native"], paths)

    def test_absolute_needed_library_paths_are_rejected(self):
        with self.assertRaises(ValueError):
            native.parse_dynamic_section(" 0x (NEEDED) Shared library: [/opt/intel/libipps.so]")

    def test_x64_executable_and_shared_elf_headers_are_accepted(self):
        for kind in (2, 3):
            with self.subTest(kind=kind):
                header = bytearray(64)
                header[:7] = b"\x7fELF\x02\x01\x01"
                struct.pack_into("<HH", header, 16, kind, 62)
                native.assert_elf_x64(header)

    def test_truncated_wrong_architecture_and_wrong_kind_are_rejected(self):
        good = bytearray(64)
        good[:7] = b"\x7fELF\x02\x01\x01"
        struct.pack_into("<HH", good, 16, 3, 62)
        cases = [good[:20], bytearray(64)]
        for offset, value in ((4, 1), (5, 2), (6, 0), (16, 1), (18, 183)):
            changed = good.copy()
            changed[offset] = value
            cases.append(changed)
        for header in cases:
            with self.subTest(header=bytes(header[:20])), self.assertRaises(ValueError):
                native.assert_elf_x64(header)

    def test_export_contract_is_complete(self):
        contract = Path(__file__).resolve().parents[2] / "src/VHSDecode.Ipp.Native/include/vhsdecode_ipp.h"
        exports = native.expected_exports(contract)
        self.assertIn("vhsdecode_ipp_fft64_forward_real", exports)
        self.assertIn("vhsdecode_ipp_sos32_set_state", exports)
        self.assertIn("vhsdecode_ipp_status_string", exports)
        self.assertGreaterEqual(len(exports), 38)


if __name__ == "__main__":
    unittest.main()
