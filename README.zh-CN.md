# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-20.1 -->

这是 [`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode)
中解码相关部分的 .NET 11 重写，当前以 release `v0.4.0`、commit
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
| 解决方案和测试 | 已实现 | .NET 11 `.slnx`；标准 xUnit v3 测试可在 Visual Studio Test Explorer 和 `dotnet test` 中使用。 |
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
- VHS 使用有界连续 RF 流水线：一个 producer 独占有序输入读取，最多保留 32 个
  前瞻槽位，同时最多解调 8 个 block。每个完成的 block 会独立发布，因此当前场只等待
  自己需要的 block，不再等待整批任务。seek、输入流切换或释放会先取消并收拢 producer，
  再允许其他读取者接触 FFmpeg/GNU Radio stream。
- VSync 包络/极小值计算与谐波功率比搜索会在同一个只读 padded 输入上并发执行；
  两个分支完成后，候选仲裁和 detector 状态更新仍按顺序进行。
- 启用 worker 时，VHS 场解码会并发执行亮度 TBC 渲染与色度场解码。任何时刻最多只有
  一个色度任务在运行，并且会在推进下一场前回到调用线程按顺序提交状态。
- 较长的 TBC sinc 重采样任务共享 worker 配额并保持输出顺序；
  `--threads 0` 和 `--threads 1` 保留确定性的串行路径。
- 线性 wow 调整每行只计算一次恒定导数，在 median/MAD 修复后再展开；启用 worker 时，
  源位置与电平准备仅以固定两路并发执行。
- VHS heterodyne 与 carrier 表使用有界并行构造。只有载波和相位参数一致时，场解码才
  复用相位分析工作区；AFC 参数变化会回到原始重建路径。
- HiFi 使用有界并行 block 解码，之后按顺序进行后处理和写入。
- 托管 real FFT 复用池化的 packing 与 scratch 缓冲区，float32 SOS 前后向滤波
  在同一个扩展缓冲区内原地完成；每次变换结束都会归还租用项，避免反复冲击 LOH。
- RF span 直接写入请求的最终输出窗口，不再先分配整块边界场数组再做第二次切片复制。
- 标准 VHS 场解码最多复用两套精确长度的 RF span 缓冲，对应固定读取窗口只可能覆盖的
  两种 block 数。同步场解码结束后立即归还缓冲；公开 `Read` 结果、CVBS 延迟渲染和
  LD VITS 保留源仍拥有独立数组。
- AVX/FMA 内核加速精确 float32 转换、VHS RF envelope 准备、VHS Rust 风格
  FM 角度近似、LD 量化、VHS 色度移位和复数频域滤波。forward/inverse radix-4 FFT 与
  16-tap TBC sinc 内核使用固定指针索引消除边界检查，不改变算术顺序；差分测试
  保证变换位模式与输出 hash 不变。
- 恢复元数据以磁盘流式写入，snapshot 队列容量为 1，场顺序历史和 RF 缓存均有
  硬上限；长时间解码不会保留所有已解码场，也不会无限排入未来工作。
- CUDA/OpenCL 不是运行时依赖。当前 trace 不支持把孤立的 32K FFT 在主机与设备间
  往返；未来可选 GPU 后端必须批量处理常驻显存的 DSP 阶段，并保留精确 CPU 回退。

在一台 Windows 夹具机器上，Release 单帧测量为：

| 解码 | 本项目 | Python v0.4.0 |
| --- | ---: | ---: |
| NTSC VHS | 2.346 s | 7.193 s |
| NTSC LaserDisc | 1.651 s | 5.865 s |

这些数字只对应特定夹具，不是通用 benchmark。当前 VHS A/B 均使用 .NET SDK/runtime
`11.0.100-preview.6.26359.118`、`--threads 20`、默认色度和默认重采样。
在可重复的 40-frame PAL probe 上，保存的连续流水线改造前基线中位数为 11.60 秒，
当前中位数为 7.71 秒，提升 33.5%。平均活跃核心从约 2.2-2.5 升至 3.3-3.7；
配对 TBC、JSON 和 chroma SHA-256 全部一致。

当前 40/160/320-frame 持续运行分别用时 7.65/26.58/52.51 秒，工作集峰值为
1.76/1.88/1.67 GiB，后半程中位数为 1.42/1.30/1.28 GiB。320 帧全部写完，
内存没有随解码长度增长。此前的分配优化还将 PAL LD 四场 probe 从 5.12 GiB
降至 1.96 GiB。

有界 VHS 场内重叠把一次 160-frame 运行从 20.13 秒降到 18.55 秒（7.8%）。
TBC、chroma 与 JSON SHA-256 完全一致；任务会在当前场内等待完成，因此内存不会随
解码长度增长。

<details>
<summary>内核与分配 benchmark 历史</summary>

固定指针 PAL 尺寸 TBC sinc 隔离 A/B 将每场中位数从 3.929 ms 降到 3.727 ms，
内核提升 5.1%。后续内部窗口路径保留边缘和短输入的 clamp，同时让串行 probe 再提升
1.6%。一次新的 160-frame 运行用时 21.31 秒，私有内存四分段中位数为
0.78/1.18/1.20/1.41 GiB，峰值 1.68 GiB；TBC、JSON 与 chroma SHA-256 完全一致。

AVX RF envelope 准备将隔离的 32K-block 中位数从 57.5 us 降到 13.3 us，
内核提升 76.9%。40-frame 中位数从 7.55 秒降到 7.39 秒，160-frame 运行从
26.95 秒降到 25.70 秒；私有内存四分位中位数为 1.34/1.48/1.50/1.45 GiB，
峰值为 1.72 GiB，三项 hash 仍完全一致。

四路 AVX/SSE VHS Rust 风格 FM unwrap 将隔离的 32K-block 中位数从 610.1 us
降到 130.7 us，内核提升 78.6%。五对交错的 40-frame 完整路径 A/B 中，墙钟时间
中位数从 7.43 秒变为 7.41 秒，CPU 时间中位数从 27.88 秒降到 26.36 秒，减少
5.5%；TBC、JSON 与 chroma hash 保持完全一致。一次 160-frame 运行用时
26.48 秒，私有内存四分段中位数为 1.45/1.47/1.40/1.23 GiB，峰值 1.79 GiB。

最新的 FFmpeg stream 优化把每次读取都重建 16 MiB rewind buffer 改成了一个有界
环形缓冲。384 次隔离读取的中位数从 695.4 ms 降到 48.7 ms，分配量从 4.31 GB
降到 142.6 MB。三次 40-frame A/B 的墙钟/CPU 时间中位数从 8.98/28.47 秒降到
7.40/22.33 秒，三项输出 hash 均完全一致；采样到的 `byte[]` 分配从 36.3 GB
降到 209 MB。一次 160-frame 运行用时 25.86 秒，私有内存四分段中位数为
0.76/1.15/1.42/1.14 GiB，峰值 1.67 GiB。

最新的 VHS real-FFT 优化通过解码器自有、最多保留 16 份的 workspace 池，复用精确
长度的半频谱、Hilbert 缓冲、raw envelope 和旋转输入。五次隔离的 384-block A/B 中，
中位耗时从 1140.6 ms 降到 1054.0 ms（7.6%），分配量从 2.216 GB 降到 906.8 MB
（59.1%），Gen2 次数中位数从 168 降到 56。160-frame 完整路径 A/B 的墙钟时间基本
持平于 24.54/24.57 秒，CPU 时间从 78.03 秒降到 70.13 秒（10.1%）。当前运行峰值
为 1.68 GiB，私有内存四分段中位数为 0.88/1.55/0.78/1.51 GiB，并非单调增长；
TBC、JSON、chroma 和隔离 block 的 hash 均保持完全一致。

forward radix-4 内核现在与 inverse 一样使用固定指针索引；32768 点隔离中位数从
204.7 us 降到 195.9 us（4.3%），位模式完全一致。384-block RF 组合结果为
841.96/841.19 ms，实质持平，因此这里不宣称整块 RF 加速。

随后进行的 float32 SOS 优化保持样本主序算术顺序不变，并把 1、2、4-section 级联的
状态放入局部变量；其他 section 数使用扁平有界状态，32 section 以内使用栈空间，超过
该上限时回退到堆。五次隔离的 32K 两级/四级中位数从 110.2/155.4 ms 降到
75.3/83.3 ms（31.7%/46.4%），5/8/10-section 中位数分别降低
38.8%/40.2%/42.7%。两对 160-frame A/B 中，墙钟时间中位数从 21.22 秒降到
20.57 秒（3.1%），CPU 时间从 73.31 秒降到 68.73 秒（6.3%）；TBC、JSON 和
chroma hash 均完全一致。当前两次运行的私有内存峰值中位数为 1.71 GiB，四分段内存
并非单调增长。

</details>

<!-- SECTION: build -->

## 构建和测试

要求：

- `.NET SDK 11.0.100-preview.6.26359.118`（由 `global.json` 锁定）
- 使用 IDE 时需要 Visual Studio 2026
- 对 FFmpeg 支持的容器输入，需要 `ffmpeg` 和 `ffprobe` 位于 `PATH`
- 默认 HiFi FLAC 输出需要 `ffmpeg`

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

当前正式 Release 构建为零警告、零错误。xUnit v3 项目向
`dotnet test` 和 Visual Studio Test Explorer 暴露 **781** 个可独立发现的测试。

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
src\VHSDecode.Cli\bin\Release\net11.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net11.0\vhs-decode.exe [upstream options] input output
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
