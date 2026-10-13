using System;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class InputDiagnosticValidation
{
    internal static void Run()
    {
        using var terminal = NativeTerminalCoreV2.Create(8, 2);
        CheckDiagnostic(() => terminal.EncodeCharacter(new Rune('é'), NativeTerminalModifiers.Control),
            NativeTerminalStatus.Unsupported, "kind=key\nreason=unsupported_control_character\n",
            "codepoint=U+00E9\nmodifiers=0x04\n");
        CheckDiagnostic(() => terminal.EncodeWin32Key(
                new NativeTerminalWin32KeyEvent(112, 59, '\0', true, 8, 1)),
            NativeTerminalStatus.Unsupported, "kind=win32_key\nreason=win32_input_mode_disabled\n",
            "key=VK_0070\n");
        CheckDiagnostic(() => terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                NativeTerminalKittyKeyEventType.Press, NativeTerminalKittyModifiers.Control,
                NativeTerminalKittyKeyIdentity.Character(new Rune('é')))),
            NativeTerminalStatus.Unsupported, "kind=kitty_key\nreason=unsupported_control_character\n",
            "codepoint=U+00E9\nmodifiers=0x04\n");

        var invalidMouse = NativeTerminalInterop.CreateMouseInput(NativeTerminalMouseEvent.Press(
            NativeTerminalMouseButton.Left, NativeTerminalMouseButtons.Left,
            NativeTerminalMousePosition.Cell(1, 1)));
        invalidMouse.ButtonsHeld = 0;
        CheckDiagnostic(() => terminal.EncodeMouseInput(in invalidMouse),
            NativeTerminalStatus.InvalidArgument, "kind=mouse\nreason=invalid_mouse_button_state\n",
            "codepoint=none\nmodifiers=0x00\n");

        CheckDiagnostic(() => terminal.EncodePaste(Array.Empty<byte>()),
            NativeTerminalStatus.InvalidArgument, "kind=paste\nreason=empty_paste\n",
            "key=none\ncodepoint=none\n");

        var shortMouse = invalidMouse;
        shortMouse.StructSize = 63;
        try
        {
            terminal.EncodeMouseInput(in shortMouse);
            throw new InvalidOperationException("Short mouse ABI payload unexpectedly succeeded.");
        }
        catch (NativeTerminalException error)
        {
            Require(error.Status == NativeTerminalStatus.InvalidArgument && error.Diagnostic == null,
                "Mouse ABI preflight invented a diagnostic.");
        }

        Require(Encoding.UTF8.GetString(terminal.EncodeCharacter(new Rune('a'))) == "a",
            "Successful input encoding failed after diagnostic rejection.");
        Require(terminal.EncodeMouse(NativeTerminalMouseEvent.Press(
            NativeTerminalMouseButton.Left, NativeTerminalMouseButtons.Left,
            NativeTerminalMousePosition.Cell(1, 1))).Length == 0,
            "Successful zero-length mouse encoding failed after diagnostic rejection.");
    }

    private static void CheckDiagnostic(Func<byte[]> action, NativeTerminalStatus expected,
        string classification, string detail)
    {
        try
        {
            action();
            throw new InvalidOperationException("Rejected input unexpectedly succeeded.");
        }
        catch (NativeTerminalException error)
        {
            var diagnostic = error.Diagnostic;
            Require(error.Status == expected && diagnostic != null &&
                diagnostic.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n",
                    StringComparison.Ordinal) && diagnostic.Contains(classification, StringComparison.Ordinal) &&
                diagnostic.Contains(detail, StringComparison.Ordinal) && diagnostic.EndsWith('\n') &&
                diagnostic.Length <= 2048 && !error.Message.Contains("U+00E9", StringComparison.Ordinal),
                "Input diagnostic status, evidence, or telemetry boundary differed.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
