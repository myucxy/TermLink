package dev.termlink.product

import android.app.Activity
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.SystemClock
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import dev.termlink.harness.R
import dev.termlink.terminal.api.TerminalHostEffectV2
import dev.termlink.terminal.api.TerminalPlatformAction
import dev.termlink.terminal.api.TerminalPlatformActionSink
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.PI
import kotlin.math.pow
import kotlin.math.sin
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal const val TERMINAL_ALERTS = "terminal_alerts"
internal const val TERMINAL_SOUNDS = "terminal_sounds"
internal const val TERMINAL_CLIPBOARD = "terminal_clipboard"
internal fun Context.terminalActionPreferences() = getSharedPreferences("terminal-platform-settings", Context.MODE_PRIVATE)

/** Live output only; no terminal text is disclosed to notifications and no queue of sounds is retained. */
internal class AndroidTerminalPlatformActions(
    context: Context,
    private val account: String,
    private val session: UUID,
    parentScope: CoroutineScope,
    private val currentAccount: () -> Boolean,
    private val activity: () -> Activity?,
    private val clipboardPermit: () -> TerminalClipboardPermit? = { null },
    private val visible: () -> Boolean = { false },
    private val notificationTickets: CliNotificationTickets = CliNotificationTickets(context),
) : TerminalPlatformActionSink {
    private val context = context.applicationContext
    private val receipts = TerminalActionReceipts(this.context, account, session)
    private val preferences = this.context.terminalActionPreferences()
    private val scope = CoroutineScope(parentScope.coroutineContext + SupervisorJob(parentScope.coroutineContext[Job]))
    private val closed = AtomicBoolean()
    private val notificationTag = "terminal:$session"
    private var lastNotice = -2000L
    private val clipboard = TerminalClipboardPrompt(this.context, scope, activity, clipboardPermit, visible,
        { !closed.get() && currentAccount() })

    override fun offer(effects: List<TerminalHostEffectV2>) {
        if (closed.get()) return
        // Record denied, muted and rate-limited attempts too, before testing device policy.
        val accepted = receipts.claim(effects)
        if (accepted.isEmpty() || closed.get() || !currentAccount()) return
        val actions = accepted.map { TerminalPlatformAction.decode(it.copyPayload()) }
        val expectedPermit = clipboardPermit()
        actions.filterIsInstance<TerminalPlatformAction.ClipboardWrite>().firstOrNull()?.let { request ->
            scope.launch(Dispatchers.Main.immediate) { clipboard.offer(request.text, expectedPermit) }
        }
        val now = SystemClock.elapsedRealtime()
        if (actions.any { it == TerminalPlatformAction.Bell } && preferences.getBoolean(TERMINAL_ALERTS, false) &&
            now - lastNotice >= 2000) {
            lastNotice = now
            scope.launch(Dispatchers.Main.immediate) {
                if (closed.get() || !currentAccount() || !preferences.getBoolean(TERMINAL_ALERTS, false)) return@launch
                runCatching {
                    if (activity()?.hasWindowFocus() == true) {
                        Toast.makeText(context, R.string.terminal_attention_message, Toast.LENGTH_SHORT).show()
                    } else notifyBackground()
                }
            }
        }
        val sound = actions.firstOrNull { it is TerminalPlatformAction.Sound } as? TerminalPlatformAction.Sound
            ?: if (actions.any { it == TerminalPlatformAction.Bell }) TerminalPlatformAction.Sound(3, 3, listOf(1)) else null
        if (sound != null && sound.volume > 0 && sound.duration32nds > 0 && sound.notes.any { it != 0 } &&
            mayPlay() && reserveAudio(now)) {
            // Run even if cancellation races launch, so the process-wide audio gate is always released.
            val job = scope.launch(Dispatchers.IO) { play(sound) }
            job.invokeOnCompletion { playing.set(false) }
        }
    }

    private fun notifyBackground() {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel("terminal_attention_v1",
            context.getString(R.string.terminal_alerts), NotificationManager.IMPORTANCE_LOW).apply {
            setSound(null, null); enableVibration(false); lockscreenVisibility = NotificationCompat.VISIBILITY_PRIVATE
        })
        notificationTickets.removeSession(account, session.toString())
        val ticket = notificationTickets.save(account, JSONObject()
            .put("route", "cli").put("accountScope", account).put("sessionId", session.toString())
            .put("expiresAt", System.currentTimeMillis() + 24 * 60 * 60_000L))
        val intent = cliNotificationIntent(context, ticket)
        val pending = PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        manager.notify(notificationTag, 1, NotificationCompat.Builder(context, "terminal_attention_v1")
            .setSmallIcon(R.mipmap.ic_launcher).setContentTitle("TermLink CLI")
            .setContentText(context.getString(R.string.terminal_attention_message)).setContentIntent(pending)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE).setSilent(true).setAutoCancel(true).build())
    }

    private fun mayPlay(): Boolean = runCatching {
        val manager = context.getSystemService(NotificationManager::class.java)
        val audio = context.getSystemService(AudioManager::class.java)
        !closed.get() && currentAccount() && preferences.getBoolean(TERMINAL_SOUNDS, false) &&
            manager.currentInterruptionFilter == NotificationManager.INTERRUPTION_FILTER_ALL &&
            audio.ringerMode == AudioManager.RINGER_MODE_NORMAL && audio.getStreamVolume(AudioManager.STREAM_NOTIFICATION) > 0
    }.getOrDefault(false)

    private suspend fun play(sound: TerminalPlatformAction.Sound) {
        var track: AudioTrack? = null
        try {
            if (!mayPlay()) return
            val rate = 16000
            track = AudioTrack.Builder().setAudioAttributes(AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_NOTIFICATION_EVENT).setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build())
                .setAudioFormat(AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                .setTransferMode(AudioTrack.MODE_STREAM).setBufferSizeInBytes(maxOf(1280,
                    AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT))).build()
            if (track.state != AudioTrack.STATE_INITIALIZED) return
            track.play()
            val began = SystemClock.elapsedRealtime()
            var remaining = rate * 2 // No terminal program can hold audio longer than two seconds.
            for (note in sound.notes) {
                val samples = minOf(remaining, rate * sound.duration32nds / 32)
                val frequency = if (note == 0) 0.0 else 440.0 * 2.0.pow((71 + note - 69) / 12.0)
                var position = 0
                while (position < samples && scope.isActive && mayPlay() && SystemClock.elapsedRealtime() - began < 2100) {
                    val chunk = ShortArray(minOf(320, samples - position)) { index ->
                        val frame = position + index
                        val envelope = minOf(1.0, frame / 80.0, (samples - frame) / 80.0)
                        (sin(2 * PI * frequency * frame / rate) * 5000 * sound.volume / 7 * envelope).toInt().toShort()
                    }
                    if (track.write(chunk, 0, chunk.size, AudioTrack.WRITE_BLOCKING) != chunk.size) return
                    position += chunk.size
                }
                remaining -= samples
                if (remaining <= 0 || position < samples) return
            }
            // Let the bounded stream drain, while remaining responsive to mute/dispose.
            withContext(Dispatchers.IO) {
                val total = rate * 2 - remaining
                while (isActive && mayPlay() && track.playbackHeadPosition < total && SystemClock.elapsedRealtime() - began < 2100)
                    kotlinx.coroutines.delay(20)
            }
        } catch (_: Exception) {
            // Optional OS actions never stop the session or its durable ACK.
        } finally {
            track?.let { runCatching { it.pause(); it.flush() }; runCatching { it.release() } }
        }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        scope.cancel()
        android.os.Handler(android.os.Looper.getMainLooper()).post { clipboard.close() }
        notificationTickets.removeSession(account, session.toString())
        runCatching { context.getSystemService(NotificationManager::class.java).cancel(notificationTag, 1) }
    }

    companion object {
        private val playing = AtomicBoolean()
        private var lastAudio = -3000L
        @Synchronized private fun reserveAudio(now: Long): Boolean {
            if (now - lastAudio < 3000 || !playing.compareAndSet(false, true)) return false
            lastAudio = now
            return true
        }
    }
}
