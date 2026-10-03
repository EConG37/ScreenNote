# 软件内离线翻译 · 0.7.1

三个翻译入口均在软件内显示原文与译文，不启动浏览器、不向 AI 网站发送图片或文字，也不需要 API 密钥。总结和发送 AI 继续使用独立的网页连接。

截图浮动栏先识别图片；编辑界面优先采用已有校对文字，文字面板展开时优先采用选中内容；历史按截图时间顺序合并，每张优先采用保存的 OCR，最多十张。识别结果只放在翻译窗口，不覆盖原历史或编辑中的 OCR。原文可继续修改，Ctrl+Enter 重新翻译；改文或改方向后阻止旧任务结果覆盖新内容。译文可选中或整段复制。

默认自动选择英语到简体中文，或中文到英语；也能在窗口中手动指定方向。只支持这两个语言方向，每次最多 20,000 字符。保留原行序、空行与缩进；长行显式分段，避免推理引擎静默截掉尾部。关闭、停止或修改原文会取消任务，终止独立识别／翻译进程并清理临时输入。没有云端备用或隐式网页跳转。

## 组件与来源

- [Argos 官方模型索引](https://github.com/argosopentech/argospm-index)：`translate-en_zh-1_9`、`translate-zh_en-1_9`。采用包内原始 CTranslate2 模型和 SentencePiece 字典，未改变模型权重；省去未使用的 Stanza 分句资源。
- 模型原作者 Jörg Tiedemann、Santhosh Thottingal，论文 *OPUS-MT — Building open translation services for the World*，EAMT 2020，Lisbon。包内 README 声明原始 OPUS 模型采用 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)，原 README 保留在两个模型目录内。
- [CTranslate2](https://github.com/OpenNMT/CTranslate2) 4.8.2，MIT；[SentencePiece](https://github.com/google/sentencepiece) 0.2.1，Apache-2.0。组件、原许可证及版权说明随独立运行环境提供，仅安装在项目的 `ocr-runtime/Lib/site-packages`，不依赖全局 Python。使用 CPU／int8 推理，无需 GPU。
- 原始包 SHA-256：英文到中文 `433e7c4f034d87fbe2353161e05f18646d7999452f801a4e1f0378522b9850ab`；中文到英文 `62e7af5a3a48b530e47b7b3e5c78c2de79073ecd815750d2bf3ab35b4a67da2d`。

中文安装路径通过读取模型字节处理，避免 Windows 原生加载器的非 ASCII 文件路径问题。翻译只使用本机模型；图片、文字不作为命令行参数传递或写入日志。

模型适合基础中英阅读，翻译质量取决于句子、专有名词和 OCR 结果，不能据几个样本宣称全面准确。遇到错字可以校对左侧原文再翻译，原图和原 OCR 仍保留。
