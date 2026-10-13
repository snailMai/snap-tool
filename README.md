# Snap Tool

Windows x64 桌面区域截图工具。使用 C#、WPF 与 Windows 原生 API，支持矩形标注、本地离线中英文 OCR 和有道在线文本翻译。

源码仓库：[snailMai/snap-tool](https://github.com/snailMai/snap-tool) 。

## 使用

按下方“开发与构建”生成便携包后，运行 `dist/SnapTool/SnapTool.exe`，程序驻留系统托盘。

若旧版已经运行，先右键其托盘图标选择“退出”，再启动新版目录中的 `SnapTool.exe`；程序只允许一个实例。

1. 按 **Ctrl+F2**，拖动鼠标框选区域。
2. 松开鼠标后，拖动选区内部移动，拖动边缘或角点调整大小；在外部拖动重新框选。
3. 按 **Enter**、双击选区或点击“复制”，将图片放入剪贴板。
4. 按 **Ctrl+S** 或点击“保存”，选择 PNG 保存位置。
5. 按 **Shift+C** 或点击“识别文字”，识别调整后的选区。成功后复制文字并结束截图，在记事本或其他输入框按 **Ctrl+V** 查看结果。
6. 按 **Shift+T** 或点击“翻译”，先本地识别，再打开原文／译文对照窗口并请求有道翻译。默认译为简体中文，可选择英文；修改原文或目标语言后点击“翻译”重新请求。
7. 按 **Esc**、右键选区画面或点击“取消”，结束本次截图；识别过程中也可以取消。对照窗口中按 Esc 取消当前请求，空闲时按 Esc 关闭窗口。

Shift+C 仅在截图窗口内生效。OCR 在本机执行，不上传截图、不保存识别历史。识别过程中显示状态并禁用重复操作；无文字或失败时保留选区和原剪贴板，取消后不会输出迟到结果。结果保留识别出的换行，但不保证还原表格、多栏排版或代码缩进；小字、模糊文字可能识别不准。

## 矩形划框

1. 按 **Ctrl+F2** 框选截图后，点击工具栏的 **矩形** 或按 **R**。
2. 在选区内拖动画框，可连续绘制多个框；按住 **Shift** 拖动绘制正方形。默认红色、3 个物理像素宽的空心框。
3. 点击框线选中标注；拖动框线移动，拖动选中框的八个控制点缩放。框内空白处可以继续画框，支持嵌套。
4. 工具栏可选择红、黄、绿、蓝、白、黑，以及 2／3／5／8 px 粗细。选中框时立即修改该框，同时作为后续新框样式。
5. 按 **Delete** 或“删除框”删除选中框；按 **Ctrl+Z** 或“撤销”撤销最近一次新增、移动、缩放、样式修改或删除。
6. 按 **Enter**／“复制”复制带框图片，或 **Ctrl+S**／“保存”导出带框 PNG。绘制拖动过程中禁止输出；矩形模式双击不会误触复制。

点击 **调整选区** 或按 **V** 恢复截图选区的移动和缩放。标注保持在原图位置，超出选区的部分被裁剪；再次扩大选区可以重新看到。截图选区及其控制点不进入导出图片。**Shift+C OCR 和 Shift+T 翻译仍识别原图**，不叠加矩形标注。

标注只在本次截图内保留。按 Esc、右键画面或“取消”放弃整次截图。

## 配置有道翻译

1. 在[有道智云](https://ai.youdao.com/) 开通文本翻译服务，创建应用并绑定该服务，获取应用 ID 和应用密钥。
2. 右键 Snap Tool 托盘图标，选择“翻译设置…”，填写应用凭据并保存。对照窗口也有“翻译设置”入口。保存只保存凭据，不调用接口或验证余额。
3. 使用 Shift+T 翻译选区；未配置或接口失败时，对照窗口保留 OCR 原文，配置后可点击“翻译”重试。点击“复制原文”或“复制译文”才会修改剪贴板。

仅主动翻译时向固定接口 `https://openapi.youdao.com/api` 发送文字，不发送截图、不自动保存原文或译文。服务可能按用量收费；网络、账户权限、余额与限流都会影响请求。单次最多 5000 个 Unicode 字符，超出时提示缩小选区或手动分段；20 秒超时后不自动重试，也不切换其他服务。取消可以阻止本地结果更新，但无法撤回已经发出的请求或保证取消计费。接口依据见[有道官方文档](https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html) 。

配置位于当前用户的 `%LOCALAPPDATA%\SnapTool\translation.json`，应用 ID 与使用 Windows DPAPI 加密的密钥存于此文件。密钥不随便携包分发，换电脑或 Windows 用户后需重新填写；不必将密钥发送到聊天或写进源码。当前 OCR 重点支持中英文，翻译服务的更多语种支持不代表截图 OCR 已全部适配。

## 截图操作说明

工具栏显示在选区附近。尺寸标签使用实际像素，不是缩放后的界面单位。选区可以跨越显示器；显示器之间没有画面的空隙输出为黑色。

右键托盘图标可以截图、重新注册 Ctrl+F2 或退出；双击托盘图标也可截图。快捷键冲突时会提示，释放其他程序占用后可通过托盘重新注册。程序仅允许单实例。

截图期间保留一个任务栏入口，可以使用 Alt+Tab 切回截图；结束后入口关闭，程序回到托盘。

可以传入 `--capture` 在启动时直接进入截图。已经运行时，再次启动只提示使用现有实例。

取消截图不会写入图片或改变剪贴板。取消保存对话框返回选区；输出失败保留选区以便重试。复制完成后图片保留在 Windows 剪贴板，保存仅写入选择的文件位置。

## 开发与构建

需要 Windows x64 与 `global.json` 指定的 .NET 10 SDK。OCR 使用 RapidOcrNet 4.2.0、ONNX Runtime CPU、SkiaSharp 和 PP-OCRv5 中文模型；依赖版本记录于项目的 `packages.lock.json`。许可文件随应用放入 `licenses/`，模型来源和校验清单见 [models/README.md](models/README.md) 。

在项目根目录运行：

```powershell
# 没有匹配 SDK 时，可仅安装到项目 .tools 目录
powershell -ExecutionPolicy Bypass -File scripts/setup-sdk.ps1

# 首次克隆后下载固定版本 OCR 模型并校验 SHA256
powershell -ExecutionPolicy Bypass -File scripts/setup-ocr-models.ps1 -Download

# 编译并运行自动回归
powershell -ExecutionPolicy Bypass -File scripts/build.ps1

# 验证后生成自包含应用目录
powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Publish
```

仓库提供源码、测试、依赖锁文件、构建脚本及模型校验清单，不包含 SDK、模型缓存和运行包。构建脚本优先使用 `.tools/dotnet/dotnet.exe`，再使用 PATH 中的 SDK。首次构建需要从 NuGet 获取依赖和运行时包；模型通过上述脚本下载，构建前校验四个模型文件的 SHA256。交付目录为 `dist/SnapTool/`；运行时无需另装 .NET 或 OCR 软件，但须保留目录内的 DLL、`models/` 和 `licenses/`。模型加载路径以程序目录为准，不依赖启动时的工作目录。

自动回归使用合成像素和任务临时文件；不读取真实剪贴板、不捕获桌面。界面测试窗口入口：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-ui.ps1
# 需要在右侧显示器显示第二个测试窗口时
powershell -ExecutionPolicy Bypass -File scripts/test-ui.ps1 -Secondary
```

在测试窗口中按 Ctrl+F2 截取色块或文字，然后按 Ctrl+V 显示剪贴板图片和尺寸或识别文字；图片检查会将测试图片写入 `.tmp/ui-validation/clipboard.png`。仅用于本项目测试内容。

测试窗口用 Alt+F4 关闭。重新构建前先关闭测试窗口，重新打包前从托盘退出正在运行的交付程序，避免 Windows 文件锁阻止替换。

实际验收范围、未验证项目与后续事项见 [ROADMAP.md](ROADMAP.md) 。项目规范见 [AGENTS.md](AGENTS.md) 。
