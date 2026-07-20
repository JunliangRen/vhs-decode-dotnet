# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-07-20.15 -->

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
- 对频率严格为 40.0 MHz 的 `.s16` 输入，直接使用原生有符号 16 位 loader，
  跳过无实际转换的 FFmpeg 透传；其他格式与真正的重采样仍走原有 FFmpeg 路径。
- packed `.lds` 输入会直接解包到请求的结果数组，并兼容 Python 的末尾部分组语义，
  不再分配完整解包数组后进行第二次复制。
- 以 stream 为作用域的已解码 RF 缓存避免在重叠场读取之间重复 FFT，
  同时限制内存占用。
- VHS 使用有界连续 RF 流水线：一个 producer 独占有序输入读取，最多保留 32 个
  前瞻槽位，同时最多解调 8 个 block。每个完成的 block 会独立发布，因此当前场只等待
  自己需要的 block，不再等待整批任务。seek、输入流切换或释放会先取消并收拢 producer，
  再允许其他读取者接触 FFmpeg/GNU Radio stream。完成的 block 会在同一 worker 上限内，
  并行写入最终 RF span 中互不重叠的裁剪区间；串行与有状态 block 路径仍按顺序组装。
- VSync 包络/极小值计算与谐波功率比搜索会在同一个只读 padded 输入上并发执行；
  两个分支完成后，候选仲裁和 detector 状态更新仍按顺序进行。兼容 NumPy 的 float64
  median 对小输入保留完整排序，从 32K 样本起使用位精确 introselect。
- 启用 worker 时，VHS 场解码会并发执行亮度 TBC 渲染与色度场解码。任何时刻最多只有
  一个色度任务在运行，并且会在推进下一场前回到调用线程按顺序提交状态。
- 较长的 TBC sinc 重采样任务共享 worker 配额并保持输出顺序；
  `--threads 0` 和 `--threads 1` 保留确定性的串行路径。
- 线性 wow 调整每行只计算一次恒定导数，在 median/MAD 修复后再展开；启用 worker 时，
  源位置与电平准备仅以固定两路并发执行。
- VHS heterodyne 与 carrier 表使用有界并行构造和 session 自有的单条目缓存。key 完全
  一致时复用原数组；样本形状、载波、相位或 AFC 变化时替换旧条目，不会累积保留状态。
  相位分析直接只读场自有的重采样数组，解码仅在进入预滤波时创建唯一的可写副本。
- HiFi 使用有界并行 block 解码，之后按顺序进行后处理和写入。
- 托管 real FFT 复用池化的 packing 与 scratch 缓冲区。float32 SOS 前后向滤波租用
  一块扩展缓冲并原地运行，调用结束时同步归还；返回的输出数组仍保持正常所有权。
- double 精度 BA IIR 前后向滤波也会在一块 padded workspace 上原地运行。其私有池只在
  4M 样本以内、每个 bucket 最多保留三块数组，调用结束时同步归还；每个结果仍是独立
  拥有、精确长度的数组。
- RF span 直接写入请求的最终输出窗口，不再先分配整块边界场数组再做第二次切片复制。
- 默认 linear TBC 重采样会租用每场的 source-position 与 level-adjust 工作区，按精确 span
  使用，并在每次同步串行或并行重采样结束后归还两者。
- VHS diff-demod 尖峰修复复用现有 16 槽 real-FFT 工作区池中的一块全长复数暂存数组；
  返回的 analytic 数组仍保持独立所有权，非 VHS 路径保留原有的分配回退。
- 在小端主机上，TBC 与 chroma 样本直接从 `ushort` span 写入，不再分配整场 byte 副本；
  大端回退路径使用一块会归还的池化缓冲，因此重复写入的内存占用仍然有界。
- 真实的多 worker VHS session 使用容量为一的专用 payload writer。它会并发写入亮度与
  色度，同时让 producer 解码下一场，并独占 payload、metadata snapshot 与完成顺序。
  关闭时会 drain 队列；串行路径和公开自定义 reader 仍保留同步有序写入。
- 标准 VHS 场解码最多复用两套精确长度的 RF span 缓冲，对应固定读取窗口只可能覆盖的
  两种 block 数。同步场解码结束后立即归还缓冲；公开 `Read` 结果、CVBS 延迟渲染和
  LD VITS 保留源仍拥有独立数组。
- VHS 会在最后一个 block 内消费者完成后丢弃原始输入、原始解调、analytic 和 RF 高通
  结果。紧凑 real-FFT block 会把分离的实部/虚部 workspace 直接送入 FM unwrap，从而省去
  未使用的 RF 高通逆 FFT、三条 RF-span 复制和一个全长 `Complex[]`；LD、CVBS 与直接构造
  的 decoder 仍保留完整通道行为。
- 紧凑 VHS stream block 还会把已经量化的 SOS 色度保持为 `float[]`。RF span 组装时仅用
  AVX 或精确标量回退扩宽一次并写入复用的场缓冲；完整/直接 block 仍保留公开的
  `double[] Chroma` 契约。
- AVX/FMA 内核加速精确 float32 转换、VHS RF envelope 准备、VHS Rust 风格
  FM 角度近似、LD 量化、VHS 色度移位和复数频域滤波。forward/inverse radix-4 FFT
  使用固定指针索引。16-tap TBC sinc 内部窗口以 AVX/FMA 并行计算独立的 float 权重与
  乘积，再按原 tap 顺序累加；边缘、短输入和不支持的硬件保留标量路径。差分测试保证
  变换位模式与输出 hash 不变。
- 恢复元数据以磁盘流式写入，snapshot 队列容量为 1，场顺序历史和 RF 缓存均有
  硬上限；长时间解码不会保留所有已解码场，也不会无限排入未来工作。
- CUDA/OpenCL 不是运行时依赖。当前 trace 不支持把孤立的 32K FFT 在主机与设备间
  往返；未来可选 GPU 后端必须批量处理常驻显存的 DSP 阶段，并保留精确 CPU 回退。

当前线程矩阵使用 Intel Core Ultra 7 265K（20 个逻辑处理器）、Windows 11 build
26220、.NET SDK/runtime `11.0.100-preview.6.26359.118`、本项目检查点 `a45d433`，以及
Python v0.4.0 commit `43155200da87c0d49eb37d8ec09b1372075ee8e4`（程序报告为
`g4315520`）。隔离的 Python 环境使用 NumPy 2.4.6、SciPy 1.18.0、Numba 0.66.0 和
python-soxr 1.1.0。每项都是三次交错 Release 运行的中位数：

| CLI 模式 | 实际 worker | 本项目 | Python | 加速倍数 | 墙钟降幅 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 默认 | 5 | 4.646 s | 13.112 s | 2.82x | 64.6% |
| `--threads 1` | 1 | 9.203 s | 14.111 s | 1.53x | 34.8% |
| `--threads 5` | 5 | 4.544 s | 12.799 s | 2.82x | 64.5% |
| `--threads 10` | 10 | 4.074 s | 13.560 s | 3.33x | 70.0% |
| `--threads 20` | 20 | 3.779 s | 14.046 s | 3.72x | 73.1% |

默认值最终保持为 **5 个 worker**，与 Release 4.0 CLI 语义一致；在这台 20 逻辑处理器
机器上，显式 20 worker 最快。矩阵使用 `RF-Sample_2026-07-19_23-58-20.lds`，
公共参数为 `--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 620000000 -l 40 --overwrite`，再附加表中线程选项。

本项目 15 次运行在所有 worker 数下都得到同一组亮度 TBC、色度 TBC 和 JSON hash。
另加的三次 Python `--threads 0` 控制组彼此完全一致，并精确匹配本项目全部运行。
上游 Python 的默认/非零线程模式不能作为可靠的逐字节基准：矩阵中的 15 次运行得到
两组亮度/色度配对，其中 12 次匹配串行基准，三次 `--threads 5` 得到另一组。因此线程
矩阵比较的是实测吞吐，严格兼容性以 Python `--threads 0` 为基准。

这份 40 帧夹具的兼容性基准是 Python v0.4.0 `g4315520` 的 `--threads 0` 输出：

| 基准产物 | SHA-256 |
| --- | --- |
| 亮度 TBC | `64C518A03B208F7CF950916BC01A997021CB0F76B3D6F131FBEE74E9035FD30C` |
| 色度 TBC | `70112719879FB64FA95DC8F3ED6E5FA335D4F8B62C50FC2AF3C26D2C2098F26F` |
| JSON | `C223671830D0105271F24172923B280A96C8D0D427567C49E9C0E562D38FA881` |

一次更长的精确输出检查使用 Intel Core Ultra 7 265K（20 个逻辑处理器）、
Windows 11 build 26220 和 .NET SDK/runtime
`11.0.100-preview.6.26359.118`：

| PAL VHS，1,000 帧 / 2,000 场 | 墙钟时间 | CPU 时间 | 工作集峰值 |
| --- | ---: | ---: | ---: |
| 本项目，Release（两次） | 218.00 / 218.63 s | 238.72 / 239.50 s | 829.6-838.2 MiB |
| Python v0.4.0（`g4315520`） | 417.37 s | 未采集 | 未采集 |

两次运行都使用 `RF-Sample_2026-07-19_09-12-03.lds`，参数为
`--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 281303040 --threads 0 -l 1000 --overwrite`。
本项目两次运行的墙钟速度均约为 Python 的 1.91 倍（墙钟降低 47.7-47.8%），三项
SHA-256 在 Python 与两次本项目运行之间都逐字节一致；两边的 `--threads 0` 都选择
确定性串行模式。

另一个独立的无 seek 启动检查点使用 `RF-Sample_2026-07-19_23-58-20.lds`，保持相同
PAL VHS 参数，并使用 `--threads 0 -l 1000`。Python 与本项目得到逐字节一致的亮度
SHA-256 `E6616B63BD7DD1DB6C093FC6D1DCA7D23AABEF34EFD52089338D992F2DDCD0CD`
和色度 SHA-256
`A292BD77A8EB3373B6C631CE4552F77B6D4E5AF2228A85F01C63EDBBBFB4C0EF`。
2,000 个场记录、135 次启动恢复步骤，以及 1,000 项文件帧序列（`22..1021`）也全部
一致。打包的 Python 基准写入 8 字符身份 `g43155200`，本项目使用 `g4315520`；对应
`gitCommit`/`version` 程序身份字符串是 JSON 中仅有的差异。该正确性运行期间还有另一
个解码进程并发执行，因此它的耗时不作为 benchmark 结果。

这些数字只对应特定夹具，不是通用 benchmark。下述 40-frame 调优 A/B 使用 .NET
SDK/runtime `11.0.100-preview.6.26359.118`、`--threads 20`、默认色度和默认重采样。
在可重复的 40-frame PAL probe 上，保存的连续流水线改造前基线中位数为 11.60 秒，
最新中位数为 4.228 秒，累计提升 63.6%。最新的精确内核检查点本身将配对的
墙钟/CPU/工作集峰值中位数从 4.434 秒/16.516 秒/1.314 GiB 降至
4.228 秒/15.328 秒/1.069 GiB（4.6%/7.2%/18.6%）。进程 CPU 时间除以墙钟时间约等于
3.63 个活跃核心，因此后续仍优先推进不破坏状态的场级并行。14 次运行的配对 TBC、
JSON 和 chroma SHA-256 全部一致。

此前的 40/160/320-frame 持续运行分别用时 7.65/26.58/52.51 秒，工作集峰值为
1.76/1.88/1.67 GiB，后半程中位数为 1.42/1.30/1.28 GiB。320 帧全部写完，
内存没有随解码长度增长。此前的分配优化还将 PAL LD 四场 probe 从 5.12 GiB
降至 1.96 GiB。

有界 VHS 场内重叠把一次 160-frame 运行从 20.13 秒降到 18.55 秒（7.8%）。
TBC、chroma 与 JSON SHA-256 完全一致；任务会在当前场内等待完成，因此内存不会随
解码长度增长。

小端 TBC writer 的零拷贝写入在同一份 160-frame 输出中消除了约 455 MB 的整场临时
byte-array payload。xUnit v3 分配 probe 在预热后写入 400,000 个样本时，线程本地分配量
低于 1 KiB。新的 160-frame 运行仍保持完全一致的 luma/chroma SHA-256；墙钟时间处于
多次运行的正常波动范围内。

<details>
<summary>内核与分配 benchmark 历史</summary>

固定指针 PAL 尺寸 TBC sinc 隔离 A/B 将每场中位数从 3.929 ms 降到 3.727 ms，
内核提升 5.1%，后续内部窗口路径再提升 1.6%。AVX/FMA 优化仍保留标量 clamp 和有序
double 累加。五组交错 PAL-field A/B 中，串行/20-worker 中位数从 21.588/5.579 ms
降到 18.741/5.330 ms（13.2%/4.5%）。五组 40-frame 完整路径的墙钟/CPU 中位数从
5.511/19.297 秒降到 5.478/17.922 秒（0.6%/7.1%）。两组反向顺序的 204-frame 配对
快 1.1-1.3%，内存保持有界；TBC、chroma、JSON 和隔离场 hash 均完全一致。

session 自有的 VHS chroma 表缓存只保留一组精确 key 的 heterodyne 和一组 burst-carrier
表。相同的 40-frame GC trace 将总采样分配量从 13.854 GiB 降到 12.579 GiB，`Double[]`
从 12,611.83 MiB 降到 11,311.73 MiB，Gen2 从 38 次降到 31 次。五组交错 A/B 的墙钟/CPU
中位数从 5.49/19.23 秒降到 5.30/18.05 秒（3.5%/6.1%）。两组反向顺序的 204-frame
配对分别快 4.4% 和 4.8%；内存非单调且峰值不超过 2.0 GiB，409 场和全部输出 hash
均完全一致。继续移除剩余两次只读整场拷贝后，匹配 trace 的总采样分配量/`Double[]`
又从 12.580 GiB/11,309.71 MiB 降到 12.147 GiB/10,871.59 MiB。五组交错运行的墙钟/CPU
中位数从 5.209/18.188 秒降到 5.175/17.094 秒（0.7%/6.0%）；两组反向顺序的
204-frame 配对快 1.8% 和 1.9%，内存非单调、峰值不超过 2.05 GiB，`--length 204` 的
408 场输出精确一致。

并行 RF span 组装只读取已经完成的不可变 block，并写入最终窗口中互不重叠的区间；模拟
音频相位处理仍保持有序。五组交错 40-frame 运行的墙钟中位数从 5.165 秒降到 4.878 秒
（5.6%），CPU 时间从 18.172 秒升到 18.875 秒（3.9%），把更多核心占用转化为吞吐。
两组反向顺序的 `--length 204` 配对分别从 21.31 降到 20.35 秒、从 21.84 降到
20.18 秒（快 4.5% 和 7.6%）。当前内存非单调，峰值为 1.93/2.06 GiB；408 场和全部
hash 均精确一致。

并行 VHS payload 输出会重叠同一场的独立亮度与色度 stream 写入，并在下一场前等待两路
完成。五组交错 40-frame 运行的墙钟中位数从 4.98 秒降到 4.87 秒（快 2.2%）；CPU
中位数从 18.20 秒升到 19.50 秒，利用了原本空闲的容量。两组反向顺序的
`--length 204` 配对分别从 20.451 降到 20.181 秒、从 20.483 降到 20.353 秒
（快 1.3% 和 0.6%）。当前内存非单调，峰值为 2.03/2.06 GiB；408 场和全部 hash
均精确一致。

紧凑 VHS RF 通道路径会在缓存前释放原始输入、原始解调和 RF 高通 block 数组，跳过对应
的场组装，并且不执行未使用的 RF 高通逆 FFT。五组交错 40-frame A/B 的墙钟/CPU 中位数
从 6.01/18.86 秒降到 5.02/17.45 秒（快 16.5%/7.5%）。两组反向顺序的 204-frame
配对分别以 baseline/current 20.48/20.28 秒和 20.61/19.87 秒完成；CPU 时间为
79.88/68.91 秒和 77.17/72.44 秒。工作集峰值从 2.05-2.08 GiB 降到
1.58-1.67 GiB，四分段采样非单调；408 场以及亮度、色度、JSON hash 均精确一致。

后续紧凑 analytic 优化会把池化的实部和虚部数组直接送入 VHS FM unwrap，每次用 SIMD
归一化四个频差，并且只为完整直接 API 实体化 `Analytic`。五组交错 40-frame 配对的墙钟
在 5.02/5.03 秒范围内持平；CPU 中位数从 17.73 秒降到 17.28 秒，工作集峰值中位数从
1.47 GiB 降到 1.26 GiB。两组反向顺序的 204-frame 配对仍处于墙钟噪声范围；当前峰值为
1.32-1.41 GiB，四分段采样非单调，三份 hash 全部精确一致。

后续紧凑色度优化会把 float32 SOS 输出保持到 RF 场组装阶段。配对的 10-frame 分配 trace
中，采样托管分配从 2.95 GiB 降到 2.89 GiB，`Double[]` 从 2.75 GiB 降到 2.60 GiB，
`Single[]` 从 0.03 GiB 增到 0.11 GiB。五组交错 40-frame 配对的墙钟/CPU 中位数从
4.831/16.50 秒降到 4.769/15.75 秒（快 1.3%/4.5%）。两组反向顺序的 204-frame 配对
在 baseline/current 19.73/19.83 秒和 19.87/19.73 秒范围内墙钟持平；当前峰值为
1.46/1.39 GiB，仍处于既有的有界工作集范围内；亮度、色度与 JSON hash 全部精确一致。

后续有界 payload-writer 优化通过容量为一的队列，让下一 VHS 场解码与当前场的亮度/色度
写入重叠。payload 始终先于对应的 recovery JSON snapshot，完成阶段会 drain writer，
worker 异常也会返回解码线程。五组交错 40-frame 配对的墙钟/CPU 中位数从 4.90/16.09 秒
降到 4.79/15.47 秒（快 2.2%/3.9%）。两组反向顺序的 204-frame 配对分别以
baseline/current 20.23/19.54 秒和 20.05/19.19 秒完成（快 3.4%/4.3%）。当前四分段峰值为
1.35/0.74/0.96/1.14 和 1.27/0.95/0.97/1.09 GiB，没有单调增长；408 场及亮度、色度、
JSON hash 均精确一致。

原生采样率 `.s16` 输入现在只在声明频率严格为 40.0 MHz 时绕过 FFmpeg。新的 trace 在
前 300 个 inclusive 方法中没有出现 FFmpeg 透传或输入泵。五组交错 40-frame 配对将
墙钟/CPU 中位数从 5.33/17.11 秒降至 4.97/15.94 秒（6.8%/6.8%），工作集峰值中位数
从 1.23 GiB 降至 1.13 GiB。两组反向顺序的 204-frame 配对分别为 baseline/current
21.50/20.86 秒和 21.67/21.54 秒；候选峰值为 1.39/1.35 GiB，全部输出 hash 精确一致。

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

后续优化将 float32 SOS 的 padded workspace 改为池化复用。相同的 40-frame GC trace
中，总采样分配量从 16.772 GiB 降到 16.178 GiB，`Single[]` 分配量从 651.68 MiB 降到
47.25 MiB。五组交错完整路径 A/B 的墙钟中位数基本持平于 5.541/5.537 秒，CPU 时间
中位数从 20.000 秒降到 19.438 秒；三项输出 hash 均完全一致。当前受夹具长度限制的
204-frame 运行用时 23.39 秒，私有内存四分段中位数为 1.147/0.886/0.888/0.917 GiB，
峰值为 1.755 GiB。

下一步将默认 linear TBC resampler 的两块每场 double 工作区改为池化。相同的 40-frame
GC trace 中，总采样分配量从 16.178 GiB 降到 14.892 GiB，`Double[]` 分配量从
13.601 GiB 降到 12.316 GiB。五组交错 A/B 的墙钟中位数从 5.684 秒降到 5.571 秒
（2.0%），CPU 时间从 19.031 秒降到 18.891 秒；三项 hash 仍完全一致。重复的
204-frame 运行中，私有内存四分段中位数稳定在 1.025/1.047/1.007/1.042 GiB，
峰值为 1.869 GiB。

VHS diff-demod 修复现在把临时全长 `Complex[]` 保存在现有的有上限 FFT 工作区中。
相同的 10-frame GC trace 将总采样分配量从 4.134 GiB 降到 3.861 GiB，`Complex[]`
分配量从 622.63 MiB 降到 340.02 MiB。十组交错的 40-frame 配对和两组反向顺序的
204-frame 配对，其墙钟时间都处于运行波动范围内，因此不宣称加速；长跑内存保持有界，
全部 409-field hash 仍完全一致。

当前 double SOS 与 BA IIR 优化会融合常见的 2/4-section double cascade，并通过私有
有界池复用 BA 滤波的 padded workspace。隔离的 2/4-section SOS 中位数分别提升
37.5%/58.9%。在 32K 样本的 4/9/20 阶高通中，当前 IIR 路径比旧分配式参考实现快
23.7%/30.3%/26.6%，预热后的线程分配量从约 1.05 MB 降至 262 KB。七组交错的
40-frame 完整路径配对得到上方 4.6% 墙钟、7.2% CPU 与 18.6% 工作集峰值改善。
一次受夹具长度限制的 409-field 运行用时 17.431 秒；25-50%、50-75% 和 75-100%
输出区间分别为 4.06/4.02/4.27 秒，后半程工作集/私有内存中位数只比前半程高
10.8/7.4 MiB。记录的亮度、色度和 JSON hash 均保持精确一致。

packed `.lds` loader 现在会把解码样本直接写入请求的输出，并保留 Python 的末尾部分组
行为。五组交错的 40-frame 真实采集配对中，默认模式的墙钟/CPU 中位数从
4.687/12.422 秒降至 4.610/12.188 秒，20-worker 模式从 3.813/14.469 秒降至
3.743/13.109 秒。三组 160-frame 默认模式配对的墙钟从 15.281 秒降至 14.993 秒；
另一次五组 20-worker 配对将墙钟/CPU 中位数从 12.655/46.297 秒降至
12.601/46.156 秒，工作集峰值从 1.319 GiB 降至 1.198 GiB。42 次真实采集运行在各自
夹具上都只产生一组完全一致的亮度、色度和 JSON hash。

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
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

当前正式 Release 构建为零警告、零错误。xUnit v3 项目向
`dotnet test` 和 Visual Studio Test Explorer 暴露 **808** 个可独立发现的测试。

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
