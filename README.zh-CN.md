# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-18.1 -->

这是 [`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode)
中解码相关部分的 .NET 10 重写，当前以 release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4` 为兼容基线。

> [!IMPORTANT]
> 这是仍在进行中的兼容性移植。顶层解码路径已经实现并经过大量测试，
> 但项目目前还不声称每一种真实采集和罕见参数组合都已做到逐字节一致。

## 目录

- [范围](#范围)
- [当前状态](#当前状态)
- [兼容性覆盖](#兼容性覆盖)
- [性能](#性能)
- [构建和测试](#构建和测试)
- [使用方法](#使用方法)
- [输出和实时预览](#输出和实时预览)
- [验证](#验证)
- [剩余工作](#剩余工作)
- [详细证据](#详细证据)
- [许可证](#许可证)

<!-- SECTION: scope -->

## 范围

本项目只实现以下解码程序：

- `decode.py vhs`
- `decode.py cvbs`
- `decode.py ld`
- `decode.py hifi`
- 与 `vhs-decode`、`cvbs-decode`、`ld-decode` 和
  `hifi-decode` 等价的独立命令别名

以下内容明确不在当前范围内：

- TBC 工具以及无关的辅助程序
- 双击原版 decode 后出现的用户操作 GUI
- Matplotlib `--debug_plot` 窗口和 line-profiler 的界面/报告渲染
- 解码流水线本身不需要的滤波器调节界面

如果上游命令行兼容性需要，引用这些工具的解码参数仍会被正确解析。

<!-- SECTION: status -->

## 当前状态

| 区域 | 状态 | 当前边界 |
| --- | --- | --- |
| 解决方案和测试 | 已实现 | .NET 10 `.slnx`；标准 xUnit v3 测试可在 Visual Studio Test Explorer 和 `dotnet test` 中使用。 |
| CLI 和参数 | 已实现并做快照测试 | facade 与独立命令的帮助、别名、默认值、校验、诊断和退出行为以 v0.4.0 为目标。 |
| VHS 与磁带格式 | 已实现；仍有罕见采集差距 | VHS、S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ 及支持的 PAL/NTSC 变体共用 release 兼容解码路径。 |
| CVBS | 已实现 release 支持的系统 | PAL 和 NTSC 路径可运行；少见的 vblank 与跨参数组合仍需更多真实采集夹具。 |
| LaserDisc | 已实现；仍有罕见采集差距 | 视频、VBI、EFM、模拟音频、AC3、RF-TBC、元数据、恢复和 PAL/NTSC 路径均已连接。 |
| HiFi | 已实现；仍需真实采集验证 | 类型化 v0.4.0 CLI、有界并行解码、后处理、WAV/FLAC 输出、预览和 GNU Radio 模式均已连接。 |
| 输入 | 已广泛实现 | 已覆盖原始输入及常见的 FFmpeg/PyAV 等价容器路径；罕见 codec 和时间戳情况仍有差距。 |
| 输出与恢复 | 已实现；仍有边缘情况 | 已覆盖流式 TBC/音频输出、JSON 快照、SQLite、日志、磁盘空间处理和恢复顺序。 |
| 交互式 UI | 不在范围内 | 解码用户界面和开发者绘图/报告窗口不会实现。 |

“已实现”表示运行路径存在且有针对性的兼容性测试，并不表示所有可能的
采集都已经证明完全一致。

<!-- SECTION: coverage -->

## 兼容性覆盖

### 命令和参数

- 传统 `Program.Main` 入口和 `decode.py` 风格的分发。
- `decode.exe`、`vhs-decode.exe`、`cvbs-decode.exe`、
  `ld-decode.exe` 和 `hifi-decode.exe` apphost。
- Release 4.0 的参数名称、别名、默认值、位置参数、帮助文本、校验顺序、
  Python 风格数值行为和错误格式。
- 支持的磁带格式和颜色系统对应的 VHS 格式目录及参数文件。
- 标准输入/输出行为和与上游兼容的文件校验。

### 解码流水线

- RF 滤波、FM 解调、同步和电平检测、line-zero 恢复、场奇偶性、
  HSync 精修、TBC 重采样、掉点检测、色度、wow 校正、AGC 和元数据生成。
- `--use_saved_levels` 会复用上一场同步电平、在复用失败时重新检测，
  并在当前 VHS 场至少出现 30 个行定位错误时强制下一场执行完整检测，
  与 v0.4.0 的状态行为一致。
- VHS/S-VHS/Betamax/Video8/Hi8/U-matic/Type C/EIAJ 路由，以及上游
  release 4.0 所支持的 PAL、NTSC、PAL-M、PAL-N、MESECAM、NTSC-J、
  405-line 和 819-line 兼容路径。
- LaserDisc VBI、CAV/CLV 解释、模拟音频、EFM/pre-EFM、AC3、自动 MTF、
  AGC、VITS、播放器跳跃检测和恢复状态。
- HiFi 载波解码、掉点补偿、磁头切换插值、归一化、预览、GNU Radio
  传输和有序输出。

### 运行期与输出行为

- 对已覆盖分支保留精确或归一化后的上游诊断，包括恢复偏移、场顺序动作、
  参数文件日志和部分输出收尾。
- 按需支持流式 `.tbc`、`_chroma.tbc`、JSON、SQLite、PCM、EFM、
  pre-EFM、RF-TBC、AC3、WAV 和 FLAC 路径。
- 周期性恢复 JSON 快照以及上游风格的部分文件生命周期。
- 解码进行时，活动的 TBC、色度、JSON 和原始音频 sidecar 可被并发读取，
  预览工具无需等待解码结束。

<!-- SECTION: performance -->

## 性能

性能优化是实现的一部分，但确定性输出和 release 兼容性始终是首要约束。

- `-t` / `--threads` 驱动有界并行 RF 解调和滤波；stream、FFmpeg
  与 GNU Radio 读取保持有序。
- 以 stream 为作用域的已解码 RF 缓存避免在重叠场读取之间重复 FFT，
  同时限制内存占用。
- 较长的 TBC sinc 重采样任务共享 worker 配额并保持输出顺序；
  `--threads 0` 和 `--threads 1` 保留确定性的串行路径。
- HiFi 使用有界并行 block 解码，之后按顺序进行后处理和写入。
- 托管 FFT worker 复用临时缓冲区和不可变 root table，在安全位置使用
  原地变换，并在对齐路径上避免完整场复制。
- 与 SIMD 兼容的数值内核保留已验证夹具所要求的 NumPy/Numba
  reduction 和浮点转换边界。

在一台 Windows 夹具机器上，Release 单帧测量为：

| 解码 | 本项目 | Python v0.4.0 |
| --- | ---: | ---: |
| NTSC VHS | 2.346 s | 7.193 s |
| NTSC LaserDisc | 1.651 s | 5.865 s |

这些数字只对应特定夹具，不是通用 benchmark。一个 PAL LD 四场 Core
probe 在保持已验证输出的同时，将托管分配量从 5.12 GiB 降至 1.96 GiB。

<!-- SECTION: build -->

## 构建和测试

要求：

- .NET 10 SDK
- 使用 IDE 时需要 Visual Studio 2026
- 对 FFmpeg 支持的容器输入，需要 `ffmpeg` 和 `ffprobe` 位于 `PATH`
- 默认 HiFi FLAC 输出需要 `ffmpeg`

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

当前正式 Release 构建为零警告、零错误。xUnit v3 项目向
`dotnet test` 和 Visual Studio Test Explorer 暴露 **719** 个可独立发现的测试。

<!-- SECTION: usage -->

## 使用方法

查看 facade 或独立命令帮助：

```powershell
dotnet run --project src/VHSDecode.Cli -- vhs --help
dotnet run --project src/VHSDecode.Cli -- cvbs --help
dotnet run --project src/VHSDecode.Cli -- ld --help
dotnet run --project src/VHSDecode.Cli -- hifi --help
```

Release 构建后，可以使用 facade 分发或 apphost 别名：

```powershell
src\VHSDecode.Cli\bin\Release\net10.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net10.0\vhs-decode.exe [upstream options] input output
```

将命令替换为对应的 `cvbs`、`ld` 或 `hifi`，并使用上游 v0.4.0
参数。运行 `--help` 可查看精确的可接受参数面。

<!-- SECTION: preview -->

## 输出和实时预览

视频解码输出文件使用与上游 Python 行为兼容的读写共享方式打开。
解码活动期间：

- `.tbc` 和 `_chroma.tbc` 可以在增长过程中被打开和读取。
- 另一个进程可以解析已经发布的 `.tbc.json` 恢复快照。
- LD `.pcm`、`.efm` 和 `.prefm` sidecar 可以被并发读取。
- 允许 reader 不会在写入热点路径上增加复制或锁；实际性能影响主要取决于
  预览工具带来的竞争性存储 I/O。

writer 仍然是文件长度和快照发布时间的权威。reader 必须能够处理持续增长的
TBC 文件，并在 JSON 快照被替换后重新打开。

<!-- SECTION: verification -->

## 验证

测试套件是标准 xUnit v3，并非自制测试程序。覆盖范围包括：

- CLI/帮助/错误快照和格式/参数矩阵
- 确定性 DSP 与浮点兼容性夹具
- 串行/worker 输出和状态转换比较
- TBC、色度、JSON、SQLite、音频及 sidecar 生命周期测试
- 恢复、seek、奇偶性、场顺序和诊断顺序
- 活动输出共享和部分文件可读性
- 与上游 release 4.0 对比生成的差分夹具

已验证夹具包括逐字节一致输出和稳定的 SHA-256 基线。完整的逐算法清单及
hash 保存在下方链接的共享证据文档中。

<!-- SECTION: remaining -->

## 剩余工作

以下是有边界的兼容性和验证差距，不是缺失的顶层命令：

- 当前夹具以外的罕见容器 codec 与时间戳行为
- 更多 HiFi 真实采集端到端基线
- PAL LaserDisc、AC3 和 verbose VITS 的真实采集边缘情况
- 少见 VHS/CVBS vblank、色度 track-phase 和跨参数组合
- 罕见 first-HSync/vblank 恢复以及完整 JSON/SQLite 场元数据
- 剩余 TBC writer 位兼容边缘，以及所有格式、参数组合和真实采集的输出一致性
- 在兼容性受到夹具保护后，继续分析 CPU 利用率、分配、SIMD 和 worker 调度

交互式解码 UI 和 TBC 工具不在本目标内，也不会作为剩余解码兼容工作跟踪。

<!-- SECTION: evidence -->

## 详细证据

此前较长的实现与差分验证清单保存在
[`docs/COMPATIBILITY_EVIDENCE.md`](docs/COMPATIBILITY_EVIDENCE.md)。
该共享文档包含三种 README 共同引用的详细算法说明、数值边界、输出 hash
和夹具结果。

<!-- SECTION: license -->

## 许可证

GPL-3.0。参见 [`LICENSE`](LICENSE)。
