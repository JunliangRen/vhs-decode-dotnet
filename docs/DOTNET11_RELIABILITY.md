# .NET 11 RC1 process lifecycle and diagnostics

The SDK remains pinned to `11.0.100-rc.1.26425.128`, with runtime
`11.0.0-rc.1.26425.128`. This change adopts the following RC1 capabilities:

- All production FFmpeg, ffprobe, uname, and LD AC3 starts share an explicit
  inherited-handle list. Windows and Linux tool starts also use
  `KillOnParentExit`, so a decoder that terminates unexpectedly does not leave
  its directly launched tools running.
- ffprobe and uname use `Process.RunAndCaptureText` / `ReadAllTextAsync` to
  drain stdout and stderr together. Async capture owns the process and terminates
  its tree on cancellation, including workers started by configured wrappers;
  uname has a five-second fallback timeout.
- Preview encoder and muxer failures use `ProcessExitStatus`, including Unix
  signal information. Cancellation terminates the process tree while the launcher
  is still alive, so its descendants remain identifiable for cleanup.
- The Windows LD AC3 pipeline uses `SafeFileHandle.CreateAnonymousPipe`, typed
  standard handles, `ArgumentList`, and managed process lifetime handling.
  This removes the custom Win32 process creation, argument quoting, and handle
  inheritance code while retaining the binary pipeline and log routing.
- C# 15 `closed` records represent started, completed, and abandoned preview
  windows. Frame counts and completion timestamps belong to the completed case;
  the display uses an exhaustive pattern switch and retains its existing text.
- Release tests use the SDK's run-level timeout and `per-module` result layout,
  plus the MTP TRX reporter. Windows and Linux CI preserve the main test reports;
  focused Windows fallback runs also have timeouts. Linux CI enables the runtime
  crash reporter before starting .NET and uploads available JSON crash reports.

The production NuGet graph, CLI arguments, DSP selection, sampling defaults,
and TBC JSON format are unchanged. The TRX extension is a test-only dependency.
For Linux crash report configuration, see [LINUX_X64.md](LINUX_X64.md#process-cleanup-and-crash-diagnostics).

## Validation

- Release solution build: no warnings or errors. Self-contained Windows publish
  succeeds with the existing `NETSDK1244` extraction-mode warning.
- Windows full suite: 1,870 discovered; 1,865 passed, 5 skipped, 0 failed.
  Skips cover the Linux crash reporter and unavailable AMF/QSV preview backends.
- Linux full suite under local Ubuntu 26.04 WSL: 1,829 discovered; 1,808 passed,
  21 skipped, 0 failed. It uses the existing Linux release script's 18
  method-scoped Windows bit-oracle exclusions and verified Linux native sidecars.
  Optional IPP/CUDA/hardware encoder cases follow their existing skip conditions.
  This is functional WSL validation, not Ubuntu 22.04 release certification.
- Eight process fixtures exercise concurrent stdout/stderr capture, cancellation,
  parent termination, binary anonymous pipes and EOF, Unix termination status,
  and an intentionally failing Linux helper that produces a parseable crash JSON.
  Two fixtures cover cancellation and termination of a launcher with an
  unprotected worker; the Unix launcher exits on SIGTERM without stopping its worker.
- The existing Windows AC3 test runs real sox and LD AC3 executables and compares
  the new native pipeline with the OS binary pipeline, including paths with spaces.
- The final preview hierarchy also passes the focused preview suite: 61 passed,
  4 unavailable hardware cases skipped, 0 failed.

The bounded real-capture differential uses
`F:\BmdFiles\DDD\DanDanYouQing-DHX2.ddd.flac` (71,470,217,882 bytes), the
published `v0.4.0-2.11.0` binary at
`9854a1ebf6de17c981bf677272333eb97059e198`, and CI-pinned FFmpeg 8.1.2.
Both variants use NTSC, `current`, `exact`, and 20 workers. Each sampling mode
decodes 320 frames beginning at RF time 4.25 seconds:

| Mode | Additional arguments | Fields per run | Result |
| --- | --- | --- | --- |
| Default 20-to-40 MSPS | `--start_fileloc 170000000` | 640 | TBC, chroma, JSON, field coordinates/order, and normalized ordered logs match |
| Native 20 MSPS | `--no_resample --start_fileloc 85000000` | 640 | TBC, chroma, JSON, field coordinates/order, and normalized ordered logs match |

All four complete processes exit successfully without observed tool children
remaining. A separate test forcibly terminates the newly built decoder during
real FLAC input and confirms that its observed FFmpeg child exits automatically.
These bounded runs establish output compatibility for this capture/window;
they do not establish a speed improvement or certify the entire capture.

Local commands, executable/output hashes, TRX files, and differential results
are retained under `artifacts/dotnet11-nonperf-features/`. In particular,
`media-regression-report.json` records both sampling modes and
`real-parent-exit/result.json` records the parent-death integration check.

## 中文说明

这次接入了子进程退出保护、标准句柄与匿名管道、Unix 信号退出诊断、
C# 15 封闭预览状态，以及测试超时、TRX 报告和 Linux 崩溃报告。
真实 20 MSPS FLAC 的默认重采样和原生采样率两条路径，各验证 320 帧；
与发布版的二进制输出、JSON、字段顺序和归一化后的有序日志一致。
Linux 用户可按 `LINUX_X64.md` 中的命令，在启动解码前启用崩溃报告。
