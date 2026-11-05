# 离线翻译工具（WPF · 中 ⇄ 英）

一个完全本地运行的 WPF 翻译工具：中文 ↔ 英文互译，推理过程不联网，文本不会离开本机。

## 功能

- 中 → 英、英 → 中，支持「自动识别」方向（按是否含汉字判断）
- 自动翻译（停止输入 700ms 后触发）、Ctrl+Enter 手动翻译、译文一键复制
- 长文本按句子自动分段；中英混排时已是译文的片段原样保留
- 状态栏显示翻译耗时、当前方向与模型状态
- 模型缺失时可在应用内一键下载（优先 HuggingFace，失败自动切换 hf-mirror 镜像）
- **迷你模式**：置顶无边框小窗，上方输入、下方自动展开译文，支持拖动与方向切换
- **系统托盘**：常驻托盘图标，双击恢复主窗口，右键可进入迷你模式或退出
- **设置**（标题栏 ⚙）：开机自动启动（HKCU Run 项，无需管理员权限）、最小化到系统托盘（偏好持久化到 `%APPDATA%\OfflineTranslator\settings.json`）

## 技术方案

| 环节 | 实现 |
| --- | --- |
| 模型 | MarianMT（`Helsinki-NLP/opus-mt-en-zh`、`opus-mt-zh-en`）的 int8 量化 ONNX 版本 |
| 推理 | `Microsoft.ML.OnnxRuntime`（CPU；encoder 一次前向 + decoder 贪心自回归解码） |
| 分词 | `Tokenizers.DotNet`（加载模型自带的 `tokenizer.json`） |
| 界面 | WPF（.NET 9） |

环境要求：Windows x64 + .NET 9 运行时（或 .NET 9 SDK）。

## 运行

```powershell
dotnet run --project src/TranslationTool/TranslationTool.csproj
```

仓库根目录的 `models/` 已包含两个方向的离线模型（共约 190 MB），构建时会自动复制到输出目录，因此**开箱即可离线使用**。

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
  Services/MarianTranslator.cs       ONNX 翻译引擎（编码、贪心解码、分段、混排处理）
  Services/TranslationService.cs     引擎加载、方向识别、推理串行化
  Services/ModelRepository.cs        模型文件检测与下载（含镜像回退）
  Services/AppSettings.cs            用户偏好持久化
  Services/AutoStartManager.cs       开机自启注册表读写
  Services/TrayIconManager.cs        托盘图标与菜单（图标运行时绘制）
```

## 说明

- 单次送入模型的源文本上限为 480 token，超长文本会按句子分段后逐段翻译。
- 采用贪心解码（非 beam search），速度优先；短句翻译约 0.5~1.5 秒（CPU）。
- 模型自带的 `tokenizer.json` 里 SentencePiece 的 `precompiled_charsmap` 为 `null`，新版 tokenizers 会 panic，程序首次加载时会自动生成 `tokenizer.patched.json`（把归一化器替换为 NFKC）后再加载。
