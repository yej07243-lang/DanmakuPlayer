package app.danmaku.player

import android.app.Activity
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.os.Build
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.widget.Button
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.TextView
import java.util.Locale
import kotlin.math.roundToInt

internal fun Activity.dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()

internal fun Activity.themeColor(attribute: Int): Int {
    val value = TypedValue()
    if (!theme.resolveAttribute(attribute, value, true)) return Color.WHITE
    return if (value.resourceId != 0) getColor(value.resourceId) else value.data
}

internal fun Activity.rounded(color: Int, radius: Int = 14) = GradientDrawable().apply {
    setColor(color)
    cornerRadius = dp(radius).toFloat()
}

internal fun Activity.ripple(color: Int = Color.TRANSPARENT, radius: Int = 14) =
    RippleDrawable(ColorStateList.valueOf(0x30ffffff), rounded(color, radius), rounded(Color.WHITE, radius))

internal fun Activity.uiButton(label: String, description: String? = null, click: () -> Unit) =
    Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label
        contentDescription = description
        isAllCaps = false
        minimumHeight = dp(48)
        minimumWidth = dp(48)
        minHeight = dp(48)
        minWidth = dp(48)
        setPadding(dp(12), dp(6), dp(12), dp(6))
        setOnClickListener { click() }
    }

internal fun Activity.selectButton(button: Button, selected: Boolean) {
    button.isSelected = selected
    val accent = themeColor(android.R.attr.colorAccent)
    button.background = ripple(if (selected) (accent and 0x00ffffff) or 0x30000000 else Color.TRANSPARENT)
    button.setTextColor(themeColor(if (selected) android.R.attr.colorAccent else android.R.attr.textColorPrimary))
}

internal fun Activity.heading(label: String, size: Float = 20f) = TextView(this).apply {
    text = label
    textSize = size
    setTypeface(typeface, android.graphics.Typeface.BOLD)
    if (Build.VERSION.SDK_INT >= 28) isAccessibilityHeading = true
}

internal fun Activity.buttonStrip(vararg buttons: View) = HorizontalScrollView(this).apply {
    isFillViewport = true
    isHorizontalScrollBarEnabled = false
    addView(LinearLayout(this@buttonStrip).apply {
        gravity = Gravity.CENTER_VERTICAL
        buttons.forEach { addView(it, LinearLayout.LayoutParams(-2, -2)) }
    })
}

@Suppress("DEPRECATION")
internal fun Activity.applySystemInsets(view: View, includeIme: Boolean = false) {
    if (Build.VERSION.SDK_INT >= 30) window.setDecorFitsSystemWindows(false)
    view.setOnApplyWindowInsetsListener { root, insets ->
        if (Build.VERSION.SDK_INT >= 30) {
            val types = WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout()
            val bars = insets.getInsets(if (includeIme) types or WindowInsets.Type.ime() else types)
            root.setPadding(bars.left, bars.top, bars.right, bars.bottom)
        } else {
            root.setPadding(
                insets.systemWindowInsetLeft, insets.systemWindowInsetTop,
                insets.systemWindowInsetRight, insets.systemWindowInsetBottom,
            )
        }
        insets
    }
}

internal fun playbackTime(seconds: Double): String {
    val total = seconds.toLong().coerceAtLeast(0)
    return String.format(Locale.ROOT, "%d:%02d", total / 60, total % 60)
}
