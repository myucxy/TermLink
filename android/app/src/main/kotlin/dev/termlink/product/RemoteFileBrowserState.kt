package dev.termlink.product

import java.util.Locale
import java.util.UUID

data class RemoteFileBrowserState(
    val pcId: UUID? = null,
    val pcName: String = "",
    val currentPath: String = "",
    val parentPath: String? = null,
    val entries: List<RemoteFileEntry> = emptyList(),
    val nextOffset: Long? = null,
    val activeRequestId: UUID? = null,
    val requestOffset: Long? = null,
    val loading: Boolean = false,
    val openingCommandId: String? = null,
    val error: String? = null,
    val rollbackPage: RemoteDirectoryPage? = null,
)

/** A drive root has no Windows parent, so its parent in the Android browser is the drive list. */
internal fun remoteFileParentTarget(currentPath: String, parentPath: String?): String? =
    parentPath ?: "".takeIf { currentPath.isNotBlank() }

internal sealed interface RemoteFileBrowserEvent {
    data class LoadStarted(
        val requestId: UUID,
        val pcId: UUID,
        val pcName: String,
        val path: String,
        val offset: Long?,
    ) : RemoteFileBrowserEvent

    data class LoadSucceeded(
        val requestId: UUID,
        val page: RemoteDirectoryPage,
    ) : RemoteFileBrowserEvent

    data class LoadFailed(
        val requestId: UUID,
        val message: String,
    ) : RemoteFileBrowserEvent

    data class ShellStarted(val commandId: String) : RemoteFileBrowserEvent
    data object ShellFinished : RemoteFileBrowserEvent
    data class ShellFailed(val message: String) : RemoteFileBrowserEvent
}

internal fun reduceRemoteFileBrowser(
    state: RemoteFileBrowserState,
    event: RemoteFileBrowserEvent,
): RemoteFileBrowserState = when (event) {
    is RemoteFileBrowserEvent.LoadStarted -> {
        val appending = event.offset != null && state.pcId == event.pcId && state.currentPath == event.path
        val rollbackPage = if (!appending && state.pcId == event.pcId &&
            (state.currentPath.isNotBlank() || state.entries.isNotEmpty() || state.parentPath != null)
        ) {
            RemoteDirectoryPage(state.currentPath, state.parentPath, state.entries, state.nextOffset)
        } else {
            null
        }
        state.copy(
            pcId = event.pcId,
            pcName = event.pcName,
            currentPath = event.path,
            parentPath = if (appending) state.parentPath else null,
            entries = if (appending) state.entries else emptyList(),
            nextOffset = if (appending) state.nextOffset else null,
            activeRequestId = event.requestId,
            requestOffset = event.offset,
            loading = true,
            error = null,
            rollbackPage = rollbackPage,
        )
    }
    is RemoteFileBrowserEvent.LoadSucceeded -> {
        if (state.activeRequestId != event.requestId) {
            state
        } else {
            val entries = if (state.requestOffset == null) {
                event.page.entries.distinctBy { entry -> entry.path.lowercase(Locale.ROOT) }
            } else {
                (state.entries + event.page.entries).distinctBy { entry ->
                    entry.path.lowercase(Locale.ROOT)
                }
            }
            state.copy(
                currentPath = event.page.currentPath,
                parentPath = event.page.parentPath,
                entries = entries,
                nextOffset = event.page.nextOffset,
                activeRequestId = null,
                requestOffset = null,
                loading = false,
                error = null,
                rollbackPage = null,
            )
        }
    }
    is RemoteFileBrowserEvent.LoadFailed -> {
        if (state.activeRequestId != event.requestId) {
            state
        } else {
            val rollback = state.rollbackPage.takeIf { state.requestOffset == null }
            state.copy(
                currentPath = rollback?.currentPath ?: state.currentPath,
                parentPath = rollback?.parentPath ?: state.parentPath,
                entries = rollback?.entries ?: state.entries,
                nextOffset = rollback?.nextOffset ?: state.nextOffset,
                activeRequestId = null,
                requestOffset = null,
                loading = false,
                error = event.message,
                rollbackPage = null,
            )
        }
    }
    is RemoteFileBrowserEvent.ShellStarted -> state.copy(openingCommandId = event.commandId, error = null)
    RemoteFileBrowserEvent.ShellFinished -> state.copy(openingCommandId = null)
    is RemoteFileBrowserEvent.ShellFailed -> state.copy(openingCommandId = null, error = event.message)
}
