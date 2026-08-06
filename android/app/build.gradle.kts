plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
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
        versionCode = 3
        versionName = "0.1.3"
        manifestPlaceholders["appAuthRedirectScheme"] = "dev.termlink"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    defaultConfig {
        buildConfigField("String", "TERMLINK_APPSHIP_BASE_URL", "\"$appShipBaseUrl\"")
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
