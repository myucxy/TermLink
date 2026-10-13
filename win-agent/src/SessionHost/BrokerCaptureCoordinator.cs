using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.SessionHost;

// One Graphics Host owns the file handles until its exact Output has been durably applied.
internal sealed class BrokerCaptureCoordinator : IDisposable
{
    private const uint FileReadData = 1;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly KittyCaptureStore captures;
    private readonly KittyFileBroker files;
    private readonly KittyFileBroker temporaryFiles;
    private readonly List<KittyFileBroker.CapturedFile> held = new();
    private int capturedBytes;
    private readonly Guid sessionId;
    private readonly string workingDirectoryRoot;
    private KittySharedMemoryBroker sharedMemory;
    private bool disposed;

    internal string TemporaryDirectory { get; }

    internal void AttachSharedMemory(KittySharedMemoryBroker broker) =>
        sharedMemory = broker ?? throw new ArgumentNullException(nameof(broker));

    internal BrokerCaptureCoordinator(string captureRoot, string workingDirectory, Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidDataException("Graphics broker working directory is unavailable.");
        this.sessionId = sessionId;
        workingDirectoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var root = LocalPayloadCodec.ParseBrokerCaptureRoot(LocalPayloadCodec.BrokerCaptureRoot(captureRoot));
        ValidateTrustedDirectory(root);
        captures = new KittyCaptureStore(root, sessionId);
        try
        {
            var historyRoot = Path.GetDirectoryName(root) ?? throw new InvalidDataException("Broker history root is unavailable.");
            ValidateTrustedDirectory(historyRoot);
            var tempParent = Path.Combine(historyRoot, "broker-temp");
            Directory.CreateDirectory(tempParent);
            ProtectPrivateDirectory(tempParent);
            TemporaryDirectory = Path.Combine(tempParent, sessionId.ToString("N"));
            Directory.CreateDirectory(TemporaryDirectory);
            ProtectPrivateDirectory(TemporaryDirectory);
            using var workingHandle = OpenDirectory(workingDirectory);
            using var tempHandle = OpenDirectory(TemporaryDirectory);
            files = new KittyFileBroker(workingHandle);
            try { temporaryFiles = new KittyFileBroker(tempHandle); }
            catch { files.Dispose(); throw; }
        }
        catch { captures.Dispose(); throw; }
    }

    internal void Apply(TerminalCoreAuthority authority, ulong sequence, byte[] output,
        Action<LocalFrameType, byte[]> publishDurable)
    {
        if (disposed) throw new ObjectDisposedException(nameof(BrokerCaptureCoordinator));
        if (authority == null || output == null || publishDurable == null)
            throw new ArgumentNullException();
        try
        {
            try
            {
                authority.ApplyCapturedOutput(sequence, output, Resolve,
                    (_, records) => Publish(sequence, output, records, publishDurable));
            }
            catch (KittyCaptureQuotaExceededException)
            {
                // The first native transaction was canceled before publication. Repeat the exact bytes
                // with durable OverLimit records so this Output cannot silently lose its image requests.
                ReleaseHeld(deleteTemporary: false);
                authority.ApplyCapturedOutput(sequence, output,
                    _ => NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.OverLimit),
                    (_, records) => Publish(sequence, output, records, publishDurable));
            }
            ReleaseHeld(deleteTemporary: true);
        }
        catch
        {
            ReleaseHeld(deleteTemporary: false);
            throw;
        }
    }

    private NativeTerminalBrokerCapture Resolve(NativeTerminalBrokerRequest request)
    {
        if (request.Medium == (byte)'s')
        {
            if (sharedMemory == null)
                return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.Unavailable);
            var (bytes, failure) = sharedMemory.Capture(request.Reference, request.ReadOffset, request.ReadLength);
            if (failure.HasValue) return NativeTerminalBrokerCapture.FromFailure(failure.Value);
            try
            {
                if (bytes.Length > NativeTerminalBrokerOutputReferenceV1.MaximumCaptureBytes -
                    NativeTerminalBrokerOutputReferenceV1.MaximumInlineBytes - capturedBytes)
                    return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.OverLimit);
                capturedBytes += bytes.Length;
                return NativeTerminalBrokerCapture.FromBytes(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (request.Medium != (byte)'f' && request.Medium != (byte)'t')
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.UnsupportedMedium);
        try
        {
            var file = (request.Medium == (byte)'t' ? temporaryFiles : files)
                .Capture(AuthorizedRelativePath(request.Reference,
                    request.Medium == (byte)'t' ? TemporaryDirectory : workingDirectoryRoot),
                    request.ReadOffset, request.ReadLength,
                    temporary: request.Medium == (byte)'t');
            // Leave room for the maximum 64 TLCAPV1 record headers in the same 32 MiB group.
            if (file.Bytes.Length > NativeTerminalBrokerOutputReferenceV1.MaximumCaptureBytes -
                NativeTerminalBrokerOutputReferenceV1.MaximumInlineBytes - capturedBytes)
            {
                file.Dispose();
                return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.OverLimit);
            }
            held.Add(file);
            capturedBytes += file.Bytes.Length;
            var bytes = file.Bytes.ToArray();
            try { return NativeTerminalBrokerCapture.FromBytes(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (ArgumentException)
        {
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.Denied);
        }
        catch (NotSupportedException)
        {
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.Denied);
        }
        catch (UnauthorizedAccessException)
        {
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.Denied);
        }
        catch (KittyFileCaptureLimitException)
        {
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.OverLimit);
        }
        catch (IOException)
        {
            return NativeTerminalBrokerCapture.FromFailure(NativeTerminalBrokerFailure.Unavailable);
        }
    }

    private static string AuthorizedRelativePath(string reference, string root)
    {
        if (reference == null) throw new ArgumentException("A broker file reference is required.");
        var path = reference.Replace('/', '\\');
        if (Path.IsPathFullyQualified(path))
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Broker file is outside its authorized root.");
            path = full[prefix.Length..];
        }
        else if (Path.IsPathRooted(path))
            throw new ArgumentException("Broker file is not fully qualified.");
        KittyFileBroker.ValidateRelativePath(path);
        return path;
    }

    private void Publish(ulong sequence, byte[] output, IReadOnlyList<NativeTerminalBrokerRecordV1> records,
        Action<LocalFrameType, byte[]> publishDurable)
    {
        if (records.Count == 0)
        {
            publishDurable(LocalFrameType.Output, output);
            return;
        }
        var canonical = NativeTerminalBrokerCaptureCodecV1.Encode(sequence, records);
        try
        {
            using NativeTerminalBrokerOutputReferenceV1 reference =
                records.All(record => record.Outcome != 0) &&
                canonical.Length <= NativeTerminalBrokerOutputReferenceV1.MaximumInlineBytes
                    ? NativeTerminalBrokerOutputReferenceV1.InlineFailures(sequence, output, canonical)
                    : CreateExternalReference(sequence, output, canonical);
            publishDurable(LocalFrameType.BrokerOutput, reference.Encode());
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private NativeTerminalBrokerOutputReferenceV1 CreateExternalReference(ulong sequence,
        byte[] output, byte[] canonical)
    {
        var receipt = captures.Put(Guid.NewGuid(), sequence, canonical);
        try { return NativeTerminalBrokerOutputReferenceV1.External(sequence, output,
            receipt.ObjectId, receipt.Length, receipt.Sha256); }
        finally { CryptographicOperations.ZeroMemory(receipt.Sha256); }
    }

    private void ReleaseHeld(bool deleteTemporary)
    {
        foreach (var file in held)
        {
            if (deleteTemporary) file.DeleteAfterDurableApply();
            file.Dispose();
        }
        held.Clear();
        capturedBytes = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseHeld(deleteTemporary: false);
        temporaryFiles.Dispose();
        files.Dispose();
        captures.Dispose();
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Broker file root must be absolute.");
        var handle = CreateFile(Path.GetFullPath(path), FileReadData | FileReadAttributes,
            FileShareReadWriteDelete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Broker file root cannot be opened.");
        }
        return handle;
    }

    private static void ValidateTrustedDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Broker capture root is unavailable or redirected.");
        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new InvalidDataException("Current Windows identity is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var creator = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!current.Equals(owner) && !system.Equals(owner) && !administrators.Equals(owner))
            throw new InvalidDataException("Broker capture root has an untrusted owner.");
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0 &&
                !current.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference) &&
                !administrators.Equals(rule.IdentityReference) && !creator.Equals(rule.IdentityReference))
                throw new InvalidDataException("Broker capture root is writable by another identity.");
        }
    }

    private static void ProtectPrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Broker temporary root is redirected.");
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new InvalidDataException("Current Windows identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(current);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        ValidateTrustedDirectory(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
