# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-04.02 -->

这是 [`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode)
中解码相关部分的 .NET 11 重写，兼容目标为上游 release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`。

> [!IMPORTANT]
> 这仍是持续进行中的兼容性移植。顶层解码路径已经实现并经过大量测试，但尚未声称
> 每一种真实采集和罕见参数组合都已做到逐字节一致。

完整兼容矩阵、实现细节、历次性能数据、验证证据和剩余差距请查看
**[中文详细说明](docs/README.detailed.zh-CN.md)**。

## 目录

- [项目概览](#项目概览)
- [快速开始](#快速开始)
- [行为配置与后端](#行为配置与后端)
- [最新性能](#最新性能)
- [兼容性状态](#兼容性状态)
- [构建与测试](#构建与测试)

<!-- SECTION: overview -->

## 项目概览

- 只实现解码部分：VHS、CVBS、LaserDisc 和 HiFi。
- 以 release 4.0 的命令、参数、别名、默认值、诊断和输出生命周期为兼容目标。
- VHS 家族包括 VHS/S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ
  以及上游支持的 PAL/NTSC 变体。
- TBC 工具、双击启动的用户 GUI 和开发者绘图窗口明确不在范围内。
- Visual Studio 2026 `.slnx` 包含 **1,319** 项标准 xUnit v3 测试；测试可在
  Test Explorer 中查看，也可用 `dotnet test` 运行。

<!-- SECTION: start -->

## 快速开始

从 [GitHub Releases](https://github.com/JunliangRen/vhs-decode-dotnet/releases)
下载当前仅含二进制文件的 Windows x64 包。发布包以单文件 `decode.exe` 为主。

```powershell
decode.exe vhs [upstream options] input.lds output
decode.exe cvbs [upstream options] input.lds output
decode.exe ld [upstream options] input.lds output
decode.exe hifi [upstream options] input.lds output.wav
```

同时支持 `vhs-decode.exe`、`ld-decode.exe` 等独立命令别名。使用
`decode.exe <command> --help` 查看完整兼容参数。

<!-- SECTION: profiles -->

## 行为配置与后端

`--compat-version` 选择上游行为：

| 值 | 含义 |
| --- | --- |
| `v0.4.0` | 默认值，目标是固定的 Python release 行为。 |
| `current` | 显式启用上游 PR 341 的分阶段行为，包括较新的 VHS 同步和 color-under 处理。 |

严格兼容基准是 Python v0.4.0 commit `g4315520` 的 `--threads 0` 输出。
Python 原版在不同 worker 数下的输出哈希并不稳定，因此 Python 多线程结果只用于
速度比较，不作为逐字节 oracle。

`--dsp-backend` 选择 DSP 实现：

| 值 | 含义 |
| --- | --- |
| `exact` | 默认托管路径，适合兼容性敏感的解码。 |
| `ipp-fast` | 实验性的 Windows x64 VHS real-RF 路径，使用 Intel IPP；可能改变浮点位，并且绝不会静默回退到 `exact`。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

CVBS、LaserDisc 和 HiFi 当前会拒绝 `ipp-fast`，这些命令应使用 `exact`。
兼容性敏感场景启用 IPP 前请阅读
[详细后端说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: performance -->

## 最新性能

下表固定使用同一份私有本地 40 MHz PAL VHS `.ldf` 夹具和相同的 40 帧窗口，
不会公开源文件名。Python 两列和 19 个未受影响的 .NET 单元格沿用已审计测量值。
本分支基于 main `4f50fa9`，只把受影响的 IPP-fast/current 20-worker 单元格重测
三次；兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 15.207 s | 16.780 s | 4.267 s / 3.564x | 4.690 s / 3.578x | 3.608 s / 4.215x | 3.393 s / 4.946x |
| `--threads 1` | 17.694 s | 19.414 s | 9.732 s / 1.818x | 11.640 s / 1.668x | 7.089 s / 2.496x | 7.947 s / 2.443x |
| `--threads 5` | 15.719 s | 17.801 s | 4.167 s / 3.772x | 4.648 s / 3.830x | 3.607 s / 4.358x | 3.388 s / 5.254x |
| `--threads 10` | 16.037 s | 18.266 s | 3.274 s / 4.899x | 4.096 s / 4.459x | 3.069 s / 5.225x | 2.732 s / 6.686x |
| `--threads 20` | 16.405 s | 18.395 s | 2.919 s / 5.620x | 3.765 s / 4.886x | 2.667 s / 6.150x | 2.180 s / 8.438x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: prior-full-refresh=60 affected-refresh=3 repeats=3 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。高 worker 数下，每个池化 IPP VHS workspace 现在独占一个
有界的 companion FFT plan，并发执行互相独立的实数与 Hilbert 逆变换；DSP 算术和
16 个 workspace 的保留上限均未改变。固定 100 帧的五对测试把平均墙钟从 9.347
降至 9.035 秒（缩短 3.3%），平均有效核心数从 4.74 增至 4.88。正式 40 帧三对
测试全部更快；刷新的 20-worker 单元格为 2.180 秒，达到对应 Python PR341 的
8.438x。

刷新运行和两个 profile/thread 门禁都保持确定性。基线/候选门禁匹配了亮度、色度、
原始 JSON、stdout、归一化 stderr/日志和有序 `fileLoc`。IPP-fast 仍是显式启用的数值近似后端，
因此不声称其产物与 Exact 逐字节一致。Python v0.4.0 在非零 worker 数下可能改变
输出 hash，因此严格 oracle 仍是 Python v0.4.0 `g4315520 --threads 0`。完整命令、
硬件、hash、内存边界和历史测量请查看
[详细性能说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: compatibility -->

## 兼容性状态

主要解码流水线、流式输出、恢复行为和 CLI 表面已经实现。聚焦测试和真实 RF 门禁
覆盖亮度、色度、JSON、有序 `fileLoc`、stdout、归一化 stderr/日志、确定性和
有界内存。罕见采集和少见参数组合仍在持续验证，因此构建成功或文件大小相同都不
等同于兼容性证明。

TBC、色度、JSON 和日志文件允许在解码期间并发读取，兼容的预览工具可以检查
部分输出而不会阻塞写入。

在原生输入路径上，40 kHz、单声道 PCM16 的直接 raw `fLaC` `.ldf`/`.flac` 使用
内置 libsndfile。这包括默认 40 MHz VHS `.ldf`、VHS `--no_resample`，以及未指定
`--inputfreq` 的 LD；默认 VHS `.flac` 和全部 CVBS 输入仍走 FFmpeg/PyAV 兼容路径。
Ogg/FLAC、立体声、PCM24、其他采样率和未完成的文件头也继续使用 FFmpeg。

<!-- SECTION: build -->

## 构建与测试

项目固定使用 .NET SDK `11.0.100-preview.6.26359.118`。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1319
```

在 Visual Studio 2026 中打开 `VHSDecodeDotNet.slnx`，即可构建、调试并通过
Test Explorer 运行 xUnit v3 测试。

<!-- SECTION: detail -->

## 详细资料

- [中文详细说明](docs/README.detailed.zh-CN.md)
- [兼容性证据](docs/COMPATIBILITY_EVIDENCE.md)
- [English overview](README.md)
- [日本語概要](README.ja.md)

<!-- SECTION: license -->

## 许可证

GPL-3.0。参见 [`LICENSE`](LICENSE)。
