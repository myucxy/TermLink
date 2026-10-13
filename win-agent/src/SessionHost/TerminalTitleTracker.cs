using System;
using System.Collections.Generic;
using System.Text;
using TermLink.LocalIpc;

namespace TermLink.SessionHost
{
    /// <summary>
    /// Observes OSC 0/2/21 window-title controls in the exact ConPTY output byte
    /// stream. Bytes are never removed or changed; this is a side-band parser
    /// used only to publish ordered title metadata after the corresponding
    /// output frame has been committed.
    /// </summary>
    internal sealed class TerminalTitleTracker
    {
        private enum ParseState
        {
            Normal,
            Escape,
            OscCommand,
            OscPayload,
            OscEscape,
            IgnoreOsc,
            IgnoreOscEscape
        }

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly List<byte> command = new List<byte>(4);
        private readonly List<byte> payload = new List<byte>(256);
        private ParseState state;
        private bool titleCommand;

        internal void Process(byte[] bytes, Action<string> titleObserved)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (titleObserved == null) throw new ArgumentNullException("titleObserved");
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                switch (state)
                {
                    case ParseState.Normal:
                        if (value == 0x1b) state = ParseState.Escape;
                        break;
                    case ParseState.Escape:
                        if (value == (byte)']')
                        {
                            command.Clear();
                            payload.Clear();
                            titleCommand = false;
                            state = ParseState.OscCommand;
                        }
                        else
                        {
                            state = value == 0x1b ? ParseState.Escape : ParseState.Normal;
                        }
                        break;
                    case ParseState.OscCommand:
                        if (value == (byte)';')
                        {
                            titleCommand = IsTitleCommand(command);
                            state = titleCommand ? ParseState.OscPayload : ParseState.IgnoreOsc;
                        }
                        else if (value == 0x07)
                        {
                            Reset();
                        }
                        else if (value == 0x1b)
                        {
                            state = ParseState.IgnoreOscEscape;
                        }
                        else if (command.Count < 4 && value >= (byte)'0' && value <= (byte)'9')
                        {
                            command.Add(value);
                        }
                        else
                        {
                            state = ParseState.IgnoreOsc;
                        }
                        break;
                    case ParseState.OscPayload:
                        if (value == 0x07)
                        {
                            Complete(titleObserved);
                        }
                        else if (value == 0x1b)
                        {
                            state = ParseState.OscEscape;
                        }
                        else if (payload.Count < LocalPayloadCodec.MaximumTitleBytes)
                        {
                            payload.Add(value);
                        }
                        else
                        {
                            state = ParseState.IgnoreOsc;
                        }
                        break;
                    case ParseState.OscEscape:
                        if (value == (byte)'\\') Complete(titleObserved);
                        else
                        {
                            if (payload.Count + 2 <= LocalPayloadCodec.MaximumTitleBytes)
                            {
                                payload.Add(0x1b);
                                payload.Add(value);
                                state = ParseState.OscPayload;
                            }
                            else state = ParseState.IgnoreOsc;
                        }
                        break;
                    case ParseState.IgnoreOsc:
                        if (value == 0x07) Reset();
                        else if (value == 0x1b) state = ParseState.IgnoreOscEscape;
                        break;
                    case ParseState.IgnoreOscEscape:
                        if (value == (byte)'\\') Reset();
                        else state = value == 0x1b ? ParseState.IgnoreOscEscape : ParseState.IgnoreOsc;
                        break;
                }
            }
        }

        private void Complete(Action<string> titleObserved)
        {
            try
            {
                if (!titleCommand) return;
                var title = StrictUtf8.GetString(payload.ToArray());
                // Validate/sanitize through the shared local protocol contract.
                var encoded = LocalPayloadCodec.Title(title);
                titleObserved(LocalPayloadCodec.ParseTitle(encoded));
            }
            catch (DecoderFallbackException)
            {
                // Invalid OSC text remains ordinary terminal output but is not
                // promoted into trusted session metadata.
            }
            finally
            {
                Reset();
            }
        }

        private static bool IsTitleCommand(IList<byte> value)
        {
            var selector = 0;
            foreach (var digit in value) selector = selector * 10 + digit - (byte)'0';
            return selector == 0 || selector == 2 || selector == 21;
        }

        private void Reset()
        {
            command.Clear();
            payload.Clear();
            titleCommand = false;
            state = ParseState.Normal;
        }
    }
}
