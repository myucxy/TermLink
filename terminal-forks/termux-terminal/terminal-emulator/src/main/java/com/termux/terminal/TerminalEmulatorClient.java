package com.termux.terminal;

/**
 * Process-independent callbacks used by {@link TerminalEmulator}.
 *
 * <p>TermLink intentionally excludes Termux's Android PTY session. Keeping this small boundary
 * prevents the emulator core from depending on a local shell process.</p>
 */
public interface TerminalEmulatorClient {

    void onTerminalCursorStateChange(boolean state);

    Integer getTerminalCursorStyle();

    void logError(String tag, String message);

    void logWarn(String tag, String message);

    void logInfo(String tag, String message);

    void logDebug(String tag, String message);

    void logVerbose(String tag, String message);
}
