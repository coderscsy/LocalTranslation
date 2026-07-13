# 架构与执行状态

## 数据流

```text
文本输入 ───────────────────────────────┐
截图 -> PP-OCRv5 -> 原文 ───────────────┼-> 自动语种检测 -> Provider Router -> 译文
系统声音 -> ASR 引擎 -> 分段对话文本 ───┘                              ├-> 顶层实时字幕
                                                                      └-> 电影时间轴 / SRT

Provider Router
├── 用户自己的 GGUF（LLamaSharp）
├── 本地 / 局域网 OpenAI-compatible 服务
└── 用户配置的在线 OpenAI-compatible Provider
```

## 分层

- `LocalTranslator.Core`：语言、自动检测、翻译及字幕数据模型、抽象接口。
- `LocalTranslator.Infrastructure`：OCR、屏幕与音频采集、Meetily Parakeet ONNX、SenseVoice/FunASR ASR API、Whisper fallback、LLamaSharp、Provider、安全配置和 SRT。
- `LocalTranslator.App`：WPF 主界面、模型编辑器、Provider 设置、视频字幕控制台和字幕叠加窗。

## 关键约束

- 不写死本地模型品牌、路径或提示词；用户可完整编辑。
- 外部 GGUF / Whisper 文件只“移除配置”或取消选择，不删除用户文件；只有软件托管下载的文件才允许卸载。
- 源语言默认自动检测；明显选错语言时阻止请求，避免模型把输入当成问答。
- 在线 Provider 只有用户显式选择后才发送文本，API Key 使用 DPAPI 加密。
- OpenAI-compatible Provider 会规范化端点后缀，通过 `/models` 自动发现并过滤 embedding/rerank 模型；API Key 是可选字段，由服务端决定是否需要鉴权。
- 在线翻译统一使用 translation-only system prompt 和 `<source_text>` 边界，温度为 0；Qwen Provider 额外发送 `reasoning_effort=none` 与 `enable_thinking=false`，避免推理 Token 挤占字幕译文。
- Chat Completions 请求先完整序列化为 UTF-8 `StringContent` 并携带明确的 `Content-Length`，兼容不接受流式 `JsonContent` 的自定义桥接服务。
- 默认离线翻译模型是软件托管的 TranslateGemma 4B Q4_K_M，注册表与模型文件分离，模型文件可卸载后重新安装。
- 视频字幕使用系统输出设备的全部声音，分段识别会产生数秒延迟。
- ASR 引擎可切换：默认使用由 Meetily 解码流程适配而来的 Parakeet TDT V3 INT8 进程内 ONNX 引擎识别英语/欧洲语言；SenseVoice 处理中文、日语和混说场景；Whisper GGML 作为内置 fallback。
- Parakeet 对同一段发言采用累计修订：1.8 秒先发布预览，之后每新增约 1.35 秒音频便用当前整段音频重新识别，并覆盖同一字幕行；早期误听和跨切片词组会被后续上下文纠正，而不是固化成碎片。
- Meetily Parakeet 模型由 `SpeechModelManager` 下载到数据盘，逐文件校验长度；取消或失败会清理 `.download` 临时文件。`VideoSubtitleService.StopAsync` 会 Dispose 三个 ONNX Session，避免停止后继续持有模型。
- Whisper fallback 模型由 `SpeechModelManager` 下载到应用数据目录，并在安装前校验文件长度与 SHA-256；失败或取消时清理临时文件。
- 实时字幕按 WASAPI 设备真实混音格式采集，经立体声转单声道和 WDL 重采样后统一为 16 kHz PCM；不同 ASR 使用独立切片阈值和滚动重叠，静音/低置信度结果会被过滤。音频通道容量为 32 并使用背压等待，不再通过 `DropOldest` 静默漏掉语音。
- ASR 输出先进入语义缓冲：连续短片段即时更新原文，但只有遇到停顿、时间或长度阈值才提交给翻译模型，避免碎片句被断章误译。
- 语音识别与 Provider 翻译采用独立有界队列：先显示识别原文，再异步补齐译文，慢速远程模型不会阻塞后续 Whisper 分段。
- 视频翻译 Provider/模型独立于普通文字翻译配置；模型发现优先选择 `gemma-4-26b-a4b-it-mlx`，再回退普通 `gemma-4-26b-a4b-it`。`TranslationWindowManager` 以锁保护当前 ASR 流和最近三句完整原文，默认使用通用视频/直播协议，并提供游戏/电竞场景提示协议。
- ASR 结果进入语言检测和翻译队列前会过滤完整括号包裹的字幕署名、caption/subtitle credit 和网址型幻觉。
- 视频译文上屏前验证目标文字脚本；回显原文或返回错误语言时自动执行一次严格重试，第二次仍不合格则阻止该句上屏。
- 翻译通道允许 1–4 个并发消费者共享同一个 `OpenAiCompatibleTranslationService` 与已加载模型，默认并发度为 3；列表按原时间轴位置替换，悬浮层忽略晚于当前画面的旧句回包。
- 电影式悬浮字幕支持半透明双行排版、译文蹦字、无边框拖动、右下角 Thumb 缩放，以及通过 Win32 `SetWindowLong`/`WS_EX_TRANSPARENT` 实现的穿透；全局 `Ctrl+Shift+F8` 可随时切换交互模式。字号、坐标与宽高保存在本地视频设置文件。字幕开始后主窗口与控制台隐藏到系统托盘。
- 所有简体中文目标输出在路由层和字幕层执行 OpenCC 繁转简归一化。

## 后续质量项

- 音频设备选择、Silero VAD、Parakeet 可选 DirectML/CUDA 后端与性能基准
- SenseVoice ONNX 直连 runtime、CPU/GPU 后端选择与更多 ASR 模型规格
- 专用翻译模型模板预设、术语表、长文本分段和质量基准
- 发布包、自包含构建、断网与多显示器验收
