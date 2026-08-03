package dev.termlink.harness

import android.content.res.AssetManager
import dev.termlink.terminal.replay.GoldenStreamBundle

object GoldenStreamAssetLoader {
    const val defaultCase = "terminal-golden-streams/smoke-powershell-v1"

    fun load(assetManager: AssetManager, goldenCase: GoldenStreamCase): GoldenStreamBundle =
        load(assetManager, goldenCase.assetPath)

    fun load(assetManager: AssetManager, casePath: String = defaultCase): GoldenStreamBundle =
        GoldenStreamBundle(
            manifestBytes = assetManager.readBytes("$casePath/manifest.json"),
            eventsBytes = assetManager.readBytes("$casePath/events.jsonl"),
            outputBytes = assetManager.readBytes("$casePath/output.bin"),
            expectationsBytes = assetManager.readBytes("$casePath/expectations.json"),
        )

    private fun AssetManager.readBytes(path: String): ByteArray = open(path).use { it.readBytes() }
}
