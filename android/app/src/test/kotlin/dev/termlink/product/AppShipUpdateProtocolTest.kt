package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AppShipUpdateProtocolTest {
    @Test
    fun buildCheckUrlBindsPlatformArchitectureChannelAndBuild() {
        val url = AppShipUpdateProtocol.buildCheckUrl(
            baseUrl = "https://appship.example:8910/",
            slug = "termlink-android",
            architecture = "arm64-v8a",
            channel = "stable",
            buildNumber = 6,
        )

        assertEquals(
            "https://appship.example:8910/api/v1/apps/termlink-android/updates" +
                "?platform=android&arch=arm64-v8a&channel=stable&build=6",
            url,
        )
    }

    @Test
    fun noUpdateResponseRequiresMatchingIdentityAndNoArtifact() {
        val status = AppShipUpdateProtocol.parseAndValidate(
            body = """
                {
                  "status":"no_update",
                  "application":"termlink-android",
                  "channel":"stable",
                  "latest":{"versionName":"0.1.6","buildNumber":6},
                  "policy":{"minSupportedBuild":null,"forceAfter":null},
                  "artifact":null
                }
            """.trimIndent(),
            baseUrl = "https://appship.example:8910",
            expectedSlug = "termlink-android",
            expectedChannel = "stable",
            expectedArchitecture = "x86_64",
            currentBuild = 6,
        )

        assertEquals(ProductUpdateDecision.NO_UPDATE, status.decision)
        assertEquals("0.1.6", status.versionName)
        assertEquals(6L, status.buildNumber)
        assertTrue(status.downloadUrl == null)
    }

    @Test
    fun optionalResponseValidatesArtifactAndNormalizesDownloadHost() {
        val status = AppShipUpdateProtocol.parseAndValidate(
            body = """
                {
                  "status":"optional",
                  "application":"termlink-android",
                  "channel":"stable",
                  "latest":{"versionName":"0.1.7","buildNumber":7,"releaseNotes":" terminal improvements "},
                  "policy":{"minSupportedBuild":null,"forceAfter":null},
                  "artifact":{
                    "id":"12345678-1234-1234-1234-123456789abc",
                    "platform":"android",
                    "fileKind":"apk",
                    "architecture":"universal",
                    "originalFilename":"termlink-android-0.1.7.apk",
                    "sizeBytes":123,
                    "expectedSizeBytes":123,
                    "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "mimeType":"application/vnd.android.package-archive",
                    "status":"ready",
                    "signingStatus":"manual_approved",
                    "downloadEnabled":true,
                    "downloadUrl":"https://download.example/api/v1/artifacts/12345678-1234-1234-1234-123456789abc/download"
                  }
                }
            """.trimIndent(),
            baseUrl = "https://appship.example:8910",
            expectedSlug = "termlink-android",
            expectedChannel = "stable",
            expectedArchitecture = "x86_64",
            currentBuild = 6,
        )

        assertEquals(ProductUpdateDecision.OPTIONAL, status.decision)
        assertEquals("0.1.7", status.versionName)
        assertEquals("terminal improvements", status.releaseNotes)
        assertEquals(
            "https://appship.example:8910/api/v1/artifacts/12345678-1234-1234-1234-123456789abc/download",
            status.downloadUrl,
        )
        assertEquals(123L, status.artifactSizeBytes)
        assertEquals("a".repeat(64), status.artifactSha256)
    }

    @Test
    fun invalidResponseIsRejectedInsteadOfInventingAnOptionalUpdate() {
        val error = runCatching {
            AppShipUpdateProtocol.parseAndValidate(
                body = """
                    {
                      "status":"optional",
                      "application":"termlink-android",
                      "channel":"stable",
                      "latest":{"versionName":"0.1.5","buildNumber":5},
                      "policy":{"minSupportedBuild":null},
                      "artifact":null
                    }
                """.trimIndent(),
                baseUrl = "https://appship.example:8910",
                expectedSlug = "termlink-android",
                expectedChannel = "stable",
                expectedArchitecture = "x86_64",
                currentBuild = 6,
            )
        }.exceptionOrNull()

        assertTrue(error is AppShipUpdateException)
    }

    @Test
    fun cacheFreshnessMatchesNuanYaThrottle() {
        assertTrue(isUpdateCacheFresh(1_000L, 61_000L, intervalMs = 60_000L))
        assertTrue(!isUpdateCacheFresh(1_000L, 61_001L, intervalMs = 60_000L))
        assertTrue(!isUpdateCacheFresh(0L, 1_000L, intervalMs = 60_000L))
    }
}
