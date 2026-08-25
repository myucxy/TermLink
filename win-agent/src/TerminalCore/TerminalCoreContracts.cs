using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.TerminalCore
{
    public static class TerminalCapabilities
    {
        public const string PresentationMetadataV1 = "terminal_presentation_metadata_v1";
        public const string NormalizedPresentationV1 = "terminal_presentation_v1";
        public const string PresentationSnapshotV1 = "terminal_presentation_snapshot_v1";
        public const string PresentationDeltaV1 = "terminal_presentation_delta_v1";
        public const string PresentationResyncV1 = "terminal_presentation_resync_v1";
        public const string TypedInputV1 = "terminal_typed_input_v1";
    }

    public enum TerminalSourceEventKind
    {
        Output = 1,
        Resize = 2,
        Title = 3,
        Lifecycle = 4,
        InputResult = 5,
        Gap = 6
    }

    public sealed class TerminalSourceEvent
    {
        public TerminalSourceEvent(ulong sequence, TerminalSourceEventKind kind, ReadOnlyMemory<byte> payload)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            SourceEventSequence = sequence;
            Kind = kind;
            Payload = payload;
        }

        public ulong SourceEventSequence { get; }
        public TerminalSourceEventKind Kind { get; }
        public ReadOnlyMemory<byte> Payload { get; }
    }

    public enum TerminalPresentationKind
    {
        Snapshot = 1,
        Delta = 2,
        ResyncRequired = 3
    }

    public sealed class TerminalPresentation
    {
        public TerminalPresentation(
            ulong sourceEventSequence,
            ulong presentationRevision,
            TerminalPresentationKind kind,
            ReadOnlyMemory<byte> payload)
        {
            if (sourceEventSequence == 0) throw new ArgumentOutOfRangeException(nameof(sourceEventSequence));
            if (presentationRevision == 0) throw new ArgumentOutOfRangeException(nameof(presentationRevision));
            SourceEventSequence = sourceEventSequence;
            PresentationRevision = presentationRevision;
            Kind = kind;
            Payload = payload;
        }

        public ulong SourceEventSequence { get; }
        public ulong PresentationRevision { get; }
        public TerminalPresentationKind Kind { get; }
        public ReadOnlyMemory<byte> Payload { get; }
    }

    public enum TerminalInputKind
    {
        Text = 1,
        Key = 2,
        Paste = 3,
        RawBytes = 4
    }

    public enum TerminalKey
    {
        Enter = 1,
        Tab = 2,
        Backspace = 3,
        Escape = 4,
        Delete = 5,
        Insert = 6,
        Home = 7,
        End = 8,
        PageUp = 9,
        PageDown = 10,
        ArrowUp = 11,
        ArrowDown = 12,
        ArrowLeft = 13,
        ArrowRight = 14,
        F1 = 21,
        F2 = 22,
        F3 = 23,
        F4 = 24,
        F5 = 25,
        F6 = 26,
        F7 = 27,
        F8 = 28,
        F9 = 29,
        F10 = 30,
        F11 = 31,
        F12 = 32
    }

    [Flags]
    public enum TerminalInputModifiers
    {
        None = 0,
        Control = 1,
        Alt = 2,
        Shift = 4,
        Meta = 8
    }

    public sealed class TerminalInput
    {
        private TerminalInput(
            TerminalInputKind kind,
            string text,
            TerminalKey key,
            ReadOnlyMemory<byte> bytes,
            TerminalInputModifiers modifiers)
        {
            Kind = kind;
            Text = text;
            Key = key;
            Bytes = bytes;
            Modifiers = modifiers;
        }

        public TerminalInputKind Kind { get; }
        public string Text { get; }
        public TerminalKey Key { get; }
        public ReadOnlyMemory<byte> Bytes { get; }
        public TerminalInputModifiers Modifiers { get; }

        public static TerminalInput FromText(string text, TerminalInputModifiers modifiers = TerminalInputModifiers.None) =>
            new TerminalInput(TerminalInputKind.Text, text ?? throw new ArgumentNullException(nameof(text)), default, default, modifiers);

        public static TerminalInput FromKey(TerminalKey key, TerminalInputModifiers modifiers = TerminalInputModifiers.None) =>
            new TerminalInput(TerminalInputKind.Key, null, key, default, modifiers);

        public static TerminalInput FromPaste(ReadOnlyMemory<byte> bytes) =>
            new TerminalInput(TerminalInputKind.Paste, null, default, bytes, TerminalInputModifiers.None);

        public static TerminalInput FromRawBytes(ReadOnlyMemory<byte> bytes) =>
            new TerminalInput(TerminalInputKind.RawBytes, null, default, bytes, TerminalInputModifiers.None);
    }

    public interface ITerminalSessionSource
    {
        IAsyncEnumerable<TerminalSourceEvent> ReadAsync(ulong afterSequence, CancellationToken cancellationToken);
    }

    public interface IOrderedTerminalJournal
    {
        ValueTask AppendAsync(TerminalSourceEvent item, CancellationToken cancellationToken);
        IAsyncEnumerable<TerminalSourceEvent> ReplayAsync(ulong afterSequence, CancellationToken cancellationToken);
    }

    public interface ITerminalPresentationProvider
    {
        ValueTask<TerminalPresentation> CreateSnapshotAsync(ulong sourceEventSequence, CancellationToken cancellationToken);
        ValueTask<TerminalPresentation> CreateDeltaAsync(ulong afterRevision, ulong throughSourceEventSequence, CancellationToken cancellationToken);
    }

    public interface ITerminalPresentationConsumer
    {
        ulong PresentationRevision { get; }
        ValueTask ApplyAsync(TerminalPresentation update, CancellationToken cancellationToken);
        ValueTask RequireResyncAsync(ulong expectedRevision, CancellationToken cancellationToken);
    }

    public interface ITerminalInputEncoder
    {
        byte[] Encode(TerminalInput input);
    }

    public interface ITerminalTransport
    {
        IReadOnlyCollection<string> Capabilities { get; }
        ValueTask SendSourceEventAsync(TerminalSourceEvent item, CancellationToken cancellationToken);
        ValueTask SendPresentationAsync(TerminalPresentation update, CancellationToken cancellationToken);
        ValueTask RequestResyncAsync(ulong expectedRevision, CancellationToken cancellationToken);
    }
}
