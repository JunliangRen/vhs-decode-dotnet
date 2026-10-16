# .NET 11 process lifecycle and diagnostics

The current SDK is pinned to `11.0.100-rc.2.26504.105`, with runtime
`11.0.0-rc.2.26504.105`. The following capabilities were introduced during
the RC1 adoption and are retained on RC2:

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

Those RC1 process-lifecycle changes did not alter the production NuGet graph,
CLI arguments, DSP selection, sampling defaults, or TBC JSON format. The TRX
extension is a test-only dependency. The subsequent RC2 package updates and
their validation are recorded in COMPATIBILITY_EVIDENCE.md.
For Linux crash report configuration, see [LINUX_X64.md](LINUX_X64.md#process-cleanup-and-crash-diagnostics).

## RC2 launch profiles, process observations and sustained queues

[RC2 expands evaluated MSBuild properties in launch-profile arguments](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc2/sdk.md#msbuild-properties-in-launch-profile-arguments). Shared
properties live in `src/VHSDecode.Cli/Properties/DecodeDiagnostics.props`.

| Profile | DSP | Input / processing rate |
| --- | --- | --- |
| `Exact40` | `exact`, `current` | 20 MSPS input, default 40 MSPS resampling |
| `Exact20` | `exact`, `current` | Native 20 MSPS with `--no_resample` |
| `Ipp40` | `ipp-fast`, `current` | 20 MSPS input, default 40 MSPS resampling |

All three use NTSC VHS, `--write_db`, 20 workers, 320 frames and start frame 0
by default. Supply an actual capture; the placeholder is not a fixture. Each
launch gets a unique output directory. The first `Default` profile preserves
ordinary `dotnet run` behavior and does not enable observations.

```powershell
dotnet run --project src/VHSDecode.Cli --configuration Release --launch-profile Exact20 --property:DecodeSamplePath="F:/captures/input.flac"
```

Use the same command with `Exact40` or `Ipp40`. Override `DecodeFrames`,
`DecodeThreads`, `DecodeStartFrame` and `DecodeObservationRoot` as MSBuild
properties. For example, `--property:DecodeFrames=6400` requests a longer run;
account for the size of TBC outputs. `DecodeObservationId` accepts a known run
name; use a fresh ID for every run. Paths should be absolute. The input rate is
fixed at 20 MSPS; missing IPP is an error, with no silent backend substitution.

Profiles set `VHSDECODE_PROCESS_DIAGNOSTICS=1`. Only explicit diagnostic mode
creates the output parent directory before the legacy I/O argument validation.
The CLI writes `<output-base>.process.jsonl`: start, periodic and stop records
include elapsed time, process/runtime/OS identity, selected settings, working
set, OS private memory, CPU time, thread count, managed live/allocated estimates,
last-GC heap/fragmentation, collection counts, exit code and sampling errors.
Records are flushed during execution, use UTF-8 without BOM and CRLF, and
existing reports are never overwritten. Sampling defaults to one second;
`VHSDECODE_PROCESS_DIAGNOSTICS_INTERVAL_MS` accepts 50 through 60000.

The observer runs inside the decoder and includes its own sampling cost.
FFmpeg and other child processes are excluded. OS private memory is
`Process.PrivateMemorySize64`, whose meaning differs across platforms; it is
not private RSS/PSS. Last-GC values describe the most recent collection.
No full GC is forced. An interrupted process may lack a stop record; a null
exit code or non-null sampling error means an incomplete report. Normal CLI
runs have no observer. TBC/JSON/SQLite formats are unchanged. A short flat
memory trace does not establish the absence of a leak.

Repeat the actual HiFi request/reply queue and JSON snapshot worker tests:

```powershell
pwsh -File tools/run-decode-queue-stress.ps1 -DurationSeconds 60
```

The harness verifies complete delivery, per-producer order, blocked-producer
cancellation without a receiver, and final field order. Use `-NoBuild` after
a matching build; pass `-ArtifactsPath` when that build used a separate artifact
root. Each iteration has a two-minute test limit and retains its
own TRX/log in a unique artifact directory, plus a summary. The current
iteration finishes even after the requested observation window elapses.
These synthetic lifecycle checks do not deterministically reproduce the rare
upstream ConcurrentQueue race or establish real-media throughput, full-tape
behavior, or memory-leak freedom.

### RC2 feature validation (2026-10-16)

- Release builds on Windows and local Ubuntu 26.04 WSL: zero warnings/errors.
- Windows suite: 1,912 total, 1,898 passed, 14 platform/hardware skips, zero failures.
- Linux portable suite: 1,871 total, 1,865 passed, six skips, zero failures;
  the existing 18-method / 41-case Windows frozen-oracle exclusions are separate.
- Fourteen new cases cover opt-in settings, live JSONL flushing, file closure,
  report preservation, queue delivery/order, blocked-producer cancellation and
  1,000/4,096-field repeated snapshot completion. The Windows and Linux queue harnesses
  additionally pass continuous 60-second windows.
- Each of `Exact40`, `Exact20` and `Ipp40` decodes 1,280 real 20 MSPS NTSC VHS
  frames at start frame 128 with 20 workers, once through its observed launch
  profile and once directly without observation. TBC/chroma and raw TBC JSON
  hashes match within each pair; database schema and complete canonical SQL
  exports match. Every run exits zero with no observed remaining tool children.
  Paths containing spaces and completed process observations are verified.
- All three profiles also decode 32 frames under WSL and produce valid process
  observations. The default profile retains the ordinary CLI help behavior.

These runs compare observation enabled/disabled on the same RC2 candidate,
not RC1/RC2 speed or different DSP modes. Local artifacts are under
`artifacts/dotnet11-rc2-upgrade/diagnostic-validation/`. They are bounded
compatibility observations, not full-tape or leak-freedom certification.

## Historical RC1 validation

The results below were measured on RC1. RC2 upgrade results are recorded separately
in [COMPATIBILITY_EVIDENCE.md](COMPATIBILITY_EVIDENCE.md#net-11-rc2-toolchain-upgrade).

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
