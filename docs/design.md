# Rope design

Clippboard uses an original paper-and-peg logo, a braided vector rope across the full monitor work area, and neutral paper surfaces. Cloud is white with a quiet blue accent; Paper uses warm cream; Midnight uses charcoal with a lavender accent. Desktop content remains visible around the notes and compact toolbar.

The opening movement combines a critically damped drop with a small rope-tension bounce and staggered note rotation driven by a continuous damped oscillator. The drop eases over 880 ms and rope/note settling over 1,100 ms; frozen rope pens avoid per-frame brush allocations. Scrolling settles over 340 ms, note hover lifts the paper gently, deletions fade before removal, and reordering moves notes from their previous positions. Settings tabs fade; panels reveal and dismiss, and expanded notes ease into a larger size. Windows reduced-motion preferences and the app's animation switch suppress animated movement. Notes follow the rope curve while scrolling.

Lavender, Ocean and Rose join Cloud, Paper and Midnight. Theme selection uses compact two-column swatches. Slider tracks show the selected range and expose precise minus/plus adjustments. Pop-up tooltips explicitly receive the owner's palette to maintain readable contrast in dark themes.

Appearance has a separate temporary draft: themes, note width, spacing and font size preview on the rope while editing. Storage, capture, shortcuts and cache budgets remain committed settings. Save commits the normalized draft; closing or cancelling restores the saved appearance. Failed saves restore the saved appearance and display an error.

The layout draws inspiration from Apple's discussion of responsive, interruptible motion in [Designing Fluid Interfaces](https://developer.apple.com/videos/play/wwdc2018/803/) and Google's research on shape and visual hierarchy in [Expressive Material Design](https://design.google/library/expressive-material-design-google-research). Graphics and controls are original WPF vectors; no platform icon assets are redistributed.

Visual previews use synthetic notes exclusively. They never read or modify the real clipboard.

Button highlights fade over 140–200 ms alongside a small hover response. Switch knobs settle over 220 ms, with checked initial state positioned before the first rendered frame. Keyboard focus outlines sit above hover layers. Text fields receive themed tooltips without scaling the field or moving the caret. Reduced motion applies these states immediately.
