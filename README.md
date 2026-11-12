# ASUS TX GAMING MOUSE 控制台

[![build](https://github.com/tbaon306-dot/asus-tx-gaming-mouse-console/actions/workflows/build.yml/badge.svg)](https://github.com/tbaon306-dot/asus-tx-gaming-mouse-console/actions/workflows/build.yml)

一个 85 KB 的原生 Windows 小工具：**不用启动奥创中心（Armoury Crate）**，直接控制华硕 **TX GAMING MOUSE（P310，`VID_0B05&PID_1A8D`）** 的 DPI、按键映射和板载配置档。

> 本项目与华硕（ASUS）无关，不含华硕的任何二进制文件或美术资源。
> 程序运行时需要从**你自己已安装的奥创中心**里取 `ArmouryMsSDK.dll`（见[准备 SDK](#准备-sdk)）。

---

## 为什么会有这个项目

奥创中心为了改一个 DPI，要拉起一堆 SYSTEM 服务、常驻数个进程、附带监控与商店。而鼠标控制本身只需要几个 HID 指令。

这个项目只做一件事：**把鼠标控制台从奥创里摘出来**，做成一个能双击就用、关掉就干净的窗口程序。

| | 奥创中心 | 本项目 |
|---|---|---|
| 体积 | 数 GB | 85 KB（+ 运行时读取的 SDK） |
| 进程 | 服务 + 托盘 + 框架，常年常驻 | 一个窗口进程，关掉即退出 |
| 权限 | 安装/更新需要管理员 | 免安装、免管理员 |
| 联网 | 有 | 无（代码中没有任何网络调用） |

## 功能

- **概览** — 型号、固件、接收器版本、当前配置档与 DPI；鼠标休眠时给出提示并提供「重新读取」
- **性能** — 4 档 DPI（100–12000，步进 100）、轮询率、角度捕捉，可写回设备并可保存到板载
- **按键** — 8 个物理键独立重映射：鼠标功能 / 键盘组合（带录制）/ 多媒体键
- **配置档** — 切换、恢复出厂
- **日志** — 所有 SDK 调用与结果落盘，便于排查

## 它是怎么工作的

奥创自带一个 Native SDK：`ArmouryMsSDK.dll`，导出 `startService` / `ExecuteFunction` / `freeBuffer`。所有操作都是一段 XML 进、一段 XML 出。协议细节见 [docs/PROTOCOL.md](docs/PROTOCOL.md)。

**关键坑：这个 SDK 与 Win32 消息泵不共存。** 只要进程里有消息循环（哪怕是一个隐藏的 WinForms 窗口），`ExecuteFunction` 就有很大概率触发 `0xC0000409` 静默崩溃，进程直接消失、不留堆栈。控制台程序则从不崩溃——同一时刻交替测试，控制台 6/6 成功，GUI 3/6 崩溃。对比数据见 [docs/FINDINGS.md](docs/FINDINGS.md#1-sdk-与消息泵不共存)。

所以架构是：

```
┌────────────────────────┐        stdin/stdout        ┌──────────────────────────┐
│  界面进程（WinForms）   │  ──  base64(XML)  ──▶      │  子进程 --host            │
│  有消息泵，绝不加载 DLL │  ◀──  base64(结果)  ──      │  无窗口、无消息泵，加载 DLL│
└────────────────────────┘                            └──────────────────────────┘
```

界面进程**从不** `LoadLibrary`；所有 SDK 调用转交 `--host` 子进程。子进程崩了就地重启并重试一次，另有 25 秒看门狗兜底。

> 这套隔离把冷启动崩溃率从约 30% 降到 **0/10**。

## 准备 SDK

程序需要 `ArmouryMsSDK.dll` 及其同目录的 `config.ini`。它随奥创中心的 ArmouryDevice 组件安装，通常在这里：

```
C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK
```

程序会按以下顺序自动查找：

1. 环境变量 `MOUSECTL_SDK_DIR`
2. `<程序所在目录>\MouseSDK`
3. `<程序所在目录>`
4. `%LOCALAPPDATA%\ASUSMouseConsole\MouseSDK`
5. `C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK`

**所以：只要这台机器装过奥创（哪怕服务全停），通常不需要做任何准备。**

想把 SDK 拷到别处随身带：

```powershell
./tools/fetch-sdk.ps1 -Destination .\MouseSDK
```

> 仓库和 Release 都不分发 SDK —— 那是华硕的专有文件，再分发不合适。

## 使用

```powershell
./tools/build.ps1                      # 编译到 build/
./build/ASUSMouseConsole.exe           # 运行
```

> 如果提示"禁止运行脚本"，用这条命令绕过（不改系统设置）：
> ```powershell
> powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1
> ```

也可以构建安装包（**会把你本机的 SDK 打进去，仅供自己或朋友使用，不要公开发布**）：

```powershell
./tools/package.ps1                    # 输出 dist/ASUSMouseConsole-Setup.exe
```

### 命令行

| 参数 | 作用 |
|---|---|
| `--selftest` | 自检：读设备 → 原值写回 → 回读，结果写入日志 |
| `--switch N` | 切换到第 N 个配置档 |
| `--tab N` | 启动时直接停在第 N 个页签 |
| `--probe [1..5]` | 崩溃复现实验（见 docs/FINDINGS.md） |
| `--host` | 内部使用：SDK 宿主子进程 |

## 已知限制（都是实测结论）

- **轮询率改不动**：设备在 2.4G 下恒定约 125 Hz（实测 656 帧 / 7.000 s，间隔中位数 8.00 ms）。奥创自己也改不动、写入也"成功"。这是设备固件行为。
- **鼠标空闲几分钟会休眠**：此时 SDK 返回 `固件=0`、`DPI=100` 之类的假数据。程序会识别并提示你动一下鼠标。
- **保存到板载需要奥创不在运行**：`asus_framework` 在跑时，保存指令返回成功但不会落盘。双击 `tools/stop-ac.cmd` 即可（会自己请求管理员权限，跑完会恢复成普通权限）。
- 只支持 **P310（PID 6797）** 这一款。其他型号理论上可加，但需要对应型号的协议差异。
- 写入返回 `1` 只代表指令被接受，不代表已经生效——本仓库对"物理按键真的变了吗"验证不足，见 [docs/FINDINGS.md](docs/FINDINGS.md)。

## 目录结构

```
src/MouseCenter.cs      主程序（单文件，约 1800 行）
src/Setup.cs            安装器源码（自解压单文件）
src/SetupInfo.cs        安装器版本信息
tools/build.ps1         编译主程序
tools/package.ps1       打包安装程序
tools/fetch-sdk.ps1     从本机奥创取 SDK
tools/stop-ac.ps1       停掉奥创服务与进程（管理员）
tools/start-ac.ps1      恢复奥创服务（管理员）
docs/PROTOCOL.md        SDK 的 XML 协议
docs/FINDINGS.md        逆向与实测记录
assets/app.ico          图标（自绘）
```

## 构建环境

- Windows + .NET Framework 4.x（`csc.exe`，系统自带）
- 主程序 **必须编译为 x86**（SDK 是 32 位的）
- 无需 Visual Studio、无需 NuGet

## 免责声明

改键、改 DPI 都是会写进鼠标存储的操作。本项目按 MIT 协议"按原样"提供，不提供任何担保。使用前请确认你清楚自己在改什么，出事（包括把左键映射没了导致鼠标点不动）请自行承担。

按键页在写入前会检查"是否有任何键提供左键"，没有会拦截一次。

## License

[MIT](LICENSE)