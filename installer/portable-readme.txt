拾屏 ScreenNote 0.10.2 - 免安装版

一、怎么用
1. 把整个文件夹解压到任意位置（U 盘、移动硬盘也可以）。
2. 双击 ScreenNote.exe 启动。
   如果 Windows 弹出「已保护你的电脑」，点「更多信息」→「仍要运行」。
   这是因为程序没有购买代码签名证书，不是病毒提示。
3. 默认快捷键 Alt+A 截图，也可以右键托盘图标打开设置。
4. 截图、文字识别、中英翻译全部在本机完成，不联网、不上传。

二、文件放在哪
- 截图历史：这个文件夹下的 data\history（跟着文件夹走，换电脑拷整个文件夹即可）。
- 设置：%LOCALAPPDATA%\ScreenNote\settings.json。免安装版和安装版共用这一份设置。

三、怎么卸载
直接删掉整个文件夹。若想连设置一起清掉，再删除 %LOCALAPPDATA%\ScreenNote。

四、其他
- 不需要安装 .NET 或 Python，运行环境和模型都在包里。
- 支持 Windows 10 2004 及以上的 64 位系统。
- 详细说明见 docs\windows-installer.md 与 docs\verification.md。