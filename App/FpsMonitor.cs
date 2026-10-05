using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OmenSuperHub {
  public enum FpsMode { Auto, Dxgi, PresentMon }

  /// <summary>
  /// FPS 采集源的统一接口。上层显示与配置只依赖该接口，不关心具体采集方式。
  /// </summary>
  public interface IFpsSource {
    string Name { get; }
    bool Available { get; }
    bool HasReading { get; }
    float Fps { get; }
    void Start();
    void Stop();
  }

  /// <summary>
  /// FPS 监控门面：持有当前采集源，负责模式切换、生命周期与"自动"模式下的可用性回退。
  /// </summary>
  public static class FpsMonitor {
    static readonly object Sync = new object();
    static IFpsSource source;
    static FpsMode mode = FpsMode.Auto;
    static bool enabled = false;

    public static bool Enabled { get { lock (Sync) return enabled; } }
    public static FpsMode Mode { get { lock (Sync) return mode; } }
    public static string SourceName { get { var s = source; return s != null ? s.Name : ""; } }

    public static bool HasReading {
      get {
        var s = source;
        return s != null && s.Available && s.HasReading;
      }
    }

    public static float CurrentFps {
      get {
        var s = source;
        if (s == null || !s.Available || !s.HasReading) return 0f;
        return s.Fps;
      }
    }

    public static FpsMode ParseMode(string value) {
      if (value == "dxgi") return FpsMode.Dxgi;
      if (value == "presentmon") return FpsMode.PresentMon;
      return FpsMode.Auto;
    }

    public static string ModeLabel(FpsMode value) {
      switch (value) {
        case FpsMode.Dxgi: return Strings.FpsModeDxgi;
        case FpsMode.PresentMon: return Strings.FpsModePresentMon;
        default: return Strings.FpsModeAuto;
      }
    }

    public static void SetMode(FpsMode value) {
      lock (Sync) { mode = value; }
      if (Enabled) Restart();
    }

    public static void Start() {
      lock (Sync) { enabled = true; }
      Restart();
    }

    public static void Stop() {
      lock (Sync) { enabled = false; }
      StopCurrent();
    }

    static void StopCurrent() {
      IFpsSource current;
      lock (Sync) { current = source; source = null; }
      if (current == null) return;
      try { current.Stop(); } catch (Exception ex) { Logger.Error($"FpsMonitor.Stop: {ex.Message}"); }
    }

    static void Restart() {
      StopCurrent();
      IFpsSource next = CreateSource();
      if (next == null) return;
      lock (Sync) { source = next; }
      try { next.Start(); } catch (Exception ex) { Logger.Error($"FpsMonitor.Start: {ex.Message}"); }
    }

    static IFpsSource CreateSource() {
      FpsMode m;
      lock (Sync) m = mode;
      if (m == FpsMode.Dxgi) return new DxgiFpsSource();
      if (m == FpsMode.PresentMon) return new PresentMonFpsSource();
      // 自动：优先 PresentMon（覆盖率与准确性更高），其二进制不可用时回退 DXGI
      return PresentMonFpsSource.BinaryPresent
          ? (IFpsSource)new PresentMonFpsSource()
          : new DxgiFpsSource();
    }

    /// <summary>
    /// 显示用文本：未启用返回 null；无全屏前台程序或无读数时返回 "--"。
    /// </summary>
    public static string FormatDisplayText() {
      if (!Enabled) return null;
      int pid;
      if (!TryGetGameProcessId(out pid)) return "--";
      if (!HasReading) return "--";
      return ((int)Math.Round(CurrentFps)).ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>
    /// 判断当前前台窗口是否为覆盖整个屏幕的程序（全屏 / 无边框游戏），并返回其进程 ID。
    /// 桌面、任务栏等外壳窗口也会覆盖屏幕，因此需要排除 explorer。
    /// </summary>
    public static bool TryGetGameProcessId(out int processId) {
      processId = 0;
      try {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return false;
        if (pid == (uint)Process.GetCurrentProcess().Id) return false;

        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { return false; }
        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)) return false;

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return false;

        var bounds = Screen.FromHandle(hwnd).Bounds;
        if (rect.Left > bounds.Left + 1 || rect.Top > bounds.Top + 1 ||
            rect.Right < bounds.Right - 1 || rect.Bottom < bounds.Bottom - 1) return false;

        processId = (int)pid;
        return true;
      } catch { return false; }
    }
  }
}
