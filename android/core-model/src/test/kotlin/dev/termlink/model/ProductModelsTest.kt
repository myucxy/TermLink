package dev.termlink.model

import org.junit.Assert.assertFalse
import org.junit.Test

class ProductModelsTest {
    @Test
    fun catalogModelsDoNotDefineClientSidePaidAuthorization() {
        val enumNames = arrayOf(DeviceType::class, DeviceTrustState::class, GrantPermission::class, SessionState::class)
            .flatMap { it.java.enumConstants.map(Any::toString) }
        assertFalse(enumNames.any { it == "PRO" || it == "IS_PRO" })
    }
}
