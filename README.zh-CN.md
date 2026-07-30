# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-30.02 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,122** 项标准 xUnit v3 测试；测试可在
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

下表固定使用同一份私有本地 40 MHz NTSC `BETAMAX_HIFI` `.lds` 样本，
所有运行采用相同的有界帧区间，且不会公开样本文件名。每项 .NET 提升都相对于
同一请求 worker 数下的 Python v0.4.0；兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 16.983 s | 6.416 s / 2.647x | 7.678 s / 2.212x | 5.667 s / 2.997x | 7.184 s / 2.364x |
| `--threads 1` | 21.263 s | 20.547 s / 1.035x | 22.961 s / 0.926x | 20.129 s / 1.056x | 22.320 s / 0.953x |
| `--threads 5` | 16.880 s | 6.173 s / 2.735x | 7.802 s / 2.164x | 5.802 s / 2.909x | 7.186 s / 2.349x |
| `--threads 10` | 17.612 s | 4.880 s / 3.609x | 6.118 s / 2.879x | 4.636 s / 3.799x | 5.744 s / 3.066x |
| `--threads 20` | 18.330 s | 3.997 s / 4.586x | 4.767 s / 3.845x | 3.966 s / 4.622x | 4.919 s / 3.726x |
<!-- LATEST_PERFORMANCE_END -->

最新一轮 Exact 优化复用两块不会逃逸的 worker-local complex-demodulation 缓冲，
同时保留诊断输出仍各自独立。数据类型、表达式、运算顺序、FFT 行为和有序状态提交
均未改变；全部 profile/thread 门禁和上表 60 次刷新运行都保持精确。四组相反顺序的
current/20-worker 500 帧配对中，中位墙钟仅变化 +0.26%，CPU 时间减少 1.82%；
更长的默认与 v0.4.0 配对介于 -0.32% 到 +1.10%，因此吞吐按中性处理。两组相反
顺序的 1000 帧 counters 配对把分配从 111.461 降至 88.022 GiB（减少 21.03%），
GC pause 从 0.994 降至 0.791 秒（减少 20.43%）。3000 帧长跑的前/中/后
1000 帧耗时为 72.00/69.11/68.50 秒，四段工作集中位数为
722/1305/602/864 MiB，说明内存有界且没有越跑越慢。

每个 .NET 单元格依次给出墙钟中位数和相对同一行 Python 的倍速；低于 `1.000x`
表示更慢。默认实际使用 **5 个 workers**。非零线程的 Python 行只用于吞吐比较，
严格兼容基准仍是 Python `--threads 0`。

默认 worker 的实际数量、完整命令、构建 hash、硬件、重复测量方法、输出 hash
以及旧数据请查看[详细性能说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: compatibility -->

## 兼容性状态

主要解码流水线、流式输出、恢复行为和 CLI 表面已经实现。聚焦测试和真实 RF 门禁
覆盖亮度、色度、JSON、有序 `fileLoc`、stdout、归一化 stderr/日志、确定性和
有界内存。罕见采集和少见参数组合仍在持续验证，因此构建成功或文件大小相同都不
等同于兼容性证明。

TBC、色度、JSON 和日志文件允许在解码期间并发读取，兼容的预览工具可以检查
部分输出而不会阻塞写入。

<!-- SECTION: build -->

## 构建与测试

项目固定使用 .NET SDK `11.0.100-preview.6.26359.118`。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1122
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
