package dev.termlink.product

import java.nio.charset.StandardCharsets
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class NativeOidcAuthTest {
    @Test
    fun endpointPolicyRequiresHttpsSameOriginAndNoQuery() {
        val issuer = "https://issuer.example/realms/termlink"
        assertEquals(
            "https://issuer.example/realms/termlink/token",
            OidcEndpointPolicy.validateEndpoint(issuer, "https://issuer.example/realms/termlink/token", "token").toString(),
        )
        assertThrows(IllegalArgumentException::class.java) {
            OidcEndpointPolicy.validateEndpoint(issuer, "http://issuer.example/token", "token")
        }
        assertThrows(IllegalArgumentException::class.java) {
            OidcEndpointPolicy.validateEndpoint(issuer, "https://other.example/token", "token")
        }
        assertThrows(IllegalArgumentException::class.java) {
            OidcEndpointPolicy.validateEndpoint(issuer, "https://issuer.example/token?leak=true", "token")
        }
    }

    @Test
    fun discoveryIssuerComparisonIsExact() {
        OidcEndpointPolicy.requireDiscoveryIssuer("https://issuer.example/realms/termlink", "https://issuer.example/realms/termlink")
        assertThrows(IllegalArgumentException::class.java) {
            OidcEndpointPolicy.requireDiscoveryIssuer("https://issuer.example/realms/termlink", "https://ISSUER.example/realms/termlink")
        }
    }

    @Test
    fun embeddedIdentityUsesLoginPromptAfterHostedRegistration() {
        assertEquals("login", EmbeddedAuthPolicy.LOGIN_PROMPT)
        assertNotEquals("create", EmbeddedAuthPolicy.LOGIN_PROMPT)
    }

    @Test
    fun accountScopeSeparatesIssuerAndSubject() {
        val one = AccountScope.canonical("https://issuer.example/realms/one", "subject")
        val same = AccountScope.canonical("https://issuer.example/realms/one", "subject")
        val otherIssuer = AccountScope.canonical("https://issuer.example/realms/two", "subject")
        val otherSubject = AccountScope.canonical("https://issuer.example/realms/one", "other")
        assertEquals(one, same)
        assertNotEquals(one, otherIssuer)
        assertNotEquals(one, otherSubject)
    }

    @Test
    fun accessTokenContractSelectsOnlyTheExpectedIssuerPartyAndAudience() {
        val issuer = "https://issuer.example/realms/termlink"
        val now = 1_700_000_000L
        val valid = jwt(
            """{"iss":"$issuer","sub":"subject","azp":"termlink-android","typ":"Bearer","jti":"token-id","aud":["termlink-server"],"nbf":${now - 1},"exp":${now + 300}}""",
        )
        assertEquals("subject", OidcJwtClaims.accessTokenSubject(valid, issuer, "termlink-android", now))

        for (claims in listOf(
            """{"iss":"$issuer","sub":"subject","azp":"termlink-windows","typ":"Bearer","jti":"token-id","aud":"termlink-server","exp":${now + 300}}""",
            """{"iss":"$issuer","sub":"subject","azp":"termlink-android","typ":"ID","jti":"token-id","aud":"termlink-server","exp":${now + 300}}""",
            """{"iss":"$issuer","sub":"subject","azp":"termlink-android","typ":"Bearer","jti":"token-id","aud":"other","exp":${now + 300}}""",
            """{"iss":"$issuer","sub":"subject","azp":"termlink-android","typ":"Bearer","jti":"token-id","aud":"termlink-server","exp":$now}""",
        )) {
            assertThrows(NativeOidcException::class.java) {
                OidcJwtClaims.accessTokenSubject(jwt(claims), issuer, "termlink-android", now)
            }
        }
    }

    @Test
    fun credentialFreeMetadataNeverClaimsToBeARefreshRequest() {
        assertNotEquals(
            "refresh_token",
            CredentialFreeAuthState.SANITIZED_REQUEST_GRANT_TYPE,
        )
    }

    private fun jwt(claims: String): String {
        val encoder = Base64.getUrlEncoder().withoutPadding()
        val header = encoder.encodeToString("{\"alg\":\"RS256\"}".toByteArray(StandardCharsets.UTF_8))
        val payload = encoder.encodeToString(claims.toByteArray(StandardCharsets.UTF_8))
        return "$header.$payload.signature"
    }
}
