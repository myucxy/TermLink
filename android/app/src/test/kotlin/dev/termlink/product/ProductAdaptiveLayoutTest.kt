package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductAdaptiveLayoutTest {
    @Test fun flatTabletAndSmallCoverKeepTheirFullWindow() {
        assertEquals(ProductDisplayArea(0, 0, 1600, 2560), productDisplayArea(1600, 2560, 0, 0, emptyList()))
        assertEquals(ProductDisplayArea(0, 0, 480, 800), productDisplayArea(480, 800, 0, 0, emptyList()))
    }

    @Test fun bookAvoidsAnOccludingHingeAndKeepsTheLargestContinuousPane() {
        val fold = ProductDisplayFold(ProductDisplayArea(700, 0, 730, 1800), true)
        assertEquals(ProductDisplayArea(730, 0, 1600, 1800), productDisplayArea(1600, 1800, 0, 0, listOf(fold)))
        val equal = ProductDisplayFold(ProductDisplayArea(800, 0, 800, 1800), true)
        assertEquals(ProductDisplayArea(0, 0, 800, 1800), productDisplayArea(1600, 1800, 0, 0, listOf(equal)))
        assertEquals(ProductDisplayArea(800, 0, 1600, 1800), productDisplayArea(1600, 1800, 0, 0, listOf(equal), true))
    }

    @Test fun tabletopUsesTheUprightPaneAndDoesNotSubtractTheKeyboardTwice() {
        val fold = ProductDisplayFold(ProductDisplayArea(0, 900, 1800, 920), false)
        val area = productDisplayArea(1800, 1900, 0, 0, listOf(fold))
        assertEquals(ProductDisplayArea(0, 0, 1800, 900), area)
        assertEquals(0, productPaneImeBottom(area, 1900, 700))
        assertEquals(200, productPaneImeBottom(area, 1900, 1200))
    }

    @Test fun splitScreenCoordinatesAndStaleOutsideFoldsCannotHideTheApp() {
        val fold = ProductDisplayFold(ProductDisplayArea(800, 0, 820, 1800), true)
        assertEquals(ProductDisplayArea(0, 0, 600, 900), productDisplayArea(600, 900, 900, 100, listOf(fold)))
        assertEquals(ProductDisplayArea(0, 0, 700, 900), productDisplayArea(1000, 900, 100, 100, listOf(fold)))
        val partial = ProductDisplayFold(ProductDisplayArea(500, 300, 510, 600), true)
        assertEquals(ProductDisplayArea(0, 0, 1000, 900), productDisplayArea(1000, 900, 0, 0, listOf(partial)))
    }

    @Test fun multipleSeparatingFoldsStillLeaveOneContinuousUsablePane() {
        val folds = listOf(
            ProductDisplayFold(ProductDisplayArea(600, 0, 620, 1600), true),
            ProductDisplayFold(ProductDisplayArea(1220, 0, 1240, 1600), true),
        )
        val area = productDisplayArea(1840, 1600, 0, 0, folds)
        assertTrue(area.width > 0 && area.height == 1600)
        for (fold in folds) assertTrue(area.right <= fold.bounds.left || area.left >= fold.bounds.right)
    }
}
