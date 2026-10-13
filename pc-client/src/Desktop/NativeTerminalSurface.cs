using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TermLink.TerminalCore;

namespace TermLink.Desktop;

/// <summary>A presentation of the SessionHost's immutable frame; owns no parser or shell.</summary>
public sealed class NativeTerminalSurface : Control
{
    private NativeTerminalRenderFrameV4? frame;
    private NativeTerminalRenderFrameV5? graphicsFrame;
    private readonly Dictionary<NativeTerminalGraphicsResource, WriteableBitmap> bitmapCache = new();
    private readonly Dictionary<(uint Row, uint Column), NativeTerminalGlyphDrawV5> glyphs = new();
    // A8 expands to four-byte white masks; charge the expanded pixels, not the TLGRS1 payload.
    internal const long MaximumBitmapCacheBytes = 128L * 1024 * 1024;
    internal long CachedBitmapBytes { get; private set; }
    private readonly DispatcherTimer blinkTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool blinkVisible = true;
    private readonly TerminalInputMethod inputMethod;
    private string preedit = string.Empty;
    private (int Row, int Column)? anchor;
    private (int Row, int Column)? extent;
    private readonly Dictionary<(string Text, bool Bold, bool Italic, int Color), FormattedText> textCache = new();
    private const int MaximumCachedRuns = 2048;
    private NativeTerminalMouseButtons reportedButtons;
    private double wheelRemainder;
    public bool ViewingHistory { get; set; }
    private static readonly int[] BasicPalette =
    [0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5,
     0x7f7f7f, 0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff];

    public NativeTerminalSurface()
    {
        inputMethod = new TerminalInputMethod(this);
        TextInputMethodClientRequested += (_, e) => e.Client = inputMethod;
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        blinkTimer.Tick += (_, _) => SetBlinkPhase(!blinkVisible);
        SetFont(16);
    }

    public double TerminalFontSize { get; private set; }
    public double CellWidth { get; private set; }
    public double CellHeight { get; private set; }
    public NativeTerminalRenderFrameV4? Frame => frame;
    public NativeTerminalRenderFrameV5? GraphicsFrame => graphicsFrame;
    public event Action<NativeTerminalKey, NativeTerminalModifiers>? KeyRequested;
    public event Action<string>? TextRequested;
    public event Action<int>? HistoryScrollRequested;
    public event Action<NativeTerminalMouseEvent>? MouseRequested;

    public void SetFont(double size)
    {
        if (!double.IsFinite(size) || size < 8 || size > 48) throw new ArgumentOutOfRangeException(nameof(size));
        TerminalFontSize = size;
        textCache.Clear();
        var measure = Text("M", false, false, 0xffffff);
        CellWidth = Math.Max(1, measure.WidthIncludingTrailingWhitespace);
        CellHeight = Math.Max(1, Math.Ceiling(measure.Height));
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Present(NativeTerminalRenderFrameV4 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ClearGraphicsBitmaps();
        graphicsFrame = null;
        glyphs.Clear();
        PresentText(value);
    }

    public void Present(NativeTerminalRenderFrameV5 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(graphicsFrame, value))
        {
            UpdateGraphicsBitmaps(value.Resources);
            glyphs.Clear();
            foreach (var glyph in value.Glyphs) glyphs.Add((glyph.Row, glyph.Column), glyph);
            graphicsFrame = value;
        }
        PresentText(value.Text);
    }

    private void PresentText(NativeTerminalRenderFrameV4 value)
    {
        if (frame?.Columns != value.Columns || frame?.Rows != value.Rows) ClearSelection();
        frame = value;
        if (anchor.HasValue && (anchor.Value.Column >= EffectiveColumns(anchor.Value.Row) || extent!.Value.Column >= EffectiveColumns(extent.Value.Row)))
            ClearSelection();
        inputMethod.CursorChanged();
        UpdateBlinkTimer();
        InvalidateVisual();
    }

    public (int Columns, int Rows) MeasureGrid(Size available) => (
        Math.Clamp((int)Math.Floor(available.Width / CellWidth), 1, 512),
        Math.Clamp((int)Math.Floor(available.Height / CellHeight), 1, 512));

    public Rect CursorRectangle => frame is null ? default :
        new Rect(frame.Cursor.Column * RowScale(frame.Cursor.Row) * CellWidth,
            frame.Cursor.Row * CellHeight, RowScale(frame.Cursor.Row) * CellWidth, CellHeight);

    public (int Row, int Column)? CellAt(Point point)
    {
        if (frame is null || point.X < 0 || point.Y < 0) return null;
        var row = (int)Math.Floor(point.Y / CellHeight);
        if (row >= frame.Rows) return null;
        var column = (int)Math.Floor(point.X / (CellWidth * RowScale(row)));
        if (column >= EffectiveColumns(row)) return null;
        if (frame.RenderRows[row].Cells[column].WideContinuation) column--;
        return (row, column);
    }

    public void Select((int Row, int Column) start, (int Row, int Column) end)
    {
        ValidateCell(start); ValidateCell(end);
        anchor = start; extent = end;
        InvalidateVisual();
    }

    public void ClearSelection() { anchor = null; extent = null; InvalidateVisual(); }

    public string SelectedText()
    {
        if (frame is null || anchor is null || extent is null) return string.Empty;
        var (first, last) = SelectionRange();
        var result = new StringBuilder();
        for (var row = first / frame.Columns; row <= last / frame.Columns; row++)
        {
            var line = new StringBuilder();
            for (var column = Math.Max(0, first - row * frame.Columns);
                column <= Math.Min(frame.RenderRows[row].MeaningfulEnd - 1, Math.Min(EffectiveColumns(row) - 1, last - row * frame.Columns)); column++)
            {
                var cell = frame.RenderRows[row].Cells[column];
                if (!cell.WideContinuation) line.Append(frame.Graphemes[cell.GraphemeId - 1].Text);
            }
            result.Append(line);
            if (row < last / frame.Columns && !frame.RenderRows[row].SoftWrapped) result.Append('\n');
        }
        return result.ToString();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = DefaultColor(frame?.Palette.BackgroundAlias ?? 257);
        var foreground = DefaultColor(frame?.Palette.ForegroundAlias ?? 256);
        if (frame?.ReverseVideo == true) (background, foreground) = (foreground, background);
        context.FillRectangle(Brush(background), new Rect(Bounds.Size));
        if (frame is null) return;
        using var viewport = context.PushClip(new Rect(0, 0, Math.Min(Bounds.Width, frame.Columns * CellWidth), Math.Min(Bounds.Height, frame.Rows * CellHeight)));
        var visibleRows = Math.Min(frame.Rows, (int)Math.Ceiling(Bounds.Height / CellHeight));
        DrawImages(context, 0);
        for (var row = 0; row < visibleRows; row++)
        {
            var scale = RowScale(row);
            var line = frame.RenderRows[row];
            var columns = Math.Min(EffectiveColumns(row), (int)Math.Ceiling(Bounds.Width / (CellWidth * scale)));
            var top = row * CellHeight;
            for (var column = 0; column < columns; column++)
            {
                var cell = line.Cells[column];
                var style = frame.Renditions[cell.RenditionId];
                var (_, bg) = Colors(style, foreground, background);
                var backgroundKind = style.Inverse ? style.Foreground.Kind : style.Background.Kind;
                if (bg != background || backgroundKind != NativeTerminalRenderColorKindV1.Default)
                    context.FillRectangle(Brush(bg), new Rect(column * scale * CellWidth, top, scale * CellWidth, CellHeight));
            }
        }
        DrawImages(context, 1);
        DrawTextRows(context, visibleRows, foreground, background, selectedOnly: false);
        DrawImages(context, 2);
        if (anchor is not null)
        {
            for (var row = 0; row < visibleRows; row++)
                for (var column = 0; column < EffectiveColumns(row); column++)
                    if (IsSelected(row, column)) context.FillRectangle(Brush(frame.Palette.SelectionBackgroundRgb ?? 0x315579),
                        new Rect(column * RowScale(row) * CellWidth, row * CellHeight, RowScale(row) * CellWidth, CellHeight));
            DrawTextRows(context, visibleRows, foreground, background, selectedOnly: true);
        }
        if (frame.Cursor.Visible && anchor is null)
        {
            var rectangle = CursorRectangle;
            var color = frame.Palette.DefaultCursorRgb ?? foreground;
            if (frame.Cursor.Shape == NativeTerminalRenderCursorShapeV2.Bar) rectangle = rectangle.WithWidth(2);
            else if (frame.Cursor.Shape == NativeTerminalRenderCursorShapeV2.Underline) rectangle = new Rect(rectangle.X, rectangle.Bottom - 2, rectangle.Width, 2);
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32(0x70000000U | (uint)color)), rectangle);
        }
        if (preedit.Length != 0)
        {
            var cursor = CursorRectangle;
            var text = Text(preedit, false, false, foreground);
            context.FillRectangle(Brush(background), new Rect(cursor.X, cursor.Y, text.WidthIncludingTrailingWhitespace, CellHeight));
            context.DrawText(text, cursor.TopLeft);
            context.DrawLine(new Pen(Brush(foreground), 1), new Point(cursor.X, cursor.Bottom - 1), new Point(cursor.X + text.WidthIncludingTrailingWhitespace, cursor.Bottom - 1));
        }
    }

    private void DrawTextRows(DrawingContext context, int visibleRows, int foreground, int background, bool selectedOnly)
    {
        for (var row = 0; row < visibleRows; row++)
        {
            var scale = RowScale(row);
            var line = frame!.RenderRows[row];
            var columns = Math.Min(EffectiveColumns(row), (int)Math.Ceiling(Bounds.Width / (CellWidth * scale)));
            var top = row * CellHeight;
            // Paint all backgrounds before wide glyphs, then clip the physical half-row.
            using var clip = context.PushClip(new Rect(0, top, Bounds.Width, CellHeight));
            var verticalScale = line.LineRendition is NativeTerminalLineRendition.DoubleHeightTop or NativeTerminalLineRendition.DoubleHeightBottom ? 2 : 1;
            var translatedTop = line.LineRendition == NativeTerminalLineRendition.DoubleHeightBottom ? top - CellHeight : top;
            using var transform = context.PushTransform(new Matrix(scale, 0, 0, verticalScale, 0, translatedTop));
            for (var column = 0; column < columns; column++)
            {
                var cell = line.Cells[column];
                if (cell.WideContinuation) continue;
                var glyph = frame.Graphemes[cell.GraphemeId - 1];
                if (selectedOnly && !IsSelected(row, column) && !(glyph.DisplayWidth == 2 && IsSelected(row, column + 1))) continue;
                var style = frame.Renditions[cell.RenditionId];
                if (style.Conceal || (style.Blink && !blinkVisible)) continue;
                var (fg, _) = Colors(style, foreground, background);
                var left = column * CellWidth;
                var width = glyph.DisplayWidth * CellWidth;
                using (context.PushClip(new Rect(left, 0, width, CellHeight)))
                {
                    if (glyphs.TryGetValue(((uint)row, (uint)column), out var graphicGlyph))
                        DrawGlyph(context, graphicGlyph, new Rect(left, 0, width, CellHeight), fg);
                    else
                    {
                        var text = Text(glyph.Text, style.Bold, style.Italic, fg);
                        var measured = text.WidthIncludingTrailingWhitespace;
                        // A full grapheme reaches the platform shaper; fit its physical advance to core columns.
                        var fit = measured > 0 ? width / measured : 1;
                        using var glyphTransform = context.PushTransform(new Matrix(fit, 0, 0, 1, left, 0));
                        context.DrawText(text, default);
                    }
                }
                DrawDecorations(context, left, width, style, Resolve(style.UnderlineColor, fg));
            }
        }
    }

    private void DrawImages(DrawingContext context, int layer)
    {
        if (graphicsFrame is null) return;
        foreach (var image in graphicsFrame.Images)
        {
            var imageLayer = image.Z < -1073741824 ? 0 : image.Z < 0 ? 1 : 2;
            if (imageLayer != layer) continue;
            var layout = image.Layout;
            var clip = image.Clip;
            using var clipped = context.PushClip(new Rect((image.Column + (double)clip.Left) * CellWidth,
                (image.Row + (double)clip.Top) * CellHeight, (clip.Right - clip.Left) * CellWidth, (clip.Bottom - clip.Top) * CellHeight));
            var destination = new Rect((image.Column + Value(layout.X)) * CellWidth, (image.Row + Value(layout.Y)) * CellHeight,
                Value(layout.Width) * CellWidth, Value(layout.Height) * CellHeight);
            var source = layout.Source;
            context.DrawImage(GraphicsBitmap(image.ResourceId), new Rect(source.X, source.Y, source.Width, source.Height), destination);
        }
    }

    private void DrawGlyph(DrawingContext context, NativeTerminalGlyphDrawV5 glyph, Rect destination, int color)
    {
        var mask = new ImageBrush(GraphicsBitmap(glyph.ResourceId))
        {
            SourceRect = new RelativeRect(new Rect(glyph.SourceX, 0, glyph.SourceWidth, glyph.SourceHeight), RelativeUnit.Absolute),
            Stretch = Stretch.Fill,
        };
        using var opacity = context.PushOpacityMask(mask, destination);
        context.FillRectangle(Brush(color), destination);
    }

    private WriteableBitmap GraphicsBitmap(ulong id)
    {
        if (!graphicsFrame!.Resources.TryGet(id, out var resource)) throw new InvalidOperationException("Missing validated graphics resource.");
        if (bitmapCache.TryGetValue(resource, out var cached)) return cached;
        var byteCount = checked((long)resource.Width * resource.Height * 4);
        if (CachedBitmapBytes + byteCount > MaximumBitmapCacheBytes) throw new InvalidOperationException("Graphics bitmap cache exceeds its expanded pixel budget.");
        var bitmap = new WriteableBitmap(new PixelSize((int)resource.Width, (int)resource.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var target = bitmap.Lock();
            var row = new byte[resource.Width * 4];
            var pixels = resource.Pixels;
            for (var y = 0; y < resource.Height; y++)
            {
                for (var x = 0; x < resource.Width; x++)
                {
                    var destination = x * 4;
                    if (resource.Format == NativeTerminalGraphicsImageFormat.Alpha8)
                    {
                        var alpha = pixels[(int)(y * resource.Width + x)];
                        row[destination] = row[destination + 1] = row[destination + 2] = row[destination + 3] = alpha;
                    }
                    else
                    {
                        var source = (int)((y * resource.Width + x) * 4);
                        var alpha = pixels[source + 3];
                        row[destination] = (byte)((pixels[source + 2] * alpha + 127) / 255);
                        row[destination + 1] = (byte)((pixels[source + 1] * alpha + 127) / 255);
                        row[destination + 2] = (byte)((pixels[source] * alpha + 127) / 255);
                        row[destination + 3] = alpha;
                    }
                }
                Marshal.Copy(row, 0, IntPtr.Add(target.Address, checked((int)y * target.RowBytes)), row.Length);
            }
            bitmapCache.Add(resource, bitmap);
            CachedBitmapBytes += byteCount;
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static double Value(NativeTerminalGraphicsRatio ratio) => (double)ratio.Numerator / ratio.Denominator;
    private void UpdateGraphicsBitmaps(NativeTerminalGraphicsResources resources)
    {
        // Compare once per frame; drawing continues to use the current immutable resource reference.
        foreach (var (previous, bitmap) in bitmapCache.ToArray())
        {
            bitmapCache.Remove(previous);
            if (resources.TryGet(previous.Id, out var current) && previous.HasSameContent(current))
                bitmapCache.Add(current, bitmap);
            else
            {
                bitmap.Dispose();
                CachedBitmapBytes -= (long)previous.Width * previous.Height * 4;
            }
        }
    }

    private void ClearGraphicsBitmaps()
    {
        foreach (var bitmap in bitmapCache.Values) bitmap.Dispose();
        bitmapCache.Clear();
        CachedBitmapBytes = 0;
    }

    internal void SetBlinkPhase(bool visible) { blinkVisible = visible; InvalidateVisual(); }
    private void UpdateBlinkTimer()
    {
        if (this.IsAttachedToVisualTree() && frame?.Renditions.Any(style => style.Blink) == true) blinkTimer.Start();
        else { blinkTimer.Stop(); blinkVisible = true; }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBlinkTimer();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        blinkTimer.Stop();
        ClearGraphicsBitmaps();
        base.OnDetachedFromVisualTree(e);
    }

    private void DrawDecorations(DrawingContext context, double left, double width, NativeTerminalRenderRenditionV3 style, int color)
    {
        var pen = new Pen(Brush(color), 1);
        if (style.Strikethrough) context.DrawLine(pen, new Point(left, CellHeight / 2), new Point(left + width, CellHeight / 2));
        if (style.Overline) context.DrawLine(pen, new Point(left, 1), new Point(left + width, 1));
        var y = CellHeight - 2;
        switch (style.UnderlineStyle)
        {
            case NativeTerminalRenderUnderlineStyleV3.None: break;
            case NativeTerminalRenderUnderlineStyleV3.Double:
                context.DrawLine(pen, new Point(left, y - 2), new Point(left + width, y - 2));
                goto case NativeTerminalRenderUnderlineStyleV3.Single;
            case NativeTerminalRenderUnderlineStyleV3.Single:
                context.DrawLine(pen, new Point(left, y), new Point(left + width, y)); break;
            case NativeTerminalRenderUnderlineStyleV3.Curly:
                for (double x = 0; x < width; x += 2)
                    context.DrawLine(pen, new Point(left + x, y - ((int)x / 2 % 2)), new Point(left + Math.Min(x + 2, width), y - (1 - (int)x / 2 % 2)));
                break;
            default:
                var dash = style.UnderlineStyle == NativeTerminalRenderUnderlineStyleV3.Dotted ? 1 : 3;
                for (double x = 0; x < width; x += dash + 2)
                    context.DrawLine(pen, new Point(left + x, y), new Point(left + Math.Min(x + dash, width), y));
                break;
        }
    }

    private (int Foreground, int Background) Colors(NativeTerminalRenderRenditionV3 style, int foreground, int background)
    {
        var fg = Resolve(style.Foreground, foreground); var bg = Resolve(style.Background, background);
        if (style.Inverse) (fg, bg) = (bg, fg);
        if (style.Faint) fg = (((fg >> 16 & 255) * 2 / 3) << 16) | (((fg >> 8 & 255) * 2 / 3) << 8) | ((fg & 255) * 2 / 3);
        return (fg, bg);
    }

    public int DefaultColor(int index) => index switch {
        256 => frame?.Palette.DefaultForegroundRgb ?? 0xe6edf3,
        257 => frame?.Palette.DefaultBackgroundRgb ?? 0x090d12,
        >= 0 and <= 255 => frame?.Palette.ColorsRgb[index] ?? PaletteColor(index),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static NativeTerminalHostThemeV1 HostTheme(bool dark)
    {
        var colors = new uint[259];
        for (var index = 0; index < 256; index++) colors[index] = (uint)PaletteColor(index);
        colors[256] = dark ? 0xe6edf3U : 0x18212bU;
        colors[257] = dark ? 0x090d12U : 0xffffffU;
        colors[258] = colors[256];
        return NativeTerminalHostThemeV1.Create(dark, colors);
    }

    private int Resolve(NativeTerminalRenderColorV1 color, int fallback) => color.Kind switch {
        NativeTerminalRenderColorKindV1.Default => fallback,
        NativeTerminalRenderColorKindV1.Rgb => color.Value,
        _ => frame!.Palette.ColorsRgb[color.Value] ?? PaletteColor(color.Value),
    };

    private static int PaletteColor(int index)
    {
        if (index < 16) return BasicPalette[index];
        if (index >= 232) { var gray = 8 + 10 * (index - 232); return gray * 0x010101; }
        index -= 16;
        static int Component(int value) => value == 0 ? 0 : 55 + value * 40;
        return Component(index / 36) << 16 | Component(index / 6 % 6) << 8 | Component(index % 6);
    }

    private FormattedText Text(string text, bool bold, bool italic, int color)
    {
        var key = (text, bold, italic, color);
        if (textCache.TryGetValue(key, out var cached)) return cached;
        if (textCache.Count >= MaximumCachedRuns) textCache.Clear();
        var result = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("avares://TermLink/Assets/Fonts#CaskaydiaCove Nerd Font Mono"), italic ? FontStyle.Italic : FontStyle.Normal,
                bold ? FontWeight.Bold : FontWeight.Normal), TerminalFontSize, Brush(color));
        textCache.Add(key, result);
        return result;
    }

    private static SolidColorBrush Brush(int rgb) => new(Color.FromUInt32(0xff000000U | (uint)rgb));
    private int RowScale(int row) => frame!.RenderRows[row].LineRendition == NativeTerminalLineRendition.SingleWidth ? 1 : 2;
    private int EffectiveColumns(int row) => Math.Max(1, frame!.Columns / RowScale(row));
    private void ValidateCell((int Row, int Column) cell)
    {
        if (frame is null || cell.Row < 0 || cell.Row >= frame.Rows || cell.Column < 0 || cell.Column >= EffectiveColumns(cell.Row)) throw new ArgumentOutOfRangeException(nameof(cell));
    }
    private (int First, int Last) SelectionRange()
    {
        var first = anchor!.Value.Row * frame!.Columns + anchor.Value.Column;
        var last = extent!.Value.Row * frame.Columns + extent.Value.Column;
        return (Math.Min(first, last), Math.Max(first, last));
    }
    private bool IsSelected(int row, int column)
    {
        if (anchor is null || extent is null) return false;
        var (first, last) = SelectionRange(); var index = row * frame!.Columns + column;
        return index >= first && index <= last;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text)) { preedit = string.Empty; ClearSelection(); TextRequested?.Invoke(e.Text); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        NativeTerminalKey? key = e.Key switch {
            Key.Enter => NativeTerminalKey.Enter, Key.Back => NativeTerminalKey.Backspace,
            Key.Tab => NativeTerminalKey.Tab, Key.Escape => NativeTerminalKey.Escape,
            Key.Delete => NativeTerminalKey.Delete, Key.Insert => NativeTerminalKey.Insert,
            Key.Home => NativeTerminalKey.Home, Key.End => NativeTerminalKey.End,
            Key.PageUp => NativeTerminalKey.PageUp, Key.PageDown => NativeTerminalKey.PageDown,
            Key.Up => NativeTerminalKey.ArrowUp, Key.Down => NativeTerminalKey.ArrowDown,
            Key.Left => NativeTerminalKey.ArrowLeft, Key.Right => NativeTerminalKey.ArrowRight,
            >= Key.F1 and <= Key.F12 => (NativeTerminalKey)((uint)NativeTerminalKey.F1 + (uint)(e.Key - Key.F1)), _ => null,
        };
        if (key is null) return;
        var modifiers = NativeTerminalModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= NativeTerminalModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= NativeTerminalModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= NativeTerminalModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= NativeTerminalModifiers.Meta;
        ClearSelection(); KeyRequested?.Invoke(key.Value, modifiers); e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (ShouldReportMouse(e.KeyModifiers)) {
            var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch {
                PointerUpdateKind.LeftButtonPressed => NativeTerminalMouseButton.Left,
                PointerUpdateKind.MiddleButtonPressed => NativeTerminalMouseButton.Middle,
                PointerUpdateKind.RightButtonPressed => NativeTerminalMouseButton.Right, _ => (NativeTerminalMouseButton?)null,
            };
            if (button is null) return;
            reportedButtons |= ButtonFlag(button.Value);
            ClearSelection(); e.Pointer.Capture(this);
            MouseRequested?.Invoke(NativeTerminalMouseEvent.Press(button.Value, reportedButtons, MousePosition(e.GetPosition(this)), Modifiers(e.KeyModifiers)));
            e.Handled = true; return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var cell = CellAt(e.GetPosition(this)); if (cell is null) return;
        Focus(); Select(cell.Value, cell.Value); e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (reportedButtons != 0 || (anchor is null && ShouldReportMouse(e.KeyModifiers))) {
            MouseRequested?.Invoke(NativeTerminalMouseEvent.Move(reportedButtons, MousePosition(e.GetPosition(this)), Modifiers(e.KeyModifiers)));
            e.Handled = true; return;
        }
        if (e.Pointer.Captured != this || anchor is null) return;
        var cell = CellAt(e.GetPosition(this)); if (cell is not null) Select(anchor.Value, cell.Value);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        var button = e.InitialPressMouseButton switch {
            MouseButton.Left => NativeTerminalMouseButton.Left, MouseButton.Middle => NativeTerminalMouseButton.Middle,
            MouseButton.Right => NativeTerminalMouseButton.Right, _ => (NativeTerminalMouseButton?)null,
        };
        if (button is not null && (reportedButtons & ButtonFlag(button.Value)) != 0 && frame is not null) {
            reportedButtons &= ~ButtonFlag(button.Value);
            MouseRequested?.Invoke(NativeTerminalMouseEvent.Release(button.Value, reportedButtons, MousePosition(e.GetPosition(this)), Modifiers(e.KeyModifiers)));
            e.Handled = true;
        }
        if (reportedButtons == 0) e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        if (frame is not null) foreach (var button in new[] { NativeTerminalMouseButton.Left, NativeTerminalMouseButton.Middle, NativeTerminalMouseButton.Right }) {
            if ((reportedButtons & ButtonFlag(button)) == 0) continue;
            reportedButtons &= ~ButtonFlag(button);
            MouseRequested?.Invoke(NativeTerminalMouseEvent.Release(button, reportedButtons, MousePosition(default)));
        }
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollWheel(e.Delta.Y, e.GetPosition(this), e.KeyModifiers); e.Handled = true;
    }

    public void ScrollWheel(double delta, Point position, KeyModifiers modifiers) {
        if (frame is null || !double.IsFinite(delta)) return;
        wheelRemainder += Math.Clamp(delta, -32, 32);
        var ticks = (int)wheelRemainder; wheelRemainder -= ticks;
        if (ticks == 0) return;
        if (!ViewingHistory && !modifiers.HasFlag(KeyModifiers.Shift) && (frame.MouseReporting || frame.AlternateScroll)) {
            var input = NativeTerminalMouseEvent.Wheel(ticks > 0 ? NativeTerminalMouseWheelDirection.Up : NativeTerminalMouseWheelDirection.Down,
                reportedButtons, MousePosition(position), Modifiers(modifiers));
            for (var index = 0; index < Math.Abs(ticks); index++) MouseRequested?.Invoke(input);
        } else HistoryScrollRequested?.Invoke(ticks * 3);
    }

    private bool ShouldReportMouse(KeyModifiers modifiers) => frame?.MouseReporting == true && !ViewingHistory && !modifiers.HasFlag(KeyModifiers.Shift);
    private NativeTerminalMousePosition MousePosition(Point point) {
        var row = Math.Clamp((int)(point.Y / CellHeight), 0, frame!.Rows - 1);
        // Mouse coordinates address physical cells, including a scaled line's inaccessible half.
        var column = Math.Clamp((int)(point.X / CellWidth), 0, frame.Columns - 1);
        return NativeTerminalMousePosition.CellAndPixel((uint)column, (uint)row,
            (uint)Math.Clamp(point.X, 0, frame.Columns * CellWidth - 1), (uint)Math.Clamp(point.Y, 0, frame.Rows * CellHeight - 1));
    }
    private static NativeTerminalMouseButtons ButtonFlag(NativeTerminalMouseButton button) => (NativeTerminalMouseButtons)(1 << ((int)button - 1));
    private static NativeTerminalModifiers Modifiers(KeyModifiers value) =>
        (value.HasFlag(KeyModifiers.Shift) ? NativeTerminalModifiers.Shift : 0) |
        (value.HasFlag(KeyModifiers.Control) ? NativeTerminalModifiers.Control : 0) |
        (value.HasFlag(KeyModifiers.Alt) ? NativeTerminalModifiers.Alt : 0) |
        (value.HasFlag(KeyModifiers.Meta) ? NativeTerminalModifiers.Meta : 0);

    private sealed class TerminalInputMethod(NativeTerminalSurface owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => owner.CursorRectangle;
        public override TextSelection Selection { get => new(0, 0); set { } }
        public override void SetPreeditText(string? text) { owner.preedit = text ?? string.Empty; owner.InvalidateVisual(); }
        public void CursorChanged() => RaiseCursorRectangleChanged();
    }
}
