using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using TermLink.Common;

namespace TermLink.ProductionTerminalHistory.Validation
{
    internal static class PrivateHistoryDirectoryValidation
    {
        private static readonly FileSystemRights Mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        internal static void Run()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var currentSid = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var temp = Path.GetFullPath(Path.GetTempPath());
            var sandbox = Path.Combine(temp, "termlink-private-history-" + Guid.NewGuid().ToString("N"));
            var storage = Path.Combine(sandbox, "storage");
            var historyParent = Path.Combine(storage, "Data", "History");
            var history = Path.Combine(historyParent, "machine");
            var legacy = Path.Combine(storage, "production-history", "legacy-pc");
            var outside = Path.Combine(sandbox, "outside");
            var link = Path.Combine(storage, "linked-history");
            var hardLink = Path.Combine(history, "linked-capture.bin");
            var siblingHardLink = Path.Combine(historyParent, "sibling-machine", "linked-capture.bin");
            var migrationHistoryParent = Path.Combine(sandbox, "storage-migration", "Data", "History");
            Require(IsChildOf(sandbox, temp), "Test directory escaped the process TEMP root.");
            Directory.CreateDirectory(sandbox);
            try
            {
                AddInheritedUsersWrite(sandbox);
                Directory.CreateDirectory(legacy);
                Directory.CreateDirectory(outside);
                var session = Path.Combine(legacy, "session");
                var captures = Path.Combine(legacy, "broker-captures", "capture-session");
                var currentOwned = Path.Combine(legacy, "owned-by-user");
                var adminOwned = Path.Combine(legacy, "owned-by-admins");
                Directory.CreateDirectory(session);
                Directory.CreateDirectory(captures);
                Directory.CreateDirectory(currentOwned);
                Directory.CreateDirectory(adminOwned);
                SetOwner(currentOwned, currentSid);
                SetOwner(adminOwned, administratorsSid);
                var wal = Path.Combine(session, "history.wal.dpapi");
                var capture = Path.Combine(captures, "image.capture");
                File.WriteAllBytes(wal, [0, 1, 2, 0, 255]);
                File.WriteAllBytes(capture, [9, 8, 7, 6]);
                var walHash = Hash(wal);
                var captureHash = Hash(capture);
                Require(HasForeignMutation(legacy), "Wide inherited ACL fixture was not created.");

                PrivateHistoryDirectory.Prepare(storage, historyParent);
                Directory.Move(legacy, history);
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(OwnerOf(Path.Combine(history, "owned-by-user")).Equals(currentSid) &&
                    OwnerOf(Path.Combine(history, "owned-by-admins")).Equals(administratorsSid),
                    "Preparation changed a trusted Windows owner.");
                Require(Hash(Path.Combine(history, "session", "history.wal.dpapi")) == walHash &&
                    Hash(Path.Combine(history, "broker-captures", "capture-session", "image.capture")) == captureHash,
                    "Preparing migrated history changed existing bytes.");
                Require(HasForeignMutation(sandbox), "Preparing storage changed ACL outside storageRoot.");

                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(Path.Combine(history, "session", "history.wal.dpapi")) == walHash &&
                    Hash(Path.Combine(history, "broker-captures", "capture-session", "image.capture")) == captureHash,
                    "Repeated preparation changed existing bytes.");

                var longDirectory = Path.Combine(history, "broker-captures",
                    new string('s', 96), new string('f', 48));
                var longCapture = Path.Combine(longDirectory, "capture.bin");
                Require(longCapture.Length > 260, "Long-path fixture did not exceed the Win32 legacy path limit.");
                Directory.CreateDirectory(longDirectory);
                File.WriteAllBytes(longCapture, [7, 0, 255, 8, 6]);
                var longHash = Hash(longCapture);
                Require(File.Exists(longCapture), "Long-path capture fixture was not created.");
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(longCapture) == longHash, "Securing the long-path capture changed its bytes.");
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(longCapture) == longHash, "Repeated long-path preparation changed its bytes.");

                // 正斜杠绝对路径的重复 Prepare：递归内容扫描必须使用规范化
                // 后的最终路径；\\?\ 前缀对正斜杠路径无效，重构曾让原始路径
                // 进入枚举并抛 Win32Exception。
                var forwardSlashHistory = storage + "/Data/History/machine";
                PrivateHistoryDirectory.Prepare(storage, forwardSlashHistory);
                PrivateHistoryDirectory.Prepare(storage, forwardSlashHistory);
                ValidatePrivateTree(storage, history);
                Require(Hash(longCapture) == longHash,
                    "Forward-slash path preparation changed existing bytes.");

                // NULL DACL（安全描述符无 DACL 指针）曾使 HasProtectedDacl 空引用：
                // 真实 Prepare 路径验证不再抛异常、收敛到受保护 DACL、既有字节
                // 不变、非授权写被移除；直接 helper 断言 NULL DACL 描述符不被
                // 误判为已保护。
                var nullDaclDirectory = Path.Combine(history, "null-dacl-fixture");
                Directory.CreateDirectory(nullDaclDirectory);
                var nullDaclFile = Path.Combine(nullDaclDirectory, "capture.dat");
                File.WriteAllBytes(nullDaclFile, [3, 1, 4, 1, 5]);
                var nullDaclFileHash = Hash(nullDaclFile);
                // 父目录设置 NULL DACL 会触发 Windows 自动继承传播，把仅有
                // 继承 ACE 的子文件清成空 DACL，随后连安全信息都无法打开
                //（错误 5）。夹具先预持有子文件的 WRITE_DAC 句柄，再设置
                // 父/子 NULL DACL，避免夹具自己移除后续步骤所需权限。
                using (var nullDaclChildHandle = OpenSecurityDescriptorHandle(nullDaclFile))
                {
                    SetNullDacl(nullDaclDirectory);
                    SetNullDacl(nullDaclChildHandle);
                }
                Require(HasNullDacl(nullDaclDirectory) && HasNullDacl(nullDaclFile),
                    "NULL DACL fixture was not created.");
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(nullDaclFile) == nullDaclFileHash,
                    "Recovering a NULL DACL entry changed existing bytes.");
                Require(HasProtectedDaclOnly(nullDaclDirectory) &&
                    HasProtectedDaclOnly(nullDaclFile),
                    "NULL DACL entries did not converge to the protected descriptor.");
                // 验证进程以管理员运行，写入探针无法证明非授权写被移除；
                // 改为断言 DACL 仅保留当前用户/SYSTEM/Administrators 三个
                // 完全控制项，无其他身份。
                var nullDaclSecurity = new FileSecurity(nullDaclFile,
                    AccessControlSections.Access);
                var rules = nullDaclSecurity.GetAccessRules(true, true,
                    typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
                var allowedIdentities = new[]
                {
                    currentSid.Value,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                };
                Require(rules.All(rule =>
                        rule.AccessControlType == AccessControlType.Allow &&
                        allowedIdentities.Contains(rule.IdentityReference.Value) &&
                        (rule.FileSystemRights & FileSystemRights.Write) != 0) &&
                    rules.Select(rule => rule.IdentityReference.Value).Distinct().Count() == 3,
                    "A recovered NULL DACL entry retained identities beyond the protected triplet.");
                Require(!NullDaclDescriptorIsJudgedUnprotected(),
                    "A NULL DACL descriptor was misjudged as already protected.");

                // 初始化耗时对照：旧路径（全量 Prepare 父目录+目标）与新路径
                //（仅祖先链 PreparePaths+完整 Prepare 目标）各 3 次，记录中位数。
                // 兄弟目录与目标目录各写入批量子目录/文件以模拟真实规模；
                // 不做强时限断言，仅输出供审查记录。
                var timingTarget = Path.Combine(history, "timing-target");
                var timingSibling = Path.Combine(historyParent, "timing-sibling-machine");
                Directory.CreateDirectory(timingTarget);
                Directory.CreateDirectory(timingSibling);
                for (var index = 0; index < 60; index++)
                {
                    var sub = Path.Combine(timingTarget, "session-" + index.ToString("D3"));
                    Directory.CreateDirectory(sub);
                    for (var file = 0; file < 8; file++)
                        File.WriteAllBytes(Path.Combine(sub, "chunk-" + file + ".dat"),
                            new byte[64]);
                    var siblingSub = Path.Combine(timingSibling, "session-" + index.ToString("D3"));
                    Directory.CreateDirectory(siblingSub);
                    for (var file = 0; file < 8; file++)
                        File.WriteAllBytes(Path.Combine(siblingSub, "chunk-" + file + ".dat"),
                            new byte[64]);
                }

                var targetFiles = Directory.EnumerateFiles(timingTarget, "*", SearchOption.AllDirectories).Count();
                var siblingFiles = Directory.EnumerateFiles(timingSibling, "*", SearchOption.AllDirectories).Count();
                var previousStyle = new List<double>();
                var currentStyle = new List<double>();
                for (var round = 0; round < 3; round++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    PrivateHistoryDirectory.Prepare(storage, historyParent);
                    PrivateHistoryDirectory.Prepare(storage, history);
                    watch.Stop();
                    previousStyle.Add(watch.Elapsed.TotalMilliseconds);

                    watch.Restart();
                    PrivateHistoryDirectory.PreparePaths(storage, history);
                    PrivateHistoryDirectory.Prepare(storage, history);
                    watch.Stop();
                    currentStyle.Add(watch.Elapsed.TotalMilliseconds);
                }
                double Median(List<double> values)
                {
                    var sorted = values.OrderBy(value => value).ToList();
                    return sorted[sorted.Count / 2];
                }

                Console.WriteLine(
                    $"History init timing: target files={targetFiles}, sibling files={siblingFiles}, " +
                    $"previous-style median={Median(previousStyle):F1} ms " +
                    $"({previousStyle[0]:F1}/{previousStyle[1]:F1}/{previousStyle[2]:F1}), " +
                    $"current-style median={Median(currentStyle):F1} ms " +
                    $"({currentStyle[0]:F1}/{currentStyle[1]:F1}/{currentStyle[2]:F1})");

                // 兄弟目录扫描范围证明：FileShare.None 哨兵无法证明不扫描——
                // 生产打开只申请 READ_CONTROL/WRITE_DAC/READ_ATTRIBUTES，
                // 不受独占数据共享限制。改为在兄弟机器树内放置会被完整内容
                // 扫描拒绝的硬链接夹具：旧式全父目录扫描必须拒绝，而与产品
                // 参数一致的新式调用（祖先链 PreparePaths + 目标 Prepare）
                // 不经过兄弟内容，保持成功。
                var outsideMarker = Path.Combine(outside, "unchanged.bin");
                File.WriteAllBytes(outsideMarker, [4, 3, 2, 1]);
                var outsideHash = Hash(outsideMarker);
                var siblingMachine = Path.Combine(historyParent, "sibling-machine");
                Directory.CreateDirectory(siblingMachine);
                File.WriteAllBytes(Path.Combine(siblingMachine, "sentinel.wal"), [1]);
                // 负对照：兄弟目录干净时旧式父目录扫描通过——后续拒绝确实
                // 来自硬链接夹具，而不是共享冲突或其它夹具因素。
                PrivateHistoryDirectory.Prepare(storage, historyParent);
                Require(CreateHardLink(siblingHardLink, outsideMarker, IntPtr.Zero),
                    "The Windows test environment cannot create the sibling hard-link fixture (" +
                    Marshal.GetLastWin32Error() + ").");
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, historyParent),
                    "A full parent-directory scan accepted a hard link inside a sibling machine history.");
                PrivateHistoryDirectory.PreparePaths(storage, history);
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(File.Exists(siblingHardLink) && Hash(outsideMarker) == outsideHash,
                    "Initializing the current machine changed sibling history or its target.");

                // 迁移前必须拒绝 History 段重解析点：PreparePaths 接收最终
                // historyRoot 并自行排除末段（与产品修复后的真实参数一致）。
                // 拒绝必须发生在移动之前——legacy 原位置与字节不变、外部
                // 目录无写入、目标从未被创建。
                var migrationStorage = Path.Combine(sandbox, "storage-migration");
                var migrationLegacy = Path.Combine(migrationStorage, "production-history", "legacy-pc");
                var migrationHistoryRoot = Path.Combine(migrationHistoryParent, "machine");
                var migrationOutside = Path.Combine(sandbox, "outside-migration");
                Directory.CreateDirectory(Path.Combine(migrationLegacy, "session"));
                var migrationWal = Path.Combine(migrationLegacy, "session", "history.wal.dpapi");
                File.WriteAllBytes(migrationWal, [11, 22, 33, 44, 55]);
                var migrationWalHash = Hash(migrationWal);
                Directory.CreateDirectory(migrationOutside);
                var migrationOutsideMarker = Path.Combine(migrationOutside, "outside-marker.bin");
                File.WriteAllBytes(migrationOutsideMarker, [1, 3, 5, 7]);
                var migrationOutsideHash = Hash(migrationOutsideMarker);
                Directory.CreateDirectory(Path.Combine(migrationStorage, "Data"));
                try { Directory.CreateSymbolicLink(migrationHistoryParent, migrationOutside); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    throw new InvalidOperationException("The Windows test environment cannot create the required directory symlink fixture.", error);
                }
                ExpectRejected(() => PrivateHistoryDirectory.PreparePaths(migrationStorage, migrationHistoryRoot),
                    "A redirected History segment was accepted before legacy migration.");
                Require(Directory.Exists(migrationLegacy) && Hash(migrationWal) == migrationWalHash,
                    "Pre-migration rejection moved or changed the legacy history.");
                Require(Hash(migrationOutsideMarker) == migrationOutsideHash &&
                    !File.Exists(Path.Combine(migrationOutside, "machine")) &&
                    !Directory.Exists(migrationHistoryRoot),
                    "Pre-migration rejection wrote outside the private storage root.");
                // 负对照：History 是真实目录时，同一参数的祖先链准备通过，
                // legacy 迁移与目标最终加固按产品顺序正常完成。
                Directory.Delete(migrationHistoryParent);
                Directory.CreateDirectory(migrationHistoryParent);
                PrivateHistoryDirectory.PreparePaths(migrationStorage, migrationHistoryRoot);
                PrivateHistoryDirectory.Prepare(migrationStorage, migrationLegacy);
                Directory.Move(migrationLegacy, migrationHistoryRoot);
                PrivateHistoryDirectory.Prepare(migrationStorage, migrationHistoryRoot);
                ValidatePrivateTree(migrationStorage, migrationHistoryRoot);
                Require(Hash(Path.Combine(migrationHistoryRoot, "session", "history.wal.dpapi")) == migrationWalHash,
                    "The controlled migration changed legacy bytes.");

                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, outside),
                    "History path outside storageRoot was accepted.");
                Require(Hash(outsideMarker) == outsideHash && HasForeignMutation(outside),
                    "Rejected outside path changed data or ACL beyond storageRoot.");
                try { Directory.CreateSymbolicLink(link, outside); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    throw new InvalidOperationException("The Windows test environment cannot create the required directory symlink fixture.", error);
                }
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, link),
                    "Reparse-point history path was accepted.");
                Require(Hash(outsideMarker) == outsideHash,
                    "Rejected reparse-point path changed its target.");
                Require(CreateHardLink(hardLink, outsideMarker, IntPtr.Zero),
                    "The Windows test environment cannot create the required hard-link fixture (" +
                    Marshal.GetLastWin32Error() + ").");
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, history),
                    "Hard-linked history file was accepted.");
                Require(Hash(outsideMarker) == outsideHash,
                    "Rejected hard link changed its target.");
                Console.WriteLine("Private history directory: inherited ACL, migration, long-path idempotence, path, reparse and hard-link guards passed.");
            }
            finally
            {
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link);
                if (File.Exists(hardLink)) File.Delete(hardLink);
                if (File.Exists(siblingHardLink)) File.Delete(siblingHardLink);
                if (Directory.Exists(migrationHistoryParent) &&
                    (File.GetAttributes(migrationHistoryParent) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(migrationHistoryParent);
                Require(IsChildOf(sandbox, temp) && Path.GetFileName(sandbox).StartsWith(
                    "termlink-private-history-", StringComparison.Ordinal),
                    "Refusing to delete a directory outside the test TEMP root.");
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
        }

        internal static void AddInheritedUsersWrite(string path)
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }

        private static void SetOwner(string path, SecurityIdentifier owner)
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            security.SetOwner(owner);
            directory.SetAccessControl(security);
        }

        private static SecurityIdentifier OwnerOf(string path) =>
            new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
            throw new InvalidOperationException("Windows owner SID is unavailable.");

        internal static void ValidatePrivateTree(string storage, string history)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var allowed = new[] { current, system, administrators };
            var path = history;
            while (true)
            {
                CheckSecurity(new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, path);
                if (string.Equals(path, storage, StringComparison.OrdinalIgnoreCase)) break;
                path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("History path escaped storageRoot.");
            }
            foreach (var directory in Directory.EnumerateDirectories(history, "*", SearchOption.AllDirectories))
                CheckSecurity(new DirectoryInfo(directory).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, directory);
            foreach (var file in Directory.EnumerateFiles(history, "*", SearchOption.AllDirectories))
                CheckSecurity(new FileInfo(file).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, file);
        }

        private static void CheckSecurity(FileSystemSecurity security, SecurityIdentifier[] allowed, string path)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            Require(owner != null && allowed.Any(value => value.Equals(owner)), "Untrusted owner: " + path);
            Require(security.AreAccessRulesProtected, "ACL inheritance remains enabled: " + path);
            Require(!security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & Mutation) != 0 &&
                    !allowed.Any(value => value.Equals(rule.IdentityReference))),
                "Another identity retains mutation rights: " + path);
        }

        // NULL DACL fixture support: SE_FILE_OBJECT constants and the
        // SetSecurityInfo call with a NULL DACL pointer construct the exact
        // "protected nothing" descriptor form that used to crash the compare.
        private const uint FileShareReadWrite = 3;
        private const uint FileBackupSemantics = 0x02000000;
        private const uint FileReadControl = 0x20000;
        private const uint FileWriteDac = 0x40000;
        private const uint FileObject = 1;
        private const uint DaclSection = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security, uint disposition,
            uint flags, IntPtr template);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint objectType,
            uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint objectType,
            uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl,
            out IntPtr sacl, out IntPtr descriptor);

        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenSecurityDescriptorHandle(string path)
        {
            var handle = CreateFile(path, FileReadControl | FileWriteDac,
                FileShareReadWrite, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                FileBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new InvalidOperationException(
                    "Security descriptor fixture could not open the path: " + Marshal.GetLastWin32Error());
            return handle;
        }

        private static void SetNullDacl(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            var result = SetSecurityInfo(handle, FileObject, DaclSection,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
                throw new InvalidOperationException(
                    "NULL DACL fixture rejected the write: " + result);
        }

        private static void SetNullDacl(string path)
        {
            using var handle = OpenSecurityDescriptorHandle(path);
            SetNullDacl(handle);
        }

        private static bool HasNullDacl(string path)
        {
            using var handle = CreateFile(path, FileReadControl,
                FileShareReadWrite, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                FileBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new InvalidOperationException(
                    "NULL DACL probe could not open the path: " + Marshal.GetLastWin32Error());
            var result = GetSecurityInfo(handle, FileObject, DaclSection,
                out _, out _, out var dacl, out _, out var descriptor);
            try
            {
                if (result != 0)
                    throw new InvalidOperationException(
                        "NULL DACL probe failed: " + result);
                return dacl == IntPtr.Zero;
            }
            finally { LocalFree(descriptor); }
        }

        private static bool NullDaclDescriptorIsJudgedUnprotected()
        {
            var descriptor = new RawSecurityDescriptor("D:PNO_ACCESS_CONTROL");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var method = typeof(PrivateHistoryDirectory).GetMethod(
                    "HasProtectedDacl",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null) throw new InvalidOperationException(
                    "HasProtectedDacl reflection target disappeared.");
                return (bool)method.Invoke(null, new object[] { pointer, new byte[8] })!;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        private static bool HasProtectedDaclOnly(string path)
        {
            var security = new FileSecurity(path, AccessControlSections.Access);
            var control = security.GetSecurityDescriptorBinaryForm();
            // 受保护位在自相对安全描述符的 Control 字段（offset 2，低 16 位）。
            var controlFlags = control.Length >= 4
                ? BitConverter.ToUInt16(control, 2)
                : 0;
            const uint protectedDacl = 0x1000;
            return (controlFlags & protectedDacl) != 0;
        }

        private static bool HasForeignMutation(string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            return !current.Equals(users) && new DirectoryInfo(path).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    users.Equals(rule.IdentityReference) && (rule.FileSystemRights & Mutation) != 0);
        }

        private static void ExpectRejected(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return; }
            throw new InvalidOperationException(message);
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        private static bool IsChildOf(string child, string parent)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
            return relative != "." && !Path.IsPathRooted(relative) &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
