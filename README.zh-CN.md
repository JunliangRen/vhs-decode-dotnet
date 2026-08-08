# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-04.03 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,349** 项标准 xUnit v3 测试；测试可在
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
| `ipp-fast` | 实验性的 Windows x64 VHS 与 LaserDisc real-RF 路径，使用 Intel IPP；可能改变浮点位，并且绝不会静默回退到 `exact`。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

LaserDisc 的视频、EFM 与模拟音频 full-complex FFT 阶段现已接入 IPP。
CVBS 和 HiFi 仍会拒绝 `ipp-fast`；需要 release 兼容行为时应使用 `exact`。
兼容性敏感场景启用 IPP 前请阅读
[详细后端说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: performance -->

## 最新性能

下表固定使用同一份私有本地 40 MHz PAL VHS `.ldf` 夹具和相同的 40 帧窗口，
不会公开源文件名。Python 两列沿用已审计测量值；本候选基于 main `ced6afb`，
20 个 .NET 单元格均重新进行三次 Release 测量。兼容性结论与速度数据分开判断；
原始运行目录含有私有夹具路径，因此只保留在本地。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 15.207 s | 16.780 s | 4.126 s / 3.686x | 4.980 s / 3.369x | 3.480 s / 4.369x | 3.342 s / 5.021x |
| `--threads 1` | 17.694 s | 19.414 s | 9.767 s / 1.812x | 11.844 s / 1.639x | 7.104 s / 2.491x | 7.887 s / 2.462x |
| `--threads 5` | 15.719 s | 17.801 s | 4.221 s / 3.724x | 4.853 s / 3.668x | 3.555 s / 4.422x | 3.437 s / 5.179x |
| `--threads 10` | 16.037 s | 18.266 s | 3.427 s / 4.680x | 4.242 s / 4.306x | 3.036 s / 5.282x | 2.565 s / 7.121x |
| `--threads 20` | 16.405 s | 18.395 s | 2.928 s / 5.602x | 3.316 s / 5.548x | 2.601 s / 6.308x | 2.239 s / 8.217x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: dotnet-full-refresh=60 repeats=3 long-paired=6 compat=8 determinism=12 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。多 worker 的紧凑 VHS 路径现在会在低通同步工作继续进行时
补齐 `Video`、`Envelope` 和 `Chroma`；任一时刻只允许一个有界 staged span，串行或
有状态路径仍使用 eager 实现。1000 帧反序 Exact 配对中，v0.4.0 和 `current` 的
墙钟分别缩短 2.66% 和 2.55%；600 帧 IPP-fast 配对中，v0.4.0 缩短 1.85%，
`current` 基本中性（-0.03%）。

float32 PocketFFT plan 现在会保留不可变的实数根表，并按 root length 共享最多 32 份
复数根表。与 main `ced6afb` 直接比较的最终三对 1000 帧 Exact `current` 长跑属于
吞吐中性：只有 1/3 对更快，main/候选墙钟中位数为 44.924/44.985 秒（候选增加
0.14%，吞吐 0.999x），CPU 时间中位数为 332.672/333.734 秒（增加 0.32%）。
匹配的最终 200 帧 allocation trace 将 sampled allocation amount 从 579,283,536
降至 541,701,824 bytes（减少 6.49%）。

60 次刷新矩阵全部保持确定性；另行执行的 `--threads 0`、默认 5、`--threads 20`
门禁在两个 profile 和两个 backend 下均匹配亮度、色度、原始 JSON、stdout、归一化
stderr/日志和有序 `fileLoc`。IPP-fast 仍是显式启用的数值近似后端，
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

在原生输入路径上，40 kHz、单声道 PCM16 且总样本数不超过 `Int32.MaxValue` 的
直接 raw `fLaC` `.ldf`/`.flac` 使用内置 libsndfile。更大的 raw-FLAC 采集会改走
FFmpeg，因为 libsndfile 1.2.2 在该边界之后不能保证精确随机访问。符合条件的输入
可包括默认 40 MHz VHS `.ldf`、VHS `--no_resample` 和未指定 `--inputfreq` 的 LD；
默认 VHS `.flac`、全部 CVBS、Ogg/FLAC、立体声、PCM24、其他采样率和未完成文件头
继续使用 FFmpeg。

<!-- SECTION: build -->

## 构建与测试

项目固定使用 .NET SDK `11.0.100-preview.6.26359.118`。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1349
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
