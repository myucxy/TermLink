package dev.termlink.product

import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AgentUpdateTaskStoreTest {
    @Test fun taskIdsSurviveRecreationAndAreIsolatedByServerAccountAndSignOut() {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val scope = "https://termlink.example\n" + UUID.randomUUID()
        val ids = mapOf(UUID.randomUUID() to UUID.randomUUID(), UUID.randomUUID() to UUID.randomUUID())
        AgentUpdateTaskStore(context, scope).save(ids)
        AgentUpdateTaskStore(context, scope).saveHiddenNotices(ids.keys)
        assertEquals(ids, AgentUpdateTaskStore(context, scope).load())
        assertEquals(ids.keys, AgentUpdateTaskStore(context, scope).loadHiddenNotices())
        assertTrue(AgentUpdateTaskStore(context, scope + "-other-account").load().isEmpty())
        assertTrue(AgentUpdateTaskStore(context, scope + "-other-account").loadHiddenNotices().isEmpty())
        assertTrue(AgentUpdateTaskStore(context, scope.replace("termlink.example", "other.example")).load().isEmpty())
        AgentUpdateTaskStore(context, null).save(ids)
        AgentUpdateTaskStore(context, null).saveHiddenNotices(ids.keys)
        assertTrue(AgentUpdateTaskStore(context, null).load().isEmpty())
        assertTrue(AgentUpdateTaskStore(context, null).loadHiddenNotices().isEmpty())
        AgentUpdateTaskStore(context, scope).save(emptyMap())
        AgentUpdateTaskStore(context, scope).saveHiddenNotices(emptySet())
        assertTrue(AgentUpdateTaskStore(context, scope).load().isEmpty())
        assertTrue(AgentUpdateTaskStore(context, scope).loadHiddenNotices().isEmpty())
    }
}
