"""Shared OBS source names and artwork geometry in the 2560×1440 design space."""

CANVAS = (2560, 1440)
FULL_FRAME = (0, 0, *CANVAS)
OUTRO_CAMERA_RECT = (112, 350, 1464, 824)
COMPARISON_RECTS = ((80, 330, 1160, 900), (1320, 330, 1160, 900))

CARD_SOURCES = (
    ("Intro artwork", "Intro-card.png"),
    ("Thank you artwork", "Thank-you-card.png"),
    ("Chapter artwork", "Chapter-card.png"),
    ("Privacy artwork", "Privacy-card.png"),
    ("Comparison frame", "Compare-frame.png"),
    ("CRT title overlay", "CRT-overlay.png"),
)
