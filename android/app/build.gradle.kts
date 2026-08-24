import java.net.URI
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
              "package_name": "dev.termlink.harness",
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

val requestedDebugServerUrl = providers.gradleProperty("termlinkServerUrl")
    .orElse(providers.environmentVariable("TERMLINK_SERVER_URL"))
    .orNull
    ?.takeIf { it.isNotBlank() }
val debugServerUrl = normalizeTermLinkServerUrl(requestedDebugServerUrl ?: publicTermLinkServerUrl)
val debugServerUri = URI(debugServerUrl)
val debugAllowsCleartextLocalTest = debugServerUri.scheme.equals("http", ignoreCase = true) &&
    requestedDebugServerUrl != null &&
    isLocalTestHost(debugServerUri.host)
val debugTerminalSnapshotRuntime = providers.gradleProperty("termlinkEnableTerminalSnapshotRuntime")
    // Debug/emulator builds exercise the same tail-first bootstrap as release.
    // Tests can still opt out explicitly with
    // -PtermlinkEnableTerminalSnapshotRuntime=false.
    .orElse("true")
    .get()
    .toBooleanStrictOrNull()
    ?: throw GradleException("termlinkEnableTerminalSnapshotRuntime must be true or false")
val termLinkVersionCode = providers.gradleProperty("termlinkVersionCode")
    .orElse("31")
    .get()
    .toIntOrNull()
    ?.takeIf { it > 0 }
    ?: throw GradleException("termlinkVersionCode must be a positive integer")
val termLinkVersionName = providers.gradleProperty("termlinkVersionName")
    .orElse("0.1.25")
    .get()
    .trim()
    .takeIf { it.isNotEmpty() }
    ?: throw GradleException("termlinkVersionName must not be blank")

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
        manifestPlaceholders["appAuthRedirectScheme"] = "dev.termlink"
        buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(publicTermLinkServerUrl))
        buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", "false")
        buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", "false")
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
            buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(debugServerUrl))
            buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", debugAllowsCleartextLocalTest.toString())
            buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", debugTerminalSnapshotRuntime.toString())
            manifestPlaceholders["termlinkUsesCleartextTraffic"] = debugAllowsCleartextLocalTest.toString()
        }
        getByName("release") {
            // Release intentionally ignores all local Debug injection inputs.
            buildConfigField("String", "TERMLINK_SERVER_URL", buildConfigString(publicTermLinkServerUrl))
            buildConfigField("boolean", "TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST", "false")
            buildConfigField("boolean", "TERMLINK_TERMINAL_SNAPSHOT_RUNTIME", "true")
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
            serverOrigin.set(debugServerUrl)
            terminalSnapshotRuntime.set(debugTerminalSnapshotRuntime)
        }
        variant.sources.assets?.addGeneratedSourceDirectory(
            generateContract,
            GenerateTailFirstE2EContract::outputDirectory,
        )
    }
}

dependencies {
    implementation(project(":terminal-engine-api"))
    implementation(project(":terminal-engine-replay"))
    implementation(project(":terminal-engine-termux"))
    implementation(project(":core-session"))
    implementation(project(":core-network"))
    implementation(project(":core-model"))
    implementation(project(":core-crypto"))
    implementation(project(":feature-history"))
    implementation(project(":feature-billing"))

    implementation(platform("androidx.compose:compose-bom:2025.06.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("net.openid:appauth:0.11.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("androidx.test:core-ktx:1.6.1")
    testImplementation("org.robolectric:robolectric:4.14.1")
    debugImplementation("androidx.compose.ui:ui-tooling")
}
