package dev.termlink.product

import dev.termlink.harness.BuildConfig
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseVariantPolicyTest {
    @Test
    fun releaseUsesThePublicSecureSnapshotPolicyAndExpectedVersion() {
        assertTrue(BuildConfig.TERMLINK_SERVER_URL == "https://termlink.kget.cc:8910")
        assertFalse(BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST)
        assertTrue(BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME)
        // Release orchestration supplies these values per publication. The
        // policy test must validate their shape instead of pinning the prior
        // AppShip release and breaking every legitimate version increment.
        assertTrue(BuildConfig.VERSION_NAME.matches(Regex("^\\d+\\.\\d+\\.\\d+$")))
        assertTrue(BuildConfig.VERSION_CODE > 0)
    }
}
