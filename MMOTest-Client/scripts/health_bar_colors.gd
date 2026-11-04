class_name HealthBarColors
## Centralized palette for every health bar. Edit these to re-theme all bars.
## Colors are treated as opaque (alpha is ignored).

# Overhead bars (above NPCs / players)
const OVERHEAD_BG   := Color(0.35, 0.05, 0.05)
const OVERHEAD_FILL := Color(0.15, 0.85, 0.15)

# Target bar (top of screen, with name + numbers)
const TARGET_BG     := Color(0.25, 0.02, 0.02)
const TARGET_FILL   := Color(0.20, 0.90, 0.20)
const TARGET_TEXT   := Color(1, 1, 1)

# Self HP bar (bottom-right, always visible)
const SELF_BG   := Color(0.30, 0.03, 0.03)
const SELF_FILL := Color(0.15, 0.85, 0.15)

# Damage numbers
const DAMAGE_HIT_COLOR  := Color(1.0, 0.25, 0.2)     # amount >= 1
const DAMAGE_ZERO_COLOR := Color(0.4, 0.6, 1.0)      # amount == 0 (miss / no damage)
