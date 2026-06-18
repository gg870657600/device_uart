using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace chengkong
{
    // ══════════════════════════════════════════════════════════════
    // 公共数据模型
    // ══════════════════════════════════════════════════════════════
    public class LogEntry
    {
        public string Text { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
    }

    public class ResultEntry
    {
        public string Text { get; set; } = "";
        public bool? IsOk { get; set; }
    }

    public partial class MainWindow : Window
    {
        // ══════════════════════════════════════════════════════════
        // 常量
        // ══════════════════════════════════════════════════════════
        private const int MaxLogLines = 100_000;
        private const int LogFlushIntervalMs = 100;
        private const int TriggerStopCount = 5;

        // ══════════════════════════════════════════════════════════
        // 串口 Tab 字段
        // ══════════════════════════════════════════════════════════
        private SshClient? _serialClient;
        private ShellStream? _serialShell;
        private CancellationTokenSource? _serialCts;
        private bool _serialRunning;
        private int _serialCount = 1;
        private int _serialOkCount;
        private int _serialNotOkCount;

        private readonly ObservableCollection<LogEntry> _serialLogEntries = new();
        private readonly ObservableCollection<ResultEntry> _serialResultEntries = new();
        private readonly ConcurrentQueue<LogEntry> _serialPendingLogs = new();
        private DispatcherTimer? _serialLogFlushTimer;

        // ══════════════════════════════════════════════════════════
        // Telnet Tab 字段
        // ══════════════════════════════════════════════════════════
        private SshClient? _telnetClient;
        private ShellStream? _telnetShell;
        private CancellationTokenSource? _telnetCts;
        private bool _telnetRunning;
        private int _telnetCount = 1;
        private int _telnetOkCount;
        private int _telnetNotOkCount;

        private readonly ObservableCollection<LogEntry> _telnetLogEntries = new();
        private readonly ObservableCollection<ResultEntry> _telnetResultEntries = new();
        private readonly ConcurrentQueue<LogEntry> _telnetPendingLogs = new();
        private DispatcherTimer? _telnetLogFlushTimer;

        // ══════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();

            // 串口 Tab 初始化
            SerialOpLogListView.ItemsSource = _serialLogEntries;
            SerialResultListView.ItemsSource = _serialResultEntries;
            InitLogFlushTimer(isSerial: true);
            AppendLog(isSerial: true, "程序已启动，请在对应 Tab 配置参数后点击 [连接] → [下发]");

            // Telnet Tab 初始化
            TelnetOpLogListView.ItemsSource = _telnetLogEntries;
            TelnetResultListView.ItemsSource = _telnetResultEntries;
            InitLogFlushTimer(isSerial: false);
            AppendLog(isSerial: false, "程序已启动，请在 Telnet Tab 配置参数后点击 [连接] → [下发]");
        }

        // ══════════════════════════════════════════════════════════
        // 公共：日志刷新 Timer
        // ══════════════════════════════════════════════════════════
        private void InitLogFlushTimer(bool isSerial)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LogFlushIntervalMs) };
            timer.Tick += (_, _) =>
            {
                var pending = isSerial ? _serialPendingLogs : _telnetPendingLogs;
                var entries = isSerial ? _serialLogEntries : _telnetLogEntries;
                var listView = isSerial ? SerialOpLogListView : TelnetOpLogListView;

                if (pending.IsEmpty) return;
                Dispatcher.Invoke(() =>
                {
                    int added = 0;
                    while (pending.TryDequeue(out var entry) && added < 2000)
                    {
                        entries.Add(entry);
                        added++;
                    }
                    int overflow = entries.Count - MaxLogLines;
                    if (overflow > 0)
                        for (int i = 0; i < overflow; i++) entries.RemoveAt(0);
                    if (entries.Count > 0)
                        listView.ScrollIntoView(entries[entries.Count - 1]);
                });
            };
            timer.Start();
            if (isSerial) _serialLogFlushTimer = timer;
            else _telnetLogFlushTimer = timer;
        }

        // ══════════════════════════════════════════════════════════
        // 公共：日志写入
        // ══════════════════════════════════════════════════════════
        private void AppendLog(bool isSerial, string text)
        {
            var pending = isSerial ? _serialPendingLogs : _telnetPendingLogs;
            pending.Enqueue(new LogEntry { Text = text });
        }

        private void AppendLogBatch(bool isSerial, IEnumerable<string> lines)
        {
            if (lines == null) return;
            var snapshot = lines.ToList();
            var entries = isSerial ? _serialLogEntries : _telnetLogEntries;
            var listView = isSerial ? SerialOpLogListView : TelnetOpLogListView;

            Dispatcher.InvokeAsync(() =>
            {
                foreach (string line in snapshot)
                    entries.Add(new LogEntry { Text = line });
                int overflow = entries.Count - MaxLogLines;
                if (overflow > 0)
                    for (int i = 0; i < overflow; i++) entries.RemoveAt(0);
                if (entries.Count > 0)
                    listView.ScrollIntoView(entries[entries.Count - 1]);
            }, DispatcherPriority.Background);
        }

        private void AppendResult(bool isSerial, string text, bool? isOk)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var resultEntries = isSerial ? _serialResultEntries : _telnetResultEntries;
                var resultListView = isSerial ? SerialResultListView : TelnetResultListView;
                var entry = new ResultEntry { Text = text, IsOk = isOk };
                resultEntries.Add(entry);
                resultListView.ScrollIntoView(entry);
            }, DispatcherPriority.Background);
        }

        private void ClearResult(bool isSerial)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isSerial) _serialResultEntries.Clear();
                else _telnetResultEntries.Clear();
            });
        }

        private void UpdateCountDisplay(bool isSerial)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isSerial)
                {
                    int total = _serialOkCount + _serialNotOkCount;
                    SerialCountTextBlock.Text = $"OK: {_serialOkCount}, 不OK: {_serialNotOkCount}, 总计: {total}";
                }
                else
                {
                    int total = _telnetOkCount + _telnetNotOkCount;
                    TelnetCountTextBlock.Text = $"OK: {_telnetOkCount}, 不OK: {_telnetNotOkCount}, 总计: {total}";
                }
            }, DispatcherPriority.Background);
        }

        private void SetButtons(bool isSerial, bool isRunning, bool isConnected)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isSerial)
                {
                    SerialSendButton.Content = isRunning ? "停止" : "下发";
                    SerialSendButton.IsEnabled = isConnected;
                    SerialStopButton.IsEnabled = isRunning;
                    SerialConnectBtn.IsEnabled = !isConnected;
                    SerialDisconnectBtn.IsEnabled = isConnected;
                }
                else
                {
                    TelnetSendButton.Content = isRunning ? "停止" : "下发";
                    TelnetSendButton.IsEnabled = isConnected;
                    TelnetStopButton.IsEnabled = isRunning;
                    TelnetConnectBtn.IsEnabled = !isConnected;
                    TelnetDisconnectBtn.IsEnabled = isConnected;
                }
            }, DispatcherPriority.Background);
        }

        private void SetStatus(bool isSerial, string text, string color)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                if (isSerial)
                {
                    SerialStatusText.Text = text;
                    SerialStatusText.Foreground = brush;
                }
                else
                {
                    TelnetStatusText.Text = text;
                    TelnetStatusText.Foreground = brush;
                }
            });
        }

        // ══════════════════════════════════════════════════════════
        // 公共：SSH 连接
        // ══════════════════════════════════════════════════════════
        private async Task<bool> ConnectSshAsync(bool isSerial)
        {
            string ip = isSerial ? SerialIp.Text.Trim() : TelnetIp.Text.Trim();
            string portStr = isSerial ? SerialPort.Text.Trim() : TelnetPort.Text.Trim();
            string user = isSerial ? SerialUser.Text.Trim() : TelnetUser.Text.Trim();
            string pwd = isSerial ? SerialPwd.Password : TelnetPwd.Password;

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user))
            {
                MessageBox.Show("请填写 IP 和用户名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!int.TryParse(portStr, out int port)) port = 22;

            AppendLog(isSerial, $"═══════ [连接] 准备连接 → IP: {ip}:{port}, 用户: {user} ═══════");
            SetStatus(isSerial, "● 连接中...", "#FF999999");

            SshClient? client = null;
            ShellStream? shell = null;

            try
            {
                // 整个 SSH 连接 + Shell 创建 + 初始读取全部在后台线程执行
                var connectTask = Task.Run(() =>
                {
                    SshClient? c = null;
                    ShellStream? s = null;
                    try
                    {
                        var connInfo = new ConnectionInfo(ip, port, user,
                            new PasswordAuthenticationMethod(user, pwd));
                        connInfo.Timeout = TimeSpan.FromSeconds(10);
                        c = new SshClient(connInfo);
                        c.Connect();

                        s = c.CreateShellStream("xterm", 80, 24, 800, 600, 2048);
                        Thread.Sleep(300);
                        ReadShellClean(s, 1000);

                        client = c;
                        shell = s;
                    }
                    catch
                    {
                        // 异常时清理已分配的资源，防止泄漏
                        try { s?.Dispose(); } catch { }
                        try { c?.Dispose(); } catch { }
                        throw;
                    }
                });

                // 20 秒总超时保护（覆盖 TCP 握手 + SSH 协议 + CreateShellStream 信道协商）
                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(20)));

                if (completed != connectTask)
                {
                    AppendLog(isSerial, "[连接] ✗ 超时（20秒），可能网络不通或 IP/端口错误");
                    SetStatus(isSerial, "● 未连接", "#FFD32F2F");
                    return false;
                }

                // 等待 connectTask 中的异常抛出
                await connectTask;

                if (client == null || !client.IsConnected || shell == null)
                {
                    AppendLog(isSerial, "[连接] ✗ 失败");
                    if (client != null && !client.IsConnected) client.Dispose();
                    if (shell != null) shell.Dispose();
                    SetStatus(isSerial, "● 未连接", "#FFD32F2F");
                    return false;
                }

                AppendLog(isSerial, "[连接] ✓ 成功");

                if (isSerial)
                {
                    _serialClient = client;
                    _serialShell = shell;
                }
                else
                {
                    _telnetClient = client;
                    _telnetShell = shell;
                }

                SetStatus(isSerial, "● 已连接", "#FF1B7D3A");
                SetButtons(isSerial, isRunning: false, isConnected: true);
                return true;
            }
            catch (Exception ex)
            {
                // 清理泄漏的资源
                try { shell?.Dispose(); } catch { }
                try { client?.Dispose(); } catch { }

                // 输出完整异常信息（类型 + 消息 + 内部异常 + 堆栈）
                AppendLog(isSerial, $"[连接] ✗ 异常: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    AppendLog(isSerial, $"  └─ 内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                foreach (var line in ex.ToString().Split('\n'))
                    AppendLog(isSerial, $"  {line.TrimEnd('\r')}");

                SetStatus(isSerial, "● 未连接", "#FFD32F2F");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 公共：SSH 断开
        // ══════════════════════════════════════════════════════════
        private void DisconnectSsh(bool isSerial)
        {
            AppendLog(isSerial, "[断开] 开始断开 SSH...");

            // 如果正在跑，先取消
            if (isSerial ? _serialRunning : _telnetRunning)
            {
                if (isSerial) _serialCts?.Cancel(); else _telnetCts?.Cancel();
                // 等待取消信号传播
                try { Thread.Sleep(200); } catch { }
            }

            ShellStream? shell = null;
            SshClient? client = null;
            if (isSerial) { shell = _serialShell; client = _serialClient; }
            else { shell = _telnetShell; client = _telnetClient; }

            try
            {
                if (shell != null) shell.Dispose();
                if (client != null && client.IsConnected) client.Disconnect();
            }
            catch { }

            if (isSerial)
            {
                _serialShell = null;
                _serialClient = null;
            }
            else
            {
                _telnetShell = null;
                _telnetClient = null;
            }

            AppendLog(isSerial, "[断开] SSH 已断开");
            SetStatus(isSerial, "● 未连接", "#FFD32F2F");
            SetButtons(isSerial, isRunning: false, isConnected: false);
        }

        // ══════════════════════════════════════════════════════════
        // 公共：读取 Shell 返回
        // ══════════════════════════════════════════════════════════
        private string ReadShellClean(ShellStream shell, int maxWaitMs)
        {
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            int idleMs = 0;
            const int pollIntervalMs = 30;
            const int idleThresholdMs = 300;

            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                if (shell.Length > 0)
                {
                    idleMs = 0;
                    try
                    {
                        string? line = shell.ReadLine();
                        if (string.IsNullOrEmpty(line)) continue;
                        string trimmed = line.Trim();
                        // 过滤 bsp 命令回显和 shell 提示符
                        if (trimmed.StartsWith("bsp ") || trimmed.Contains("root@andi")) continue;
                        sb.AppendLine(trimmed);
                    }
                    catch { break; }
                }
                else
                {
                    idleMs += pollIntervalMs;
                    if (idleMs >= idleThresholdMs && sb.Length > 0) break;
                    Thread.Sleep(pollIntervalMs);
                }
            }
            return sb.ToString().Trim();
        }

        // ══════════════════════════════════════════════════════════
        // 公共：解析结果
        // ══════════════════════════════════════════════════════════
        private (string value, List<string> trace) ParseResult(string response, string keyword, int fieldPosition)
        {
            var trace = new List<string>();
            if (string.IsNullOrWhiteSpace(response))
            {
                trace.Add("原始返回为空");
                return ("未知", trace);
            }

            var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            trace.Add($"共 {lines.Length} 行");

            int matchIndex = 0;
            foreach (string line in lines)
            {
                string trim = line.Trim();
                matchIndex++;
                if (trim.Contains(keyword))
                {
                    trace.Add($"  ✓ 第 {matchIndex} 行匹配关键字 \"{keyword}\"");
                    trace.Add($"    内容: {trim}");
                    string[] parts = trim.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    trace.Add($"    分割 ({parts.Length} 段): [{string.Join(" | ", parts)}]");
                    if (parts.Length >= fieldPosition)
                    {
                        string value = parts[fieldPosition - 1].TrimEnd('.');
                        trace.Add($"    → 第 {fieldPosition} 段: \"{value}\"");
                        return (value, trace);
                    }
                    else
                    {
                        trace.Add($"    ✗ 段数不足，期望 {fieldPosition}，实际 {parts.Length}");
                        return ("解析失败", trace);
                    }
                }
            }
            trace.Add($"  ✗ 未找到关键字 \"{keyword}\"");
            return ("解析失败", trace);
        }

        // ══════════════════════════════════════════════════════════
        // 公共：写文件日志
        // ══════════════════════════════════════════════════════════
        private void WriteLogToFile(string fileName, int count, string raw, string parsed, string keyword, int pos)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                string content = $"==================================================\r\n" +
                                 $"日志时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                                 $"执行次数：第 {count} 次\r\n" +
                                 $"解析参数：关键字=\"{keyword}\", 位置={pos}\r\n" +
                                 $"解析结果：{parsed}\r\n" +
                                 $"==================================================\r\n" +
                                 $"命令原始返回内容：\r\n{raw}\r\n" +
                                 $"==================================================\r\n\r\n";
                File.AppendAllText(path, content);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════
        // 串口特有：PreLoop（redir_off → redir_on）
        // ══════════════════════════════════════════════════════════
        private void SerialPreLoop(ShellStream shell)
        {
            AppendLog(true, "[前置] 发送: bsp redir_off");
            shell.WriteLine("bsp redir_off");
            Thread.Sleep(500);
            while (shell.Length > 0) shell.Read();

            AppendLog(true, "[前置] 发送: bsp redir_on");
            shell.WriteLine("bsp redir_on");
            Thread.Sleep(500);
            ReadShellClean(shell, 2000);
            AppendLog(true, "[前置] 串口 PreLoop 完成");
        }

        // ══════════════════════════════════════════════════════════
        // 串口特有：PostLoop（redir_off）
        // ══════════════════════════════════════════════════════════
        private void SerialPostLoop(ShellStream shell)
        {
            AppendLog(true, "[收尾] 发送: bsp redir_off");
            shell.WriteLine("bsp redir_off");
            Thread.Sleep(500);
            while (shell.Length > 0) shell.Read();
        }

        // ══════════════════════════════════════════════════════════
        // Telnet 特有：登录 telnet 子会话
        // ══════════════════════════════════════════════════════════
        private bool TelnetLogin(ShellStream shell)
        {
            AppendLog(false, "[Telnet 登录] 发送: telnet 0 2323");
            shell.WriteLine("telnet 0 2323");
            Thread.Sleep(200);  // 固定等待 200ms

            AppendLog(false, "[Telnet 登录] 发送: login:root");
            shell.WriteLine("login:root");
            Thread.Sleep(200);
            shell.WriteLine("Changeme_123");

            // 轮询等待 [root@NE_name] # 提示符，最长 8 秒
            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            while (sw.ElapsedMilliseconds < 8000)
            {
                if (shell.Length > 0)
                {
                    try
                    {
                        string? data = shell.Read();
                        if (!string.IsNullOrEmpty(data)) sb.Append(data);
                    }
                    catch { break; }
                }
                string accumulated = sb.ToString();
                if (accumulated.Contains("[root@") && accumulated.Contains("]#"))
                {
                    AppendLog(false, $"[Telnet 登录] ✓ 成功（耗时 {sw.ElapsedMilliseconds} ms）");
                    // 读掉多余的提示符
                    Thread.Sleep(100);
                    ReadShellClean(shell, 500);
                    return true;
                }
                Thread.Sleep(100);
            }

            AppendLog(false, "[Telnet 登录] ✗ 超时（8s），未检测到 [root@...# 提示符");
            return false;
        }

        // ══════════════════════════════════════════════════════════
        // 公共：单次循环执行
        // ══════════════════════════════════════════════════════════
        private void ExecuteOnce(bool isSerial, ShellStream shell,
            string userCommand, string keyword, int fieldPosition,
            int intervalSec, int currentCount,
            Action<string> logFn, Action<string, bool?> resultFn,
            Action countFn,
            ref int okCount, ref int notOkCount, ref int exceptionCount)
        {
            var localLog = new List<string>(32);
            localLog.Add("");
            localLog.Add($"═══════ 第 {currentCount} 次执行 ═══════");
            localLog.Add($"[发送] 命令: {userCommand}");
            localLog.Add($"[等待] 等 {intervalSec} 秒后读取返回...");
            AppendLogBatch(isSerial, localLog);
            localLog.Clear();

            shell.WriteLine(userCommand);

            int readMaxWaitMs = Math.Max(3000, intervalSec * 1000 * 2);

            try { Task.Delay(intervalSec * 1000).Wait(); }
            catch { }

            string raw = ReadShellClean(shell, readMaxWaitMs);

            localLog.Add($"──── 原始返回 ({raw.Length} 字节) ────");
            if (string.IsNullOrWhiteSpace(raw))
                localLog.Add("  (空)");
            else
                foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    localLog.Add($"  {line}");
            AppendLogBatch(isSerial, localLog);
            localLog.Clear();

            var (parsed, trace) = ParseResult(raw, keyword, fieldPosition);
            localLog.Add($"──── 解析过程 ────");
            localLog.AddRange(trace);
            AppendLogBatch(isSerial, localLog);
            localLog.Clear();

            string fileName = isSerial ? "SensorPosLog.txt" : "TelnetLog.txt";
            WriteLogToFile(fileName, currentCount, raw, parsed, keyword, fieldPosition);

            bool parseFailed = parsed == "解析失败" || parsed == "0.00";
            bool isOk = !parseFailed;
            if (isOk) { okCount++; exceptionCount = 0; }
            else { notOkCount++; exceptionCount++; }

            string statusText = isOk ? "正常" : "异常";
            localLog.Add($"──── 判定 ────");
            localLog.Add($"  解析值: {parsed}");
            localLog.Add($"  结果: {statusText} | OK: {okCount} | 不OK: {notOkCount} | 连续异常: {exceptionCount}");
            AppendLogBatch(isSerial, localLog);

            resultFn($"【第 {currentCount} 次】{keyword} = {parsed} → {statusText}", isOk);
            countFn();
        }

        // ══════════════════════════════════════════════════════════
        // 串口 Tab 事件
        // ══════════════════════════════════════════════════════════
        private async void SerialConnect_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ConnectSshAsync(isSerial: true);
            if (!ok) MessageBox.Show("SSH 连接失败，请检查参数和网络", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void SerialDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectSsh(isSerial: true);
        }

        private void SerialSend_Click(object sender, RoutedEventArgs e)
        {
            if (_serialRunning)
            {
                _serialCts?.Cancel();
                return;
            }

            if (_serialClient == null || !_serialClient.IsConnected || _serialShell == null)
            {
                MessageBox.Show("请先点击 [连接] 建立 SSH 会话", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 读取设置
            if (!int.TryParse(SerialIntervalTime.Text, out int interval) || interval < 1) interval = 60;
            if (!int.TryParse(SerialTotalTime.Text, out int totalMin) || totalMin < 1) totalMin = 2;
            string command = SerialSshCommand.Text.Trim();
            string keyword = SerialKeyword.Text.Trim();
            int pos = SerialFieldPosition.SelectedIndex + 1;

            if (string.IsNullOrEmpty(command)) { MessageBox.Show("请输入命令", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (string.IsNullOrEmpty(keyword)) { MessageBox.Show("请输入关键字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            // 重置计数
            _serialCount = 1;
            _serialOkCount = 0;
            _serialNotOkCount = 0;
            ClearResult(isSerial: true);

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SensorPosLog.txt"), "");

            _serialRunning = true;
            _serialCts = new CancellationTokenSource();
            SetButtons(isSerial: true, isRunning: true, isConnected: true);
            UpdateCountDisplay(isSerial: true);

            AppendLog(true, $"═══════ 任务开始：间隔 {interval}s，总时间 {totalMin} 分钟 ═══════");
            AppendLog(true, $"[参数] 命令: {command}");
            AppendLog(true, $"[参数] 关键字: \"{keyword}\"，位置: 第 {pos} 段");

            // PreLoop
            SerialPreLoop(_serialShell);

            bool stoppedByFailure = false;
            DateTime endTime = DateTime.Now.AddMinutes(totalMin);
            int currentCount = _serialCount;
            int exceptionCount = 0;

            try
            {
                while (DateTime.Now < endTime && !_serialCts.Token.IsCancellationRequested)
                {
                    var loopSw = Stopwatch.StartNew();

                    ExecuteOnce(isSerial: true, _serialShell,
                        command, keyword, pos, interval, currentCount,
                        txt => AppendLog(true, txt),
                        (txt, isOk) => AppendResult(true, txt, isOk),
                        () => UpdateCountDisplay(true),
                        ref _serialOkCount, ref _serialNotOkCount, ref exceptionCount);

                    loopSw.Stop();
                    AppendLog(true, $"  本轮耗时: {loopSw.ElapsedMilliseconds}ms");

                    if (exceptionCount >= TriggerStopCount)
                    {
                        AppendLog(true, $"[终止] ✗ 连续 {TriggerStopCount} 次异常，自动终止！");
                        AppendResult(true, $"连续 {TriggerStopCount} 次异常，任务终止！", false);
                        Dispatcher.Invoke(() => MessageBox.Show($"连续 {TriggerStopCount} 次异常，已自动终止任务！", "任务终止", MessageBoxButton.OK, MessageBoxImage.Warning));
                        stoppedByFailure = true;
                        break;
                    }

                    _serialCount++;
                    currentCount = _serialCount;
                }
            }
            catch (Exception ex)
            {
                AppendLog(true, $"[异常] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _serialRunning = false;

                // 串口：执行 PostLoop（redir_off）
                if (_serialShell != null && _serialClient != null && _serialClient.IsConnected)
                {
                    try { SerialPostLoop(_serialShell); } catch { }
                }

                // 关闭连接
                DisconnectSsh(isSerial: true);

                if (stoppedByFailure)
                    AppendLog(true, "任务因连续异常终止");
                else
                    AppendLog(true, "任务已完成");

                UpdateCountDisplay(isSerial: true);
                SetButtons(isSerial: true, isRunning: false, isConnected: false);
            }
        }

        private void SerialStop_Click(object sender, RoutedEventArgs e)
        {
            _serialCts?.Cancel();
            AppendLog(true, "[用户] 点击停止，任务已取消");
        }

        // ══════════════════════════════════════════════════════════
        // Telnet Tab 事件
        // ══════════════════════════════════════════════════════════
        private async void TelnetConnect_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ConnectSshAsync(isSerial: false);
            if (!ok) MessageBox.Show("SSH 连接失败，请检查参数和网络", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void TelnetDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectSsh(isSerial: false);
        }

        private void TelnetSend_Click(object sender, RoutedEventArgs e)
        {
            if (_telnetRunning)
            {
                _telnetCts?.Cancel();
                return;
            }

            if (_telnetClient == null || !_telnetClient.IsConnected || _telnetShell == null)
            {
                MessageBox.Show("请先点击 [连接] 建立 SSH 会话", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 读取设置
            if (!int.TryParse(TelnetIntervalTime.Text, out int interval) || interval < 1) interval = 60;
            if (!int.TryParse(TelnetTotalTime.Text, out int totalMin) || totalMin < 1) totalMin = 2;
            string command = TelnetSshCommand.Text.Trim();
            string keyword = TelnetKeyword.Text.Trim();
            int pos = TelnetFieldPosition.SelectedIndex + 1;

            if (string.IsNullOrEmpty(command)) { MessageBox.Show("请输入命令", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (string.IsNullOrEmpty(keyword)) { MessageBox.Show("请输入关键字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            // 重置计数
            _telnetCount = 1;
            _telnetOkCount = 0;
            _telnetNotOkCount = 0;
            ClearResult(isSerial: false);

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TelnetLog.txt"), "");

            _telnetRunning = true;
            _telnetCts = new CancellationTokenSource();
            SetButtons(isSerial: false, isRunning: true, isConnected: true);
            UpdateCountDisplay(isSerial: false);

            AppendLog(false, $"═══════ 任务开始：间隔 {interval}s，总时间 {totalMin} 分钟 ═══════");
            AppendLog(false, $"[参数] 命令: {command}");
            AppendLog(false, $"[参数] 关键字: \"{keyword}\"，位置: 第 {pos} 段");

            // Telnet 登录
            bool loginOk = TelnetLogin(_telnetShell);
            if (!loginOk)
            {
                AppendLog(false, "[Telnet 登录] ✗ 失败，任务终止");
                Dispatcher.Invoke(() => MessageBox.Show("Telnet 登录失败（8s 未检测到提示符），请检查设备是否正常", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                _telnetRunning = false;
                DisconnectSsh(isSerial: false);
                SetButtons(isSerial: false, isRunning: false, isConnected: false);
                return;
            }

            bool stoppedByFailure = false;
            DateTime endTime = DateTime.Now.AddMinutes(totalMin);
            int currentCount = _telnetCount;
            int exceptionCount = 0;

            try
            {
                while (DateTime.Now < endTime && !_telnetCts.Token.IsCancellationRequested)
                {
                    var loopSw = Stopwatch.StartNew();

                    ExecuteOnce(isSerial: false, _telnetShell,
                        command, keyword, pos, interval, currentCount,
                        txt => AppendLog(false, txt),
                        (txt, isOk) => AppendResult(false, txt, isOk),
                        () => UpdateCountDisplay(false),
                        ref _telnetOkCount, ref _telnetNotOkCount, ref exceptionCount);

                    loopSw.Stop();
                    AppendLog(false, $"  本轮耗时: {loopSw.ElapsedMilliseconds}ms");

                    if (exceptionCount >= TriggerStopCount)
                    {
                        AppendLog(false, $"[终止] ✗ 连续 {TriggerStopCount} 次异常，自动终止！");
                        AppendResult(false, $"连续 {TriggerStopCount} 次异常，任务终止！", false);
                        Dispatcher.Invoke(() => MessageBox.Show($"连续 {TriggerStopCount} 次异常，已自动终止任务！", "任务终止", MessageBoxButton.OK, MessageBoxImage.Warning));
                        stoppedByFailure = true;
                        break;
                    }

                    _telnetCount++;
                    currentCount = _telnetCount;
                }
            }
            catch (Exception ex)
            {
                AppendLog(false, $"[异常] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _telnetRunning = false;

                // Telnet：直接关闭 SSH（不发送 exit）
                DisconnectSsh(isSerial: false);

                if (stoppedByFailure)
                    AppendLog(false, "任务因连续异常终止");
                else
                    AppendLog(false, "任务已完成");

                UpdateCountDisplay(isSerial: false);
                SetButtons(isSerial: false, isRunning: false, isConnected: false);
            }
        }

        private void TelnetStop_Click(object sender, RoutedEventArgs e)
        {
            _telnetCts?.Cancel();
            AppendLog(false, "[用户] 点击停止，任务已取消");
        }

        // ══════════════════════════════════════════════════════════
        // 窗口关闭
        // ══════════════════════════════════════════════════════════
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 取消并等待两个 Tab
            if (_serialRunning)
            {
                _serialCts?.Cancel();
                try { Thread.Sleep(200); } catch { }
            }
            if (_telnetRunning)
            {
                _telnetCts?.Cancel();
                try { Thread.Sleep(200); } catch { }
            }

            // 串口 Tab 清理（redir_off）
            if (_serialShell != null && _serialClient != null && _serialClient.IsConnected)
            {
                try
                {
                    _serialShell.WriteLine("bsp redir_off");
                    Thread.Sleep(500);
                }
                catch { }
            }

            // 关闭所有 SSH
            try { _serialShell?.Dispose(); _serialClient?.Disconnect(); } catch { }
            try { _telnetShell?.Dispose(); _telnetClient?.Disconnect(); } catch { }
        }
    }
}
