using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Test-only capture of the default Windows render endpoint. It never changes endpoint policy.
internal static class WindowsLoopbackRecorder
{
    private const uint ClsctxAll = 23;
    private const uint Loopback = 0x00020000;
    private const uint DataDiscontinuity = 0x00000001;
    private const uint Silent = 0x00000002;
    private const int MaximumDiscontinuityDetails = 64;
    private const int MaximumBytesPerSecond = 2 * 1024 * 1024;
    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorIid = typeof(IMMDeviceEnumerator).GUID;
    private static readonly Guid AudioClientIid = typeof(IAudioClient).GUID;
    private static readonly Guid CaptureClientIid = typeof(IAudioCaptureClient).GUID;
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubformat = new("00000003-0000-0010-8000-00AA00389B71");

    internal sealed record DiscontinuityDetail(long FrameOffset, uint PacketFrames, ulong DevicePosition,
        ulong QpcPosition, uint Flags, bool FirstPacket);
    internal sealed record Result(string WavPath, string EndpointId, string Encoding, int Channels,
        uint SampleRate, int BitsPerSample, long ExpectedFrames, long Frames, long Bytes, int Discontinuities,
        IReadOnlyList<DiscontinuityDetail> DiscontinuityDetails, bool DiscontinuityDetailsTruncated);

    internal static Result Record(string outputPath, int seconds)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
        if (seconds is < 2 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!Path.IsPathFullyQualified(outputPath) || !Path.GetExtension(outputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide an absolute .wav output path.", nameof(outputPath));
        outputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(Path.GetDirectoryName(outputPath))) throw new DirectoryNotFoundException("WAV parent directory is missing.");
        if (File.Exists(outputPath)) throw new IOException("WAV destination already exists.");

        string? temporary = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr mixPointer = IntPtr.Zero;
        var initialized = false;
        var started = false;
        try {
            Check(CoInitializeEx(IntPtr.Zero, 2), "CoInitializeEx");
            initialized = true;
            var clsid = EnumeratorClsid;
            var iid = EnumeratorIid;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out var enumeratorPointer), "CoCreateInstance");
            try { enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumeratorPointer); }
            finally { Marshal.Release(enumeratorPointer); }
            Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device), "GetDefaultAudioEndpoint(render, multimedia)");
            Check(device.GetId(out var endpointPointer), "IMMDevice.GetId");
            string endpointId;
            try { endpointId = Marshal.PtrToStringUni(endpointPointer) ?? ""; }
            finally { Marshal.FreeCoTaskMem(endpointPointer); }
            iid = AudioClientIid;
            Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var clientObject), "IMMDevice.Activate(IAudioClient)");
            client = (IAudioClient)clientObject;
            Check(client.GetMixFormat(out mixPointer), "IAudioClient.GetMixFormat");
            if (mixPointer == IntPtr.Zero) throw new InvalidOperationException("GetMixFormat returned a null pointer.");
            var baseFormat = Marshal.PtrToStructure<WaveFormatEx>(mixPointer);
            if (baseFormat.ExtraSize > 256) throw new NotSupportedException("Mix format extension is too large.");
            var rawFormat = new byte[18 + baseFormat.ExtraSize];
            Marshal.Copy(mixPointer, rawFormat, 0, rawFormat.Length);
            var format = FormatInfo.Parse(rawFormat);
            var expectedFrames = checked((long)format.SampleRate * seconds);
            if (expectedFrames * format.BlockAlign > 128L * 1024 * 1024)
                throw new NotSupportedException("Requested capture exceeds 128 MiB.");
            Check(client.Initialize(0, Loopback, 10_000_000, 0, mixPointer, IntPtr.Zero), "IAudioClient.Initialize(loopback)");
            Check(client.GetBufferSize(out var bufferFrames), "IAudioClient.GetBufferSize");
            if (bufferFrames == 0 || (long)bufferFrames * format.BlockAlign > 8L * 1024 * 1024)
                throw new NotSupportedException("WASAPI capture buffer exceeds 8 MiB.");
            iid = CaptureClientIid;
            Check(client.GetService(ref iid, out var captureObject), "IAudioClient.GetService(IAudioCaptureClient)");
            capture = (IAudioCaptureClient)captureObject;

            temporary = outputPath + ".partial." + Guid.NewGuid().ToString("N");
            long frames = 0;
            var discontinuities = 0;
            var discontinuityDetails = new List<DiscontinuityDetail>();
            var firstPacket = true;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan)) {
                var header = WriteHeader(stream, format);
                var buffer = new byte[64 * 1024];
                Check(client.Start(), "IAudioClient.Start");
                started = true;
                Console.Error.WriteLine("LOOPBACK_READY " + endpointId);
                Console.Error.Flush();
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && frames < expectedFrames) {
                    Drain(capture, stream, format, bufferFrames, expectedFrames, ref frames, ref discontinuities,
                        ref firstPacket, discontinuityDetails, buffer);
                    if (frames < expectedFrames) Thread.Sleep(10);
                }
                Drain(capture, stream, format, bufferFrames, expectedFrames, ref frames, ref discontinuities,
                    ref firstPacket, discontinuityDetails, buffer);
                Check(client.Stop(), "IAudioClient.Stop");
                started = false;
                CompleteHeader(stream, header, format, frames * format.BlockAlign);
                stream.Flush(true);
            }
            File.Move(temporary, outputPath);
            temporary = null;
            return new Result(outputPath, endpointId, format.Encoding, format.Channels, format.SampleRate,
                format.BitsPerSample, expectedFrames, frames, frames * format.BlockAlign, discontinuities,
                discontinuityDetails.ToArray(), discontinuities > discontinuityDetails.Count);
        } finally {
            if (started && client != null) _ = client.Stop();
            if (mixPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(mixPointer);
            if (capture != null) Marshal.FinalReleaseComObject(capture);
            if (client != null) Marshal.FinalReleaseComObject(client);
            if (device != null) Marshal.FinalReleaseComObject(device);
            if (enumerator != null) Marshal.FinalReleaseComObject(enumerator);
            if (initialized) CoUninitialize();
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Drain(IAudioCaptureClient capture, Stream stream, FormatInfo format, uint bufferFrames,
        long expectedFrames, ref long frames, ref int discontinuities, ref bool firstPacket,
        List<DiscontinuityDetail> discontinuityDetails, byte[] buffer)
    {
        for (var packets = 0; packets < 4096 && frames < expectedFrames; packets++) {
            Check(capture.GetNextPacketSize(out var available), "IAudioCaptureClient.GetNextPacketSize");
            if (available == 0) return;
            if (available > bufferFrames) throw new InvalidOperationException("WASAPI packet exceeds the endpoint buffer.");
            Check(capture.GetBuffer(out var data, out var packetFrames, out var flags,
                out var devicePosition, out var qpcPosition),
                "IAudioCaptureClient.GetBuffer");
            try {
                if (packetFrames == 0 || packetFrames > bufferFrames)
                    throw new InvalidOperationException("WASAPI returned an invalid packet size.");
                if ((flags & DataDiscontinuity) != 0) {
                    discontinuities++;
                    if (discontinuityDetails.Count < MaximumDiscontinuityDetails)
                        discontinuityDetails.Add(new DiscontinuityDetail(frames, packetFrames, devicePosition,
                            qpcPosition, flags, firstPacket));
                }
                firstPacket = false;
                if ((flags & Silent) == 0 && data == IntPtr.Zero)
                    throw new InvalidOperationException("WASAPI returned a null non-silent packet.");
                var writeFrames = Math.Min((long)packetFrames, expectedFrames - frames);
                var frameOffset = 0L;
                var chunkFrames = buffer.Length / format.BlockAlign;
                while (frameOffset < writeFrames) {
                    var count = (int)Math.Min(chunkFrames, writeFrames - frameOffset);
                    var bytes = count * format.BlockAlign;
                    if ((flags & Silent) != 0)
                        Array.Fill(buffer, (byte)(format.EffectiveTag == 1 && format.BitsPerSample == 8 ? 0x80 : 0), 0, bytes);
                    else Marshal.Copy(IntPtr.Add(data, checked((int)(frameOffset * format.BlockAlign))), buffer, 0, bytes);
                    stream.Write(buffer, 0, bytes);
                    frameOffset += count;
                }
                frames += writeFrames;
            } finally { Check(capture.ReleaseBuffer(packetFrames), "IAudioCaptureClient.ReleaseBuffer"); }
        }
        if (frames < expectedFrames) throw new InvalidOperationException("WASAPI packet budget exceeded.");
    }

    private sealed record FormatInfo(byte[] Raw, ushort Tag, ushort EffectiveTag, int Channels,
        uint SampleRate, int BitsPerSample, int BlockAlign, string Encoding)
    {
        internal static FormatInfo Parse(byte[] raw)
        {
            if (raw.Length < 18) throw new InvalidDataException("Mix format is shorter than WAVEFORMATEX.");
            var span = raw.AsSpan();
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            var rate = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            var avg = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            var align = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
            if (raw.Length != 18 + extra || extra > 256) throw new InvalidDataException("Mix format extension length is invalid.");
            ushort effective = tag;
            if (tag == 0xfffe) {
                if (extra < 22) throw new NotSupportedException("WAVEFORMATEXTENSIBLE is incomplete.");
                var subformat = new Guid(span.Slice(24, 16));
                effective = subformat == PcmSubformat ? (ushort)1 : subformat == FloatSubformat ? (ushort)3 : (ushort)0;
                var validBits = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
                if (validBits == 0 || validBits > bits) throw new InvalidDataException("Mix format valid bits are invalid.");
            }
            if (effective == 1 ? bits is not (8 or 16 or 24 or 32) :
                effective == 3 ? bits is not (32 or 64) : true)
                throw new NotSupportedException("Only PCM and IEEE float WASAPI mix formats are supported.");
            if (channels is < 1 or > 8 || rate is < 8000 or > 192000 ||
                align != channels * bits / 8 || avg != rate * align || avg > MaximumBytesPerSecond)
                throw new NotSupportedException("WASAPI mix format exceeds the capture bounds.");
            return new FormatInfo(raw, tag, effective, channels, rate, bits, align,
                effective == 1 ? "PCM" : "IEEE_FLOAT");
        }
    }

    private readonly record struct Header(long RiffSize, long FactFrames, long DataSize, long DataStart);

    private static Header WriteHeader(Stream stream, FormatInfo format)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        var riffSize = stream.Position; writer.Write(0u);
        writer.Write("WAVEfmt "u8);
        var fmtSize = format.Tag == 1 && format.Raw.Length == 18 ? 16 : format.Raw.Length;
        writer.Write((uint)fmtSize);
        writer.Write(format.Raw, 0, fmtSize);
        if ((fmtSize & 1) != 0) writer.Write((byte)0);
        var factFrames = -1L;
        if (format.Tag != 1) {
            writer.Write("fact"u8); writer.Write(4u);
            factFrames = stream.Position; writer.Write(0u);
        }
        writer.Write("data"u8);
        var dataSize = stream.Position; writer.Write(0u);
        return new Header(riffSize, factFrames, dataSize, stream.Position);
    }

    private static void CompleteHeader(Stream stream, Header header, FormatInfo format, long dataBytes)
    {
        if (dataBytes < 0 || dataBytes % format.BlockAlign != 0 ||
            stream.Length != header.DataStart + dataBytes || dataBytes > uint.MaxValue)
            throw new InvalidDataException("WAV data is not frame-aligned or exceeds RIFF bounds.");
        if ((dataBytes & 1) != 0) stream.WriteByte(0); // RIFF chunks are word-aligned.
        if (stream.Length - 8 > uint.MaxValue)
            throw new InvalidDataException("WAV exceeds RIFF bounds.");
        var end = stream.Position;
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = header.RiffSize; writer.Write(checked((uint)(stream.Length - 8)));
        if (header.FactFrames >= 0) {
            stream.Position = header.FactFrames; writer.Write(checked((uint)(dataBytes / format.BlockAlign)));
        }
        stream.Position = header.DataSize; writer.Write(checked((uint)dataBytes));
        stream.Position = end;
    }

    internal static void SelfCheck()
    {
        static byte[] Format(ushort tag, ushort channels, uint rate, ushort bits, byte[] extra) {
            using var data = new MemoryStream();
            using var writer = new BinaryWriter(data);
            var align = checked((ushort)(channels * bits / 8));
            writer.Write(tag); writer.Write(channels); writer.Write(rate); writer.Write(rate * align);
            writer.Write(align); writer.Write(bits); writer.Write(checked((ushort)extra.Length)); writer.Write(extra);
            return data.ToArray();
        }
        static void CheckWave(FormatInfo format, int frames, int expectedFmtLength) {
            using var data = new MemoryStream();
            var header = WriteHeader(data, format);
            data.Write(new byte[frames * format.BlockAlign]);
            CompleteHeader(data, header, format, frames * format.BlockAlign);
            var bytes = data.ToArray();
            if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != expectedFmtLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)header.DataSize)) != frames * format.BlockAlign ||
                header.DataStart + frames * format.BlockAlign != bytes.Length)
                throw new InvalidDataException("WAV header self-check failed.");
            if (header.FactFrames >= 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)header.FactFrames)) != frames)
                throw new InvalidDataException("WAV fact frame count self-check failed.");
            try { CompleteHeader(data, header, format, frames * format.BlockAlign - 1); }
            catch (InvalidDataException) { return; }
            throw new InvalidDataException("WAV accepted non-aligned data.");
        }
        var pcm = FormatInfo.Parse(Format(1, 2, 48000, 16, []));
        CheckWave(pcm, 2, 16);
        var odd = FormatInfo.Parse(Format(1, 1, 48000, 8, []));
        using (var data = new MemoryStream()) {
            var header = WriteHeader(data, odd);
            data.Write(new byte[3]);
            CompleteHeader(data, header, odd, 3);
            if (data.Length != header.DataStart + 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(data.ToArray().AsSpan((int)header.DataSize)) != 3)
                throw new InvalidDataException("WAV odd-sized chunk padding self-check failed.");
        }
        var extension = new byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(extension, 32);
        FloatSubformat.TryWriteBytes(extension.AsSpan(6));
        var floating = FormatInfo.Parse(Format(0xfffe, 2, 48000, 32, extension));
        CheckWave(floating, 2, 40);
        var invalid = Format(1, 2, 48000, 16, []);
        invalid[12] = 3;
        try { _ = FormatInfo.Parse(invalid); }
        catch (NotSupportedException) { return; }
        throw new InvalidDataException("WAV accepted an invalid block alignment.");
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"{operation} failed: HRESULT 0x{hr:X8}.");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, uint clsctx, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid, out IntPtr instance);
}
