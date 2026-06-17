# 设备 SSH 命令工具

基于 WPF (.NET 8) 的桌面工具，通过 SSH 远程登录调制解调器（或其他 Linux 嵌入式设备），定时循环执行用户自定义的 shell 命令，并解析命令的返回结果，统计正常/异常次数。

适用场景：长时间稳定性测试、定时数据采集、设备状态监测等。

## 核心功能

1. 通过 SSH 协议连接远程设备（用户名/密码认证）
2. 在 SSH 会话内定时循环执行用户自定义的 shell 命令
3. 通过「关键字 + 位置」方式从命令返回结果中解析目标字段
4. 实时统计解析值的正常（绿色）/ 异常（红色）次数
5. 连续 5 次异常自动终止任务
6. 详细日志输出（连接/发送/原始返回/解析过程/判定）
7. 日志同时写入程序目录下的 `SensorPosLog.txt` 文件
8. **任意退出路径**（任务结束/取消/关闭窗口）都会执行收尾的 `bsp redir_off`

## 关键命令说明（bsp redir_on / bsp redir_off）

- `bsp redir_on`：把网口（SSH）的输入输出重定向到设备的本地串口
- `bsp redir_off`：关闭上述重定向，恢复正常的 SSH 控制台交互

本程序流程：

```
bsp redir_off   (确保干净起点)
  → bsp redir_on (重定向到串口外设)
  → 循环执行用户自定义命令（如：bsp cmd st sensor_pos）
  → bsp redir_off (收尾，恢复正常 SSH 控制台)
```

## 前置条件

1. PC 能 ping 通目标设备
2. 目标设备已开启 SSH 服务（默认 22 端口）
3. 已知 SSH 登录的用户名和密码（默认 `root` / `andisat`，可在代码中修改）
4. .NET 8 桌面运行时

## 使用说明

1. 启动程序后，界面分三部分：
   - **运行参数**：间隔时间（秒）、总测试时间（分钟）、调制解调器 IP
   - **串口命令与解析**：SSH 命令、关键字、取第几段
   - **操作日志** + **解析结果**：左侧日志、右侧计数+列表
2. 点击「下发」开始任务，按钮变为「取消」
3. 任务运行期间可随时点击「取消」停止任务
4. 任务结束情况：
   - 总时间到 → 正常结束
   - 手动取消 → 弹出取消提示
   - 连续 5 次异常 → 自动终止
   - 关闭窗口 → 同步执行收尾
   任何情况下程序都会自动执行收尾的 `bsp redir_off` 还原设备状态

## 解析逻辑说明

```
1. 把命令的原始返回按行拆分
2. 找到第一行包含「关键字」的行
3. 把该行按空格/Tab 拆分
4. 取第「位置」段作为解析值
5. 解析失败 或 解析值 == "0.00" 判定为异常，否则正常
```

**示例**：

```
原始返回：
  current pos = 12.34.
关键字 = "current pos"   位置 = 4
→ 解析值 = 12.34   → 正常
```

```
原始返回：(空)
→ 解析失败   → 异常
```

## 项目结构

```
device_uart/
├─ MainWindow.xaml        # UI 布局
├─ MainWindow.xaml.cs     # SSH 主逻辑、解析、日志
├─ App.xaml / App.xaml.cs # 应用入口
├─ AssemblyInfo.cs
├─ chengkong.csproj       # .NET 8 WPF 项目
├─ chengkong.sln
├─ README.md              # 本文件
├─ .gitignore
├─ docs/
│  └─ design/
│     └─ 2026-06-17-device-ssh-tool-design.md
└─ Properties/
   └─ PublishProfiles/
```

## 技术栈

- .NET 8 / WPF
- SSH.NET (Renci.SshNet 2025.1.0)

## 注意事项

- SSH 凭据当前硬编码在代码中（`root` / `andisat`），如需改动请修改 `MainWindow.xaml.cs` 的 `Send()` 方法中的 `sshUsername` / `sshPassword` 变量
- 程序只在 Windows 上运行（WPF 应用）
- 长任务期间请勿关闭 SSH 客户端或目标设备
- 如需调试解析问题，查看「操作日志」中的"原始返回"和"解析过程"段落
