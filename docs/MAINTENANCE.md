# 维护手册：哪里容易失效，失效了怎么修

Double Ctrl 依赖一批不受我们控制的东西：有道的非公开接口、Windows 行为、各软件的剪贴板习惯。
本文记录每一处的依赖点、失效症状和修复路径。

## 先定位问题

1. 跑接口自检：

   ```bash
   python tools/check_api.py
   ```

2. 退出程序后用诊断模式启动，复现问题：

   ```bash
   bin/DoubleCtrl.exe --debug
   ```

   日志在 `%APPDATA%\DoubleCtrl\debug.log`。**日志包含你选中的文字**，排查完请删除，分享前先检查。

| 现象 | 看什么 | 跳到 |
| --- | --- | --- |
| 显示“翻译失败” | 日志 `query failed` | [接口失效](#接口失效) |
| 显示“没有找到释义” | 日志 `empty result, raw:` | [接口失效](#接口失效) |
| 双击 Ctrl 没反应 | 日志没有 `target process` | [按键监听](#按键监听) |
| 窗口弹了但输入框是空的 | 日志 `tier=` 那行 | [取词](#取词) |
| 剪贴板内容被改 | 日志 `tier=clipboard`、`late copy` | [剪贴板](#剪贴板) |
| Win+V 多出条目 | 日志 `history` | [Win+V 历史](#win-v-历史) |
| 没有声音 | 日志 `voice:`、`mci error` | [发音](#发音) |
| 界面异常或功能突然失灵 | `%APPDATA%\DoubleCtrl\crash.log` | 按里面的异常堆栈定位，它不需要 `--debug` |

## 接口失效

这是最可能出问题的地方。接口是有道的非公开接口，没有任何稳定性承诺。

### 当前依赖

| 用途 | 接口 | 代码 |
| --- | --- | --- |
| 词典 + 句子翻译 | `POST https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4` | `src/DictClient.cs` 的 `ApiUrl` |
| 发音 | `GET https://dict.youdao.com/dictvoice?audio=<speech>` | `src/DictClient.cs` 的 `VoiceUrl` |

请求表单：`q`（查询文本）、`le`（语言）、`t=2`、`client=web`、`keyfrom=webdict`、`dicts`（裁剪返回内容）。
**目前不需要登录，也不需要签名。**

响应字段和解析函数的对应关系：

| 字段 | 内容 | 解析函数 |
| --- | --- | --- |
| `ec` | 英译中：音标、词性释义、词形变化 | `ParseEc` |
| `ce` | 中译英：`trs[].#text` 是目标词 | `ParseCe` |
| `fc` `cf` `kc` `ck` `multle` | 法、韩及其他语言，结构是 `trs[].tr[].l.i` | `ParseGeneric` |
| `newjc` `newcj` | 日语，结构是 `sense[].phrList[].jmsy` | `ParseJapanese` |
| `simple` | 拼音和发音参数 | `Parse` |
| `web_trans` | 网络释义 | `Parse` |
| `fanyi` | 句子翻译，`tran` 是译文 | `Parse` |

### 当初怎么找到这些接口的

1. 在本机有道词典的安装目录上运行 `tools/find_endpoints.py`，列出它用到的 URL，得到 `jsonapi_s`、`fsearch`、`suggest` 等地址。
2. 在安装目录的 `resultui/html/js/*.js` 里搜 `jsonapi_s`，看到 `dicts` 参数的写法。
3. 用 curl 直接调用，确认不登录也能正常返回。

### 接口失效时的修复流程

**第一步：判断是哪种失效。**

| `check_api.py` 的结果 | 含义 | 修法 |
| --- | --- | --- |
| 全部 HTTP 404 | 地址变了 | 第二步找新地址，改 `ApiUrl` |
| HTTP 403，或响应要求验证 | 开始校验 UA、来源或签名 | 第三步 |
| 个别“缺少字段” | 响应结构变了 | 第四步 |
| 全部超时或连接失败 | 多半是本机网络或代理 | 不走代理再试 |

**第二步：重新找地址。** 最快的办法是打开网页版 <https://dict.youdao.com>，在浏览器开发者工具的 Network 面板里查一个词，
看实际请求的地址和参数。也可以把有道更新到最新版后运行：

```bash
python tools/find_endpoints.py "<有道安装目录>"
```

**第三步：如果开始要求额外参数。** 同样在 Network 面板里对比网页版的请求多了哪些参数。
如果不方便继续使用，就换下面的备选接口。

**第四步：响应结构变了。** 打印原始响应：

```bash
curl -s "https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4" --data "q=translate&le=en&t=2&client=web&keyfrom=webdict"
```

对照上面的字段表改解析函数，同时更新 `tools/check_api.py` 的 `CASES`。

### 备选接口

主接口彻底不可用时可以试这些，上次验证时间是 2026-11：

| 接口 | 状态 |
| --- | --- |
| `GET https://dict.youdao.com/fsearch?client=deskdict&keyfrom=deskdict.main&q=<词>&pos=-1&doctype=xml&xmlVersion=3.2&vendor=unknown` | 可用，无需签名。返回 XML，有音标和释义，只查词不翻译句子 |
| `GET https://dict.youdao.com/suggest?type=DESKDICT&num=3&q=<词>&ver=2.0&doctype=json` | 可用，无需签名。返回简短释义，可作最低限度的兜底 |
| `https://fanyi.youdao.com/translate?&doctype=json&type=AUTO&i=<文本>` | **已失效**，实测返回空 |

### `le` 必须和文本语种匹配

`le=en` 查日文句子会返回 `★★★★` 这样的乱码。`DictClient.PickLe` 按字符判断假名、谚文、西里尔字母，
优先于用户选择的语言。如果又出现某语言返回乱码，先看这里。

## 取词

取词分三层，顺序见 `src/SelectionReader.cs` 的 `Read`：

1. **Edit 直读**：`EM_GETSEL` + `WM_GETTEXT`，覆盖资源管理器重命名框、地址栏、各种对话框。
2. **UI Automation**：`TextPattern.GetSelection`，覆盖 WPF、UWP、Windows Terminal 等。
3. **模拟复制**：浏览器、Electron 等，见下一节。

日志里的 `target process=… fg=… focus=…` 和 `tier=` 是排查的关键：

- **某程序取不到词**：记下它的进程名和 `focus` 类名。类名是自定义的 Edit 变体时，加到 `IsEditClass`。
- **终端被 Ctrl+C 中断**：把进程名或窗口类加到 `TerminalProcesses` / `TerminalClasses`，
  这样会改用 Ctrl+Insert。没有选区时 Ctrl+C 在终端里是中断信号，这是最危险的一类误操作。
- **Chromium 系的 UIA 被跳过**：判断依据是焦点类名以 `Chrome_` 开头。主动请求会让目标程序开启无障碍树，
  又慢又影响性能。如果以后想换成 UIA 读取，改这里。
- **VS Code 没选中却翻译了整行**：靠剪贴板里的 `isFromEmptySelection` 判断（`IsVsCodeEmptySelection`）。
  这是 VS Code 的内部字段，改名就会失效。用 `--debug` 看 `tier=clipboard` 的长度，
  再用剪贴板查看工具看 `Chromium Web Custom MIME Data Format` 的内容。
- **JetBrains IDE**：没有选区时同样复制整行，但没有可靠的区分办法，属于已知限制。
- **管理员权限窗口**：普通权限进程发不了按键，也读不了内容（UIPI）。这是系统限制，只能以管理员运行本程序。

## 剪贴板

`ClipboardSnapshot` 保存全部格式后再模拟复制，读完恢复。有几个历史坑：

- **目标程序写入很慢**：Chromium 冷启动时写剪贴板可能超过 1 秒。曾因此把用户剪贴板永久覆盖。
  现在超时后 `WatchLateCopy` 会继续等 `LateCopyWindowMs`，到了再恢复。
  如果又发现内容没恢复，先把这个值调大。
- **等待期间用户自己复制**：靠 `InputMonitor.UserActivity` 判断，用户有操作就放弃恢复。
- **延迟渲染的大数据**：`Take` 会触发渲染，可能卡顿，上限是 `MaxBytes`。
- **Excel**：Ctrl+C 后单元格的虚线框不会自动消失，目前未处理。

## Win+V 历史

- 恢复时写入 `CanIncludeInClipboardHistory=0`，恢复动作本身不进历史。
- 目标程序写入的那一条由 `src/ClipboardHistory.cs` 调 WinRT 的 `Clipboard.DeleteItemFromHistory` 删除。
- 依赖点：WinRT 必须在 STA 线程调用，否则报 `0x8000001D`。用反射调用是为了让构建不依赖 Windows SDK。
- 历史是异步收录的，所以要重试（`Attempts`、`RetryDelayMs`）。日志出现 `history item not found` 时先调大它们。

## 按键监听

- 用 Raw Input（`src/InputMonitor.cs`），不用 `WH_KEYBOARD_LL`：低级钩子回调超时会被系统静默摘除，
  这是“用着用着就不灵了”的典型原因。不要改回钩子。
- 双击判定阈值：`MaxTapMs`（单次按住上限）、`MaxGapMs`（两次间隔上限）。
- 在**第二次松开**时才触发，这样物理 Ctrl 已抬起，不会和模拟的组合键叠加。

## 窗口

- **弹出时不抢焦点**：选中文字后弹窗不能激活，否则资源管理器的重命名框一失焦就提交退出。不要在 `ShowSelection` 里激活窗口。
- **前台锁**：`ShowForInput` 需要 `AttachThreadInput` 才能抢到前台。抢前台时会有瞬时 `Deactivated`，所以隐藏前要再次确认。
- **透明标题区**：窗口顶部始终预留 `TitleHeight`，隐藏时全透明且可穿透点击。改标题栏高度时，
  `Popup.xaml` 的行高和 `PopupController.TitleHeight` 要一起改。
- **弹出方向**：结果回来前窗口还很矮，所以按 `ExpectedHeight` 预判。结果区最大高度变了要同步更新它。
- **窗口尺寸**：`SizeChanged` 触发时窗口句柄还没改尺寸，`GetWindowRect` 拿到的是旧高度。
  定位必须用 `ActualHeight`，否则内容变高后窗口会超出屏幕。
- **菜单**：`IosMenu` 的阴影空间要由外层容器提供，`Popup` 定位时不计入直接子元素的 `Margin`。
  菜单的关闭靠 `PopupController.OnGlobalMouseDown`，因为托盘菜单打开时程序不在前台。
- **主题**：颜色都在 `src/Theme.cs`，不要写死颜色。切换时用定时器逐帧计算颜色，同时更新两份画刷：
  窗口每帧换一组冻结画刷到资源里；菜单用 `Theme.Get` 拿可变画刷，因为 `Popup` 不在窗口元素树里，收不到资源变更。
  **不要对资源里的画刷用 `BeginAnimation`**：模板和触发器用过后 WPF 会冻结它们，第二次切换主题时程序会直接崩溃。
- **窗口动画**：`Hide` 只是开始收缩，动画结束才真正隐藏，`_closing` 表示收缩中。收缩途中再次弹出时
  `Reveal` 清掉 `_closing`，结束回调就不会把窗口藏起来。判断窗口是否可见时要同时看 `_closing`。
  跟随系统读的是注册表 `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize` 的 `AppsUseLightTheme`，
  系统切换时靠 `SystemEvents.UserPreferenceChanged` 触发。微软改了这个键的话，跟随系统会失效，手动切换不受影响。
- **图标字体**：按钮图标用 `Segoe MDL2 Assets`，Windows 10 起自带。

## 发音

下载 MP3 后用 MCI（`mciSendString`）播放。`mci error` 通常是系统缺解码器，可换 `MediaPlayer`。

## 构建

`build.ps1` 用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，不需要安装 SDK，但只支持 C# 5：
不能用 `$""`、`?.`、`nameof`、`out var` 等语法。

`.ps1` 文件**必须带 UTF-8 BOM**。否则 PowerShell 5.1 按 GBK 读取，中文注释会吞掉换行，下一行代码就消失了。

## 自动化测试注意事项

- 用 `--no-clipboard` 启动，关掉模拟复制这一层。**不要在真实剪贴板上跑自动测试**，内容会进 Win+V 历史。
- 不要用新建的 Edge 配置测试，它会自动登录当前 Windows 账号。
- 测试前先退出有道词典，否则它也会响应操作并写剪贴板，干扰判断。
