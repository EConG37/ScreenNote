# 离线 OCR 组件与验证

0.7.0 默认使用 RapidOCR 的中文 PP-OCRv4 ONNX 模型。Python 运行环境、CPU 推理库和模型随软件提供；不会下载模型、访问在线识别服务或使用用户电脑的全局 Python 包。设置中的 Windows 识别方式保留为备用。

小图按比例增强；超过 1,400 像素高度的图片用 140 像素重叠分段，按原图坐标排序并去除接缝重复。识别在独立进程中运行，取消时终止整个进程并清理临时图片。大图超时按分段数增加，最长五分钟；不阻塞截图快捷键。截图最大处理范围为 4,000 万像素。

## 本机合成样本比较

日期：2026-10-03。同一张 PNG、相同字体及文字，忽略空白后使用字符编辑距离计算错误。样本由测试生成，不含用户截图。

| 样本 | 字符数 | 中文增强错误 | Windows 错误 | 中文增强耗时 | Windows 耗时 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 中文／英文／数字混排，19px | 92 | 0 | 6 | 3.77 秒 | 0.63 秒 |
| 小字，12px | 81 | 0 | 18 | 2.57 秒 | 0.55 秒 |
| 深色界面，16px | 86 | 0 | 5 | 2.99 秒 | 0.60 秒 |
| 6,000px 长图，64 行 | 2,241 | 0 | 82 | 33.74 秒 | 1.49 秒 |
| 总计 | 2,500 | 0 | 111 | — | — |

增强方式提升了这些样本的准确度，CPU 耗时较长。样本数量有限，无法据此宣称所有真实图片完全正确。低清、压缩、特殊字体、旋转文字及复杂表格仍可能出错，可保留原图并手动校对。完整报告位于 `.build/ocr-comparison-070/comparison.json`，测试入口为 `tests/ocr-comparison.csproj`。

## 组件和许可证

第三方原始许可证、版权说明及依赖元数据保留在分发包的 `ocr-runtime` 内。下表是索引，不替代原许可证。

| 组件 | 版本 | 许可证／项目 |
| --- | --- | --- |
| CPython 嵌入运行环境 | 3.12.10 Windows AMD64 | PSF，`ocr-runtime/LICENSE.txt`；[Python 官方下载](https://www.python.org/downloads/release/python-31210/) |
| RapidOCR ONNX Runtime | 1.4.4 | Apache-2.0；[RapidAI 项目](https://github.com/RapidAI/RapidOCR) |
| PP-OCRv4 中文模型 | 包内检测／识别／方向模型 | Apache-2.0；[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| ONNX Runtime | 1.20.1 | MIT；[微软项目](https://github.com/microsoft/onnxruntime) |
| OpenCV Python | 4.10.0.84 | Apache-2.0 及包内第三方许可；[官方包源码](https://github.com/opencv/opencv-python) |
| NumPy | 1.26.4 | BSD-3-Clause 及包内第三方许可；[NumPy](https://numpy.org/) |

其他依赖为 Pillow、PyYAML、Shapely、pyclipper、protobuf、sympy、mpmath、flatbuffers、packaging、coloredlogs、humanfriendly、colorama、pyreadline3、six、tqdm；原许可在对应 `*.dist-info` 文件夹及各包的许可文件中。

运行环境来源为 Python 官方 `python-3.12.10-embed-amd64.zip`，SHA-256 为 `4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3`。核心依赖固定为 `rapidocr-onnxruntime==1.4.4 numpy==1.26.4 onnxruntime==1.20.1 opencv-python==4.10.0.84`，安装目标仅为项目的 `src/ScreenNote/ocr-runtime/Lib/site-packages`。运行时使用 `-I -B`，禁止用户／全局包搜索及字节码缓存写入。
