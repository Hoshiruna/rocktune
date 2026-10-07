# SPDX-License-Identifier: MIT
"""Musical events shared by the parser and binary encoder."""

from dataclasses import dataclass, field
from fractions import Fraction
from typing import Dict, List, Optional, Tuple


CHANNELS = ("SQ1", "SQ2", "TRI", "NOI")
DEFAULT_FPS = Fraction("60.0988")


class MusicError(ValueError):
    """An input or encoding error that can be displayed directly to the user."""


@dataclass
class Note:
    start: Fraction
    end: Fraction
    pitch: Optional[int]
    noise: Optional[int]
    line: int

    @property
    def is_rest(self):
        return self.pitch is None and self.noise is None


@dataclass
class Setting:
    name: str
    values: Tuple[int, ...]
    line: int


@dataclass
class LoopPoint:
    time: Fraction
    line: int


@dataclass
class Score:
    title: str = "Untitled"
    fps: Fraction = DEFAULT_FPS
    channels: Dict[str, List[object]] = field(default_factory=dict)
