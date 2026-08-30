package dev.termlink.terminal.api

import android.content.Context
import android.graphics.Typeface
import java.io.File

/** Stable font selectors shared by the product UI and each Android terminal renderer. */
object TerminalFontFamilyHintV1 {
    const val BUNDLED_NERD_FONT = "termlink:bundled-nerd-font-v1"
    const val SYSTEM_MONOSPACE = "termlink:system-monospace-v1"
    const val CUSTOM_APP_FONT = "termlink:custom-app-font-v1"

    const val BUNDLED_NERD_FONT_ASSET =
        "termlink-fonts/CaskaydiaCoveNerdFontMono-Regular.ttf"
    const val CUSTOM_APP_FONT_RELATIVE_PATH =
        "terminal-fonts/custom-terminal-font.bin"
}

/**
 * Resolves a [TerminalThemeV1.fontFamilyHint] without exposing a Typeface through the renderer API.
 * A missing or invalid custom font always falls back to the bundled Nerd Font, then monospace.
 */
object TerminalTypefaceResolver {
    @Volatile
    private var bundledTypeface: Typeface? = null
    @Volatile
    private var customTypeface: CustomTypeface? = null

    fun resolve(context: Context, fontFamilyHint: String?): Typeface = when (fontFamilyHint) {
        TerminalFontFamilyHintV1.BUNDLED_NERD_FONT -> bundled(context)
        TerminalFontFamilyHintV1.SYSTEM_MONOSPACE -> Typeface.MONOSPACE
        TerminalFontFamilyHintV1.CUSTOM_APP_FONT -> custom(context) ?: bundled(context)
        null, "" -> Typeface.MONOSPACE
        // V1 accepts only the bounded selectors above. Unknown values from a newer client
        // must not silently select Android's proportional default font.
        else -> Typeface.MONOSPACE
    }

    fun customFontFile(context: Context): File =
        File(context.applicationContext.filesDir, TerminalFontFamilyHintV1.CUSTOM_APP_FONT_RELATIVE_PATH)

    fun invalidateCustomFont() {
        customTypeface = null
    }

    private fun bundled(context: Context): Typeface {
        bundledTypeface?.let { return it }
        return synchronized(this) {
            bundledTypeface ?: runCatching {
                Typeface.createFromAsset(
                    context.applicationContext.assets,
                    TerminalFontFamilyHintV1.BUNDLED_NERD_FONT_ASSET,
                )
            }.getOrDefault(Typeface.MONOSPACE).also { bundledTypeface = it }
        }
    }

    private fun custom(context: Context): Typeface? {
        val file = customFontFile(context)
        if (!file.isFile) {
            customTypeface = null
            return null
        }
        val length = file.length()
        val modified = file.lastModified()
        customTypeface?.takeIf { it.length == length && it.modified == modified }?.let { return it.typeface }
        return synchronized(this) {
            customTypeface?.takeIf { it.length == length && it.modified == modified }?.typeface
                ?: runCatching { Typeface.Builder(file).build() }.getOrNull()?.also { loaded ->
                    customTypeface = CustomTypeface(length, modified, loaded)
                }
        }
    }

    private data class CustomTypeface(
        val length: Long,
        val modified: Long,
        val typeface: Typeface,
    )
}
