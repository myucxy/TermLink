package com.termux.view;

import com.termux.terminal.TerminalEmulator;

/** The minimal session surface required by {@link TerminalView}. */
public interface TerminalSessionPort {

    TerminalEmulator getEmulator();

    void write(String data);

    void writeCodePoint(boolean prependEscape, int codePoint);

    void onCopyTextToClipboard(String text);

    void onPasteTextFromClipboard();

    void updateSize(int columns, int rows, int cellWidthPixels, int cellHeightPixels);

    /**
     * Whether the attached emulator owns its row/column grid independently of this Android view.
     *
     * <p>Remote terminals use this to keep the PC PTY authoritative. The view still reports its
     * pixel capacity through {@link #updateSize(int, int, int, int)}, but must treat that capacity
     * as a presentation viewport instead of resizing or replacing the remote grid.</p>
     */
    default boolean isTerminalGridAuthoritative() {
        return false;
    }
}
