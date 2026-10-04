using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OmenSuperHub {
  public partial class FloatingForm : Form {
    private const int ContentPadding = 10;
    private const int ScreenMargin = 10;

    // 渲染缓存：text/size/color/layout/屏宽均未变化时跳过位图重建
    private Bitmap currentBitmap;
    private Font cachedFont;
    private StringFormat cachedFormat;
    private int cachedFontSize = -1;

    private string lastText;
    private int lastTextSize = -1;
    private string lastFontColor;
    private string lastLayout;
    private int lastMaxBitmapWidth = -1;

    private int opacity = 255;
    private string fontColor = "auto";
    private string layout = "horizontal";

    private bool suppressMoveRender;

    private sealed class LogicalItem {
      public string Title;
      public string TitleKey;
      public string Value;
      public Color TitleColor;
      public Color ValueColor;
    }

    private sealed class TextSegment {
      public string Text;
      public Color Color;
      public float Width;
    }

    public FloatingForm(string text, int textSize, string loc, Screen screen = null,
        int opacity = 255, string fontColor = "auto", string layout = "horizontal") {
      this.FormBorderStyle = FormBorderStyle.None;
      this.TopMost = true;
      this.ShowInTaskbar = false;
      this.StartPosition = FormStartPosition.Manual;

      this.opacity = ClampOpacity(opacity);
      this.fontColor = NormalizeFontColor(fontColor);
      this.layout = NormalizeLayout(layout);

      this.HandleCreated += (s, e) => RenderCurrentImage();

      ApplyRender(text, textSize, screen);
      SetAnchoredPosition(loc, screen);
    }

    // 构建位图；返回是否发生了重建
    private bool ApplyRender(string text, int textSize, Screen screen) {
      if (string.IsNullOrEmpty(text) || textSize <= 0)
        return false;

      var workingArea = (screen ?? Screen.PrimaryScreen).WorkingArea;
      int maxBitmapWidth = Math.Max(1, workingArea.Width - ScreenMargin * 2);
      int maxBitmapHeight = Math.Max(1, workingArea.Height - ScreenMargin * 2);

      if (currentBitmap != null
          && text == lastText
          && textSize == lastTextSize
          && fontColor == lastFontColor
          && layout == lastLayout
          && maxBitmapWidth == lastMaxBitmapWidth)
        return false;

      Bitmap oldBitmap = currentBitmap;
      Bitmap newBitmap = null;

      try {
        EnsureFont(textSize);
        using (var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        using (var measureGraphics = Graphics.FromImage(measureBitmap)) {
          var items = BuildLogicalItems(text);
          var rows = BuildRows(items, measureGraphics, cachedFont, cachedFormat,
            Math.Max(1, maxBitmapWidth - ContentPadding * 2));
          float lineHeight = (float)Math.Ceiling(cachedFont.GetHeight(measureGraphics));

          float widestRow = 1;
          foreach (var row in rows) {
            float rowWidth = 0;
            foreach (var segment in row) rowWidth += segment.Width;
            widestRow = Math.Max(widestRow, rowWidth);
          }

          int bitmapWidth = Math.Min(maxBitmapWidth,
            Math.Max(1, (int)Math.Ceiling(widestRow) + ContentPadding * 2));
          int bitmapHeight = Math.Min(maxBitmapHeight,
            Math.Max(1, (int)Math.Ceiling(lineHeight * rows.Count) + ContentPadding * 2));

          newBitmap = new Bitmap(bitmapWidth, bitmapHeight, PixelFormat.Format32bppArgb);
          using (Graphics graphics = Graphics.FromImage(newBitmap)) {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float y = ContentPadding;
            foreach (var row in rows) {
              float x = ContentPadding;
              foreach (var segment in row) {
                if (!string.IsNullOrEmpty(segment.Text)) {
                  using (Brush brush = new SolidBrush(segment.Color))
                    graphics.DrawString(segment.Text, cachedFont, brush, new PointF(x, y), cachedFormat);
                }
                x += segment.Width;
              }
              y += lineHeight;
            }
          }
        }
      } catch (ArgumentException ex) {
        newBitmap?.Dispose();
        System.Diagnostics.Debug.WriteLine($"Bitmap 创建失败: {ex.Message}");
        return false;
      }

      currentBitmap = newBitmap;
      oldBitmap?.Dispose();

      lastText = text;
      lastTextSize = textSize;
      lastFontColor = fontColor;
      lastLayout = layout;
      lastMaxBitmapWidth = maxBitmapWidth;

      AdjustFormSize();
      return true;
    }

    private void EnsureFont(int textSize) {
      if (cachedFont != null && cachedFontSize == textSize)
        return;
      cachedFont?.Dispose();
      cachedFont = new Font("Calibri", textSize, FontStyle.Bold, GraphicsUnit.World);
      cachedFontSize = textSize;
      if (cachedFormat == null)
        cachedFormat = CreateTextFormat();
    }

    private static StringFormat CreateTextFormat() {
      var format = (StringFormat)StringFormat.GenericTypographic.Clone();
      format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
      return format;
    }

    private static float MeasureText(Graphics graphics, string text, Font font, StringFormat format) {
      if (string.IsNullOrEmpty(text)) return 0;
      return graphics.MeasureString(text, font, int.MaxValue, format).Width;
    }

    // 将 monitorText 的每个逻辑项解析为 标题 + 值
    private List<LogicalItem> BuildLogicalItems(string text) {
      var items = new List<LogicalItem>();
      string[] sourceLines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

      foreach (string sourceLine in sourceLines) {
        int separatorIndex = sourceLine.IndexOf(':');
        string titleKey = separatorIndex >= 0 ? sourceLine.Substring(0, separatorIndex).Trim() : "";
        string title = titleKey.Length > 0 ? titleKey + ":" : "";
        string value = separatorIndex >= 0 ? sourceLine.Substring(separatorIndex + 1).Trim() : sourceLine.Trim();
        items.Add(new LogicalItem {
          Title = title,
          TitleKey = titleKey,
          Value = value,
          TitleColor = ResolveTitleColor(titleKey),
          ValueColor = ResolveValueColor(titleKey, value)
        });
      }

      if (items.Count == 0) {
        items.Add(new LogicalItem {
          Title = "",
          TitleKey = "",
          Value = " ",
          TitleColor = GetColorForTitle(""),
          ValueColor = GetColorForTitle("")
        });
      }
      return items;
    }

    // 按布局把逻辑项排版成若干渲染行；每行由若干带颜色的片段组成
    private List<List<TextSegment>> BuildRows(List<LogicalItem> items, Graphics graphics,
        Font font, StringFormat format, float maxWidth) {
      var rows = new List<List<TextSegment>>();
      var row = new List<TextSegment>();
      float rowWidth = 0;

      bool horizontal = layout != "vertical";
      const string separatorText = " | ";
      float separatorWidth = MeasureText(graphics, separatorText, font, format);
      Color separatorColor = Color.FromArgb(150, 150, 150);

      foreach (var item in items) {
        var segments = BuildItemSegments(item, graphics, font, format);
        float itemWidth = 0;
        foreach (var segment in segments) itemWidth += segment.Width;

        if (horizontal) {
          if (row.Count > 0) {
            if (rowWidth + separatorWidth + itemWidth > maxWidth) {
              rows.Add(row);
              row = new List<TextSegment>();
              rowWidth = 0;
            } else {
              row.Add(new TextSegment { Text = separatorText, Color = separatorColor, Width = separatorWidth });
              rowWidth += separatorWidth;
            }
          }
        } else if (row.Count > 0) {
          rows.Add(row);
          row = new List<TextSegment>();
          rowWidth = 0;
        }

        // 逐片段放置，单项超宽时在片段之间折行
        foreach (var segment in segments) {
          if (row.Count > 0 && rowWidth + segment.Width > maxWidth) {
            rows.Add(row);
            row = new List<TextSegment>();
            rowWidth = 0;
          }
          row.Add(segment);
          rowWidth += segment.Width;
        }

        if (!horizontal) {
          rows.Add(row);
          row = new List<TextSegment>();
          rowWidth = 0;
        }
      }

      if (row.Count > 0)
        rows.Add(row);

      if (rows.Count == 0)
        rows.Add(new List<TextSegment> { new TextSegment { Text = " ", Color = Color.White, Width = 0 } });

      return rows;
    }

    private List<TextSegment> BuildItemSegments(LogicalItem item, Graphics graphics,
        Font font, StringFormat format) {
      var segments = new List<TextSegment>();

      if (!string.IsNullOrEmpty(item.Title)) {
        string titleText = item.Title + " ";
        segments.Add(new TextSegment {
          Text = titleText,
          Color = item.TitleColor,
          Width = MeasureText(graphics, titleText, font, format)
        });
      }

      string[] parts = item.Value.Split(new[] { ',' }, StringSplitOptions.None);
      for (int i = 0; i < parts.Length; i++) {
        string part = parts[i].Trim();
        if (part.Length == 0) continue;
        if (i < parts.Length - 1) part += ", ";
        segments.Add(new TextSegment {
          Text = part,
          Color = item.ValueColor,
          Width = MeasureText(graphics, part, font, format)
        });
      }

      if (segments.Count == 0) {
        segments.Add(new TextSegment { Text = " ", Color = item.ValueColor, Width = 0 });
      }
      return segments;
    }

    private Color ResolveTitleColor(string titleKey) {
      if (fontColor != "auto") return GetFixedColor(fontColor);
      return GetColorForTitle(titleKey);
    }

    private Color ResolveValueColor(string titleKey, string value) {
      if (fontColor != "auto") return GetFixedColor(fontColor);

      if (titleKey == "CPU" || titleKey == "GPU") {
        float temperature;
        if (value.Contains("°C") && TryParseFirstFloat(value, out temperature))
          return GetColorForTemperature(temperature);
      } else if (titleKey == "Fan") {
        float averageSpeed;
        if (TryParseAverageNumber(value, out averageSpeed))
          return GetColorForFanSpeed((int)averageSpeed);
      }

      return GetColorForTitle(titleKey);
    }

    private static bool TryParseFirstFloat(string value, out float result) {
      result = 0;
      var builder = new StringBuilder();
      for (int i = 0; i < value.Length; i++) {
        char c = value[i];
        if ((c >= '0' && c <= '9') || c == '.' || c == '-')
          builder.Append(c);
        else if (builder.Length > 0)
          break;
      }
      return builder.Length > 0
          && float.TryParse(builder.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseAverageNumber(string value, out float average) {
      average = 0;
      double sum = 0;
      int count = 0;
      var builder = new StringBuilder();

      for (int i = 0; i <= value.Length; i++) {
        char c = i < value.Length ? value[i] : ' ';
        if (c >= '0' && c <= '9') {
          builder.Append(c);
        } else if (builder.Length > 0) {
          int number;
          if (int.TryParse(builder.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) {
            sum += number;
            count++;
          }
          builder.Length = 0;
        }
      }

      if (count == 0) return false;
      average = (float)(sum / count);
      return true;
    }

    private static Color GetFixedColor(string colorName) {
      switch (colorName) {
        case "white":   return Color.White;
        case "red":     return Color.Red;
        case "green":   return Color.Green;
        case "blue":    return Color.Blue;
        case "yellow":  return Color.Yellow;
        case "orange":  return Color.Orange;
        case "purple":  return Color.Purple;
        case "cyan":    return Color.Cyan;
        case "magenta": return Color.Magenta;
        case "gray":    return Color.Gray;
        default:        return Color.White;
      }
    }

    private static Color GetColorForTemperature(float temperature) {
      if (temperature >= 80) return Color.Red;
      if (temperature >= 65) return Color.Orange;
      if (temperature >= 50) return Color.Yellow;
      return Color.Green;
    }

    private static Color GetColorForFanSpeed(int fanSpeed) {
      if (fanSpeed >= 4000) return Color.Red;
      if (fanSpeed >= 3000) return Color.Orange;
      if (fanSpeed >= 2000) return Color.Yellow;
      return Color.Green;
    }

    private static Color GetColorForTitle(string title) {
      switch (title) {
        case "CPU": return Color.FromArgb(0, 128, 192);
        case "GPU": return Color.FromArgb(0, 128, 192);
        case "Fan": return Color.FromArgb(0, 128, 64);
        default:    return Color.FromArgb(0, 128, 192);
      }
    }

    private static int ClampOpacity(int value) {
      if (value < 0) return 0;
      if (value > 255) return 255;
      return value;
    }

    private static string NormalizeFontColor(string value) {
      return string.IsNullOrEmpty(value) ? "auto" : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLayout(string value) {
      return value == "vertical" ? "vertical" : "horizontal";
    }

    public void SetText(string text, int textSize, string loc, Screen screen = null,
        int? opacity = null, string fontColor = null, string layout = null) {
      if (InvokeRequired) {
        BeginInvoke(new Action(() => SetText(text, textSize, loc, screen, opacity, fontColor, layout)));
        return;
      }
      if (textSize <= 0) return;

      if (opacity.HasValue) this.opacity = ClampOpacity(opacity.Value);
      if (fontColor != null) this.fontColor = NormalizeFontColor(fontColor);
      if (layout != null) this.layout = NormalizeLayout(layout);

      ApplyRender(text, textSize, screen);
      SetAnchoredPosition(loc, screen);
      RenderCurrentImage();
    }

    private void AdjustFormSize() {
      if (currentBitmap == null) return;
      this.Size = currentBitmap.Size;
    }

    protected override void OnMove(EventArgs e) {
      base.OnMove(e);
      if (!suppressMoveRender)
        RenderCurrentImage();
    }

    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= WS_EX_LAYERED
                   | WS_EX_TRANSPARENT
                   | WS_EX_NOACTIVATE;
        return cp;
      }
    }

    public void SetPositionTopLeft(Screen screen = null) {
      SetAnchoredPosition("left", screen);
    }

    public void SetPositionTopRight(int textSize, Screen screen = null) {
      SetAnchoredPosition("right", screen);
    }

    private void SetAnchoredPosition(string loc, Screen screen) {
      var wa = (screen ?? Screen.PrimaryScreen).WorkingArea;
      int desiredX = loc == "left"
        ? wa.Left + ScreenMargin
        : wa.Right - this.Width - ScreenMargin;
      int desiredY = wa.Top + ScreenMargin;
      int maxX = wa.Right - this.Width;
      int maxY = wa.Bottom - this.Height;

      int x = maxX < wa.Left ? wa.Left : Math.Max(wa.Left, Math.Min(desiredX, maxX));
      int y = maxY < wa.Top ? wa.Top : Math.Max(wa.Top, Math.Min(desiredY, maxY));

      suppressMoveRender = true;
      try {
        this.Location = new Point(x, y);
      } finally {
        suppressMoveRender = false;
      }
    }

    private void RenderCurrentImage() {
      if (currentBitmap != null && IsHandleCreated)
        RenderLayered(currentBitmap);
    }

    private void RenderLayered(Bitmap bitmap) {
      if (bitmap == null) return;

      IntPtr screenDC  = GetDC(IntPtr.Zero);
      IntPtr memDC     = CreateCompatibleDC(screenDC);
      IntPtr hBitmap   = bitmap.GetHbitmap(Color.FromArgb(0));
      IntPtr oldBitmap = SelectObject(memDC, hBitmap);

      NativeSize  size  = new NativeSize(bitmap.Width, bitmap.Height);
      NativePoint ptSrc = new NativePoint(0, 0);
      NativePoint ptDst = new NativePoint(this.Left, this.Top);

      BLENDFUNCTION blend = new BLENDFUNCTION {
        BlendOp             = AC_SRC_OVER,
        BlendFlags          = 0,
        SourceConstantAlpha = (byte)opacity,
        AlphaFormat         = AC_SRC_ALPHA
      };

      UpdateLayeredWindow(this.Handle, screenDC, ref ptDst, ref size,
                          memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

      SelectObject(memDC, oldBitmap);
      DeleteObject(hBitmap);
      DeleteDC(memDC);
      ReleaseDC(IntPtr.Zero, screenDC);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        cachedFont?.Dispose();
        cachedFormat?.Dispose();
        currentBitmap?.Dispose();
      }
      base.Dispose(disposing);
    }

    // ── 常量 ─────────────────────────────────────────────────────────────
    private const int  WS_EX_LAYERED     = 0x80000;
    private const int  WS_EX_TRANSPARENT = 0x20;
    private const int  WS_EX_NOACTIVATE  = 0x08000000;
    private const int  ULW_ALPHA         = 0x02;
    private const byte AC_SRC_OVER       = 0x00;
    private const byte AC_SRC_ALPHA      = 0x01;

    // ── 结构体 ───────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize  { public int cx, cy; public NativeSize(int x, int y)  { cx = x; cy = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int x,  y;  public NativePoint(int x, int y) { this.x = x; this.y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // ── P/Invoke ─────────────────────────────────────────────────────────
    [DllImport("user32.dll")] static extern bool   UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr hObject);
  }
}
