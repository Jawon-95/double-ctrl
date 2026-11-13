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
| 输入翻译没弹出或写不进去 | 日志 `input target:`、`compose written=` | [输入翻译](#输入翻译) |
| 没有声音 | 日志 `voice:`、`mci error` | [发音](#发音) |
| 界面异常或功能突然失灵 | `%APPDATA%\DoubleCtrl\crash.log` | 按里面的异常堆栈定位，它不需要 `--debug` |

## 接口失效

这是最可能出问题的地方。接口是有道的非公开接口，没有任何稳定性承诺。

### 当前依赖

| 用途 | 接口 | 代码 |
| --- | --- | --- |
| 词典 + 句子翻译 | `POST https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4` | `src/DictClient.cs` 的 `ApiUrl` |
| 发音 | `GET https://dict.youdao.com/dictvoice?audio=<speech>` | `src/DictClient.cs` 的 `VoiceUrl` |

请求表单：`q`（查询文本）、`le`（语言）、`t`、`sign`、`client=web`、`keyfrom=webdict`、`dicts`（裁剪返回内容）。
**不需要登录，但必须签名。** 不签名时接口照常返回 200，可中文等查询会得到随机词条（比如“主控”查出 zed、footling），
这是反爬手段，不会报错。签名算法取自网页版 dict.youdao.com 的前端代码（`src/DictClient.cs` 的 `QueryAsync`）：

```
t    = (q + "webdict").length % 10          // 按 UTF-16 长度
sign = md5("web" + q + t + "Mk6hqtUp33DGGtoS63tTJbMUYjRrG1Lu" + md5(q + "webdict"))
```

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
| “签名”项失败，中文查出随机词条 | 签名常量或算法变了 | 第三步，在网页版 JS 里搜 `sign` 找新算法 |
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
curl -s "https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4" --data "q=translate&le=en&t=6&sign=<按上面算法计算>&client=web&keyfrom=webdict"
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

## 输入翻译

没有选中文字时，`App.Capture` 调 `InputTarget.Detect` 判断焦点是不是可编辑的输入框，是就进入输入翻译
（`PopupController.ShowCompose`），否则还是普通的输入窗口。日志里看 `input target:` 那行。

- **判定**：标准 Edit 看窗口样式（排除 `ES_PASSWORD`、`ES_READONLY`）；其他控件用 UIA 的焦点元素，
  有 ValuePattern 看 `IsReadOnly`，没有就要求 `ControlType.Edit`。都拿不到时，有系统文本光标也算。
  终端（`SelectionReader.IsConsole`）一律不算，粘贴进去的换行会直接执行命令。
- **Chromium 的 UIA**：取词时为了性能跳过了 Chromium 的 UIA，这里没有跳过，因为只有它能拿到网页输入框的位置。
  只在没有选中文字时才请求一次。
- **写入方式**，按顺序选（`InputTarget.Write`）：
  1. Edit：`EM_SETSEL` + `EM_REPLACESEL`，不切焦点。记住上次写入的位置和内容，用户改过原输入框就按内容重新找。
  2. ValuePattern：只用于 `NativeFrameworks` 里的框架（WPF、WinForms、UWP 等）。**网页不要用**：
     直接改 DOM 绕过了网页编辑器的状态，界面上变了，发出去的还是旧内容。写完读回确认，没变就改用粘贴。
  3. 粘贴（浏览器、Electron）：切回目标窗口，选中上次的译文，再 `Ctrl+V`，最后抢回前台。正常 30～90ms。
     - **按内容定位**：UIA 的 TextPattern/ValuePattern 只用来读内容和选中文字（不用来写）。先读原输入框内容，
       上次的译文还在就用 `TextPattern.FindText` 在光标前找到它，`Select()` 选中并等选区生效；
       已经不在了（用户删改过）就在光标处插入。读不到内容的控件才退回按字数 `Shift+←`。
       ProseMirror（Claude 的输入框）会把 UIA 选区当成自己的选区事务，粘贴也走它自己的流程，状态和界面一致。
     - **读回确认**：粘贴后轮询内容，新译文出现就说明目标程序读完了剪贴板，马上恢复剪贴板、抢回前台；
       读不到内容时才固定等 `PasteSettleMs`。内容完全没变时不更新记录，返回失败，避免下次重复插入。
     - **拦截按键**：切走的这几十毫秒里，`KeyBlocker` 临时装低级键盘钩子扣下用户的真实按键，
       弹窗拿回前台后由 `InputTarget.ResumeKeys` 按原顺序补发，否则打的字会落进原输入框，看起来光标在两个框之间跳。
       钩子超过 `MaxBlockMs` 自动放行，出意外也不会锁死键盘。
     - 剪贴板先保存再恢复，临时内容带 `CanIncludeInClipboardHistory=0`，不进 Win+V 历史。
- **输入法拼写中不同步**：`PreviewTextInputStart/Update` 标记拼写状态，拼写中定时器到点也只是顺延，
  否则拼音会被当成原文翻译，切走焦点还会打断候选框。
  用户在打字时，`PopupController.UserBusy` 会推迟同步。
  - **判断依据**：正在拼写；或者 `InputMonitor.KeyPresses` 在本次同步开始后有变化，这个计数用 Raw Input 记录全局真实按下次数，不计抬起。
  - **停顿触发的同步**：`LastKeyTick` 还要至少 `ComposeDelayMs` 没有按键才算停下。
  - **检查点**：同步会检查三次，分别在开始时、查完译文后（查词要几百毫秒），以及写入线程装好 `KeyBlocker` 后、切焦点之前。第三次用 `Dispatcher.BeginInvoke(Background)` 回到界面线程，确保弹窗队列里已有的按键和输入法消息先处理完。
  - **推迟后的处理**：`Write` 返回 null，原输入框不动，重新开始计时。
- **抢焦点期间不能隐藏**：粘贴时窗口会失去前台，`_focusAway` 让 `OnDeactivated` 跳过隐藏。
- **位置**：`PlaceAtTarget` 选上下空间多的一侧（上方够 `ComposePreferredSpace` 就优先上方），只往远离原输入框的方向长高，
  输入框最大高度只算这一侧的空间，超出出现滚动条。往下弹时标题栏区域也放在原输入框下面，悬停时不会盖住它。
  每次写入后 `RefreshTargetBounds` 重新取原输入框的位置，它长高了就跟着让开。
- **译文怎么选**：`PickTranslation` 优先整句译文，单词取第一个结果词或第一条释义的第一项。

## 剪贴板

`ClipboardSnapshot` 保存全部格式后再模拟复制，读完恢复。有几个历史坑：

- **目标程序写入很慢**：Chromium 冷启动时写剪贴板可能超过 1 秒。曾因此把用户剪贴板永久覆盖。
  现在超时后 `WatchLateCopy` 会继续等 `LateCopyWindowMs`，到了再恢复。
  如果又发现内容没恢复，先把这个值调大。
- **浏览器输入框里没选中文字**：Ctrl+C 不写剪贴板，以前要干等 `CopyTimeoutMs`，输入翻译弹出明显比记事本慢。
  现在 Chromium 里复制时同时用 UIA 查焦点输入框（`IsEditWithoutSelection`），确认选区为空就提前结束等待，弹出约 15ms。
  这一步只能缩短等待，不能改变结果：剪贴板一旦变了，仍以剪贴板为准。UIA 冷启动时太慢，就照常等剪贴板。
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
