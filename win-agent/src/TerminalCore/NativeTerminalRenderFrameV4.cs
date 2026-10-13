using System;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    public enum NativeTerminalWidthProfile : uint { LegacyV1 = 1, Unicode16 = 2 }
    public sealed class NativeTerminalRenderFrameV4DecodeException : ArgumentException {
        internal NativeTerminalRenderFrameV4DecodeException(string detail) : base("render frame v4: " + detail) { }
    }
    public sealed class NativeTerminalRenderFrameV4
    {
        internal NativeTerminalRenderFrameV4(
            NativeTerminalWidthProfile widthProfile,
            ulong presentationRevision,
            ulong nextSourceSequence,
            int columns,
            int rows,
            NativeTerminalRenderScreenV1 activeScreen,
            bool reverseVideo,
            NativeTerminalRenderCursorV2 cursor,
            NativeTerminalRenderGraphemeV1[] graphemes,
            NativeTerminalRenderRenditionV3[] renditions,
            NativeTerminalRenderPaletteV1 palette,
            NativeTerminalRenderHyperlinkV1[] hyperlinks,
            NativeTerminalRenderRowV1[] renderRows,
            string title, bool mouseReporting, bool alternateScroll)
        {
            WidthProfile = widthProfile;
            PresentationRevision = presentationRevision;
            NextSourceSequence = nextSourceSequence;
            Columns = columns;
            Rows = rows;
            ActiveScreen = activeScreen;
            ReverseVideo = reverseVideo;
            Cursor = cursor;
            Graphemes = Array.AsReadOnly(graphemes);
            Renditions = Array.AsReadOnly(renditions);
            Palette = palette;
            Hyperlinks = Array.AsReadOnly(hyperlinks);
            RenderRows = Array.AsReadOnly(renderRows);
            Title = title;
            MouseReporting = mouseReporting;
            AlternateScroll = alternateScroll;
        }

        public const int FormatVersion = 4;

        public NativeTerminalWidthProfile WidthProfile { get; }
        public ulong PresentationRevision { get; }
        public ulong NextSourceSequence { get; }
        public int Columns { get; }
        public int Rows { get; }
        public NativeTerminalRenderScreenV1 ActiveScreen { get; }
        public bool ReverseVideo { get; }
        public NativeTerminalRenderCursorV2 Cursor { get; }
        public IReadOnlyList<NativeTerminalRenderGraphemeV1> Graphemes { get; }
        public IReadOnlyList<NativeTerminalRenderRenditionV3> Renditions { get; }
        public NativeTerminalRenderPaletteV1 Palette { get; }
        public IReadOnlyList<NativeTerminalRenderHyperlinkV1> Hyperlinks { get; }
        public IReadOnlyList<NativeTerminalRenderRowV1> RenderRows { get; }
        public string Title { get; }
        public bool MouseReporting { get; }
        public bool AlternateScroll { get; }
    }


    public static class NativeTerminalRenderFrameV4Decoder {
        public const int MaximumFrameBytes = 32 * 1024 * 1024;
        public static NativeTerminalRenderFrameV4 Decode(byte[] bytes) {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }
        public static NativeTerminalRenderFrameV4 Decode(ReadOnlySpan<byte> bytes) => NativeTerminalRenderFrameV1Decoder.DecodeV4(bytes);
    }
}
