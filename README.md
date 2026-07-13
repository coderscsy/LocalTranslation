# Local Translator

Windows 中 / 英 / 日翻译与视频实时字幕工具，使用 C#、.NET 8 和 WPF 开发。目标是提供一个可本地运行、可接入自有模型、适合日常刷视频 / 看直播 / 游戏字幕的桌面翻译软件。

## 项目边界

- 重点：离线 OCR、文本翻译、截图翻译、系统声音实时字幕、OpenAI-compatible 翻译 Provider。
- 不内置云端密钥，不绑定固定模型品牌，不要求用户必须联网。
- 默认模型仅作为可用起点；翻译质量最终取决于用户选择的本地模型、局域网模型或在线 Provider。
- 视频字幕当前采集默认输出设备的系统混音，不承诺只锁定某一个播放器进程。

## 已实现

- 文本实时翻译、自动语种检测和源语言错配拦截
- PP-OCRv5 离线中 / 英 / 日截图识别与截图翻译
- 用户注册自己的 GGUF 模型：名称、文件路径、上下文、最大输出和系统提示词均可编辑
- 默认提供 TranslateGemma 4B Q4_K_M 专用翻译模型，可一键下载安装、校验和卸载
- 本地模型设为当前、编辑和移除配置；外部模型文件不会被软件删除
- 本地 / 局域网 OpenAI-compatible 服务与 OpenAI、DeepSeek、Gemini、OpenRouter、自定义 Provider
- Provider 连接测试；API Key 使用 Windows DPAPI 按当前用户加密
- OpenAI-compatible 地址可粘贴 `/v1`、`/v1/models` 或 `/v1/chat/completions`；软件自动规范化，测试后加载模型下拉列表；无鉴权服务允许 API Key 留空
- 可编辑模型下拉框会保存真实 `SelectedItem`；Qwen 翻译请求自动关闭 thinking，并使用不可执行的源文本边界，问题、命令和对话只翻译、不回答
- 系统输出声音采集、ASR 引擎可切换、近实时屏幕字幕
- 默认集成 Meetily 的 Parakeet TDT V3 INT8 解码链：英语和欧洲语言直接在 WPF 进程内 ONNX 推理，不需要 Python 或独立 ASR 服务；模型可一键下载、取消和卸载
- Parakeet 英文字幕使用累计修订：先快速显示预览，后续音频持续修正同一行，避免把 `academic accomplishments in the field of finance` 拆散、漏掉或重复翻译
- SenseVoice Small 保留为中文、日语及中英混说引擎：通过本地 / 局域网 FunASR OpenAI-compatible ASR 服务 `/v1/audio/transcriptions` 转写
- 语言感知路由：明确选择 English 时保留 Parakeet；选择自动检测、中文或日语且当前误选 Parakeet 时，会优先改用已启用的 SenseVoice，无法使用时再选择 Whisper
- 保留 Whisper GGML fallback；内置 Whisper Small Q5_1 中文均衡模型下载项：一键安装、进度显示、取消下载、完整性校验和卸载
- 简体中文输出经过 OpenCC 词级繁转简归一化，避免 Whisper 的繁体结果直接显示为“简体”
- 实时字幕 / 电影模式、双语字幕、时间轴记录与 UTF-8 SRT 导出
- 悬浮字幕采用紧凑桌面歌词条，可拖动、右下角缩放；右键菜单可放大/缩小、切换穿透、重置位置、显示到任务栏或关闭字幕，字号、坐标与尺寸自动保存
- 可关闭“鼠标拖动/缩放”进入锁定穿透状态，也可随时按 `Ctrl+Shift+F8` 在可拖动与鼠标穿透间切换
- 启动视频字幕后主界面自动收进系统托盘，双击托盘图标恢复
- 视频字幕运行时关闭设置控制台只会隐藏窗口，同传和悬浮字幕继续运行
- 主窗口右上角关闭和托盘“退出”都会先停止视频字幕服务，再销毁托盘图标并完整退出，不会留下无入口的后台进程
- 视频字幕可独立选择 OpenAI-compatible 服务与任意文本生成模型；“选择翻译模型”只读取并展开完整模型列表，不发送测试翻译，也不会强制改回预设模型
- 单个视频模型会话支持 1–4 路并发翻译请求（默认 3 路），不重复加载模型；旧句较晚返回时不会覆盖屏幕上的新字幕
- 默认采用“通用视频/直播”字幕协议，也可切换“游戏/电竞”场景优化角色名、道具、技能、地图、武器和游戏黑话；两种场景都只输出当前合并后的字幕句段译文
- ASR 连续短片段会先进入语义缓冲，遇到停顿、时间或长度阈值后再提交给翻译模型，减少 `of finance` 这类碎片被单独误译
- 纯中文→简体中文等同语言字幕直接上屏，不显示无意义的“正在翻译”；中英混合文本会保留已有中文并只翻译、整理其中的英文内容
- 翻译前过滤 `(字幕:xxx)`、`[Subtitles by xxx]` 等静音/片尾署名幻觉，避免垃圾 ASR 文本进入模型

软件不内置或宣称某个通用问答模型是“翻译模型”。翻译质量由用户选择的模型或 Provider 决定；系统提示词要求只输出译文，不回答原文。

## 构建运行

要求 Windows 10/11、.NET 8 Desktop Runtime。Visual Studio 中请将 `LocalTranslator.App` 设为启动项目；Core 和 Infrastructure 是类库，不能直接运行。

```powershell
dotnet restore LocalTranslator.sln
dotnet build LocalTranslator.sln -c Debug
dotnet run --project src/LocalTranslator.App/LocalTranslator.App.csproj
```

## 本地数据

- OCR 模型：仓库开发目录 `Models/ocr`，构建时复制到输出目录
- 用户模型注册表：`%LOCALAPPDATA%/LocalTranslator/local-models.json`
- Provider 凭据：`%LOCALAPPDATA%/LocalTranslator/translation-providers.dat`
- 视频字幕设置：`%LOCALAPPDATA%/LocalTranslator/video-subtitle-settings.json`
- 软件托管的 Whisper fallback 字幕模型：`%LOCALAPPDATA%/LocalTranslator/Models/speech/whisper-small-q5_1/ggml-small-q5_1.bin`
- 软件托管的 Meetily Parakeet 模型：`%LOCALAPPDATA%/LocalTranslator/Models/speech/parakeet-tdt-0.6b-v3-int8/`
- 日志：`%LOCALAPPDATA%/LocalTranslator/logs`

自己的 GGUF 和 Whisper GGML 文件可放在任意目录，由用户在界面中选择。软件只保存路径，不擅自删除外部文件。通过软件下载到托管目录的模型会显示“卸载并删除文件”或“卸载默认模型”按钮。

## 视频字幕说明

当前版本通过 WASAPI Loopback 按声卡真实混音格式采集默认输出设备的全部系统声音，再明确混合为单声道并重采样为 16 kHz PCM。识别使用低延时静音触发切片、滚动音频重叠和语义缓冲，因此是“分段近实时”，并非零延迟。音频队列使用有界背压，处理跟不上时不会静默丢弃原始语音；静音片段和低置信度幻觉会被过滤。当前 Windows 采集链不承诺只锁定某一个播放器进程。

视频字幕页默认选择 Meetily Parakeet TDT 0.6B V3 INT8。它支持英语及 24 种欧洲语言，约 640 MiB，直接使用 ONNX Runtime CPU 推理；停止翻译后会释放三个推理 Session 和模型内存。Parakeet 不支持中文或日语语音，这两类视频请切换 SenseVoice Small。本机 SenseVoice 模式可在页面内一键安装 Python 3.10～3.12、PyTorch、TorchAudio、FunASR 与 API 服务依赖，并通过同一个按钮启动/停止 `http://127.0.0.1:8899/v1`。也可以填写局域网 FunASR/OpenAI-compatible ASR 地址。Whisper GGML 继续作为无需服务的多语言 fallback；软件提供 Whisper Small Q5_1 中文均衡模型（约 181 MiB），也允许选择自己的 GGML 文件。

模型、ASR 缓存、日志、本地配置和应用专用 Python/ASR 依赖可通过本机 `appsettings.local.json` 的 `DataRoot` 指向其他磁盘，例如 `G:\LocalTranslationData`。一键安装会在 `DataRoot\Runtime\Python` 创建托管虚拟环境，不再把 FunASR/PyTorch 安装到系统 Python。ASR 启动命令允许高级用户修改，同时提供“恢复默认”按钮；空命令会自动恢复，管道、重定向、多命令和任意 Python 脚本会被拦截，避免误删配置或执行危险命令。

电影模式会记录字幕时间轴；停止后可导出单语或双语 SRT。ASR 引擎负责语音转文字，原文识别后立即显示，连续短片段会先合并为更完整的字幕句段；翻译在独立队列中完成并以蹦字动画替换“正在翻译…”占位，避免慢速在线模型阻塞下一段语音识别。上下文管理器以线程安全方式只保存最近三句完整原文。最终翻译仍使用用户当前选择的翻译 Provider。

架构见 [docs/architecture.md](docs/architecture.md)。

Meetily 解码代码与 NVIDIA Parakeet 模型的许可证归属见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 开源协议

本项目采用 [MIT License](LICENSE) 开源。
