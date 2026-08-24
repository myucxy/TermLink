package dev.termlink.product

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductResourceParityTest {
    @Test
    fun englishAndSimplifiedChineseExposeTheSameStringKeys() {
        val resourceRoot = locateResourceRoot()
        val english = stringKeys(resourceRoot.resolve("values/strings.xml"))
        val simplifiedChinese = stringKeys(resourceRoot.resolve("values-zh-rCN/strings.xml"))

        setOf(
            "registration_first_name_label",
            "registration_last_name_label",
            "registration_success",
            "registration_failure",
        ).forEach { key ->
            assertTrue("$key must be translated", key in english)
        }
        assertEquals(english, simplifiedChinese)
    }

    private fun locateResourceRoot(): Path {
        val workingDirectory = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        return listOf(
            workingDirectory.resolve("src/main/res"),
            workingDirectory.resolve("app/src/main/res"),
            workingDirectory.resolve("android/app/src/main/res"),
        ).firstOrNull(Files::isDirectory)
            ?: error("Android resource root was not found from $workingDirectory")
    }

    private fun stringKeys(file: Path): Set<String> {
        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file.toFile())
        val nodes = document.getElementsByTagName("string")
        return buildSet {
            repeat(nodes.length) { index ->
                add(nodes.item(index).attributes.getNamedItem("name").nodeValue)
            }
        }
    }
}
