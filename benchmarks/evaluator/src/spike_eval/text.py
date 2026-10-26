from __future__ import annotations

from collections.abc import Mapping
import unicodedata


def normalize_for_cer(text: str) -> str:
    """Return a deterministic Unicode-aware form for character error rate."""
    if not isinstance(text, str):
        raise TypeError("text must be a string")

    normalized = unicodedata.normalize("NFC", text).casefold()
    characters: list[str] = []
    for character in normalized:
        category = unicodedata.category(character)
        if character.isspace():
            characters.append(" ")
        elif category[0] in {"L", "M", "N"}:
            characters.append(character)
        else:
            characters.append(" ")

    return " ".join("".join(characters).split())


def comparison_text(item: object) -> str:
    """Select the corpus' spoken form and normalize it for evaluation."""
    if not isinstance(item, Mapping):
        raise ValueError("INVALID_SPOKEN_NORMALIZED: corpus item must be a mapping")

    spoken = item.get("spoken_normalized")
    if not isinstance(spoken, str) or not spoken.strip():
        raise ValueError("INVALID_SPOKEN_NORMALIZED: non-empty string required")

    return normalize_for_cer(spoken)

