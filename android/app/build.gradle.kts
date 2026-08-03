plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "dev.termlink.harness"
    compileSdk = 36

    defaultConfig {
        applicationId = "dev.termlink.harness"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
        manifestPlaceholders["appAuthRedirectScheme"] = "dev.termlink"
    }

    buildFeatures {
        compose = true
        buildConfig = true
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
    debugImplementation("androidx.compose.ui:ui-tooling")
}
