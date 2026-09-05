package dev.termlink.product

import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteFileModelsTest {
    @Test
    fun fileOperationEnvelopeKeepsDownloadAndOpenResultInTheirContractLocations() {
        val operationId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        val response = remoteFileOperationResponseFromJson(
            Json.parseToJsonElement(
                """{
                  "file_operation":{
                    "id":"$operationId",
                    "status":"SUCCEEDED",
                    "result":{"session_id":"$sessionId"}
                  },
                  "transfer":{"protocol":"REALTIME_V2","transfer_id":"$operationId"}
                }""",
            ).jsonObject,
        )

        assertEquals(operationId, response.id)
        assertEquals("SUCCEEDED", response.status)
        assertEquals(sessionId.toString(), response.result?.get("session_id")?.jsonPrimitive?.content)
        assertEquals("REALTIME_V2", response.transfer?.get("protocol")?.jsonPrimitive?.content)
    }

    @Test
    fun realtimeTransferMetadataRestoresTheSameResumeDescriptor() {
        val operationId = UUID.randomUUID()
        val digest = MessageDigest.getInstance("SHA-256").digest("payload".encodeToByteArray())
        val encoded = Base64.getUrlEncoder().withoutPadding().encodeToString(digest)
        val descriptor = realtimeFileTransferDescriptorFromJson(
            Json.parseToJsonElement(
                """{
                  "protocol":"REALTIME_V2",
                  "transfer_id":"$operationId",
                  "filename":"folder.zip",
                  "size_bytes":7,
                  "sha256":"$encoded",
                  "directory_zip":true
                }""",
            ).jsonObject,
            expectedTransferId = operationId,
            expectedDirectoryZip = true,
        )

        assertEquals(operationId, descriptor.transferId)
        assertEquals("folder.zip", descriptor.filename)
        assertEquals(7L, descriptor.sizeBytes)
        assertTrue(descriptor.sha256.contentEquals(digest))
        assertTrue(descriptor.directoryZip)
    }

    @Test
    fun realtimeTransferMetadataRejectsCrossTaskOrWrongDestinationKind() {
        val operationId = UUID.randomUUID()
        val encoded = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32))
        val transfer = Json.parseToJsonElement(
            """{
              "protocol":"REALTIME_V2",
              "transfer_id":"$operationId",
              "filename":"payload.bin",
              "size_bytes":0,
              "sha256":"$encoded",
              "directory_zip":false
            }""",
        ).jsonObject

        assertTrue(runCatching {
            realtimeFileTransferDescriptorFromJson(transfer, UUID.randomUUID(), false)
        }.isFailure)
        assertTrue(runCatching {
            realtimeFileTransferDescriptorFromJson(transfer, operationId, true)
        }.isFailure)
    }

    @Test
    fun unknownFileOperationStatusIsRejected() {
        val rejected = runCatching {
            remoteFileOperationResponseFromJson(
                Json.parseToJsonElement(
                    """{"file_operation":{"id":"${UUID.randomUUID()}","status":"UNKNOWN"}}""",
                ).jsonObject,
            )
        }.isFailure

        assertTrue(rejected)
    }

    @Test
    fun fileSurfacesRemainSecondaryToDevicesNavigation() {
        assertEquals(ProductPrimaryDestination.DEVICES, ProductDestination.FILES.primaryDestination())
        assertEquals(ProductPrimaryDestination.DEVICES, ProductDestination.DOWNLOADS.primaryDestination())
        assertTrue(ProductDestination.FILES.isVisibleInProductShell)
        assertTrue(ProductDestination.DOWNLOADS.isVisibleInProductShell)
    }

    @Test
    fun fixedWindowsShellValuesRoundTripAndRejectUnknownShell() {
        assertEquals(RemoteShellType.COMMAND_PROMPT, RemoteShellType.fromWireValue("command-prompt"))
        assertEquals(RemoteShellType.WINDOWS_POWERSHELL, RemoteShellType.fromWireValue("windows-powershell"))
        assertEquals(RemoteShellType.POWERSHELL_7, RemoteShellType.fromWireValue("powershell-7"))
        assertEquals(RemoteShellType.WSL, RemoteShellType.fromWireValue("wsl"))
        assertEquals("command-prompt", RemoteShellType.COMMAND_PROMPT.wireValue)
        assertEquals("windows-powershell", RemoteShellType.WINDOWS_POWERSHELL.wireValue)
        assertEquals("powershell-7", RemoteShellType.POWERSHELL_7.wireValue)
        assertEquals("wsl", RemoteShellType.WSL.wireValue)
        val rejected = runCatching { RemoteShellType.fromWireValue("pwsh") }.isFailure
        assertTrue(rejected)
    }

    @Test
    fun currentDirectoryEntrySupportsDriveAndNestedWindowsPaths() {
        assertEquals("C:", remoteCurrentDirectoryEntry("C:\\")?.name)
        assertEquals("work", remoteCurrentDirectoryEntry("C:\\dev\\work\\")?.name)
        assertEquals(null, remoteCurrentDirectoryEntry(""))
    }

    @Test
    fun directorySelectionAddsZipAndSanitizesDocumentName() {
        val selection = remoteDownloadSelection(
            pcId = UUID.randomUUID(),
            pcName = "PC",
            entry = RemoteFileEntry("bad/name.", "C:\\bad", RemoteFileKind.DIRECTORY),
        )

        assertEquals("bad_name.zip", selection.suggestedFilename)
        assertTrue(selection.directoryZip)
    }

    @Test
    fun realDownloadHeaderArraysArePreserved() {
        val digest = Base64.getUrlEncoder().withoutPadding().encodeToString(
            MessageDigest.getInstance("SHA-256").digest(ByteArray(0)),
        )
        val value = Json.parseToJsonElement(
            """{
              "filename":"empty.txt",
              "size_bytes":0,
              "sha256":"$digest",
              "directory_zip":false,
              "request":{
                "url":"https://objects.example/download",
                "method":"GET",
                "headers":{"X-Test":["one","two"],"X-Single":["only"]},
                "expires_at":"2026-08-24T12:00:00Z"
              }
            }""",
        ).jsonObject

        val descriptor = remoteDownloadDescriptorFromJson(value)

        assertEquals(listOf("one", "two"), descriptor.request.headers["X-Test"])
        assertEquals(listOf("only"), descriptor.request.headers["X-Single"])
        assertEquals(0L, descriptor.sizeBytes)
        assertFalse(descriptor.directoryZip)
    }

    @Test
    fun legacySingleStringDownloadHeaderIsTolerated() {
        val digest = Base64.getUrlEncoder().withoutPadding().encodeToString(
            MessageDigest.getInstance("SHA-256").digest(ByteArray(0)),
        )
        val value = Json.parseToJsonElement(
            """{
              "filename":"empty.txt",
              "size_bytes":0,
              "sha256":"$digest",
              "directory_zip":false,
              "request":{
                "url":"https://objects.example/download",
                "method":"GET",
                "headers":{"X-Legacy":"one"}
              }
            }""",
        ).jsonObject

        assertEquals(
            listOf("one"),
            remoteDownloadDescriptorFromJson(value).request.headers["X-Legacy"],
        )
    }

    @Test
    fun downloadDescriptorAcceptsFiveGibibytesAndRejectsOneByteMore() {
        val digest = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32))
        fun descriptor(size: Long) = Json.parseToJsonElement(
            """{
              "filename":"large.bin",
              "size_bytes":$size,
              "sha256":"$digest",
              "directory_zip":false,
              "request":{"url":"https://objects.example/download","method":"GET"}
            }""",
        ).jsonObject

        assertEquals(
            5_368_709_120L,
            remoteDownloadDescriptorFromJson(descriptor(5_368_709_120L)).sizeBytes,
        )
        assertTrue(
            runCatching {
                remoteDownloadDescriptorFromJson(descriptor(5_368_709_121L))
            }.isFailure,
        )
    }
}
