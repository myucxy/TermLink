using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TermLink.TerminalCore;

public sealed partial class NativeTerminal
{
    public unsafe (NativeTerminalHostEffectFrameV2 Effects, byte[] Projection) ApplyOutputWithLegacyProjection(ulong sequence, byte[] output)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        lock (sync) {
            ThrowIfUnavailable();
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try {
                NativeTerminalStatus status;
                fixed (byte* pointer = output) {
                    var input = NativeTerminalInterop.CreateOutputEvent(sequence, pointer, (ulong)output.Length);
                    status = ApplyWithProjection(handle, in input, ref buffer);
                }
                if (status != NativeTerminalStatus.Ok) {
                    // Reuse the normal apply path's retirement, root diagnostic and ownership checks.
                    _ = CompleteHostEffectApplyV2(status, ref buffer, "legacyProjection");
                    throw new NativeTerminalAbiException("Unexpected success from a failed projection apply.");
                }
                if (buffer.AbiVersion != 1 || buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero || buffer.Length < 80 || buffer.Length > 16UL + 123033008UL + 8388608UL)
                    throw new NativeTerminalAbiException("Invalid legacy projection buffer.");
                var bytes = new byte[checked((int)buffer.Length)]; Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 84,76,80,74,49,0,0,0 }))
                    throw new NativeTerminalAbiException("Invalid legacy projection magic.");
                var effectsLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                var projectionLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                if (effectsLength < 64 || effectsLength > NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes ||
                    projectionLength > 8388608 || 16UL + effectsLength + projectionLength != (ulong)bytes.Length)
                    throw new NativeTerminalAbiException("Invalid legacy projection bounds.");
                return (NativeTerminalHostEffectFrameV2Decoder.Decode(bytes.AsSpan(16, (int)effectsLength).ToArray()),
                    bytes.AsSpan(16 + (int)effectsLength, (int)projectionLength).ToArray());
            } catch (NativeTerminalException) { throw; }
            catch { failedClosed = true; throw; }
            finally {
                if (HasOwnedBufferMetadata(in buffer)) {
                    var error = TryReleaseHostEffectBuffer(ref buffer);
                    if (error != null) { failedClosed = true; throw new NativeTerminalAbiException("Could not release legacy projection.", error); }
                }
            }
        }
    }

    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_apply_event_with_legacy_projection_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus ApplyWithProjection(SafeTlTerminalHandle terminal, in TlTerminalEvent input, ref TlTerminalBuffer output);
}
