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
不会公开源文件名。Python 两列沿用已审计测量值；本候选基于 main `aceec7e`，
20 个 .NET 单元格均重新进行三次 Release 测量。这份超大 raw-FLAC 已超过
libsndfile 1.2.2 的精确定位门禁，因此现在会正确改走 FFmpeg；新结果不能与之前基于
`ced6afb` 的表格直接比较。兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 15.207 s | 16.780 s | 7.209 s / 2.109x | 7.914 s / 2.120x | 5.388 s / 2.822x | 5.279 s / 3.179x |
| `--threads 1` | 17.694 s | 19.414 s | 12.109 s / 1.461x | 14.143 s / 1.373x | 8.568 s / 2.065x | 9.757 s / 1.990x |
| `--threads 5` | 15.719 s | 17.801 s | 7.238 s / 2.172x | 7.651 s / 2.327x | 5.266 s / 2.985x | 5.125 s / 3.473x |
| `--threads 10` | 16.037 s | 18.266 s | 5.862 s / 2.736x | 6.720 s / 2.718x | 4.967 s / 3.229x | 4.645 s / 3.933x |
| `--threads 20` | 16.405 s | 18.395 s | 5.779 s / 2.839x | 6.323 s / 2.909x | 4.634 s / 3.540x | 4.361 s / 4.218x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: dotnet-full-refresh=60 repeats=3 cti-long-paired=8 determinism=60 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。多 worker 的紧凑 VHS 路径现在会在低通同步工作继续进行时
补齐 `Video`、`Envelope` 和 `Chroma`；任一时刻只允许一个有界 staged span，串行或
有状态路径仍使用 eager 实现。1000 帧反序 Exact 配对中，v0.4.0 和 `current` 的
墙钟分别缩短 2.66% 和 2.55%；600 帧 IPP-fast 配对中，v0.4.0 缩短 1.85%，
`current` 基本中性（-0.03%）。

托管 AVX 现在一次计算 8 个彼此独立的 `current` CTI 距离 lane，同时保留原有
float32 FMA 顺序、标量尾部和无 AVX 回退。两组反序 1000 帧 Exact 配对基本中性，
基线/候选为 50.687/50.793 秒（-0.21%）。两组 IPP-fast 配对都由候选胜出：
43.730/42.872 秒（缩短 1.96%），CPU 时间从 255.273 降至 251.797 秒
（减少 1.36%）；IPP 配对的峰值内存没有增加。

60 次刷新矩阵全部保持确定性；默认 5 以及 `--threads 1/5/10/20` 在每个 profile
内都只有一套亮度、色度、原始 JSON、stdout、归一化 stderr 和日志 hash。Exact 与
IPP-fast 的 CTI 长跑门禁还在基线/候选之间匹配全部有序 `fileLoc`。IPP-fast 仍是
显式启用的数值近似后端，
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
