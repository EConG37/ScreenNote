# Windows 安装包

当前文件：`output/ScreenNote-0.10.2-Setup-x64.exe`。

双击后按中文向导选择安装位置。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\ScreenNote`，可创建桌面快捷方式；开始菜单和 Windows「已安装的应用」中也有入口。支持 Windows 10（2004 及以上）和 Windows 11 的 64 位环境。

安装包包含 .NET 桌面运行环境、中文 OCR 和中英离线翻译模型，使用时无需另外安装 .NET 或 Python，也无需联网下载模型。首次登录 AI 网站需要网络。

## 体积构成

安装包 252 MB。压缩后约 136 MB 是两份离线翻译权重（ctranslate2 int8），约 85 MB 是中文 OCR 运行时（Python + ONNX Runtime + OpenCV + PP-OCRv4 模型），其余约 49 MB 是自包含 .NET 桌面运行时、程序文件和文档。翻译权重本身接近不可压缩，占安装包一半以上。

打包脚本在发布后删除程序从不加载的内容：除简繁中文外的 11 套框架语言资源、Python 各包的 `tests` 目录、`sympy`／`mpmath`、OpenCV 的 ffmpeg 解码 DLL 与人脸级联数据、以及 `*.pyi` 类型存根。删除记录写入 `.build/installer/0.10.2/payload-trim.txt`；删除后仍逐项校验必需文件、运行时版本与程序版本。

再明显缩小只有两条路：翻译模型改为首次使用时下载，或改为依赖系统已装的 .NET 桌面运行时（约省 40 MB）。两者都会牺牲「装完即可离线使用」。

已有程序运行时，先从托盘退出拾屏，再运行安装包升级。升级保留安装位置和截图资料；卸载只移除程序文件，截图、GIF 和用户设置继续保留。手动删除整个安装文件夹仍会删除其中的 `data` 资料。

## 免安装版

同一份程序文件也打包成免安装压缩包：`output/ScreenNote-0.10.2-portable-x64.zip`（306 MB）。压缩包里是一个 `ScreenNote-0.10.2-portable` 文件夹，解压后双击 `ScreenNote.exe` 即可运行，不需要安装，也不写注册表。

- 内容与安装包相同，同样自带 .NET 桌面运行时、中文 OCR 和离线翻译模型，无需另装 .NET 或 Python。
- 截图历史默认留在解压目录的 `data/history`，整个文件夹可以拷到 U 盘或另一台电脑继续用。
- 设置仍写入 `%LOCALAPPDATA%\ScreenNote\settings.json`，与安装版共用；卸载时删掉解压出的文件夹，需要清理设置再删该目录。
- 比安装包大（306 MB 对 252 MB），因为 zip 用 deflate 逐文件压缩，安装包用 LZMA 整体压缩。翻译权重约 136 MB，两种方式都压不动。
- 与安装包一样没有代码签名，首次运行可能提示「已保护你的电脑」，点「更多信息」→「仍要运行」。

构建：先运行 `scripts/build-installer.ps1` 得到已裁剪的 payload，再运行：

```powershell
.\scripts\build-portable.ps1
```

脚本会读取 payload 逐文件打包，回读后与源文件逐项比对 SHA-256，输出 zip 与 `.sha256`；包内的 `使用说明.txt` 来自 `installer/portable-readme.txt`。

## 构建

使用 .NET 10 SDK 和 Windows PowerShell 5.1（或 PowerShell 7），在项目根目录运行：

```powershell
.\scripts\build-installer.ps1
```

首次构建从官方来源下载固定版本的 Inno Setup 编译器和 .NET 运行时。编译器通过便携模式放在 `.build/installer-tools`，不添加系统快捷方式和程序关联。编译器和中文语言文件均校验 SHA-256。安装包及其 `.sha256` 校验文件生成到 `output`。

构建中间文件、程序文件清单与编译日志位于 `.build/installer/0.10.2`。输入来自源码发布目录，拒绝包含截图资料、设置、配对凭据和旧版扩展。

安装引擎来源：[Inno Setup 官方下载](https://jrsoftware.org/isdl.php)。独立运行发布依据：[Microsoft .NET 发布文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。
