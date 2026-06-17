# 设备 SSH 命令工具 — 设计文档

## 概述

基于原"程控电源"项目改造，删除程控电源（PDU）控制及浏览器自动化功能，保留 SSH 远程操作核心能力，新增用户自定义命令和返回解析功能，优化 UI 界面。

## 目标与非目标

### 目标
- 删除程控电源 TCP 控制、电源口通断操作
- 删除浏览器自动化（ChromeDriver 修改 PDU 监控 IP）
- 保留下发间隔时间、总测试时间、SSH 远程操作
- SSH 命令字段用户可自定义（替代硬编码 `bsp cmd st sensor_pos`）
- 命令返回字段通过"关键字 + 位置"方式用户自定义解析
- 优化 UI 布局为双行分组

### 非目标
- 不改变 SSH 连接方式（仍使用 Renci.SshNet）
- 不改变日志显示机制（左侧 TextBox + 右侧 ListView）
- 不改变程序整体架构（WPF .NET 8）

## 架构

单窗口 WPF 应用，主流程：

```
用户点击 [下发]
  → SSH 连接到调制解调器（一次连接）
  → 发送 bsp redir_off
  → 发送 bsp redir_on
  → 循环（直到总时间到 / 手动取消 / 连续5次异常）：
      发送用户自定义的 SSH 命令
      等待5秒读取返回
      按「关键字 + 位置」解析结果
      判断正常/异常，更新计数
      等待「间隔时间」
  → 如果循环正常结束 → 发送 bsp redir_off
  → 断开 SSH
```

## 设计细节

### UI 布局

双行分组布局（RowDefinition 2行 + 内容区 RowDefinition）：

```
Row 0: [间隔: _分钟] [总时间: _分钟] [调制解调器IP: __________________]
Row 1: [SSH命令: ___________________________________] [关键字: _____] [位置:▼] [下发]
Row 2: 左侧日志 TextBox | 右侧计数 TextBlock + ListView
```

### 新增 UI 控件

| 控件名 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| SshCommand | TextBox | `bsp cmd st sensor_pos` | 用户自定义的 SSH 命令 |
| Keyword | TextBox | `current pos` | 用于筛选返回行的关键字 |
| FieldPosition | ComboBox (1~10) | 2 | 取筛选行按空格分割后的第 N 段 |

### 删除内容

| 模块 | 具体删除项 |
|------|-----------|
| 浏览器自动化 | `ExecuteBrowserAutomation` 方法、`using OpenQA.Selenium.*` |
| TCP 服务器 | `StartTcpServerAsync`、`HandlePDUConnection`、字段 `_pduClient`/`_stream`/`_reader`/`_writer`/`_isConnected`/`PORT` |
| PDU 命令 | `SendPDUCommands`、`SendPDUCommandsSync` |
| UI 控件 | 电源口 ComboBox、空行 Row、旧窗口标题 |
| 未使用代码 | `PingAndExecuteCmd`、`ExecuteCmdCommand` |

### 返回解析逻辑

```
ParseKeywordField(response, keyword, position):
  → 按行拆分
  → 找包含 keyword 的行（第一个匹配行）
  → 按空格分割，取第 position 段
  → 返回该段文本
```

### 异常处理

- 连续 5 次解析结果为"异常" → 终止整个任务，不执行 `bsp redir_off`
- 手动取消 → 终止任务，执行 `bsp redir_off`
- 总时间到 → 正常终止，执行 `bsp redir_off`

## 实施计划

1. 修改 `MainWindow.xaml`：调整 Grid 布局，删除旧控件，添加新控件
2. 修改 `MainWindow.xaml.cs`：删除 PDU/浏览器/TCP 代码，改造 SSH 流程，添加通用解析器
3. 编译验证
4. 功能验证

## 开放问题

无。

## 成功标准

- 程序启动无报错，不需要任何外部设备（PDU、浏览器驱动）即可使用
- 用户可在 UI 上自定义 SSH 命令和解析关键字
- SSH 流程按设计执行：redir_off → redir_on → 自定义命令循环 → redir_off
- 连续5次异常正确终止任务