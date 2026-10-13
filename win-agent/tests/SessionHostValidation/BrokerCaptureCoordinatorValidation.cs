using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class BrokerCaptureCoordinatorValidation
{
    internal static void Run()
    {
        var session = Guid.NewGuid();
        var basePath = Path.Combine(Path.GetTempPath(), "termlink-broker-coordinator-" + session.ToString("N"));
        var working = Path.Combine(basePath, "working");
        var root = Path.Combine(basePath, "history", "broker-captures");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(root);
        ProtectDirectory(Path.GetDirectoryName(root)!);
        ProtectDirectory(root);
        try
        {
            using var authority = new TerminalCoreAuthority(20, 10, _ => { }, null,
                semanticProfile: TerminalSemanticProfile.Graphics);
            authority.ApplyDurable(new LocalFrame(LocalFrameType.HostGeometry, session, 1,
                new NativeTerminalHostGeometryV1(9, 18, 360, 216, 360, 216).Encode()));
            using var coordinator = new BrokerCaptureCoordinator(root, working, session);
            var pixels = new byte[] { 255, 0, 0, 255 };
            var ordinary = Path.Combine(working, "image.rgba");
            File.WriteAllBytes(ordinary, pixels);
            var first = Apply(coordinator, authority, 2, "f", "image.rgba", 31);
            CheckCapture(root, session, first, 2, pixels, 0);
            Require(File.Exists(ordinary), "Ordinary file was deleted after capture.");

            var temporary = Path.Combine(coordinator.TemporaryDirectory, "transient.rgba");
            File.WriteAllBytes(temporary, pixels);
            var second = Apply(coordinator, authority, 3, "t", temporary.Replace('\\', '/'), 32);
            CheckCapture(root, session, second, 3, pixels, 0);
            Require(!File.Exists(temporary), "Temporary file survived durable native commit.");

            var outside = Path.Combine(basePath, "outside.rgba");
            File.WriteAllBytes(outside, pixels);
            var denied = Apply(coordinator, authority, 4, "f", outside, 33);
            CheckCapture(root, session, denied, 4, Array.Empty<byte>(), (uint)NativeTerminalBrokerFailure.Denied);

            var absolute = Apply(coordinator, authority, 5, "f", ordinary, 34);
            CheckCapture(root, session, absolute, 5, pixels, 0);

            var sessionDirectory = Path.Combine(root, session.ToString("N"));
            for (var count = Directory.EnumerateFiles(sessionDirectory).Count();
                 count < KittyCaptureStore.MaxSessionObjects; count++)
                File.WriteAllBytes(Path.Combine(sessionDirectory,
                    Guid.NewGuid().ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending"), []);
            var overLimit = Apply(coordinator, authority, 6, "f", ordinary, 35);
            CheckCapture(root, session, overLimit, 6, Array.Empty<byte>(),
                (uint)NativeTerminalBrokerFailure.OverLimit);
            Require(File.Exists(ordinary), "Quota fallback deleted the source file.");
            Console.WriteLine("Broker coordinator: relative/absolute file, absolute TMP, denied escape and durable quota fallback passed.");
        }
        finally
        {
            var expectedPrefix = Path.Combine(Path.GetTempPath(), "termlink-broker-coordinator-");
            if (basePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    private static (LocalFrameType Type, byte[] Payload) Apply(BrokerCaptureCoordinator coordinator,
        TerminalCoreAuthority authority, ulong sequence, string medium, string reference, int imageId)
    {
        LocalFrameType? type = null;
        byte[] payload = null;
        var path = Convert.ToBase64String(Encoding.UTF8.GetBytes(reference));
        var output = Encoding.ASCII.GetBytes($"\x1b_Ga=T,t={medium},f=32,s=1,v=1,S=4,i={imageId};{path}\x1b\\");
        coordinator.Apply(authority, sequence, output, (kind, bytes) =>
        {
            Require(type == null, "One Output published more than once.");
            type = kind;
            payload = bytes;
        });
        Require(type == LocalFrameType.BrokerOutput && payload != null,
            "File request did not publish a Broker Output.");
        return (type.Value, payload);
    }

    private static void CheckCapture(string root, Guid session,
        (LocalFrameType Type, byte[] Payload) frame, ulong sequence, byte[] expected, uint outcome)
    {
        using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
        Require(reference.SourceSequence == sequence, "Broker reference sequence changed.");
        byte[] canonical;
        if (reference.IsInline)
            canonical = reference.CopyInlineCaptures();
        else
        {
            using var store = new KittyCaptureStore(root, session);
            canonical = store.ReadExact(session, sequence, reference.ObjectId,
                reference.CaptureBytes, reference.CopySha256());
        }
        using var group = NativeTerminalBrokerCaptureCodecV1.Decode(canonical);
        Require(group.SourceSequence == sequence && group.Records.Count == 1 &&
            group.Records[0].Outcome == outcome &&
            group.Records[0].CopyCaptureBytes().SequenceEqual(expected) &&
            reference.IsInline == (outcome != 0), "Broker capture contents or failure result changed.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void ProtectDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Test Windows identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
