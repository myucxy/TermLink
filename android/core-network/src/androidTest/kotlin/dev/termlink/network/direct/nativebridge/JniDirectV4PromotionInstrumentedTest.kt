package dev.termlink.network.direct.nativebridge

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class JniDirectV4PromotionInstrumentedTest {
    @Test
    fun failedNativePromotionConsumesV4TokenAndKeepsDestroyOneShot() {
        val v4Methods = JniDirectNativeMethodsV4
        val v5Methods = JniDirectNativeMethodsV5
        val path = DirectNativePathV5(
            epoch = 7,
            connectionId = ByteArray(16) { 8 },
            attemptId = ByteArray(16) { 7 },
        )
        val v4Output = LongArray(1)
        val v5Output = LongArray(1)
        assertEquals(0, v4Methods.createV4(true, 16, v4Output))
        assertEquals(
            0,
            v5Methods.createV5(
                true,
                DIRECT_NATIVE_MAX_FRAMES_V5,
                DIRECT_NATIVE_MAX_BYTES_V5.toLong(),
                path.encode(),
                v5Output,
            ),
        )
        val v4Token = v4Output.single()
        val v5Token = v5Output.single()
        var v4Destroyed = false
        var v5Destroyed = false
        try {
            val promote = DirectNativePromoteRequestV5(path, nomination()).encode()

            // The V4 owner has not gathered or nominated a socket, so the real
            // native promotion must fail after entering the shared registry.
            assertNotEquals(0, v5Methods.promoteV5(v5Token, v4Token, promote))

            // Promotion is one-shot even on failure. JNI must never expose the
            // consumed V4 owner to retry or to any ordinary V4 operation.
            assertEquals(
                DirectNativeStatusV5.INVALID_HANDLE,
                v5Methods.promoteV5(v5Token, v4Token, promote),
            )
            assertEquals(-2, v4Methods.cancelV4(v4Token, 7, ByteArray(16) { 7 }))
            assertEquals(-2, v4Methods.networkChangedV4(v4Token, 7, ByteArray(16) { 7 }))
            assertEquals(
                -2,
                v4Methods.submitSignatureV4(
                    v4Token,
                    7,
                    ByteArray(16) { 7 },
                    1,
                    ByteArray(64) { 9 },
                ),
            )
            assertEquals(
                -2,
                v4Methods.pollV4(
                    v4Token,
                    LongArray(11),
                    ByteArray(16),
                    ByteArray(16),
                    ByteArray(2_080),
                ),
            )
            assertEquals(
                -2,
                v4Methods.gatherV4(
                    v4Token,
                    longArrayOf(7, 1, 0, 0, 9_000_000, 500),
                    arrayOf(
                        ByteArray(16) { 7 },
                        byteArrayOf(10, 0, 0, 2),
                        byteArrayOf(10, 0, 0, 2),
                    ),
                    intArrayOf(),
                ),
            )

            assertEquals(0, v4Methods.destroyV4(v4Token))
            v4Destroyed = true
            assertEquals(-2, v4Methods.destroyV4(v4Token))
            assertEquals(0, v5Methods.destroyV5(v5Token))
            v5Destroyed = true
            assertEquals(DirectNativeStatusV5.INVALID_HANDLE, v5Methods.destroyV5(v5Token))
        } finally {
            if (!v4Destroyed) v4Methods.destroyV4(v4Token)
            if (!v5Destroyed) v5Methods.destroyV5(v5Token)
        }
    }

    private fun nomination() = DirectNativeCommitNominationRequestV4(
        epoch = 7,
        attemptId = ByteArray(16) { 7 },
        checkRoundId = ByteArray(16) { 14 },
        nominationId = ByteArray(16) { 18 },
        winningPair = DirectNativeCandidatePairV4(
            rank = 1,
            initiator = candidate(1, 3),
            responder = candidate(2, 4),
        ),
        role = DirectNativeRoleV4.INITIATOR,
        timeoutMillis = 500,
        selectionConfirmed = true,
        peerCertificateSha256 = ByteArray(32) { 19 },
        peerDeviceSigningPublicKey = ByteArray(32) { 20 },
        localProofNonce = ByteArray(16) { 21 },
        nowMillis = 8_000_100,
    )

    private fun candidate(id: Int, address: Int) = DirectNativeCandidateV4(
        candidateId = ByteArray(16) { id.toByte() },
        relatedCandidateId = ByteArray(16),
        endpoint = DirectNativeSocketAddressV4(
            byteArrayOf(10, 0, 0, address.toByte()),
            50_000 + id,
        ),
        kind = DirectNativeCandidateKindV4.HOST,
        priority = 2_130_706_431,
        networkCost = 10,
        expiresAtMillis = 9_000_000,
    )
}
