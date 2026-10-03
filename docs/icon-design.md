# 拾屏图标设计 · 0.5.0

0.5.0 延续 0.4 中用户从六款方案选定的 F「反白 SN」：黑色圆角底块与白色无衬线字母 SN。参考 [Notion 官方网站](https://www.notion.com/)的黑白字母视觉方向，采用拾屏自己的字母组合和轮廓。

这次直接绘制矢量图形，并把字母转成轮廓；不依赖运行电脑的字体。统一源文件为 src/ScreenNote/assets/app.svg，桌面与浏览器插件从该源生成，保持同样的形状和比例。

- 程序窗口：src/ScreenNote/assets/app.png（256 像素）。
- Windows 图标：src/ScreenNote/assets/app.ico（16–256 像素，九种尺寸）。
- 浏览器插件：browser-extension/icons/icon-16.png、icon-32.png、icon-48.png、icon-128.png。
- 六款选择稿：output/icon-options/icon-options.png，各款独立 SVG 和 PNG 保存在同一目录。

运行 scripts/build-icons.ps1 可从统一矢量源重新生成全部尺寸。程序包包含桌面图标与插件图标；插件更新后需要在扩展管理页面重新加载，确认版本为 0.5.0。从 0.4 或更早版本升级时完成一次新配对，之后桌面或浏览器重启自动连接。图标更新本身不会迁移或删除截图资料。
