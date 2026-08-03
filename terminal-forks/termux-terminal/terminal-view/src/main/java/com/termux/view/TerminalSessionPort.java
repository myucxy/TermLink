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
}
