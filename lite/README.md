# 拾屏 精简版 ScreenNote Lite

一个只做截图与标注的 Windows 桌面工具：完整版去掉 OCR 和翻译后的独立精简版，可与完整版同时安装。

## 保留的功能

- 区域截图、窗口悬停识别、多屏与高 DPI、滚动长截图
- 截图浮动栏：取消 / 编辑 / 下载 / 复制（Ctrl+S 直接下载 PNG）
- 标注编辑器：选择、画笔、箭头、直线、矩形、椭圆、文字、荧光笔、高亮选框、序号、马赛克、遮挡，撤销/重做，颜色/线宽/字号
- 发送给 AI：原生剪贴板 + 自动粘贴到 DeepSeek / ChatGPT / 豆包，也支持历史多图批量发送
- 截图历史：自动保存、缩略图、重新编辑、批量导出、删除、资料文件夹迁移
- GIF 录制、多图拼贴、放大查看、导入图片、托盘与全局快捷键、开机自启

## 和完整版的区别

| 项目 | 完整版 | 精简版 |
| --- | --- | --- |
| 图片 OCR / 文字工作台 | 有 | 无 |
| 中英离线翻译 | 有 | 无 |
| 截图、标注、发送给 AI、历史、GIF、拼图、滚动截图 | 有 | 有 |
| 安装目录 | `%LOCALAPPDATA%\Programs\ScreenNote` | `%LOCALAPPDATA%\Programs\ScreenNoteLite` |
| 设置目录 | `%LOCALAPPDATA%\ScreenNote` | `%LOCALAPPDATA%\ScreenNoteLite` |
| 自启注册表值 | `ScreenNote` | `ScreenNoteLite` |
| 默认快捷键 | Alt+A | Alt+A（两个版本同时运行时会争用，建议改其中一个） |

精简版打开完整版创建的历史记录时，会原样保留记录中的 OCR 文字字段，不会在重新保存时抹掉。

## 构建

在 `lite` 目录执行：

```powershell
# 框架依赖发布（需要本机已安装 .NET 10 Desktop Runtime）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test

# 轻量 ZIP（依赖 .NET 10 Desktop Runtime，约 0.3 MB）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package

# 自包含安装包（自带 .NET 运行时）
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1

# 自包含便携 ZIP（需先执行 build-installer.ps1 生成 payload）
powershell -ExecutionPolicy Bypass -File scripts\build-portable.ps1
```

产物默认写到仓库级 `output\lite\`（本目录独立使用时会写到本目录的 `output\`）：

- `ScreenNoteLite-1.0.0-Setup-x64.exe`
- `ScreenNoteLite-1.0.0-portable-x64.zip`
- `ScreenNoteLite-1.0.0-lite-x64.zip`
- `ScreenNoteLite\`（框架依赖发布目录）

## 体积

| 产物 | 完整版 | 精简版 |
| --- | --- | --- |
| 安装包（自包含，含 .NET） | 264 MB | **50.6 MB** |
| 便携 ZIP（自包含，含 .NET） | 321 MB | **65.6 MB** |
| 轻量 ZIP（依赖已安装的 .NET） | 276 MB（0.7.x） | **0.3 MB** |
| 发布目录（框架依赖） | 533 MB | **0.7 MB** |

完整版的体积主要来自 `ocr-runtime`（350 MB）和 `translation-models`（162 MB），精简版两者都不包含；剩余部分是 .NET 自包含运行时。

> 如果安装包放在受限目录（例如被安全策略禁止删除子项的文件夹）运行后立刻退出，请把安装包复制到普通文件夹（如下载目录）再运行。

## 说明

- 发送到 AI 的行为边界见 [docs/ai-bridge.md](docs/ai-bridge.md)。
- 本目录是独立 fork；完整版的功能改动需要按需同步到这里。
- 精简版不含 Python 运行时、OCR 模型与翻译模型。