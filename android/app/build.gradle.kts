import java.net.URI
import java.security.MessageDigest
import org.gradle.api.DefaultTask
import org.gradle.api.provider.Property
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.tasks.CacheableTask
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.TaskAction

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

val publicTermLinkServerUrl = "https://termlink.kget.cc:8910"

fun normalizeTermLinkServerUrl(value: String): String {
    val candidate = value.trim().trimEnd('/')
    require(candidate.isNotEmpty()) { "termlinkServerUrl cannot be blank" }
    val uri = runCatching { URI(candidate) }
        .getOrElse { throw GradleException("termlinkServerUrl is invalid", it) }
    val scheme = uri.scheme?.lowercase()
    require(scheme == "https" || scheme == "http") { "termlinkServerUrl must use HTTPS or explicit local-test HTTP" }
    require(uri.rawUserInfo == null) { "termlinkServerUrl cannot contain credentials" }
    require(!uri.host.isNullOrBlank()) { "termlinkServerUrl must include a host" }
    require(uri.rawQuery == null && uri.rawFragment == null) { "termlinkServerUrl cannot contain a query or fragment" }
    require(uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") { "termlinkServerUrl cannot contain a path" }
    return URI(scheme, null, uri.host.lowercase(), uri.port, null, null, null).toString().trimEnd('/')
}

fun isLocalTestHost(host: String): Boolean {
    val normalized = host.lowercase()
    if (normalized == "localhost" || normalized.endsWith(".localhost") || normalized == "::1") return true
    val octets = normalized.split('.').mapNotNull(String::toIntOrNull)
    if (octets.size != 4 || octets.any { it !in 0..255 }) return false
    return octets[0] == 10 ||
        octets[0] == 127 ||
        octets[0] == 192 && octets[1] == 168 ||
        octets[0] == 172 && octets[1] in 16..31
}

fun buildConfigString(value: String): String =
    "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""

@CacheableTask
abstract class GenerateTailFirstE2EContract : DefaultTask() {
    @get:Input
    abstract val applicationId: Property<String>

    @get:Input
    abstract val serverOrigin: Property<String>

    @get:Input
    abstract val terminalSnapshotRuntime: Property<Boolean>

    @get:OutputDirectory
    abstract val outputDirectory: DirectoryProperty

    private fun jsonString(value: String): String =
        "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""

    @TaskAction
    fun generate() {
        val outputFile = outputDirectory.file("termlink-tail-first-e2e-contract-v1.json").get().asFile
        outputFile.parentFile.mkdirs()
        outputFile.writeText(
            """
            {
              "schema_version": 1,
              "transport": "explicit_broadcast_run_as_file",
              "package_name": ${jsonString(applicationId.get())},
              "receiver_class": "dev.termlink.product.debug.TailFirstE2EReceiver",
              "broadcast_action": "dev.termlink.product.debug.TAIL_FIRST_E2E",
              "response_relative_path": "no_backup/tail-first-e2e/response.json",
              "server_origin": ${jsonString(serverOrigin.get())},
              "terminal_snapshot_runtime": ${terminalSnapshotRuntime.get()},
              "operations": [
                "probe",
                "open_session",
                "inspect",
                "page_up",
                "open_history",
                "close_history",
                "show_ime"
              ]
            }
            """.trimIndent() + "\n",
            Charsets.UTF_8,
        )
    }
}

@CacheableTask
abstract class GenerateDirectProductE2EContract : DefaultTask() {
    @get:Input
    abstract val applicationId: Property<String>

    @get:Input
    abstract val serverOrigin: Property<String>

    @get:Input
    abstract val directTransportShadow: Property<Boolean>

    @get:Input
    abstract val directTransportDataPlane: Property<Boolean>

    @get:OutputDirectory
    abstract val outputDirectory: DirectoryProperty

    private fun jsonString(value: String): String =
        "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""

    @TaskAction
    fun generate() {
        check(directTransportShadow.get() && directTransportDataPlane.get()) {
            "Direct product E2E contract requires both acceptance gates"
        }
        val outputFile = outputDirectory.file("termlink-direct-product-e2e-contract-v1.json").get().asFile
        outputFile.parentFile.mkdirs()
        outputFile.writeText(
            """
            {
              "schema_version": 1,
              "kind": "termlink.android-direct-product-e2e-contract",
              "transport": "explicit_broadcast_run_as_file",
              "application_id": ${jsonString(applicationId.get())},
              "receiver_class": "dev.termlink.product.debug.DirectProductE2EReceiver",
              "broadcast_action": "dev.termlink.product.debug.DIRECT_PRODUCT_E2E",
              "response_relative_path": "no_backup/direct-product-e2e/response.json",
              "server_origin": ${jsonString(serverOrigin.get())},
              "build_type": "debug",
              "shadow_acceptance": ${directTransportShadow.get()},
              "data_plane_acceptance": ${directTransportDataPlane.get()},
              "required_server_capabilities": [
                "direct_transport_shadow_v1",
                "direct_transport_shadow_multipair_v2",
                "direct_transport_data_v1",
                "direct_session_cutover_v1"
              ],
              "operation_extra": "operation",
              "request_id_extra": "request_id",
              "run_id_extra": "run_id",
              "arguments_json_extra": "arguments_json",
              "probe_operation": "probe",
              "operations": [
                "probe",
                "inspect_transport",
                "send_text_marker",
                "send_key",
                "request_resize",
                "background",
                "foreground",
                "lock_screen",
                "wake_screen",
                "open_late_session",
                "start_long_output",
                "stop_long_output",
                "prepare_session",
                "close_test_sessions"
              ],
              "diagnostics": [
                "app_process_id",
                "app_process_start_time",
                "candidate_kinds",
                "carrier",
                "contiguous_applied_sequence",
                "digest_conflict_count",
                "duplicate_apply_count",
                "duplicate_drop_count",
                "durable_ack",
                "fallback_reason",
                "gap_count",
                "last_received_sequence",
                "marker_digest",
                "network_generation",
                "path_epoch",
                "relay_generation",
                "signalling_generation",
                "transport_receipt_high_watermark",
                "viewport_digest"
              ]
            }
            """.trimIndent() + "\n",
            Charsets.UTF_8,
        )
    }
}

val requestedDebugServerUrlProperty = providers.gradleProperty("termlinkServerUrl")
    .orNull
    ?.takeIf { it.isNotBlank() }
val requestedDebugServerUrl = requestedDebugServerUrlProperty
    ?: providers.environmentVariable("TERMLINK_SERVER_URL").orNull?.takeIf { it.isNotBlank() }
val debugServerUrl = normalizeTermLinkServerUrl(requestedDebugServerUrl ?: publicTermLinkServerUrl)
val debugServerUri = URI(debugServerUrl)
val debugAllowsCleartextLocalTest = debugServerUri.scheme.equals("http", ignoreCase = true) &&
    requestedDebugServerUrl != null &&
    isLocalTestHost(debugServerUri.host)
val debugTerminalSnapshotRuntime = providers.gradleProperty("termlinkEnableTerminalSnapshotRuntime")
    // Snapshot V2 is the host-history authority. Debug must match Release by
    // default so an existing positive ACK can recover from the Windows Agent.
    .orElse("true")
    .get()
    .toBooleanStrictOrNull()
    ?: throw GradleException("termlinkEnableTerminalSnapshotRuntime must be true or false")
val debugDirectTransportShadow = providers.gradleProperty("termlinkEnableDirectTransportShadow")
    // Direct transport remains a shadow-only experiment until the cross-network
    // failure matrix proves that it cannot perturb the Relay connection.
    .orElse("false")
    .get()
    .toBooleanStrictOrNull()
    ?: throw GradleException("termlinkEnableDirectTransportShadow must be true or false")
val debugDirectTransportDataPlane = providers.gradleProperty("termlinkEnableDirectTransportDataPlane")
    // The real Direct data plane is available only to an explicitly isolated
    // acceptance build. Public-origin use additionally requires an exact,
    // repeated origin acknowledgement. Omitted always means disabled.
    .orElse("false")
    .get()
    .toBooleanStrictOrNull()
    ?: throw GradleException("termlinkEnableDirectTransportDataPlane must be true or false")
val debugPublicDirectAcceptanceOrigin = providers.gradleProperty(
    "termlinkPublicDirectAcceptanceOrigin",
).orNull
    ?.trim()
    ?.takeIf { it.isNotEmpty() }
    ?.let(::normalizeTermLinkServerUrl)
val termLinkVersionCode = providers.gradleProperty("termlinkVersionCode")
    .orElse("32")
    .get()
    .toIntOrNull()
    ?.takeIf { it > 0 }
    ?: throw GradleException("termlinkVersionCode must be a positive integer")
val termLinkVersionName = providers.gradleProperty("termlinkVersionName")
    .orElse("0.1.26")
    .get()
    .trim()
    .takeIf { it.isNotEmpty() }
    ?: throw GradleException("termlinkVersionName must not be blank")
val debugApplicationIdSuffix = providers.gradleProperty("termlinkDebugApplicationIdSuffix")
    .orNull
    ?.trim()
    ?.takeIf { it.isNotEmpty() }
    ?.let { if (it.startsWith('.')) it else ".$it" }

if (debugDirectTransportDataPlane) {
    if (!debugDirectTransportShadow) {
        throw GradleException(
            "termlinkEnableDirectTransportDataPlane=true requires " +
                "-PtermlinkEnableDirectTransportShadow=true",
        )
    }
    val explicitPrivateOrigin = requestedDebugServerUrlProperty != null &&
        isLocalTestHost(debugServerUri.host)
    val explicitlyAcknowledgedPublicOrigin = requestedDebugServerUrlProperty != null &&
        debugServerUrl == publicTermLinkServerUrl &&
        debugPublicDirectAcceptanceOrigin == publicTermLinkServerUrl
    if (!explicitPrivateOrigin && !explicitlyAcknowledgedPublicOrigin) {
        throw GradleException(
            "Direct data-plane acceptance requires either an explicit private/localhost " +
                "-PtermlinkServerUrl origin, or an exact public-origin acknowledgement " +
                "through -PtermlinkPublicDirectAcceptanceOrigin",
        )
    }
    if (debugApplicationIdSuffix == null) {
        throw GradleException(
            "Direct data-plane acceptance requires an isolated " +
                "-PtermlinkDebugApplicationIdSuffix",
        )
    }
}

android {
    namespace = "dev.termlink.harness"
    compileSdk = 36

    val appShipBaseUrl = providers.gradleProperty("termlinkAppShipBaseUrl")
        .orElse(providers.environmentVariable("TERMLINK_APPSHIP_BASE_URL"))
        .orElse(providers.environmentVariable("APPSHIP_BASE_URL"))
        .orElse("https://appship.kget.cc:8910")
        .get()
        .replace("\\", "\\\\")
        .replace("\"", "\\\"")
    val releaseStoreFile = providers.gradleProperty("termlinkReleaseStoreFile")
        .orElse(providers.environmentVariable("TERMLINK_RELEASE_STORE_FILE"))
        .orNull
    val releaseStorePassword = providers.gradleProperty("termlinkReleaseStorePassword")
        .orElse(providers.environmentVariable("TERMLINK_RELEASE_STORE_PASSWORD"))
        .orNull
    val releaseKeyAlias = providers.gradleProperty("termlinkReleaseKeyAlias")
        .orElse(providers.environmentVariable("TERMLINK_RELEASE_KEY_ALIAS"))
        .orNull
    val releaseKeyPassword = providers.gradleProperty("termlinkReleaseKeyPassword")
        .orElse(providers.environmentVariable("TERMLINK_RELEASE_KEY_PASSWORD"))
        .orNull

    defaultConfig {
        applicationId = "dev.termlink.harness"
        minSdk = 26
        targetSdk = 36
        versionCode = termLinkVersionCode
        versionName = termLinkVersionName
        // AppAuth declares an optional browser receiver in its library manifest.
        // The app-owned embedded flow removes that component at merge time, but
        // the library placeholder must still have a non-routable value.
        manifestPlaceholders["appAuthRedirectScheme"] = "dev.termlink.disabled"
        manifestPlaceholders["termlinkDirectProductE2EEnabled"] = "false"
        buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(publicTermLinkServerUrl))
        buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", "false")
        buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", "true")
        buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_SHADOW", "true")
        buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_DATA_PLANE", "true")
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    defaultConfig {
        buildConfigField("String", "TERMLINK_APPSHIP_BASE_URL", "\"$appShipBaseUrl\"")
    }

    buildTypes {
        getByName("debug") {
            debugApplicationIdSuffix?.let { applicationIdSuffix = it }
            buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(debugServerUrl))
            buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", debugAllowsCleartextLocalTest.toString())
            buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", debugTerminalSnapshotRuntime.toString())
            buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_SHADOW", debugDirectTransportShadow.toString())
            buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_DATA_PLANE", debugDirectTransportDataPlane.toString())
            manifestPlaceholders["termlinkUsesCleartextTraffic"] = debugAllowsCleartextLocalTest.toString()
            manifestPlaceholders["termlinkDirectProductE2EEnabled"] = debugDirectTransportDataPlane.toString()
        }
        getByName("release") {
            // Release intentionally ignores all local Debug injection inputs.
            buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(publicTermLinkServerUrl))
            buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", "false")
            buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", "true")
            buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_SHADOW", "true")
            buildConfigField("boolean", "TERMLINK_DIRECT_TRANSPORT_DATA_PLANE", "true")
        }
    }

    // Release signing is opt-in and supplied by the build environment. Never commit
    // a keystore path or password; an unset configuration intentionally produces an
    // unsigned release artifact that cannot be published as production.
    if (!releaseStoreFile.isNullOrBlank() &&
        !releaseStorePassword.isNullOrBlank() &&
        !releaseKeyAlias.isNullOrBlank()) {
        signingConfigs {
            create("termlinkRelease") {
                storeFile = file(releaseStoreFile)
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword ?: releaseStorePassword
            }
        }
        buildTypes.getByName("release").signingConfig = signingConfigs.getByName("termlinkRelease")
    }

    packaging {
        resources.excludes += "META-INF/versions/9/OSGI-INF/MANIFEST.MF"
    }

    sourceSets {
        named("main") {
            assets.srcDir(rootProject.file("../testdata"))
            assets.srcDir(layout.buildDirectory.dir("generated/termlink-license-assets"))
        }
        named("test") {
            resources.srcDir(rootProject.file("../protocol/test-vectors"))
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
        unitTests.isIncludeAndroidResources = true
    }
}

androidComponents {
    onVariants(selector().withBuildType("debug")) { variant ->
        val generateContract = tasks.register<GenerateTailFirstE2EContract>(
            "generate${variant.name.replaceFirstChar { it.uppercase() }}TailFirstE2EContract",
        ) {
            applicationId.set(variant.applicationId)
            serverOrigin.set(debugServerUrl)
            terminalSnapshotRuntime.set(debugTerminalSnapshotRuntime)
        }
        variant.sources.assets?.addGeneratedSourceDirectory(
            generateContract,
            GenerateTailFirstE2EContract::outputDirectory,
        )

        if (debugDirectTransportDataPlane) {
            val generateDirectProductContract = tasks.register<GenerateDirectProductE2EContract>(
                "generate${variant.name.replaceFirstChar { it.uppercase() }}DirectProductE2EContract",
            ) {
                applicationId.set(variant.applicationId)
                serverOrigin.set(debugServerUrl)
                directTransportShadow.set(debugDirectTransportShadow)
                directTransportDataPlane.set(debugDirectTransportDataPlane)
            }
            variant.sources.assets?.addGeneratedSourceDirectory(
                generateDirectProductContract,
                GenerateDirectProductE2EContract::outputDirectory,
            )
        }
    }
}

val verifyAiWebAssets = tasks.register("verifyAiWebAssets") {
    doLast {
        val web = rootProject.file("../ai-web")
        val assets = project.file("src/main/assets/ai-web")
        val manifest = assets.resolve("manifest.json")
        check(manifest.isFile) { "AI Web assets missing: run npm ci && npm run build in ai-web" }
        val value = groovy.json.JsonSlurper().parse(manifest) as Map<*, *>
        val sourceNames = (listOf("src", "scripts").flatMap { dir -> web.resolve(dir).walkTopDown().filter { it.isFile }.toList() } +
            listOf("package.json", "package-lock.json", "tsconfig.json", "vite.config.ts", "index.html").map(web::resolve))
            .map { it.relativeTo(web).invariantSeparatorsPath }.toSet()
        check(sourceNames == (value["sources"] as Map<*, *>).keys) { "AI Web source set changed. Run npm run build in ai-web." }
        for ((name, sourceRoot) in listOf("sources" to web, "files" to assets)) {
            val entries = value[name] as Map<*, *>
            for ((relative, expected) in entries) {
                val file = sourceRoot.resolve(relative as String).canonicalFile
                check(file.toPath().startsWith(sourceRoot.canonicalFile.toPath()) && file.isFile) { "AI Web file missing: $relative" }
                val actual = MessageDigest.getInstance("SHA-256").digest(file.readBytes()).joinToString("") { "%02x".format(it) }
                check(actual == expected) { "AI Web assets are stale: $relative. Run npm run build in ai-web." }
            }
        }
    }
}
val syncTermLinkLicenseAssets = tasks.register<Sync>("syncTermLinkLicenseAssets") {
    from(rootProject.file("../LICENSE"), rootProject.file("../NOTICE"))
    into(layout.buildDirectory.dir("generated/termlink-license-assets/licenses/termlink"))
}
tasks.named("preBuild").configure { dependsOn(verifyAiWebAssets, syncTermLinkLicenseAssets) }

dependencies {
    implementation(project(":terminal-engine-api"))
    implementation(project(":terminal-engine-replay"))
    implementation(project(":terminal-engine-native"))
    implementation(project(":core-session"))
    implementation(project(":core-network"))
    implementation(project(":core-model"))
    implementation(project(":core-crypto"))
    implementation(project(":feature-history"))
    implementation(project(":feature-billing"))

    implementation(platform("androidx.compose:compose-bom:2025.06.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.webkit:webkit:1.14.0")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("net.openid:appauth:0.11.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.1")
    implementation("org.commonmark:commonmark:0.24.0")
    implementation("org.commonmark:commonmark-ext-gfm-tables:0.24.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("androidx.test:core-ktx:1.6.1")
    testImplementation("org.robolectric:robolectric:4.14.1")
    debugImplementation("androidx.compose.ui:ui-tooling")
}
