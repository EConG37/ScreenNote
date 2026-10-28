# v0.9.2 Oil UI 工具栏精修

日期：2026-10-27。沿用已确定的单行布局与红／绿／黄语义，用 Oil UI 0.26.0 打磨局部层级。绿色实色复制按钮为主操作，回车提示可见；取消去掉底色，编辑保留浅黄色。点击区域由 34 增至 36 逻辑像素，统一悬停、按下、焦点与禁用状态。

- 84 项功能及布局检查通过，1280／900／640 逻辑宽度下所有按钮保持单行且完整可见；截图动作、快捷键和 100%／200% 坐标检查通过。
- 使用合成材料查看默认、禁用与键盘焦点效果，并检查 200% 渲染。局部低风险样式改动由主 Agent 自检；不改变截图流程、不增加出场动画，按下时即时轻微下沉。
- 证据：`.build/toolbar-oil-ui/brief.md`、`.build/toolbar-oil-ui/before.png`、`.build/toolbar-oil-ui/visual-final/feature-polish.json`、`.build/toolbar-oil-ui/visual-final/toolbar-default-200.png`、`.build/toolbar-oil-ui/visual-final/toolbar-keyboard-focus.png`。

---

# v0.9.1 单行截图工具栏

日期：2026-10-27。取消、复制、编辑改为统一图标与圆角样式，使用柔和的红／绿／黄配色；工具栏所有操作保持同一行。较窄屏幕下辅助工具收起文字，主要操作保留完整名称和快捷键。

- 84 项功能与布局检查通过，包含 1280／900／640 逻辑宽度的同排显示、所有入口不裁切，以及原有标注、历史、复制与编辑路径。截图动作检查包含取消、复制、编辑、GIF、滚动截图、快捷键及 100%／200% 坐标。
- 视觉自检采用合成截图，查看完整文字与紧凑图标两种布局；局部样式改动未增加动画，未进行独立评审。
- 证据：`.build/toolbar-single-row-final/feature-polish.json`、`.build/toolbar-single-row-final/capture-toolbar-1280.png`、`.build/toolbar-single-row-final/capture-toolbar-640.png`。

---

# v0.9.0 GIF 压缩、滚动长图与台词拼图

日期：2026-10-27。本地版本，GitHub Releases 尚未发布。

- GIF 默认生成微信小图，目标 950,000 字节以下；合并重复帧、按变化区域编码并逐步降低尺寸／帧率／色数，完整原文件另存保留。支持已有 GIF 压缩和取消。用户原有 10,748,082 字节、913×514、57 帧的样本压为 615,502 字节、320×180、29 帧，总时长均为 6.18 秒；小字清晰度会下降。
- 恢复选区内手动向下滚动截图，匹配重叠内容，仅追加新露出的行。无法匹配时保留已有内容并提示减小滚动距离。控制窗口采用非模态显示，实际鼠标滚轮可继续操作原页面。
- 新增多图与台词拼图，支持历史多选、连续截图和图片导入，台词裁切高度、错位、顺序、输出宽度及四种布局可调；支持复制、PNG 保存与进入编辑，原截图保留。
- 截图浮动栏改为两排，右下排依次为红色取消、绿色复制、黄色编辑；84 项功能和布局检查通过，包含 1280／900／640 逻辑宽度下完整显示。
- 24 项合成检查通过：长图逐像素接缝、中文页面、重复／反向／无重叠拒绝；台词底部裁切与布局；GIF 压缩比例、完整时长、最终画面、独立 GDI+ 解码、重复帧、取消、临时文件清理与原文件保留。
- 11 项实际桌面拼图检查通过：真实鼠标滚轮、长图自动复制与历史保留、台词高度和排序、继续截图返回同一窗口、拼图复制与完成编辑。19 项真实 GIF 录制界面检查、97 项历史与设置回归通过。
- Release 构建为 0 个错误、0 个警告；打包后的 0.9.0 通过 11 项自检，包含桌面截图 API、图片导出和实际本地中英文 OCR。截图动作检查包含十项操作、颜色、两排布局、GIF／滚动区域、100%／200% 坐标与取消。

证据：`.build/composition-validation-final/result.txt`、`.build/composition-ui-wheel/result.txt`、`.build/composition-ui-wheel/live-scroll.png`、`.build/composition-ui-wheel/collage-workspace.png`、`.build/feature-polish-composition-final/feature-polish.json`、`.build/gif-recording-ui-composition-final/result.txt`、`.build/ui-history-composition-final/ui-history-test.json`、`.build/self-test-composition-stage/self-test.json`。

微信最终是否将粘贴文件展示为动画仍需当前客户端实测，压缩目标并非微信官方通用限制。滚动截图支持向下滚动，需保留重叠并避开固定导航／页脚；实机混合缩放多显示器未新增验证。

以下为历史版本验证记录。

---

# v0.8.0 选区 GIF 录制

日期：2026-10-27。本地构建已生成，GitHub Releases 尚未发布此版本。

- 框选后点击「录制 GIF」或按 Ctrl+G，返回带虚拟桌面偏移的物理像素范围；复用原有移动、缩放及边缘裁切。GIF 不经过静态图片历史与标注导出。
- 29 项录制检查通过：逐帧自适应调色板、WIC 与独立 GDI+ 多帧解码、帧时长、循环标记、真实时间播放、慢采集、分辨率限制、停止与取消、文件大小限制、异常清理及文件剪贴板格式。
- 19 项本机桌面界面检查通过：临时红蓝色块实录、控制窗口覆盖在选区内仍不出现在 GIF 中、停止后自动复制完整 GIF 文件、关闭结果仍可粘贴、倒计时和录制中的取消清理、从截图窗口进入录制、重复截图互斥与工作台恢复。只保存测试色块；不向聊天软件发送测试文件。
- 截图动作检查通过，包含新增按钮和 Ctrl+G、负坐标虚拟桌面、原有复制／编辑／AI／OCR／翻译／总结／高亮、100%／200% 合成缩放和取消。78 项功能与布局检查通过，九项浮动操作在 1280／900／640 逻辑宽度下完整显示。
- GIF 随资料位置迁移检查通过，保留原文件、跳过未完成文件。97 项截图历史与设置流程回归通过；已发布到本地的 0.8.0 程序通过 11 项自检，包括实际桌面截图 API（不保存桌面图像）、标注、导出与本地中英文 OCR。Release 构建为 0 个错误、0 个警告。
- 录制与复制通过不代表每个聊天软件都支持直接粘贴动画；具体客户端的粘贴、附件展示和发送仍需实际使用验收。实机多屏不同缩放比例未新增验证。

证据：`.build/gif-recording-validation-final/result.txt`、`.build/gif-recording-ui-release.txt`、`.build/gif-recording-ui-release/gif-result.png`、`.build/feature-polish-gif/feature-polish.json`、`.build/ui-history-gif-release/ui-history-test.json`、`.build/published-self-test-gif-release/self-test.json`。可运行 `tests/gif-recording-validation.csproj` 复现合成检查，实际桌面检查为 `tests/gif-recording-ui.csproj`，需要可访问本机桌面的会话；两者可传入独立输出文件夹。

格式实现依据 [GIF89a 规范](https://www.w3.org/Graphics/GIF/spec-gif89a.txt)，使用逐帧局部调色板及图形控制扩展。复制采用 [Windows 文件剪贴板格式](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)，避免静态位图格式使接收端丢失动画。控制条排除采用 [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)，需要 Windows 10 2004 或更新版本。

---

# v0.7.2 移除滚动截图

日期：2026-10-03。按用户要求移除滚动截图，已有截图和长图历史继续保留。

- 删除截图浮动栏和编辑／历史顶部入口，以及采集窗口、拼接逻辑、操作分支和专用图标。
- 75 项功能与布局检查通过：浮动栏八项操作在 1280／900／640px 宽度下完整显示；顶部五项操作在 900／1040／1260px 宽度下不遮挡；高亮、原图与标注、历史保存、AI 忙碌状态均通过。
- 截图动作验证通过：复制、编辑、三项 AI 目标、OCR、翻译、总结、高亮，Enter／Ctrl+E、取消，以及 100%／200% 缩放下选区保持原色。
- 证据：`.build/feature-polish-072.txt`、`.build/feature-polish-072/feature-polish.json`、`.build/capture-actions-validation-072.txt`。

- 发布版 11 项自检通过，正式桌面版本已更新至 0.7.2。实际桌面入口、快捷键与设置保存通过；主界面滚动截图按钮与截图浮动栏入口均确认不存在。更新前 2 张截图的原图、当前图、标注/OCR 文档和索引校验值不变，配对校验值不变。程序、OCR 与翻译组件和双向模型的校验值与已自检发布版一致。证据：`.build/publish-stage-072.txt`、`.build/desktop-install-072.json`、`.build/settings-install-072.json`、`.build/actual-capture-toolbar-072.json`。

以下为更早的历史版本验证记录。

---

# v0.7.1 软件内翻译验证

日期：2026-10-03。

- 30 项翻译检查通过：真实 CPU 中英模型、自动／手动方向、中文安装路径、空行与段落、无分词标记、参数错误、取消及自身临时文字清理；原文修改取消旧任务，过时结果不覆盖新内容；复制使用每次全新的文字检验，关闭取消、错误重试，源文与译文独立。
- 三个入口均通过实际代码验收：编辑界面采用选中校对文字，历史复用保存 OCR，截图浮动栏先真实 OCR 再离线翻译；均不进入浏览器发送状态，不打开 AI 反馈窗口，原 OCR 和标注不被改写。
- 翻译界面 880／580px 内容宽度下完整显示原文、译文、语言选择、复制和翻译动作。示例仅含合成文字。
- 97 项功能与布局回归通过：翻译不再创建 AI 提示词；发送 AI 时本地翻译仍可用，浮动栏九项动作、原图／标注、滚动输入与完成保持通过。
- OCR 识别、取消后终止进程及临时图片清理通过。识别和翻译加入取消回调，关闭窗口时及时终止后台进程。
- 证据：`.build/translation-validation-071.txt`、`.build/translation-validation-071/report.json`、`.build/translation-validation-071/samples.json`、`.build/feature-polish-071.txt`、`.build/ocr-validation-071.txt`。
- 发布版 11 项自检通过，正式桌面版本已更新至 0.7.1。实际桌面入口、快捷键、设置保存与九项浮动菜单检查通过；更新前十二张截图的原图、当前图、标注/OCR 文档和索引校验值不变，配对校验值不变。程序和双向模型的校验值与已自检发布版一致。分发包包含两个离线模型及组件许可，不含用户历史、设置或配对资料。证据：`.build/publish-stage-071.txt`、`.build/desktop-install-071.json`、`.build/settings-install-071.json`、`.build/actual-capture-toolbar-071.json`、`.build/package-validation-071.json`。

---

# v0.7.0 验证记录

日期：2026-10-03。简单快捷键与真实按键录入、AI 目标下拉、离线中文增强 OCR、可交互的滚动截图。

- 快捷键 56 项检查通过：真实系统占用、原设置保留、优先模式接受占用的简单组合并抑制竞争程序动作；录入时收到真实组合且两边都不触发；释放后竞争程序恢复。PrintScreen 单键、Alt+A 两键与最多三键录入通过。并未宣称覆盖系统保留键或所有软件的键盘钩子。
- 截图浮动栏的九项操作、三个 AI 目标分别选择、原图与标注保留、100%／200% 坐标及键盘取消通过。菜单只准备网页草稿，不自动提交聊天。
- 97 项功能与布局检查通过，包含真实窗口滚轮、手动拼接、自动向下滚动、完成长图返回和取消不新增。模态控制条造成的原窗口输入障碍已移除。
- 桌面界面回归 97 项通过，包含真实键盘事件录入、识别方式保存、设置保存后保持打开、历史恢复、资料迁移、删除及失效 OCR 结果隔离。
- 四组合成 OCR 对比通过，中文增强 0／2,500 字符编辑错误，Windows 111／2,500；6,000px 长图的最后一段保持完整。普通样本约 2.6–3.8 秒，长图约 34 秒。仅适用于这些样本；详细数据见 [OCR 组件与样本](ocr-components.md)。
- 证据：`.build/hotkey-validation-070.txt`、`.build/capture-actions-validation-070.txt`、`.build/feature-polish-070.txt`、`.build/ui-history-flow-070.txt`、`.build/ocr-comparison-070/comparison.json`。本轮未改网页附件协议，也未向登录网站上传用户截图。
- 正式安装目录已更新到 0.7.0，桌面图标唤醒同一常驻进程。真实 Alt+A 打开截图；设置中实际按 Alt+S 正确录入，Esc 恢复原组合；中文增强默认选中，保存后保持打开。实际截图浮动栏显示九项入口，下拉显示三个 AI 网站；取消不新增历史。
- 更新前自行保存退出后记录的七张截图，其原图、当前图、标注/OCR 文档及索引校验值不变，配对文件校验值不变。证据：`.build/desktop-install-070.json`、`.build/settings-install-070.json`、`.build/actual-capture-toolbar-070.json`。
- 隔离资料的发布版 11 项自检通过，包含实际本机中英 OCR。安装版程序与 OCR 关键文件校验值和自检发布版一致。0.7.0 ZIP 包包含离线运行环境、模型和原许可证；包内未包含用户历史或配对文件。证据：`.build/publish-stage-070.txt`、`.build/package-validation-070.json`。

---

# v0.6.0 验证记录

日期：2026-10-03。一级工具栏、界面重做、高亮选框、AI 网页翻译／总结、滚动截图，合并默认浏览器启动与设置保存修复。

- Release 编译通过，无警告或错误。
- 新功能与布局 56 项检查通过：六项一级入口、空状态、多选及发送禁用；900／1040／1260 宽度下完整显示，绘画工具在 OCR 展开时保持一行；新高亮透明区域、导出、删除、撤销、重做与历史恢复，选择新高亮工具不改色原来选中的标注；中文稀疏文字和逐像素长图拼接、重复帧、反向滚动、跳过太快的帧、尺寸变化及真实 Windows 采集。
- 95 项桌面界面回归通过，覆盖历史、删除、资料迁移、OCR 编辑、复制／编辑／取消和设置保存后留在窗口。
- 浏览器扩展 42 项模拟检查通过，新增提示词与附件一起确认、取消草稿不上传、部分附件失败不报完成；原持久配对、实时通知和单次领取保持通过。
- 本机真实 HTTP/WebSocket、DPAPI 和默认浏览器启动协议检查通过；组合材料保留图片顺序与提示词，未确认提示词的旧扩展不会报成功。
- Windows 默认打开方法已实测打开 DeepSeek 官方网页，本机 HTTPS 默认关联为 QuarkHTM；测试不排队、不上传材料，不关闭用户浏览器。关闭浏览器后的重连流程使用替代启动器和真实本机连接验证，未为测试强制关闭用户正在使用的浏览器。
- 安装目录更新到 0.6.0，原桌面入口唤醒同一常驻进程；实际六项一级入口可见且有可访问名称。保存设置成功后窗口仍可见并可继续保存。四张现有截图的原图、当前图、标注/OCR 文档及索引校验值不变，配对文件校验值不变。证据：.build/desktop-install-060.json、.build/settings-install-060.json。
- 本地双语 OCR、PNG 输出、原标注渲染与撤销／重做、200% 截图坐标检查通过；报告 output/self-test/self-test.json。扩展文件已更新，自动定位重新加载入口未成功，未操作其他扩展；使用新增图片翻译／总结前需手动重新加载拾屏扩展并刷新 AI 标签页。
- 证据：.build/feature-polish-060.txt、.build/ui-validation-060.txt、.build/bridge-validation-060.txt、.build/default-browser-launch-060.txt；示例预览路径记录在 .build/feature-preview-path.txt。三轮视觉检查与调整见 [界面品质检查](design-review.md)。

翻译与总结结果在所选 AI 网页查看，不自动提交聊天。新版需要重新加载 0.6.0 扩展并刷新已打开 AI 页面，0.5.x 配对保留。真实登录网站附件上传兼容性未做实机验收；滚动截图需要缓慢向下滚动并避开固定导航，无法匹配时不盲目拼接。

---

# v0.5.2 中间修复验证记录

日期：2026-10-03。新增发送时启动默认浏览器，并调整设置保存后保留窗口。

- Release 构建通过，0 个错误、0 个警告。
- 95 项实际桌面界面检查通过：原有 91 项不变，新增设置保存后窗口仍可见、显示保存结果、同一窗口可继续修改保存、失败仍保留窗口与原资料位置、提供手动关闭入口的验证。
- 本机 HTTP/WebSocket、DPAPI 配对恢复和新增浏览器启动协议检查通过。启动测试使用替代的浏览器打开方法和真实临时本机连接，验证先保留任务再启动、重新连接后领取、全部附件确认才成功、一次领取、活跃连接不重复打开、关闭后的旧心跳不阻止启动、打开失败允许重试、默认浏览器缺少扩展与草稿未确认分别提示。
- 协议报告：.build/bridge-validation-052.txt；界面报告：.build/ui-history-flow-052-de1aa4d72cf9439c8058ecb099b31570/ui-history-test.json。
- 浏览器扩展沿用 0.5.0，配对信息不变，桌面更新无需重新配对。真实 AI 登录页面上传行为仍沿用此前的未验收范围。

浏览器调用依据：[.NET Windows 默认打开方式](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0)。

---

# v0.5.1 验证记录

本次新增历史截图的单张与多选删除，删除前确认，原图、标注与 OCR 文字同步删除。

- Release 构建通过，0 个警告、0 个错误。
- 91 项实际桌面界面检查通过：原有 68 项保持通过，新增 23 项删除验证，覆盖单张、多选、忙碌拒绝、部分失败统计、其他编辑与选中项保留、删除当前编辑图后取消旧 OCR/停止自动保存/清空图层和文字、关闭重启不复活、删除全部后的空状态。
- 删除存储专项通过：预检文件锁、只读与删除权限，删除前拒绝路径越界、跨库记录及目录链接；成功后原图和编辑文档全部移除，旁边记录字节不变，锁定失败项原图/OCR/标注仍可读取。
- 自动化删除只使用临时合成截图，未删除用户实际截图。极端情况下外部程序在预检后创建文件或更改权限，删除可能报失败；不会扩大删除范围或把残留目录报成功。
- 界面报告：.build/ui-history-flow-051-a1377fe344c8449abfb8a8229eb99f91/ui-history-test.json；删除存储验证：tests/history-validation.cs。
- 正式桌面已更新到 0.5.1，快捷方式正常唤醒同一托盘进程；实际单张与批量删除入口可见。打开单张删除确认并点击「否」，五张用户截图的原图、当前图、编辑文档和索引校验值不变，原配对文件校验值不变。证据：.build/desktop-install-051.json、delete-install-051.json。

浏览器扩展沿用 0.5.0，配对格式和位置保持一致，本次桌面更新无需重新加载扩展或重新配对。

---

# v0.5 验证记录

日期：2026-10-02。新增资料文件夹设置、快捷键检测与建议、选区原色显示和持久配对。

## 已通过

- Release 构建：0 个错误、0 个警告；程序与扩展版本统一 0.5.0。
- 68 项实际桌面界面检查：保留原有 43 项历史/编辑流程，补文件夹切换、未保存 OCR 和标注恢复、多选保留、原库备份、设置提交失败不切库、重启读取新库。原位置无法保存时，内存草稿能直接保存到新库。
- 两轮独立目录离线恢复：每轮使用新的恢复目录，新截图合并回正式库，第二次恢复不覆盖第一次恢复之后更新的文档。测试仅使用项目内资料和模拟目录，不操作用户配对或截图。
- 设置界面检查：资料路径、文件夹选择、卸载删除资料提醒可见；内容可滚动，保存按钮固定可见。
- 迁移专项：后台复制、完整 OCR 字段与可编辑标注恢复、保留源、合并与幂等重试、提交锁定后恢复、同编号原图冲突、损坏文件、路径嵌套和目录链接处理通过。
- 45 项真实 Windows 快捷键检查：占用检测、失败保留原键、临时探测和建议筛选通过；避免 Windows 保留键与 F12。
- 合成屏幕像素检查：选区像素保持原色，外围变暗；移动、调整大小、画布变化、100%/200% 比例、复制/编辑/取消分支通过。
- 39 项浏览器模拟检查全部通过，包含持久凭据恢复、旧 session 迁移、桌面离线保留、401 撤销、过时响应不删除新授权，以及原有实时通知/多图/草稿检查。
- 本机 HTTP/WebSocket 与 26 项配对持久化断言通过：Windows 当前用户 DPAPI 加密保存、软件重启恢复、扩展绑定、自动恢复任务链路、重置/断开成功后持久撤销；保存或撤销失败报告失败且保留一致状态。
- 新版程序自检通过，包含实际本地中英文 OCR 与截图调用；真实桌面图像仅在内存中检查，没有保存。
- 正式桌面程序已更新到 0.5.0，桌面快捷方式正常唤醒同一托盘进程。现有 5 张用户截图迁到安装目录 data/history；逐一核对原图、当前图、编辑文档与索引的 SHA-256，目标完全一致，原 LocalAppData 文件保持不变。
- 实际设置窗口显示正确资料路径，文件夹选择、可用快捷键建议与保存按钮正常，卸载提醒和持久配对说明可见。
- ZIP 分发包仅包含程序文件与已知资源，排除截图、历史、偏好和配对文件；包内扩展确认 0.5.0。

## 证据与范围

- 界面报告：.build/ui-history-flow-05-d887de0a91424e7dba591c4c76126fca/ui-history-test.json。
- 文件夹迁移：tests/storage-location-validation.cs；界面与离线恢复：tests/ui-history-flow.cs。
- 快捷键：output/hotkey-validation.txt；原色选区：tests/capture-actions-validation.cs。
- 配对持久化：browser-extension/tests/BridgeHarness/PairingPersistenceChecks.cs；浏览器模拟：browser-extension/tests/worker.test.cjs 与 content.test.cjs。
- 离线 OCR 自检：.build 下 self-test-05 的独立报告。
- 正式安装与资料校验：.build/desktop-install-05.json、user-history-migration-05.json、settings-install-05.json。

DeepSeek、ChatGPT、豆包的真实登录页面及上传速度仍未实测。浏览器重启恢复通过模拟和本机协议验证，尚未操作用户实际浏览器配置；升级后需重新加载 0.5.0 扩展并首次配对一次。

实现依据：[Windows 全局快捷键占用规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[Chrome 持久扩展存储](https://developer.chrome.com/docs/extensions/reference/api/storage)。

---

# v0.4 验证记录

日期：2026-10-02。已选定 F「反白 SN」图标，桌面与插件使用同一矢量源。

## 已通过

- Release 构建：0 个错误、0 个警告。
- 31 项浏览器模拟检查：实时通知不等 alarm、没有 AI 标签页时打开目标、20 秒心跳、断线重连、通知与空轮询交错不丢任务、并发领取与刷新不重复上传、遗留任务锁不延迟新任务，以及原有多图与草稿行为。
- 真实本机 WebSocket / HTTP 协议检查：来源和令牌鉴权、连接绑定扩展、即时通知、握手超时后持续连接、心跳、待领取任务重连恢复、领取一次与完整附件确认。使用临时端口，不影响用户软件。
- 合成两图任务测量：发布到通知 2 ms、队列等待 4 ms、模拟确认总共 7 ms。它只衡量本机桥接链路，不代表 DeepSeek 网页加载或上传速度。
- 历史专项检查：直接读取当前保存版本的 PNG，保留最新标注、原图和 OCR，按剩余大小限制早退，原子保存与损坏项隔离检查通过。
- 43 项实际桌面界面流程：历史恢复、最新标注批量准备、复制与编辑分支、取消、重启与升级恢复通过；测试资料与用户历史分开，测试后恢复剪贴板。
- 选定图标从统一 SVG 输出九种 Windows ICO 尺寸与四种浏览器 PNG 尺寸，透明边缘保留；插件 manifest 同时配置列表图标与工具栏图标。
- 正式程序已更新到 0.4.0，桌面入口切换到选定 F 图标。四张用户历史保留；退出托盘后再次从桌面启动，只唤醒同一进程并打开历史。

## 验证证据与范围

- 本机协议：browser-extension/tests/BridgeHarness/Program.cs。
- 浏览器模拟：browser-extension/tests/worker.test.cjs、content.test.cjs。
- 界面报告：.build/ui-history-flow-04-1e02fe0f413845ce8d3007fd24b778b0/ui-history-test.json。
- 历史检查：tests/history-validation.cs。
- 正式桌面入口：.build/desktop-install-04.json。

延迟定位：旧插件在没有可见 AI 页面时，仅靠 30 秒 alarm 领取任务；网页就绪随后另有最多 15 秒等待。0.4 实时通知移除常规领取等待，定时检查保留作断线后备。真实登录网页的加载、上传速度仍需分别验收；必须重新加载 0.4.0 插件并重新配对，旧插件不会使用新链路。

实现依据：[Chrome WebSocket 与后台保活](https://developer.chrome.com/docs/extensions/how-to/web-platform/websockets)、[.NET WebSocket.CreateFromStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket.createfromstream?view=net-10.0)。

---
# v0.3 验证记录

日期：2026-10-02。平台：当前 Windows 电脑、.NET 10 SDK / Desktop Runtime。

## 已通过

- Release 构建、发布：0 个错误、0 个警告。
- 43 项界面流程检查：包含 v0.2 的历史保存与恢复检查，以及复制截图不打开编辑器、保留原编辑状态和待保存文字/标注、剪贴板写入原图、编辑截图进入新的编辑状态并保留历史。
- 框选行为专项：仅显示“复制截屏 / 编辑截屏 / 取消”；Enter 和双击默认复制，Ctrl+E 编辑，Esc、右键和取消按钮退出。取消不会产生新的历史。
- 最终程序自检：11 项通过，包含实际桌面截图调用（仅在内存检查）、标注与撤销、PNG 导出、真实本地中英文 OCR。
- 图标检查：透明 PNG、九种尺寸的 ICO（16–256 像素）有效，可由 Windows 读取；桌面快捷方式创建、更新自身目标、拒绝覆盖其他程序入口和预演行为通过。
- 单实例唤醒专项：同一路径、同一用户的隐藏窗口可以收到唤醒消息；排除自身进程及无关窗口，释放监听后不再回调。
- 实际桌面入口：已在 Windows 重定向后的 OneDrive 桌面创建“拾屏.lnk”，目标与图标均为正式发布程序；0.3.0 打开已有两张历史，关闭后托盘驻留，双击桌面入口唤醒同一进程并打开历史，没有新增实例或弹出重复运行提示，历史数量保持不变。
- 编辑器和历史多选界面预览检查：白、冷灰与少量蓝色，无控件文字截断；测试图片及历史数据均与用户资料隔离。

## 验证证据

- 界面流程报告：.build/ui-history-flow-6a911f55bf6d4ed89a7933cc8392a907/ui-history-test.json。
- 实际桌面自检：output/desktop-self-test-03/self-test.json。
- 框选专项：tests/capture-actions-validation.csproj。
- 单实例专项：.build/desktop-activation-validation。
- 正式程序与桌面入口：.build/desktop-install-03.json。
- 界面预览：output/editor-preview-03.png 与 output/history-preview-03.png。

网页实际登录页面、多屏缩放、自启与长期运行的验证范围继续见 v0.2 记录。新增界面与图标不改变网页连接方式。

---
# v0.2 验证记录

日期：2026-10-02。平台：当前 Windows 电脑、.NET 10 SDK / Desktop Runtime。

## 已通过

- 最终 Release 构建、发布：0 个错误、0 个警告。运行中的版本为 0.2.0。
- 36 项界面流程检查：默认展示历史，截图内容立即进入紧凑编辑界面，替换图片前保留未触发定时保存的标注与文字，切换历史与重启后恢复，标注继续编辑及撤销，多选保持，独立图片按截图时间输出并包含最新标注。
- 实际桌面取消截图流程：注入识别和发送进行中的状态，仍能打开原生截图选区；启动截图会取消旧 OCR 请求；Esc 取消不新增历史，也不丢失原编辑。此检查模拟繁忙状态，没有同时运行真实 OCR 或网页上传。
- 升级恢复检查：备份图片与中文文字自动进入历史，原备份保留；中断恢复后重试使用同一条未完成记录，不重复创建。
- 历史专项：原图与全部可编辑标注保存、中文 OCR 修改、缩略图更新、损坏记录隔离、路径检查；无 current.json 的未完成记录不展示。1248×605 图片创建、更新及重新打开通过。
- 提交被文件锁阻止时，上一版索引和图片文字仍完整。历史采用同目录临时文件与覆盖重命名，避免系统拒绝目录重命名或额外权限的文件替换操作。
- 真实默认资料目录实测：旧窗口 1248×605 成品图与 71 字编辑文字已恢复到 %LOCALAPPDATA%\ScreenNote\history，检查文字完全一致、只有一条记录。旧版无法导出独立标注对象，因此这张升级图片保留的是已合成图；新版截图保留可编辑标注。
- 最终程序自检：11 项通过，包含实际桌面截图调用（只在内存检查，不保存桌面图像）、标注渲染与撤销、坐标转换、文字选区整理与撤销、PNG 导出、真实本地中英文 OCR。
- 浏览器扩展：25 项模拟行为检查通过，覆盖多图带入、网站目标、草稿处理、附件确认、部分失败及不自动发送。
- 本地网页连接协议：32 项检查通过，覆盖配对与鉴权、多图独立顺序、目标隔离、10 张 / 12 MB 限制、部分成功不报告全部完成、断开失效。
- 使用示例图片生成并检查紧凑编辑与历史多选界面预览；预览数据与用户真实历史分开。

## 验证证据

- 最终界面报告：.build/ui-history-flow-2091ad365cb74217998634ffb9ad57f7/ui-history-test.json。
- 最终整体自检：output/desktop-self-test-02-final/self-test.json。
- 网页连接协议：output/bridge-validation-02.txt。
- 历史专项：tests/history-validation.csproj；界面流程：tests/ui-history-flow.csproj；浏览器模拟：browser-extension/tests。
- 界面预览：output/editor-preview.png 与 output/history-preview.png。

## 尚待真实使用验证

- DeepSeek、ChatGPT、豆包实际登录页面中的文字与附件行为。模拟检查和协议通过不能代表登录网页已通过。
- Chrome / Edge 加载 0.2.0 扩展、重新配对与完整网页流程；旧扩展需重新加载并刷新已有 AI 页面。
- 多屏不同缩放比例下的实际框选；全局快捷键与其他软件共存；开机自启和长期托盘运行。

文件更新操作使用的官方说明：[ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)、[MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)。本机临时目录的比较测试证实覆盖重命名可用；未修改系统或资料目录权限。

---
# 第一版验证记录

日期：2026-10-02。平台：当前 Windows 电脑、.NET 10 SDK / Desktop Runtime。

## 已通过

- 整体程序 Release 构建：0 个错误、0 个警告。
- 桌面程序自检：标注及马赛克渲染、撤销重做、Windows 桌面截图调用（仅在内存中检查尺寸，不保存桌面图像）、快捷键解析、200% 比例下的选区坐标转换、选区边界裁切、只整理选中文字、文字整理撤销、保持截图像素尺寸、PNG 导出、实际本地中英文 OCR。
- 标注专项：移动、颜色修改、删除和文字修改的撤销重做；全部工具导出；导出不包含选中边框；非 96 DPI 图片的像素尺寸保持。
- OCR 专项：中文图片、英文图片、空白图片、10,500 像素超大图片的等比例缩小、文件错误反馈、C# 调用、取消后终止识别进程及临时文件清理。
- 文本整理：中文和英文断行合并、段落边界、列表、缩进和代码围栏保留、空行及空格清理。
- 浏览器脚本语法检查通过。
- 浏览器模拟页面与后台测试：11 个用例通过，覆盖中文多行、已有草稿追加/替换/取消、网页拒绝修改、过期任务、图片未确认、配对、页面激活、确认与令牌失效；没有自动发送提问。
- 本地网页连接协议：19 项断言通过，覆盖 Host、Origin、配对鉴权、单次领取、结果确认、PNG 内容传递及断开。
- 真实桌面界面使用测试图片和本地 OCR 生成预览，已检查布局。

## 尚待真实使用验证

- 用户已登录的 DeepSeek 页面：文字填入是否被网页保留，以及 PNG 附件上传是否匹配当前页面。
- Chrome / Edge 实际加载扩展后的完整连接流程。模拟测试结果不代表真实 DeepSeek 页面通过。
- 多屏不同缩放比例的实际截图选区与原屏幕对应关系。
- 全局快捷键与其他软件共存、自启开关在登录后的效果，以及托盘长时间运行。
- 图片剪贴板在用户常用聊天和办公软件中的粘贴兼容性。

本次读取 DeepSeek 公共聊天页面没有获得可用页面内容，没有访问用户账号或修改其浏览器设置。

## 重现验证

运行 `scripts/build.ps1 -Test` 构建并执行整体桌面自检，结果写入 `output/self-test/self-test.json`。本次含真实截图调用的最终自检在实际桌面会话中通过，原始报告为 `output/desktop-self-test/self-test.json`。隔离会话的桌面截图返回无效句柄，不能在该会话执行真实截图测试。OCR 测试位于 `tests/ocr-smoke.ps1` 和 `tests/ocr-validation.csproj`；标注测试位于 `tests/annotations-validation.csproj`；网页连接测试位于 `browser-extension/tests`。

构建脚本将 .NET/NuGet 临时配置和缓存放在项目 `.build` 目录，不要求全局安装额外依赖。编译输出及测试图片均位于项目内。

## 0.6.1 截图浮动栏与连接回归

- 截图操作测试：九个直接入口、正确裁剪、内联高亮及原图与可编辑图层分别保留、键盘和取消、100/200% 缩放。
- 界面与功能检查：92 项，包括九个浮动按钮在 1280/900/640 逻辑宽度下完整显示、内联高亮直接进入编辑器，以及此前 OCR、滚动拼接、历史与设置行为。
- 浏览器模拟检查：44 项，新增离线启动不创建 WebSocket、重试期间保留配对、恢复后无需新配对，以及预检发现授权撤销时不建立实时连接。真实登录网页附件上传仍需实际使用验证。
- 历史流程回归：截图替换、复制不打开编辑、可编辑标注与文字、迁移、单张及批量删除，全部使用隔离资料。


## 0.10.0 当前本机验证（2026-10-27）

- 新工作区 29 项检查：窗口悬停识别与原生单击、负坐标桌面、选区上方工具、实际鼠标画箭头、撤销重做、逐张台词上下边界、切换及排序后保留裁切、连续框选、大图查看、缩放，以及缺段时禁止完成和回退恢复。
- 浮动栏与既有功能 84 项检查：1280／900／640 逻辑宽度下保持一行，取消→编辑→复制顺序与可见性、聚焦高亮和历史保留等。独立截图操作检查覆盖十个入口、100／200% DPI、快捷键与取消。
- 真实桌面滚动与拼图 11 项：一次输入 -2400 滚轮单位（20 格），排队采集至完成，再核对长图高度与实际页面滚动距离，随后验证继续截图、预览、排序、复制和保存到编辑历史。原有合成与 GIF 压缩 24 项检查通过。
- 浏览器 JavaScript 49 项检查：PNG 粘贴入口、保留草稿、没有附件时不误报、重复注入不重复处理、已有网页重连和夸克独立连接页。原生通信分别以 chrome-extension:// 与 extension:// 跑完整的 103 项传输检查，含实时通知、配对、鉴权、超时与单次领取。
- 实机夸克原先未安装拾屏扩展，已加载输出目录内 0.7.0 并重新配对；扩展连接页显示已连接，桌面状态显示「浏览器扩展已连接」，本机实时连接已建立。原先失效的配对已保留加密备份；截图、GIF、设置文件的升级前后哈希保持一致。
- DeepSeek 当前停留在登录页，实际账号附件上传未执行，未上传私人图片或发送聊天。登录后仍须以真实附件显示验收，不将模拟测试当作实际账号上传成功。
- 0.10.0 Release 构建零警告、零错误，运行中的旧版通过自身托盘菜单保存退出，已更新并启动新版。记录位于 .build/feature-expansion/ 与 .build/install-feature-expansion.json。


## 0.10.1 原生复制粘贴（2026-10-27）

- 桌面截图、编辑图片、历史多图、OCR 文字与总结已统一改用 BrowserPasteService。桌面不再启动本地扩展连接服务，设置不再提供配对入口；新的分发包不再包含浏览器扩展。
- 原生剪贴板与浏览器兼容性 22 项检查通过：真实 Chromium 从 Windows 剪贴板读到两张有效 PNG，签名、像素与顺序正确；文字可读取并追加；目标地址校验、前台保护、剪贴板变化、取消、未命名登录输入框保护通过。
- 上述兼容性检查使用独立本地页面与合成图片。测试浏览器的调试接口仅用于读取剪贴板验证，不用于产品发送流程；该结果不能算作系统 Ctrl+V 或 AI 网站上传的完整通过。
- 本机当前前台程序阻止测试进程自动激活浏览器，完整原生 Ctrl+V 测试因此停在保护分支。程序保留剪贴板并提示手动粘贴，没有绕过前台限制。
- 实际 DeepSeek 页面 2 项检查通过：不能自动继续时给出 Ctrl+V 提示，合成图片保留 Bitmap 与 PNG 两种格式。未确认已登录账号的附件上传成功，未发送聊天或上传私人截图。
- 界面与截图功能 84 项检查通过；截图、OCR 修改、历史恢复、迁移与删除 97 项检查通过。历史检查需在可访问桌面的会话中运行，受限会话无法创建屏幕选择框。
- Release 构建零警告、零错误。测试记录位于 .build/native-paste-final/ 与 .build/native-paste-regression/，安装记录位于 .build/install-native-paste.json。


## 0.10.2 简化提醒与工具栏（2026-10-27）

- 去掉发送截图时的「截图已保留」进度／结果提醒窗口。截图、编辑区及历史统一使用原生复制粘贴流程，结果只更新现有状态栏。
- 去掉截图时的操作说明框、关闭主窗口时的托盘气泡提醒，以及重复启动时的「已经运行」提示框。
- 主窗口与截图浮动栏均移除「总结」，同时清理对应提示词与处理逻辑。取消、编辑截屏、复制截屏仍按单行顺序排列；GIF 和滚动截图枚举编号保持原值。
- 截图动作验证通过：剩余 9 个按钮、截图边界、100%／200% 坐标、GIF／滚动区域、快捷键和取消行为正常。界面、标注和工具栏布局 75 项检查通过。
- 本次未发送 AI 聊天或重新验证网站附件上传。构建与安装记录位于 .build/quiet-ui/ 和 .build/install-quiet-ui.json。


---

## 0.10.2 安装包裁剪（2026-10-28）

- 从安装包中删掉程序从不加载的内容：除简繁中文外的 11 套 .NET 框架语言资源（cs／de／es／fr／it／ja／ko／pl／pt-BR／ru／tr）、`ocr-runtime` 下各 Python 包的 `tests` 目录、`sympy` 与 `mpmath`、OpenCV 的 `opencv_videoio_ffmpeg*_64.dll` 与 `haarcascade_*.xml`、以及 `*.pyi` 类型存根。裁剪在 `dotnet publish` 之后执行，删除清单写入 `.build/installer/0.10.2/payload-trim.txt`。
- payload 由 4064 个文件／676.5 MB 降到 1539 个文件／592.8 MB；安装包由 268.94 MB（sha256 8BA0E3E6…）降到 252.13 MB（sha256 6447D89B…）。删掉的 83.7 MB 大多是高度可压缩的文本与重复资源，压缩后只省 16.8 MB，说明体积大头是两份 79 MB 的离线翻译权重。
- 静默安装 exit 0；1539/1539 按清单逐文件 SHA-256 一致，缺失、大小不符、哈希不符、多余文件均为 0；includedFrameworks 为 Microsoft.NETCore.App 10.0.14 与 Microsoft.WindowsDesktop.App 10.0.14；静默卸载 exit 0 且目录清理干净。
- 在裁剪后的 payload 上实跑功能回归：`ocr-runtime/python.exe -I -B rapid-ocr-worker.py` 对合成中英文图片识别正确（置信度 0.98，约 2 s），说明去掉 ffmpeg 解码 DLL 与 sympy 后 OpenCV／RapidOCR 仍可用；`translation-worker.py` 中→英、英→中各一次成功（约 0.6 s／0.4 s）。
- 构建脚本改用 `Substring` 计算相对路径，不再依赖只有 PowerShell 7 才提供的 `[IO.Path]::GetRelativePath`；本次整条打包流程在 Windows PowerShell 5.1 上跑通。
- 证据：`.build/installer/0.10.2/build-report.json`、`payload-manifest.json`、`payload-trim.txt`、`compiler.log`、`output/ScreenNote-0.10.2-Setup-x64.exe.sha256`；验证脚本 `.build/verify-installer.ps1` 覆盖安装、逐文件哈希比对与卸载。
