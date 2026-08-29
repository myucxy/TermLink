using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace TermLink.Setup
{
    internal static class SetupText
    {
        internal static bool IsChinese => string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);

        internal static string Pick(string chinese, string english)
        {
            return IsChinese ? chinese : english;
        }

        internal static string PublicError(Exception error)
        {
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                error = aggregate.InnerExceptions[0];
            if (error is OperationCanceledException)
                return Pick("操作已取消。", "The operation was cancelled.");
            if (error is TimeoutException)
                return Pick("操作超时，请稍后重试。", "The operation timed out. Try again later.");
            if (error is UnauthorizedAccessException || error is SecurityException)
                return Pick("权限不足，无法完成安装操作。", "TermLink Setup does not have permission to complete the operation.");
            if (error is IOException)
                return Pick("本机文件操作失败，请关闭占用文件的程序后重试。", "A local file operation failed. Close any app using the files and try again.");
            if (error is ArgumentException || error is FormatException)
                return Pick("安装参数或配置无效，请检查后重试。", "The setup input or configuration is invalid. Check it and try again.");

            var code = StableErrorCode(error == null ? null : error.Message);
            var message = Pick(
                "安装操作失败，请重试；若问题持续，请查看安装日志。",
                "The setup operation failed. Try again; if it persists, check the setup log.");
            return code == null ? message : message + " [" + code + "]";
        }

        internal static string DiagnosticRecorded()
        {
            return Pick(
                "完整诊断已写入本机 TermLink 安装日志。",
                "Full diagnostics were written to the local TermLink setup log.");
        }

        private static string StableErrorCode(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 64) return null;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= 'A' && character <= 'Z') &&
                    !(character >= '0' && character <= '9') &&
                    character != '_' && character != '-') return null;
            }
            return value;
        }
    }
}
