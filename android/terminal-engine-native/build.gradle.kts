import java.io.File

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

val terminalCoreRoot = rootProject.projectDir.resolve("../terminal-core").canonicalFile
val generatedNativeRoot = layout.buildDirectory.dir("generated/termlink-native")
val nativeBuildRoot = layout.buildDirectory.dir("intermediates/termlink-native")
val supportedAbis = listOf("arm64-v8a", "armeabi-v7a", "x86", "x86_64")

fun executableFromEnvironment(name: String, fallback: File): File =
    providers.environmentVariable(name).orNull?.let(::File) ?: fallback

val cmakeExecutable = executableFromEnvironment(
    "TERMLINK_CMAKE",
    File("D:/Dev/Env/msys64/mingw64/bin/cmake.exe"),
)
val ninjaExecutable = executableFromEnvironment(
    "TERMLINK_NINJA",
    File("D:/Dev/Env/msys64/mingw64/bin/ninja.exe"),
)
val androidSdkRoot = executableFromEnvironment(
    "ANDROID_SDK_ROOT",
    File("D:/Dev/Env/Android/Sdk"),
)
val androidNdkRoot = androidSdkRoot.resolve("ndk/27.0.12077973")
val cargoExecutable = executableFromEnvironment(
    "TERMLINK_CARGO",
    File("D:/Dev/Env/Rust/rustup/toolchains/1.97.1-x86_64-pc-windows-msvc/bin/cargo.exe"),
)

val buildTermlinkNative by tasks.registering {
    group = "build"
    description = "Builds the Rust terminal core and JNI wrapper for all Android ABIs."
    inputs.files(
        fileTree(terminalCoreRoot) {
            include(
                "Cargo.lock",
                "Cargo.toml",
                "rust-toolchain.toml",
                "crates/**/*.rs",
                "crates/**/Cargo.toml",
                "include/**/*.h",
            )
        },
        fileTree("src/main/cpp"),
    )
    outputs.dir(generatedNativeRoot)

    doLast {
        for (abi in supportedAbis) {
            val buildDir = nativeBuildRoot.get().dir(abi).asFile
            val outputDir = generatedNativeRoot.get().dir(abi).asFile
            buildDir.mkdirs()
            outputDir.mkdirs()
            val configure = providers.exec {
                isIgnoreExitValue = true
                commandLine(
                    cmakeExecutable.absolutePath,
                    "-S", file("src/main/cpp").absolutePath,
                    "-B", buildDir.absolutePath,
                    "-G", "Ninja",
                    "-DCMAKE_MAKE_PROGRAM=${ninjaExecutable.absolutePath}",
                    "-DCMAKE_TOOLCHAIN_FILE=${androidNdkRoot.resolve("build/cmake/android.toolchain.cmake").absolutePath}",
                    "-DANDROID_ABI=$abi",
                    "-DANDROID_PLATFORM=android-26",
                    "-DANDROID_NDK=${androidNdkRoot.absolutePath}",
                    "-DTERMLINK_TERMINAL_CORE_ROOT=${terminalCoreRoot.absolutePath}",
                    "-DTERMLINK_CARGO=${cargoExecutable.absolutePath}",
                    "-DTERMLINK_JNI_OUTPUT_DIR=${outputDir.absolutePath}",
                    "-DCMAKE_BUILD_TYPE=Release",
                )
            }.result.get()
            check(configure.exitValue == 0) { "CMake configure failed for $abi" }
            val build = providers.exec {
                isIgnoreExitValue = true
                commandLine(
                    cmakeExecutable.absolutePath,
                    "--build", buildDir.absolutePath,
                    "--target", "termlink_terminal_jni",
                )
            }.result.get()
            check(build.exitValue == 0) { "Native build failed for $abi" }
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

    sourceSets.getByName("main").jniLibs.srcDir(generatedNativeRoot)

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    api(project(":terminal-engine-api"))

    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
}

tasks.named("preBuild").configure {
    dependsOn(buildTermlinkNative)
}
