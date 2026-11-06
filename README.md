# 离线翻译工具（WPF · 中 ⇄ 英）

一个完全本地运行的 WPF 翻译工具：中文 ↔ 英文互译，推理过程不联网，文本不会离开本机。

## 功能

- 中 → 英、英 → 中，支持「自动识别」方向（按是否含汉字判断）
- 自动翻译（停止输入后触发，勾选状态随设置持久化）、Ctrl+Enter 手动翻译、译文一键复制
- 手动清空 + 可选的自动清空：翻译完成后若内容在设定时间内未变化则自动清空（5 秒 / 10 秒 / 30 秒 / 1 分钟 / 5 分钟可选）
- 长文本按句子自动分段；中英混排时已是译文的片段原样保留
- 状态栏显示翻译耗时、当前方向与模型状态
- 模型缺失时可在应用内一键下载（优先 HuggingFace，失败自动切换 hf-mirror 镜像）
- **迷你模式**：置顶无边框小窗，上方输入、下方自动展开译文，支持拖动与方向切换
- **系统托盘**：常驻托盘图标，单击/双击恢复上次使用的窗口（迷你模式下恢复迷你窗口），右键可进入迷你模式或退出
- **ASCII 海洋背景**（可选）：移植自 [ascii-sharks](https://github.com/maxhumber/ascii-sharks) 的鲨鱼/鱼群生态模拟——boids 鱼群被鲨鱼捕食、散开、重新聚拢，以字符网格逐帧绘制为窗口背景
- **设置**（标题栏 ⚙）：开机自动启动（HKCU Run 项，无需管理员权限）、最小化到系统托盘、自动清空、海洋背景（偏好持久化到 `%APPDATA%\OfflineTranslator\settings.json`）

## 技术方案

| 环节 | 实现 |
| --- | --- |
| 模型 | MarianMT（`Helsinki-NLP/opus-mt-en-zh`、`opus-mt-zh-en`）：encoder int8 量化 + decoder fp16 |
| 推理 | `Microsoft.ML.OnnxRuntime`（CPU；beam search 解码，beam=4，含 no-repeat-ngram 与重复惩罚） |
| 分词 | `Tokenizers.DotNet`（加载模型自带的 `tokenizer.json`） |
| 界面 | WPF（.NET 9） |

环境要求：Windows x64 + .NET 9 运行时（或 .NET 9 SDK）。

## 运行

```powershell
dotnet run --project src/TranslationTool/TranslationTool.csproj
```

仓库根目录的 `models/` 已包含两个方向的离线模型（共约 350 MB），构建时会自动复制到输出目录，因此**开箱即可离线使用**。

模型目录结构（缺失时应用会提示并可自动下载，下载源为 HuggingFace / hf-mirror）：

```
models/
  en-zh/{tokenizer.json, generation_config.json, encoder_model.onnx, decoder_model.onnx}
  zh-en/{...}
```

## 目录结构

```
TranslationTool.slnx
models/                              离线模型文件
src/TranslationTool/
  App.xaml(.cs)                      共享 TranslationService、设置与退出清理
  MainWindow.xaml(.cs)               主界面、托盘、设置、模式切换
  MiniWindow.xaml(.cs)               迷你模式窗口（置顶小窗、自动翻译、译文展开）
  Services/MarianTranslator.cs       ONNX 翻译引擎（beam search 解码、分段、混排处理）
  Services/TranslationService.cs     引擎加载、方向识别、推理串行化
  Services/ModelRepository.cs        模型文件检测与下载（含镜像回退）
  Services/AppSettings.cs            用户偏好持久化
  Services/AutoStartManager.cs       开机自启注册表读写
  Services/TrayIconManager.cs        托盘图标与菜单（图标运行时绘制）
  Services/Ocean/OceanWorld.cs       海洋生态模拟（boids 鱼群 + 骨骼链鲨鱼 + 捕食循环）
  Services/Ocean/OceanRenderer.cs    ASCII 网格渲染器（WriteableBitmap 像素绘制、字形遮罩）
  Services/Ocean/OceanEngine.cs      动画驱动（固定步长模拟、30fps 限帧、失焦暂停）
  Services/Ocean/OceanSpecies.cs     小鱼物种参数
  Services/Ocean/OceanMath.cs        数学工具（哈希、洋流、角度差）
```

## 说明

- 单次送入模型的源文本上限为 480 token，超长文本会按句子分段后逐段翻译。
- 采用 beam search（beam=4）+ no-repeat-ngram + 重复惩罚 1.3；opus-mt 在问候语等句式上极易陷入重复循环，这些约束配合 fp16 解码器可显著改善质量。短句翻译约 0.5~2 秒（CPU）。
- 解码器选用 fp16 而非 int8 动态量化：实测部分句子的生成分布在 int8 下明显劣化（fp16 与 fp32 行为一致）。
- 模型自带的 `tokenizer.json` 里 SentencePiece 的 `precompiled_charsmap` 为 `null`，新版 tokenizers 会 panic，程序首次加载时会自动生成 `tokenizer.patched.json`（把归一化器替换为 NFKC）后再加载。
- 海洋背景移植了原版的核心机制：Reynolds boids（分离/对齐/聚合 + 背后盲区 + 惊慌传染）、鲨鱼游荡/追击/分离转向、跟随者脊柱链、饥饿与进食、被吃的鱼群重新游回；渲染为 ASCII 字符网格（超采样抗锯齿边缘、明暗肤色、随机点缀色）。模拟按 1/60s 固定步长推进，画面 30fps，窗口隐藏或最小化时自动暂停；开启后单核 CPU 占用约 15%~20%。
