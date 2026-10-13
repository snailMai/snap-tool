# OCR 模型

`v5/` 保存随便携包分发的 PP-OCRv5 检测、方向分类、中英文识别模型及配套字典，不提交到 Git。首次克隆后需运行下方下载命令。`manifest.json` 固定下载地址与 SHA256；构建前校验，运行时只读本地文件，不访问网络。

模型来源：[RapidAI 官方模型清单](https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml) 。三个 ONNX 的 SHA256 与该清单一致；字典来自相同版本的中文识别模型目录，其 SHA256 为本项目下载时计算并固定。模型许可证见 `../licenses/PaddleOCR-LICENSE.txt`。

缺失文件可运行 `powershell -ExecutionPolicy Bypass -File scripts/setup-ocr-models.ps1 -Download` 恢复；已有文件哈希不符时停止，不自动覆盖。
