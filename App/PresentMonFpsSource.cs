using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OmenSuperHub {
  /// <summary>
  /// 方案B：调用同目录下的 PresentMon 子进程，解析其 CSV 输出并按前台游戏进程聚合 FPS。
  /// 需要将 PresentMon.exe 放在程序目录（或程序目录下的 PresentMon 子目录）；
  /// 缺失时 BinaryPresent / Available 为 false，"自动"模式会回退到 DXGI。
  /// 基于 ETW，覆盖 D3D / OpenGL / Vulkan 及窗口化场景，准确性优于方案A。
  /// </summary>
  public sealed class PresentMonFpsSource : IFpsSource {
    // PresentMon v2.x 标准输出 CSV 参数。
    // 使用独立会话名，并用 --stop_existing_session 清理上次被强杀后残留的 ETW 会话；
    // 否则 PresentMon 会因“会话已存在”直接退出、stdout 全空，导致 FPS 恒为 --。
    const string SessionName = "OmenSuperHubFps";
    const string Arguments = "--output_stdout --no_console_stats --session_name " + SessionName + " --stop_existing_session";
    const int SampleWindow = 16;        // 每个进程保留的最近帧间隔数
    const int MaxTrackedProcesses = 64; // 跟踪的进程上限，超出则清空重建

    static readonly string[] CandidatePaths = { "PresentMon.exe", @"PresentMon\PresentMon.exe" };

    readonly object sync = new object();
    readonly Dictionary<int, Queue<double>> windows = new Dictionary<int, Queue<double>>();

    Process process;
    Thread reader;
    volatile bool running;
    volatile bool available;
    volatile bool started;

    /// <summary>定位 PresentMon.exe，找不到返回 null。</summary>
    public static string ResolveBinaryPath() {
      string baseDir = AppDomain.CurrentDomain.BaseDirectory;
      foreach (string relative in CandidatePaths) {
        string path = Path.Combine(baseDir, relative);
        if (File.Exists(path)) return path;
      }
      return null;
    }

    public static bool BinaryPresent { get { return ResolveBinaryPath() != null; } }

    public string Name { get { return "PresentMon"; } }
    public bool Available { get { return available; } }

    public bool HasReading {
      get {
        int pid;
        if (!FpsMonitor.TryGetGameProcessId(out pid)) return false;
        lock (sync) {
          Queue<double> q;
          return windows.TryGetValue(pid, out q) && q.Count > 0;
        }
      }
    }

    public float Fps {
      get {
        int pid;
        if (!FpsMonitor.TryGetGameProcessId(out pid)) return 0f;
        lock (sync) {
          Queue<double> q;
          if (!windows.TryGetValue(pid, out q) || q.Count == 0) return 0f;
          double sum = 0;
          foreach (double v in q) sum += v;
          double avg = sum / q.Count;
          return avg > 0 ? (float)(1000.0 / avg) : 0f;
        }
      }
    }

    public void Start() {
      lock (sync) {
        if (started) return;
        started = true;
        running = true;
        available = false;
        windows.Clear();
      }

      string exe = ResolveBinaryPath();
      if (exe == null) {
        Logger.Error("PresentMonFpsSource: 未找到 PresentMon.exe，方案B不可用");
        lock (sync) { running = false; started = false; }
        return;
      }

      try {
        process = new Process {
          StartInfo = new ProcessStartInfo {
            FileName = exe,
            Arguments = Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
          }
        };
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "PresentMonReader" };
        reader.Start();
      } catch (Exception ex) {
        Logger.Error($"PresentMonFpsSource.Start: {ex.Message}");
        lock (sync) { running = false; started = false; process = null; }
      }
    }

    public void Stop() {
      Process p;
      Thread t;
      lock (sync) {
        if (!started) return;
        running = false;
        started = false;
        p = process;
        t = reader;
        process = null;
        reader = null;
      }
      try { if (p != null && !p.HasExited) p.Kill(); } catch { }
      try { if (t != null && t.IsAlive) t.Join(1000); } catch { }
      try { if (p != null) p.Dispose(); } catch { }
      available = false;
      lock (sync) windows.Clear();
    }

    void ReadLoop() {
      Process p;
      lock (sync) p = process;
      if (p == null) return;

      try {
        p.Start();

        // 捕获 stderr：PresentMon 出错时会在此输出 error 行（如 ETW 会话冲突、权限不足），
        // 否则这些信息会被丢弃，导致只能看到“无输出”而无法定位原因。
        Thread errReader = new Thread(() => {
          try {
            string err;
            while ((err = p.StandardError.ReadLine()) != null) Logger.Error($"PresentMonFpsSource: {err}");
          } catch { }
        }) { IsBackground = true, Name = "PresentMonErr" };
        errReader.Start();

        int pidIndex = -1;
        int msIndex = -1;
        string header = p.StandardOutput.ReadLine();
        if (header != null) {
          header = header.TrimStart('\uFEFF');
          string[] cols = header.Split(',');
          for (int i = 0; i < cols.Length; i++) {
            string name = cols[i].Trim().Trim('"');
            if (string.Equals(name, "ProcessID", StringComparison.OrdinalIgnoreCase)) pidIndex = i;
            else if (string.Equals(name, "msBetweenPresents", StringComparison.OrdinalIgnoreCase)) msIndex = i;
          }
        }

        if (pidIndex < 0 || msIndex < 0) {
          Logger.Error($"PresentMonFpsSource: 输出缺少预期列 ({(string.IsNullOrEmpty(header) ? "无输出" : header)})");
          return;
        }

        available = true;

        string line;
        while (running && (line = p.StandardOutput.ReadLine()) != null) {
          string[] cols = line.Split(',');
          if (cols.Length <= pidIndex || cols.Length <= msIndex) continue;

          int pid;
          double ms;
          if (!int.TryParse(cols[pidIndex].Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) continue;
          if (!double.TryParse(cols[msIndex].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) continue;
          if (ms <= 0) continue;

          lock (sync) { PushSample(pid, ms); }
        }
      } catch (Exception ex) {
        if (running) Logger.Error($"PresentMonFpsSource.ReadLoop: {ex.Message}");
      } finally {
        available = false;
      }
    }

    void PushSample(int pid, double ms) {
      if (windows.Count > MaxTrackedProcesses) windows.Clear();
      Queue<double> q;
      if (!windows.TryGetValue(pid, out q)) {
        q = new Queue<double>(SampleWindow);
        windows[pid] = q;
      }
      q.Enqueue(ms);
      while (q.Count > SampleWindow) q.Dequeue();
    }
  }
}
