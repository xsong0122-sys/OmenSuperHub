using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmenSuperHub {
  /// <summary>
  /// 方案A：DXGI GetFrameStatistics 轮询。
  /// 枚举显示输出并读取 PresentCount 增量估算 FPS，零外部依赖。
  /// 局限：窗口化 / Vulkan / OpenGL 场景可能返回不可用（此时 Available 置 false）。
  /// </summary>
  public sealed class DxgiFpsSource : IFpsSource {
    const int IntervalMs = 500;
    const int SampleWindow = 16; // 平滑窗口（采样次数）

    static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

    #region DXGI COM 互操作
    // 说明：COM 互操作按 vtable 槽位分发，必须按真实顺序声明前置方法（未用到的用占位签名）。
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1 {
      // IDXGIObject
      [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
      [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
      [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
      [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
      // IDXGIFactory
      [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
      [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
      [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
      [PreserveSig] int CreateSwapChain([MarshalAs(UnmanagedType.IUnknown)] object device, IntPtr desc, out IntPtr ppSwapChain);
      [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
      // IDXGIFactory1
      [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter1);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1 {
      // IDXGIObject
      [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
      [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
      [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
      [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
      // IDXGIAdapter
      [PreserveSig] int EnumOutputs(uint output, [MarshalAs(UnmanagedType.Interface)] out IDXGIOutput ppOutput);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput {
      // IDXGIObject
      [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
      [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
      [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
      [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
      // IDXGIOutput
      [PreserveSig] int GetDesc(IntPtr desc);
      [PreserveSig] int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr desc);
      [PreserveSig] int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr concernedDevice);
      [PreserveSig] int WaitForVBlank();
      [PreserveSig] int TakeOwnership(IntPtr device, bool exclusive);
      [PreserveSig] int ReleaseOwnership();
      [PreserveSig] int GetGammaControlCapabilities(IntPtr gammaCaps);
      [PreserveSig] int SetGammaControl(IntPtr array);
      [PreserveSig] int GetGammaControl(IntPtr array);
      [PreserveSig] int SetDisplaySurface(IntPtr scanoutSurface);
      [PreserveSig] int GetDisplaySurfaceData(IntPtr destination);
      [PreserveSig] int GetFrameStatistics(out DXGI_FRAME_STATISTICS stats);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_FRAME_STATISTICS {
      public uint PresentCount;
      public uint PresentRefreshCount;
      public uint SyncRefreshCount;
      public long SyncQPCTime;
      public long SyncDisplayCount;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
    #endregion

    sealed class OutputState {
      public IDXGIOutput Output;
      public uint PrevPresent;
    }

    readonly object sync = new object();
    readonly List<OutputState> outputs = new List<OutputState>();
    readonly Queue<float> samples = new Queue<float>(SampleWindow);
    Timer timer;
    bool running;
    volatile bool available = true;
    volatile bool hasReading;
    float fps;
    long lastTicks;

    public string Name { get { return "DXGI"; } }
    public bool Available { get { return available; } }
    public bool HasReading { get { return hasReading; } }
    public float Fps { get { return fps; } }

    public void Start() {
      lock (sync) {
        if (running) return;
        running = true;
        available = true;
        timer = new Timer(OnTick, null, 0, IntervalMs);
      }
    }

    public void Stop() {
      Timer t;
      lock (sync) {
        if (!running) return;
        running = false;
        t = timer;
        timer = null;
        outputs.Clear();
        samples.Clear();
      }
      if (t != null) t.Dispose();
      hasReading = false;
      fps = 0f;
    }

    void OnTick(object state) {
      lock (sync) {
        if (!running) return;
        try {
          if (outputs.Count == 0) {
            if (!InitializeLocked()) { available = false; hasReading = false; return; }
            available = true;
            CaptureBaselineLocked();
            lastTicks = Stopwatch.GetTimestamp();
            return;
          }

          long now = Stopwatch.GetTimestamp();
          double elapsed = (now - lastTicks) / (double)Stopwatch.Frequency;
          lastTicks = now;
          if (elapsed <= 0) return;

          uint best = 0;
          bool any = false;
          foreach (var o in outputs) {
            DXGI_FRAME_STATISTICS stats;
            if (o.Output.GetFrameStatistics(out stats) != 0) continue;
            any = true;
            uint delta = stats.PresentCount - o.PrevPresent; // 无符号回绕即正确的帧增量
            o.PrevPresent = stats.PresentCount;
            if (delta > best) best = delta;
          }

          if (!any) { available = false; hasReading = false; return; }

          samples.Enqueue((float)(best / elapsed));
          while (samples.Count > SampleWindow) samples.Dequeue();
          float sum = 0f;
          foreach (var v in samples) sum += v;
          fps = sum / samples.Count;
          hasReading = true;
        } catch (Exception ex) {
          available = false;
          hasReading = false;
          Logger.Error($"DxgiFpsSource: {ex.Message}");
        }
      }
    }

    bool InitializeLocked() {
      outputs.Clear();
      try {
        IDXGIFactory1 factory = null;
        Guid iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null) return false;

        for (uint a = 0; ; a++) {
          IDXGIAdapter1 adapter;
          if (factory.EnumAdapters1(a, out adapter) != 0 || adapter == null) break;
          for (uint o = 0; ; o++) {
            IDXGIOutput output;
            if (adapter.EnumOutputs(o, out output) != 0 || output == null) break;
            outputs.Add(new OutputState { Output = output });
          }
        }
      } catch (Exception ex) {
        Logger.Error($"DxgiFpsSource.Initialize: {ex.Message}");
        return false;
      }
      return outputs.Count > 0;
    }

    void CaptureBaselineLocked() {
      foreach (var o in outputs) {
        DXGI_FRAME_STATISTICS stats;
        if (o.Output.GetFrameStatistics(out stats) == 0) o.PrevPresent = stats.PresentCount;
      }
    }
  }
}
