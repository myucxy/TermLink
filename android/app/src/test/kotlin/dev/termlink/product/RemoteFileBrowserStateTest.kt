package dev.termlink.product

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

class RemoteFileBrowserStateTest {
    @Test
    fun driveRootParentReturnsToDriveList() {
        assertEquals("", remoteFileParentTarget("C:\\", null))
        assertEquals("C:\\", remoteFileParentTarget("C:\\work", "C:\\"))
        assertNull(remoteFileParentTarget("", null))
    }

    @Test
    fun staleResponseCannotReplaceNewerDirectoryRequest() {
        val pcId = UUID.randomUUID()
        val oldId = UUID.randomUUID()
        val newId = UUID.randomUUID()
        var state = reduceRemoteFileBrowser(
            RemoteFileBrowserState(),
            RemoteFileBrowserEvent.LoadStarted(oldId, pcId, "PC", "C:\\old", null),
        )
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadStarted(newId, pcId, "PC", "C:\\new", null),
        )

        val afterStale = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadSucceeded(
                oldId,
                RemoteDirectoryPage("C:\\old", null, listOf(entry("old", "C:\\old")), null),
            ),
        )

        assertEquals(state, afterStale)
        assertEquals(newId, afterStale.activeRequestId)
    }

    @Test
    fun paginationAppendsAndDeduplicatesPathsIgnoringCase() {
        val pcId = UUID.randomUUID()
        val firstId = UUID.randomUUID()
        var state = reduceRemoteFileBrowser(
            RemoteFileBrowserState(),
            RemoteFileBrowserEvent.LoadStarted(firstId, pcId, "PC", "C:\\", null),
        )
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadSucceeded(
                firstId,
                RemoteDirectoryPage("C:\\", null, listOf(entry("One", "C:\\One")), 1L),
            ),
        )
        val secondId = UUID.randomUUID()
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadStarted(secondId, pcId, "PC", "C:\\", 1L),
        )
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadSucceeded(
                secondId,
                RemoteDirectoryPage(
                    "C:\\",
                    null,
                    listOf(entry("one duplicate", "c:\\one"), entry("Two", "C:\\Two")),
                    null,
                ),
            ),
        )

        assertEquals(listOf("C:\\One", "C:\\Two"), state.entries.map(RemoteFileEntry::path))
        assertFalse(state.loading)
        assertEquals(null, state.nextOffset)
    }

    @Test
    fun failedDirectoryNavigationRollsBackToLastSuccessfulPage() {
        val pcId = UUID.randomUUID()
        val firstId = UUID.randomUUID()
        var state = reduceRemoteFileBrowser(
            RemoteFileBrowserState(),
            RemoteFileBrowserEvent.LoadStarted(firstId, pcId, "PC", "C:\\", null),
        )
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadSucceeded(
                firstId,
                RemoteDirectoryPage("C:\\", null, listOf(entry("work", "C:\\work")), null),
            ),
        )
        val failedId = UUID.randomUUID()
        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadStarted(failedId, pcId, "PC", "C:\\missing", null),
        )

        state = reduceRemoteFileBrowser(
            state,
            RemoteFileBrowserEvent.LoadFailed(failedId, "not found"),
        )

        assertEquals("C:\\", state.currentPath)
        assertEquals(listOf("C:\\work"), state.entries.map(RemoteFileEntry::path))
        assertEquals("not found", state.error)
        assertFalse(state.loading)
    }

    @Test
    fun openingStateTracksTheExactTerminalCommandId() {
        val started = reduceRemoteFileBrowser(
            RemoteFileBrowserState(),
            RemoteFileBrowserEvent.ShellStarted("custom-project"),
        )
        assertEquals("custom-project", started.openingCommandId)
        assertNull(reduceRemoteFileBrowser(started, RemoteFileBrowserEvent.ShellFinished).openingCommandId)
    }

    private fun entry(name: String, path: String) = RemoteFileEntry(name, path, RemoteFileKind.DIRECTORY)
}
