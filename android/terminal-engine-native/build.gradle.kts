import groovy.json.JsonSlurper
import java.security.MessageDigest

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

val applicationRoot = rootProject.projectDir.parentFile
val nativeRoot = applicationRoot.resolve("native/terminal-core/android")
val nativeLock = applicationRoot.resolve("terminal-core.lock.json")
val supportedAbis = listOf("arm64-v8a", "armeabi-v7a", "x86", "x86_64")

val verifyTerminalNativeArtifacts by tasks.registering {
    group = "verification"
    description = "Verifies the pinned prebuilt terminal JNI libraries; no core source checkout is needed."
    inputs.file(nativeLock)
    inputs.dir(nativeRoot)
    doLast {
        val lock = JsonSlurper().parse(nativeLock) as Map<*, *>
        check(lock["schema_version"] == 2 && lock["revision"].toString().matches(Regex("[0-9a-f]{40}"))) {
            "Invalid terminal-core binary lock"
        }
        val artifacts = lock["artifacts"] as List<*>
        for (abi in supportedAbis) {
            val relative = "native/terminal-core/android/$abi/libtermlink_terminal_jni.so"
            val records = artifacts.map { it as Map<*, *> }.filter { it["path"] == relative }
            check(records.size == 1) { "Missing or duplicate native lock entry: $relative" }
            val binary = applicationRoot.resolve(relative)
            check(binary.isFile) { "Missing prebuilt library: $relative. Obtain the pinned DLL/SO package from the core maintainer." }
            val digest = MessageDigest.getInstance("SHA-256").digest(binary.readBytes())
                .joinToString("") { "%02x".format(it) }
            check(binary.length() == (records.single()["size"] as Number).toLong() && digest == records.single()["sha256"]) {
                "Native binary SHA-256/size mismatch: $relative"
            }
        }
    }
}

android {
    namespace = "dev.termlink.terminal.nativecore"
    compileSdk = 36
    ndkVersion = "27.0.12077973"

    defaultConfig {
        minSdk = 26
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk {
            abiFilters += supportedAbis
        }
    }

    sourceSets.getByName("main").jniLibs.srcDir(nativeRoot)

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    api(project(":terminal-engine-api"))

    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
    testImplementation("androidx.test:core-ktx:1.6.1")
    testImplementation("org.robolectric:robolectric:4.14.1")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
}

tasks.named("preBuild").configure {
    dependsOn(verifyTerminalNativeArtifacts)
}
