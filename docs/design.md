# Rope design

Clippboard uses an original paper-and-peg logo, a braided vector rope across the full monitor work area, and neutral paper surfaces. Cloud is white with a quiet blue accent; Paper uses warm cream; Midnight uses charcoal with a lavender accent. Desktop content remains visible around the notes and compact toolbar.

The opening movement combines a critically damped drop with a small rope-tension bounce and staggered note rotation. Scroll movement eases over 230 ms. Windows reduced-motion preferences and the app's animation switch suppress movement. Notes follow the rope curve while scrolling, rather than retaining a fixed vertical alignment.

The layout draws inspiration from Apple's discussion of responsive, interruptible motion in [Designing Fluid Interfaces](https://developer.apple.com/videos/play/wwdc2018/803/) and Google's research on shape and visual hierarchy in [Expressive Material Design](https://design.google/library/expressive-material-design-google-research). Graphics and controls are original WPF vectors; no platform icon assets are redistributed.

Visual previews use synthetic notes exclusively. They never read or modify the real clipboard.
