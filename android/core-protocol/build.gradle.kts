import com.google.protobuf.gradle.*

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("com.google.protobuf")
}

android {
    namespace = "dev.termlink.protocol"
    compileSdk = 36
    defaultConfig { minSdk = 26 }
    sourceSets.getByName("main") {
        proto { srcDir(rootProject.file("../protocol/protobuf")) }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

protobuf {
    protoc { artifact = "com.google.protobuf:protoc:4.31.1" }
    generateProtoTasks {
        all().configureEach {
            builtins { create("java") { option("lite") } }
        }
    }
}

dependencies {
    api("com.google.protobuf:protobuf-javalite:4.31.1")
    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
}
