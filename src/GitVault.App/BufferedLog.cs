using System.IO;
using System.Text;

namespace GitVault.App;

/// <summary>有界日志队列：界面独立取走近期文本，单一后台任务按批次写入文件。</summary>
internal sealed class BufferedLog(Func<string> directory, Func<string, string, Task>? append = null)
{
    /// <summary>批次触发数量、最大待写条数与界面近期文本容量。</summary>
    internal const int BatchSize = 100;
    internal const int Capacity = 10000;
    private const int DisplayCapacity = 60000;
    /// <summary>锁同时保护消息顺序、显示缓冲、队列和停止状态。</summary>
    private readonly object gate = new();
    /// <summary>最多保留 Capacity 条待写消息，顺序由同一锁保护。</summary>
    private readonly Queue<(DateTime Time, string Line)> pending = new();
    /// <summary>与落盘队列独立的近期显示文本，可在慢写入时先交给界面。</summary>
    private readonly StringBuilder display = new();
    /// <summary>至多保留一个后台批次唤醒请求。</summary>
    private readonly SemaphoreSlim wake = new(0, 1);
    /// <summary>首条消息启动的唯一后台写入任务。</summary>
    private Task? worker;
    /// <summary>停止接收后只排空已接收消息。</summary>
    private bool stopped;
    /// <summary>上次取走显示文本后累积的消息数量。</summary>
    private int displayCount;
    /// <summary>本生命周期持续保留的日志不完整提示。</summary>
    private string warning = "";

    /// <summary>入队只操作内存；容量不足时保留界面近期文本并明确报告落盘不完整。</summary>
    public bool Add(string message, DateTime? timestamp = null)
    {
        lock (gate)
        {
            if (stopped) return false;
            var time = timestamp ?? DateTime.Now;
            var line = $"[{time:HH:mm:ss}] {message}{Environment.NewLine}";
            display.Append(line);
            if (display.Length > DisplayCapacity) display.Remove(0, display.Length - 50000);
            displayCount++;
            if (pending.Count == Capacity) warning = "日志队列已满，部分日志未落盘；可复制界面近期日志。";
            else pending.Enqueue((time, line));
            worker ??= Task.Run(WriteAsync);
            if (pending.Count >= BatchSize) Signal();
            return displayCount >= BatchSize;
        }
    }

    /// <summary>界面每批只更新一次文本；告警持续保留，不被任务完成提示覆盖。</summary>
    public (string Text, string Warning) TakeDisplay()
    {
        lock (gate)
        {
            var text = display.ToString();
            display.Clear();
            displayCount = 0;
            return (text, warning);
        }
    }

    /// <summary>停止接收并在有限时间内排空；超时不会阻塞 UI 或伪称日志完整。</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task? task;
        lock (gate) { stopped = true; task = worker; Signal(); }
        if (task is null) return true;
        try { await task.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            lock (gate) warning = "日志尚未全部写入文件，关闭后可能丢失部分日志。";
            return false;
        }
        lock (gate) return warning.Length == 0;
    }

    /// <summary>合并唤醒请求，不为每条消息创建任务或无限信号队列。</summary>
    private void Signal()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    /// <summary>每 200 毫秒或达到批量阈值后写入；文件 I/O 完全位于后台任务。</summary>
    private async Task WriteAsync()
    {
        while (true)
        {
            await wake.WaitAsync(TimeSpan.FromMilliseconds(200));
            (DateTime Time, string Line)[] batch;
            lock (gate)
            {
                batch = pending.ToArray();
                pending.Clear();
                if (batch.Length == 0 && stopped) return;
            }
            if (batch.Length == 0) continue;
            try
            {
                var folder = directory();
                Directory.CreateDirectory(folder);
                // 按消息产生日期分组，同一天的消息保持原入队顺序。
                foreach (var day in batch.GroupBy(entry => entry.Time.Date))
                {
                    var path = Path.Combine(folder, $"gitvault-{day.Key:yyyy-MM-dd}.log");
                    var text = string.Concat(day.Select(entry => entry.Line));
                    if (append is null) await File.AppendAllTextAsync(path, text);
                    else await append(path, text);
                }
            }
            catch (Exception error)
            {
                // 日志故障不改变 Git 结果；本批不重试，避免部分追加后的重复记录。
                lock (gate) warning = "日志文件无法写入，部分日志未落盘；可复制界面近期日志。 " + error.Message;
            }
            lock (gate)
            {
                if (stopped && pending.Count == 0) return;
                if (pending.Count >= BatchSize || stopped) Signal();
            }
        }
    }
}
