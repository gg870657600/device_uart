using Renci.SshNet;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace chengkong
{
    /// <summary>
    /// 日志条目：Text=显示文本，IsOk=正常(true)/异常(false)/中性(null)
    /// </summary>
    public class LogEntry
    {
        public string Text { get; set; } = "";
        public bool? IsOk { get; set; }
    }

    public partial class MainWindow : Window
    {
        private bool _isSending = false;
        private CancellationTokenSource _cancellationTokenSource = new();
        private static int Count = 1;
        private int _okCount = 0;
        private int _notOkCount = 0;

        // 当前活跃的 SSH 会话资源（供窗口关闭时收尾使用）
        private SshClient? _activeClient;
        private ShellStream? _activeShell;
        private readonly object _cleanupLock = new();
        private bool _cleanupDone;
        private Task? _sshTask;

        private readonly ObservableCollection<LogEntry> _logEntries = new();

        public MainWindow()
        {
            InitializeComponent();
            LogListView.ItemsSource = _logEntries;
            AppendLogLeft("程序已启动，请配置参数后点击 [下发]");
        }

        #region UI 日志辅助方法

        private void AppendLogLeft(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                RegisterResultTextBox.AppendText(text + Environment.NewLine);
                RegisterResultTextBox.ScrollToEnd();
            }, DispatcherPriority.Background);
        }

        private void AppendLogRight(string text, bool? isOk = null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var entry = new LogEntry { Text = text, IsOk = isOk };
                _logEntries.Add(entry);
                LogListView.ScrollIntoView(entry);
            }, DispatcherPriority.Background);
        }

        private void ClearLogRight()
        {
            Dispatcher.InvokeAsync(() =>
            {
                _logEntries.Clear();
            });
        }

        private void UpdateCountDisplay()
        {
            Dispatcher.InvokeAsync(() =>
            {
                int total = _okCount + _notOkCount;
                CountTextBlock.Text = $"OK个数: {_okCount}, 不OK个数: {_notOkCount}, 总个数: {total}";
            }, DispatcherPriority.Background);
        }

        private void UpdateButtonText(string content)
        {
            Dispatcher.InvokeAsync(() =>
            {
                SendButton.Content = content;
            }, DispatcherPriority.Background);
        }

        #endregion

        #region 下发主逻辑

        private async void Send(object sender, RoutedEventArgs e)
        {
            if (_isSending)
            {
                _cancellationTokenSource?.Cancel();
                _isSending = false;
                MessageBox.Show("已停止定时任务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _okCount = 0;
            _notOkCount = 0;
            Count = 1;
            ClearLogRight();

            int totalTime = int.Parse(TotalTime.Text);
            int intervalTime = int.Parse(IntervalTime.Text);
            string targetIp = ModemIp.Text;
            string sshUsername = "root";
            string sshPassword = "andisat";
            string userCommand = SshCommand.Text.Trim();
            string keyword = Keyword.Text.Trim();
            int fieldPosition = FieldPosition.SelectedIndex + 1; // ComboBox 索引0=位置1

            if (string.IsNullOrEmpty(userCommand))
            {
                MessageBox.Show("请输入 SSH 命令", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(keyword))
            {
                MessageBox.Show("请输入关键字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateButtonText("取消");
            UpdateCountDisplay();

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SensorPosLog.txt"), string.Empty);

            _isSending = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;
            DateTime startTime = DateTime.Now;

            try
            {
                DateTime endTime = startTime.AddMinutes(totalTime);
                MessageBox.Show($"开始定时任务，总时间: {totalTime}分钟，间隔: {intervalTime}秒",
                    "定时任务", MessageBoxButton.OK, MessageBoxImage.Information);

                bool stoppedByFailure = false;

                // 单次 SSH 会话，在会话内循环执行命令
                _sshTask = Task.Run(() =>
                {
                    stoppedByFailure = ExecuteSshLoopSync(targetIp, sshUsername, sshPassword,
                        userCommand, keyword, fieldPosition,
                        intervalTime, endTime, cancellationToken);
                }, cancellationToken);

                try { await _sshTask; }
                catch { /* ExecuteSshLoopSync 内部已处理 */ }
                finally { _sshTask = null; }

                TimeSpan actualRunTime = DateTime.Now - startTime;
                string timeString = FormatTimeSpan(actualRunTime);

                if (stoppedByFailure)
                {
                    AppendLogLeft("任务因连续5次异常而终止");
                }
                else
                {
                    MessageBox.Show($"定时任务完成\n实际运行时间: {timeString}",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                string actualRunTime = FormatTimeSpan(DateTime.Now - startTime);
                MessageBox.Show($"定时任务已取消\n实际运行时间: {actualRunTime}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"定时任务过程中出错: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSending = false;
                UpdateButtonText("下发");
            }
        }

        /// <summary>
        /// 窗口关闭事件：取消任务 → 同步等待 SSH 线程退出 → 同步收尾 → 关闭
        /// 确保 bsp redir_off 真正送达设备后再退出
        /// </summary>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isSending) return;

            // 1. 取消任务
            try { _cancellationTokenSource?.Cancel(); } catch { }

            // 2. 同步等 SSH 线程退出（让 shell 在后台线程里被释放）
            try
            {
                if (_sshTask != null && !_sshTask.IsCompleted)
                {
                    _sshTask.Wait(5000);
                }
            }
            catch { }

            // 3. 同步执行收尾（内部已用 _cleanupDone 防重入）
            try { CleanupOnce(); }
            catch (Exception ex)
            {
                // Closing 阶段 UI 即将消失，最多往 stderr 写一条
                try { System.Diagnostics.Debug.WriteLine($"Closing cleanup error: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 收尾 SSH 资源：bsp redir_off + dispose shell + disconnect client
        /// 多次调用只执行一次，线程安全
        /// 单次执行总耗时控制在 5 秒内（防止 SSH 假死时永久卡住）
        /// </summary>
        private void CleanupOnce()
        {
            SshClient? clientToCleanup;
            ShellStream? shellToCleanup;

            lock (_cleanupLock)
            {
                if (_cleanupDone) return;
                _cleanupDone = true;
                clientToCleanup = _activeClient;
                shellToCleanup = _activeShell;
            }

            if (clientToCleanup == null && shellToCleanup == null) return;

            DateTime cleanupStart = DateTime.Now;
            const int totalBudgetMs = 5000;
            const int redirWaitMs = 500;

            try
            {
                if (shellToCleanup != null)
                {
                    // 1) 发送 redir_off（剩余预算内）
                    if ((DateTime.Now - cleanupStart).TotalMilliseconds < totalBudgetMs - 1000)
                    {
                        try
                        {
                            AppendLogLeft("[收尾] 发送: bsp redir_off");
                            shellToCleanup.WriteLine("bsp redir_off");
                            Thread.Sleep(redirWaitMs);
                            // 清空残余读取
                            try { while (shellToCleanup.Length > 0) shellToCleanup.Read(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            AppendLogLeft($"[收尾] redir_off 失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        AppendLogLeft("[收尾] 剩余预算不足，跳过 redir_off");
                    }

                    // 2) Dispose shell
                    try { shellToCleanup.Dispose(); } catch { }
                }

                if (clientToCleanup != null)
                {
                    // 3) Disconnect client
                    try
                    {
                        if (clientToCleanup.IsConnected)
                            clientToCleanup.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        AppendLogLeft($"[收尾] SSH 断开失败: {ex.Message}");
                    }
                }

                double costMs = (DateTime.Now - cleanupStart).TotalMilliseconds;
                AppendLogLeft($"[断开] SSH 已断开 (收尾耗时: {costMs:F0}ms)");
            }
            catch (Exception ex)
            {
                AppendLogLeft($"[收尾] 整体异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// SSH 单会话循环（运行在后台线程）
        /// 流程：redir_off → redir_on → 循环(自定义命令) → redir_off
        /// 任何情况结束（正常 / 取消 / 5次异常 / SSH异常）都会在 finally 中执行收尾
        /// 返回 true 表示因连续5次异常终止
        /// </summary>
        private bool ExecuteSshLoopSync(string targetIp, string username, string password,
            string userCommand, string keyword, int fieldPosition,
            int intervalSec, DateTime endTime, CancellationToken cancellationToken)
        {
            bool stoppedByFailure = false;
            SshClient? client = null;
            ShellStream? shell = null;

            // 重置收尾标志
            lock (_cleanupLock) { _cleanupDone = false; }

            try
            {
                AppendLogLeft($"═══════ [连接] 准备连接 → IP: {targetIp}, 用户: {username} ═══════");

                var connectionInfo = new ConnectionInfo(targetIp, username,
                    new PasswordAuthenticationMethod(username, password));

                client = new SshClient(connectionInfo);
                client.Connect();
                if (!client.IsConnected)
                {
                    AppendLogLeft("[连接] ✗ 失败");
                    return false;
                }
                AppendLogLeft("[连接] ✓ 成功");
                AppendLogLeft($"[参数] SSH命令: {userCommand}");
                AppendLogLeft($"[参数] 解析关键字: \"{keyword}\", 位置: 第 {fieldPosition} 段");

                shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 2048);

                // 暴露给类成员，供 Closing 事件访问
                lock (_cleanupLock)
                {
                    _activeClient = client;
                    _activeShell = shell;
                }

                // 前置命令
                AppendLogLeft("[前置] 发送: bsp redir_off");
                ClearShellBuffer(shell, 1000);
                shell.WriteLine("bsp redir_off");
                ClearShellBuffer(shell, 1000);

                AppendLogLeft("[前置] 发送: bsp redir_on");
                shell.WriteLine("bsp redir_on");
                ClearShellBuffer(shell, 1000);

                int currentCount = Count;
                int consecutiveZeroCount = 0;
                const int triggerStopCount = 5;

                // 主循环：发命令 → 等「间隔时间（秒）」→ 读返回 → 解析
                while (DateTime.Now < endTime && !cancellationToken.IsCancellationRequested)
                {
                    AppendLogLeft($"");
                    AppendLogLeft($"═══════ 第 {currentCount} 次执行 ═══════");
                    AppendLogLeft($"[发送] 命令: {userCommand}");
                    AppendLogLeft($"[等待] 等 {intervalSec} 秒后读取返回...");

                    shell.WriteLine(userCommand);

                    // 等待「间隔时间」秒（发命令到读返回之间的等待）
                    try
                    {
                        Task.Delay(intervalSec * 1000, cancellationToken).Wait();
                    }
                    catch (AggregateException) when (cancellationToken.IsCancellationRequested)
                    {
                        AppendLogLeft($"[取消] 第 {currentCount} 次执行等待中收到取消信号");
                        break;
                    }

                    string result = ReadShellClean(shell, 5000);

                    // 输出原始返回内容
                    AppendLogLeft($"──── 原始返回 (长度: {result.Length}) ────");
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        AppendLogLeft($"  (空)");
                    }
                    else
                    {
                        int lineNo = 1;
                        foreach (string line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            AppendLogLeft($"  [{lineNo:D2}] {line}");
                            lineNo++;
                        }
                    }

                    // 解析
                    var (currentValue, parseTrace) = ParseKeywordFieldDetailed(result, keyword, fieldPosition);
                    AppendLogLeft($"──── 解析过程 ────");
                    foreach (string trace in parseTrace)
                    {
                        AppendLogLeft($"  {trace}");
                    }

                    // 写文件日志
                    WriteToLog(currentCount, result, currentValue, keyword, fieldPosition);

                    // 判定：解析失败 或 解析值 == "0.00" 都视为异常
                    bool parseFailed = currentValue == "解析失败" || currentValue == "0.00";
                    bool isOk = !parseFailed;
                    if (isOk)
                    {
                        _okCount++;
                        consecutiveZeroCount = 0;
                    }
                    else
                    {
                        _notOkCount++;
                        consecutiveZeroCount++;
                    }

                    string statusText = isOk ? "正常" : "异常";
                    AppendLogLeft($"──── 判定 ────");
                    AppendLogLeft($"  解析值: {currentValue}");
                    AppendLogLeft($"  判定规则: 解析成功 且 解析值 != \"0.00\" 才算正常");
                    AppendLogLeft($"  本次结果: {statusText} | 累计 OK: {_okCount} | 不OK: {_notOkCount} | 连续异常: {consecutiveZeroCount}");
                    AppendLogRight($"【第 {currentCount} 次】{keyword} = {currentValue} → {statusText}", isOk);
                    UpdateCountDisplay();

                    // 连续5次异常 → 终止整个任务，但仍执行收尾 redir_off
                    if (consecutiveZeroCount >= triggerStopCount)
                    {
                        AppendLogLeft($"[终止] ✗ 连续 {triggerStopCount} 次异常，自动终止任务！");
                        AppendLogRight($"【第 {currentCount} 次】连续 {triggerStopCount} 次异常，任务终止！", false);

                        Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show($"连续 {triggerStopCount} 次异常，已自动终止任务！", "任务终止",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        });

                        stoppedByFailure = true;
                        // 不调用 Cancel()，避免形成死循环；走外层正常结束逻辑
                        break;
                    }

                    Count++;
                    currentCount = Count;
                }

                return stoppedByFailure;
            }
            catch (Exception ex) when (ex is OperationCanceledException ||
                (ex is AggregateException ae && ae.InnerExceptions.Any(e => e is OperationCanceledException)))
            {
                AppendLogLeft($"[异常] 操作已取消 (OperationCanceledException)");
                AppendLogRight("操作已取消");
                return false;
            }
            catch (Exception ex)
            {
                AppendLogLeft($"[异常] SSH 错误: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                // 任何路径（正常 / 取消 / 5次异常 / SSH异常）都执行收尾
                CleanupOnce();
                // 清空类成员引用
                lock (_cleanupLock)
                {
                    _activeClient = null;
                    _activeShell = null;
                }
            }
        }

        #endregion

        #region 辅助方法

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            return $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        private void WriteToLog(int restartCount, string response, string parsedValue, string keyword, int fieldPosition)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SensorPosLog.txt");

                string logContent = $"==================================================\r\n" +
                                    $"日志时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                                    $"执行次数：第 {restartCount} 次\r\n" +
                                    $"解析参数：关键字=\"{keyword}\", 位置={fieldPosition}\r\n" +
                                    $"解析结果：{parsedValue}\r\n" +
                                    $"==================================================\r\n" +
                                    $"命令原始返回内容：\r\n" +
                                    $"{response}\r\n" +
                                    $"==================================================\r\n\r\n";

                File.AppendAllText(logPath, logContent);
            }
            catch
            {
            }
        }

        private void ClearShellBuffer(ShellStream shell, int waitMs)
        {
            Thread.Sleep(waitMs);
            while (shell.Length > 0)
                shell.Read();
        }

        private string ReadShellClean(ShellStream shell, int waitMs)
        {
            Thread.Sleep(waitMs);
            StringBuilder sb = new StringBuilder();
            while (shell.Length > 0)
            {
                string? line = shell.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.Trim().StartsWith("bsp ") || line.Contains("root@andi")) continue;
                sb.AppendLine(line.Trim());
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 通用返回解析器：按关键字筛选行，再按空格分割取指定位置
        /// 返回值: (解析值, 过程描述列表)
        /// </summary>
        private (string value, List<string> trace) ParseKeywordFieldDetailed(string response, string keyword, int fieldPosition)
        {
            var trace = new List<string>();

            if (string.IsNullOrWhiteSpace(response))
            {
                trace.Add("原始返回为空 → 返回 \"未知\"");
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
                    trace.Add($"    行内容: {trim}");

                    string[] parts = trim.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    trace.Add($"    分割结果 ({parts.Length} 段): [{string.Join(" | ", parts)}]");

                    if (parts.Length >= fieldPosition)
                    {
                        string value = parts[fieldPosition - 1].TrimEnd('.');
                        trace.Add($"    → 提取第 {fieldPosition} 段: \"{value}\"");
                        return (value, trace);
                    }
                    else
                    {
                        trace.Add($"    ✗ 段数不足，期望第 {fieldPosition} 段，实际只有 {parts.Length} 段");
                        return ("解析失败", trace);
                    }
                }
            }

            trace.Add($"  ✗ 未找到包含关键字 \"{keyword}\" 的行");
            return ("解析失败", trace);
        }

        private async Task<bool> PingDeviceAsync(string ipAddress, int timeoutMinutes = 3)
        {
            try
            {
                AppendLogLeft("正在 ping 调制解调器......");

                var pingTask = Task.Run(() =>
                {
                    var ping = new System.Net.NetworkInformation.Ping();
                    var startTime = DateTime.Now;
                    var timeout = TimeSpan.FromMinutes(timeoutMinutes);

                    while (DateTime.Now - startTime < timeout)
                    {
                        try
                        {
                            var reply = ping.Send(ipAddress, 5000);

                            if (reply.Status == IPStatus.Success)
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                        }

                        Thread.Sleep(1000);
                    }

                    return false;
                });

                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(timeoutMinutes));
                var completedTask = await Task.WhenAny(pingTask, timeoutTask);

                bool pingResult = completedTask == pingTask && await pingTask;

                AppendLogLeft(pingResult ? "ping 调制解调器 已通" : "ping 调制解调器 3分钟 未通");

                return pingResult;
            }
            catch (Exception ex)
            {
                AppendLogLeft("ping 调制解调器 异常 未通");
                Trace.WriteLine($"Ping错误: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}