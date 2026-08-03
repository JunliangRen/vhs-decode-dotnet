# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-03.01 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,283** 项标准 xUnit v3 测试；测试可在
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
不会公开源文件名。基础矩阵是 2026-08-02 在 main commit `c92af1d` 上完成的
三次交错运行中位数。2026-08-03，在完成有界 ACC 分段和 Super-Gaussian FFT
并行化后，最终 cap-12 分支候选以三次交错运行重新测量了全部 `current` 单元格；Python
与 .NET v0.4.0 单元格沿用此前审计值。随后又用 15 次最终候选运行刷新了
`IPP-fast + current` 列，其中混合基数 Super-Gaussian 变换改走 IPP DFT32。
兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 15.207 s | 16.780 s | 4.389 s / 3.465x | 7.030 s / 2.387x | 3.609 s / 4.213x | 3.801 s / 4.415x |
| `--threads 1` | 17.694 s | 19.414 s | 10.065 s / 1.758x | 13.871 s / 1.400x | 7.215 s / 2.453x | 9.527 s / 2.038x |
| `--threads 5` | 15.719 s | 17.801 s | 4.282 s / 3.671x | 7.428 s / 2.396x | 3.568 s / 4.406x | 3.583 s / 4.968x |
| `--threads 10` | 16.037 s | 18.266 s | 3.494 s / 4.589x | 6.040 s / 3.024x | 3.098 s / 5.177x | 3.027 s / 6.035x |
| `--threads 20` | 16.405 s | 18.395 s | 3.235 s / 5.071x | 5.118 s / 3.594x | 2.654 s / 6.182x | 2.545 s / 7.229x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: base=90 current-refresh=30 ipp-current-refresh=15 repeats=3 -->

本轮保留的 IPP 候选只把 `ipp-fast + current` 的 356400 点托管 float32 变换
替换为任意长度 IPP DFT32。同一份私有本地 PAL VHS 样本的一组固定 200 帧
`--threads 20` 配对中，墙钟从 `10.815` 降到 `9.470` 秒（缩短 12.43%，
吞吐 1.142x），进程 CPU 时间从 `65.797` 降到 `51.047` 秒，峰值工作集减少
1.7 MiB。亮度和原始 JSON 逐字节一致；1.42102 亿个色度样本中 0.1627% 有位差，
全部样本的 99.99987% 误差不超过 1，RMSE 为 0.063，符合已说明的近似
`ipp-fast` 约定。另行执行的 Exact v0.4.0 与 `current` 真实样本门禁均为九项全同。

随后又用 Exact `current --threads 20` 做了一组 80 帧短筛，保留上面的完整矩阵不变：
decoder-local burst-probe 缓冲复用的墙钟基本持平（`9.393` 降至 `9.371` 秒），
进程 CPU 时间从 `74.406` 降至 `67.922` 秒，峰值工作集从 `435.1` 降至
`430.5` MiB。这一组结果不作为新的吞吐提升声明。亮度 TBC、色度 TBC、JSON、
stdout、归一化 stderr/日志以及零线程/默认/20 worker 确定性均一致。

2026-08-03 最新一组短筛把分类后的同步脉冲改为不可变值，避免为每个脉冲分配
单独对象。在同一份 80 帧 Exact `current --threads 20` 门禁中，墙钟从
`10.309` 降至 `10.003` 秒（快 `2.97%`），进程 CPU 时间从 `69.453` 增至
`70.797` 秒（增加 `1.93%`），峰值工作集基本不变，为 `435.3`/`435.0` MiB；
九项兼容性比较全部一致。这一组只更新近期优化证据，不替代上面的完整矩阵中位数。

本轮保留的高 worker Exact VHS 候选在串行完成频谱准备后，并行执行两个彼此独立的
逆 FFT 阶段。同一份私有本地 PAL VHS 夹具上，一组固定 200 帧 Exact
`current --threads 20` 配对结果如下：

| 本轮保留优化 | main `eec3658` | 分支候选 | 变化 |
| --- | ---: | ---: | ---: |
| 墙钟时间 | 11.945 s | 11.480 s | 缩短 3.89% / 吞吐 1.041x |
| 进程 CPU 时间 | 95.359 s | 90.828 s | 降低 4.75% |
| 有效核心数 | 7.98 | 7.91 | 基本不变 |

退出状态、field 数、亮度、色度、原始 JSON、stdout、归一化 stderr、归一化日志和
有序 `fileLoc` 九项门禁全部一致。测量脚本没有返回可用的峰值工作集，因此不声明
内存数字；实现没有增加样本长度级别的缓冲，并继续受现有 12-block 外层流水线约束。
这组 200 帧数据的范围不同，不替代上面的 40 帧重复运行矩阵。

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；低于
`1.000x` 表示更慢。Python PR341 使用合并提交
`2f21e8ed6018b14561396cc95f1f6828054470b8`，它是 `current` 的上游对应版本。
默认实际使用 **5 个 workers**。基础矩阵共 90 次运行，即 30 个模式/profile
组合各重复三次；十个刷新的 `current` 单元格另增加 30 次最终候选交错运行，
最终 IPP DFT32 列再增加 15 次运行。

每个 .NET profile 和 Python PR341 在各模式下都只产生一套确定性 hash。Python
v0.4.0 在每个默认/非零线程模式的三次运行中都产生三套亮度、色度、JSON 和日志
hash，但有序 `fileLoc`、stdout 与归一化 stderr 保持稳定，因此这些 Python 行只用于
吞吐比较。另行执行的 40 帧 `--threads 0` 门禁中，Exact v0.4.0 与 Exact
`current` 在输出字节、元数据、stdout/stderr 和归一化日志上匹配各自 Python
对应版本；严格 oracle 仍是 Python v0.4.0 `g4315520 --threads 0`。

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
  --no-build --no-restore --minimum-expected-tests 1283
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
