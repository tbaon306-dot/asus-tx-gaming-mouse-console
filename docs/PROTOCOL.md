# ArmouryMsSDK 协议笔记

这份文档记录的是**实测**出来的调用约定，不是华硕的官方文档（没有官方文档）。

---

## 入口

`ArmouryMsSDK.dll` 导出三个函数：

```c
int  __stdcall startService(const wchar_t* config);   // 传 NULL 即可
int  __stdcall ExecuteFunction(const wchar_t* xmlIn, wchar_t** xmlOut);
int  __stdcall freeBuffer(wchar_t** buffer);          // 每个返回的 xmlOut 都要还回去
```

- 参数与返回都是 **UTF-16**，`xmlOut` 由 SDK 分配，用完必须 `freeBuffer`，否则泄漏。
- `ExecuteFunction` 返回 `1` 表示**指令被接受**，不是"已生效"。返回 `0` 表示失败（常见于设备未连接/休眠）。
- **非线程安全**，所有调用必须串行（本项目用一个 STA 工作线程串起来）。

## 启动顺序

```
LoadLibrary("ArmouryMsSDK.dll")      // 工作目录必须是 SDK 目录，它会读同目录的 config.ini
  ↓
startService(NULL)                   // 异步，返回后要等约 400ms 才真正就绪
  ↓
ExecuteFunction(function 10)         // 枚举设备，必须做
  ↓
正常的读写
```

跳过枚举直接写，设备不会理会。枚举的响应里带 `<pid>6797</pid>`，可以据此判断鼠标在不在。

## 报文外壳

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<root>
  <device_type key="1">
    <device key="6797">
      <function key="..."> ... </function>
    </device>
  </device_type>
</root>
```

`device key` 是 PID 的十进制。枚举时用 `key="-1"` 表示"任一设备"。

---

## 读：function 6

```xml
<function key="6">
  <data_number>0</data_number>
  <file_name>-1</file_name>
  <profile_index>-1</profile_index>
</function>
```

| `data_number` | 内容 |
|---|---|
| `0` | 设备信息（`profile_index` 用 -1） |
| `2` | 指定配置档的性能参数（`profile_index` = 1..3） |
| `1` | 按键映射（27 个固定槽位） |

`data_number 0` 返回的字段：`version`（固件）、`rf_version`（接收器）、`profile`（当前档）、`profile_number`（档位总数）、`currentdpi`（当前 DPI **索引**）、`currentdpichannel`（当前通道 1–4）、`usb_version`、`type`、`stick_mode`、`booster_version`。

> **注意**：这里没有鼠标型号名。型号是拿 PID 硬映射出来的。

`data_number 2` 返回：`dpi_1..dpi_4`（都是**索引**）、`polling_rate`、`angle_snapping`。

---

## 写性能：function 1

```xml
<function key="1">
  <profile_index>3</profile_index>
  <dpi_number>4</dpi_number>
  <accelerate>-1</accelerate>
  <decelerate>-1</decelerate>
  <angle_snapping>0</angle_snapping>
  <angle_tuning>-1</angle_tuning>
  <button_response>-1</button_response>
  <polling_rate>3</polling_rate>
  <dpi_channel>2</dpi_channel>
  <dpi_1>5</dpi_1>
  <dpi_2>6</dpi_2>
  <dpi_3>8</dpi_3>
  <dpi_4>12</dpi_4>
</function>
```

- **DPI 是索引，不是值**：`索引 = (DPI - 100) / 100`，反过来 `DPI = 100 + 索引 × 100`。范围 100–12000，步进 100。
- **`dpi_channel` 必须带**。少了它，设备会把"当前活动通道"重置为 1——你会发现自己从 900 DPI 莫名其妙变成 600。
- `-1` 表示"不改这个字段"。
- `polling_rate` 取值 `0..3` → 125 / 250 / 500 / 1000 Hz。**但设备在 2.4G 下其实恒定 125 Hz**，见 [FINDINGS](FINDINGS.md#2-轮询率是假的)。

## 写按键：function 0

```xml
<function key="0">
  <profile_index>3</profile_index>
  <button_count_1 key="1">
    <button_function>1</button_function>
    <target_key_1>1280</target_key_1>
    <target_key_2>1281</target_key_2>
    <target_key_3>-1</target_key_3>
    <source_key>273</source_key>
  </button_count_1>
  ...
  <button_number>8</button_number>
</function>
```

- **必须带 `source_key`**（物理键码），否则设备不知道这个块对应哪个键。
- 要么全写、要么不写：8 个键一次性下发，不能只发一个。
- `button_function`：`0`=鼠标功能、`1`=键盘组合、`3`=多媒体、`4`=宏、`9`=无功能、`10`=连发。
- 键码编码是 `列 × 256 + 行`，来自一张 28 行的键位网格（见 `src/MouseCenter.cs` 的 `Keys2.Grid`）。

## 配置档：function 5

```xml
<function key="5">
  <profile_function>3</profile_function>
  <profile_parameter>3</profile_parameter>
</function>
```

| `profile_function` | 含义 |
|---|---|
| `0` | 全部配置档恢复出厂 |
| `1` | 当前配置档恢复出厂 |
| `3` | **保存到板载**（`profile_parameter` = 目标档） |
| `4` | **切换**到 `profile_parameter` 指定的档 |

`profile_function=3` 只有在 `asus_framework` 不运行时才真正落盘。

---

## 坑

1. **进程里有消息泵就会崩** —— 最重要的一条，见 [FINDINGS](FINDINGS.md#1-sdk-与消息泵不共存)。宿主进程必须无窗口、无消息循环。
2. **每次写之前都要重新 `function 10`**，状态不是持久的。
3. **读回来的按键不是真相**：`data_number 1` 的 27 个槽位顺序和写入顺序不一致，而且这份数据来自主机侧缓存，写进去的东西不会立刻回显。本项目因此把按键状态存在自己的文件里。
4. **`startService` 之后要等**，立刻发指令会在原生层 AV。
5. **`freeBuffer` 里传的是指针的地址**（`wchar_t**`），不是指针本身。