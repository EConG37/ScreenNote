# 拾屏精简版 ScreenNote Lite 1.2.7

Windows 区域截图、标注、历史、多图拼贴、滚动长图、GIF 和带声音的视频录制工具。不包含完整版的 OCR 与离线翻译。

## 安装与体积

- 轻量安装包：ScreenNoteLite-1.2.7-Light-Setup-x64.exe，约 2.22 MiB，使用电脑已有的 x64 .NET 10 Desktop Runtime。
- 自带环境安装包：ScreenNoteLite-1.2.7-Setup-x64.exe，约 47 MB，无需另装 .NET。
- 两种构建功能相同，输出到 releases/1.2.7，并提供对应的免安装 ZIP 和 SHA-256 校验文件。

轻量版共享系统运行环境；首次安装该环境仍需单独磁盘空间。安装程序会检测运行环境。从自带环境版升级后，会在安装成功时清理原有运行库，保留资料。

若从开发目录启动安装程序提示临时目录拒绝访问，请复制到普通下载目录运行。无需关闭安全软件或修改整个磁盘的权限。

## 录制与编辑

点击录制，选择 GIF 或视频并框选区域，再点击开始录制。支持暂停、继续、停止保存和放弃。编辑窗口自动播放 GIF 和视频，支持播放/暂停、拖动进度、选段循环与预览静音。调整起止时间后会停在相应画面，裁剪框覆盖在动态预览上。支持画面裁剪、时间裁剪、GIF/MP4 导出；MP4 保留原有声音，无声录制继续保持无声。

GIF 默认 15 帧/秒，可选择 10 MB 目标。通过降低尺寸和颜色数压缩；保持完整时长仍无法达到目标时会明确报错，可以缩短时长或选择不压缩。

## 资料

- 默认安装目录：%LOCALAPPDATA%\Programs\ScreenNoteLite。
- 设置：%LOCALAPPDATA%\ScreenNoteLite\settings.json。
- 默认资料：程序目录下的 data/history、data/recordings，可在设置中迁移。
- 卸载保留资料；手动删除整个程序文件夹会删除其中的资料，请先迁移重要内容。
- 支持 Windows 10 2004 及以上 x64 系统，与已归档完整版使用独立设置和自启项。同时运行需使用不同快捷键。

## 构建与验证

需要 .NET 10 SDK，在 lite 目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test
powershell -ExecutionPolicy Bypass -File scripts\validate.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -FrameworkDependent
powershell -ExecutionPolicy Bypass -File scripts\build-portable.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-portable.ps1 -FrameworkDependent
```

自带环境包移除设计器、调试工具和多余语言资源，不启用 WPF 不支持的 IL 裁剪。验证覆盖基础自检、GIF/视频、暂停、录制界面及媒体往返导出，均针对精简版源码。界面验证会短暂打开测试窗口，使用 -SkipUi 可跳过。

完整操作见根目录 README；AI 网页复制粘贴见 docs/ai-bridge.md。
