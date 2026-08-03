plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.kapt")
}

android {
    namespace = "dev.termlink.database"
    compileSdk = 36

    defaultConfig {
        minSdk = 26
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
    }
}

kapt {
    arguments {
        arg("room.schemaLocation", "$projectDir/schemas")
        arg("room.incremental", "true")
    }
}

dependencies {
    api(project(":core-model"))
    api("androidx.room:room-runtime:2.7.2")
    api("androidx.room:room-ktx:2.7.2")
    kapt("androidx.room:room-compiler:2.7.2")

    testImplementation(kotlin("test"))
    testImplementation("junit:junit:4.13.2")
}
