pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    // D:\Dev installs an organization-wide init script that contributes a local mirror.
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "TermLinkAndroid"
include(":app")
include(":terminal-engine-api")
include(":terminal-engine-replay")
include(":terminal-engine-termux")
include(":core-session")
include(":core-network")
include(":termux-terminal-emulator")
include(":termux-terminal-view")

project(":termux-terminal-emulator").projectDir =
    file("../terminal-forks/termux-terminal/terminal-emulator")
project(":termux-terminal-view").projectDir =
    file("../terminal-forks/termux-terminal/terminal-view")
