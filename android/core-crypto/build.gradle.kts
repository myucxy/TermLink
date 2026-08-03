plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "dev.termlink.crypto"
    compileSdk = 36
    defaultConfig { minSdk = 26 }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true }
}

dependencies {
    api(project(":core-protocol"))
    implementation("org.bouncycastle:bcprov-jdk18on:1.80")
    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
}
