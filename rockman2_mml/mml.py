# SPDX-License-Identifier: MIT
"""Parse the documented phase-one MML dialect into timed musical events."""

import re
from fractions import Fraction

from .model import CHANNELS, LoopPoint, MusicError, Note, Score, Setting


NOTE_OFFSETS = {"c": 0, "d": 2, "e": 4, "f": 5, "g": 7, "a": 9, "b": 11}
SECTION = re.compile(r"^(SQ1|SQ2|TRI|NOI)\s*:\s*(.*)$", re.IGNORECASE)


class ChannelParser:
    def __init__(self, name, text):
        self.name = name
        self.text = text
        self.position = 0
        self.tempo = Fraction(120)
        self.octave = 4
        self.default_length = 4
        self.time = Fraction(0)
        self.events = []
        self.pending_tie = False
        self.has_loop = False

    def error(self, message):
        line = self.text.count("\n", 0, self.position) + 1
        return MusicError("{} line {}: {}".format(self.name, line, message))

    def skip_space(self):
        while self.position < len(self.text) and self.text[self.position].isspace():
            self.position += 1

    def number(self, required=True, decimal=False):
        self.skip_space()
        pattern = r"\d+(?:\.\d+)?" if decimal else r"\d+"
        match = re.match(pattern, self.text[self.position:])
        if not match:
            if required:
                raise self.error("Expected a number here")
            return None
        self.position += len(match.group())
        return Fraction(match.group()) if decimal else int(match.group())

    def duration(self, denominator):
        if not 1 <= denominator <= 256:
            raise self.error("Note length denominator must be between 1 and 256")
        dots = 0
        while self.position < len(self.text) and self.text[self.position] == ".":
            self.position += 1
            dots += 1
        dotted_factor = Fraction(2) - Fraction(1, 2 ** dots)
        return Fraction(240, denominator) / self.tempo * dotted_factor

    def note(self, character, line):
        pitch = noise = None
        if character == "n":
            if self.name != "NOI":
                raise self.error("n notes are only supported on the NOI channel")
            noise = self.number()
            if not 1 <= noise <= 15:
                raise self.error("Noise number must be between 1 and 15")
            self.skip_space()
            if self.position < len(self.text) and self.text[self.position] == "/":
                self.position += 1
                denominator = self.number()
            else:
                denominator = self.default_length
        else:
            if character != "r":
                if self.name == "NOI":
                    raise self.error("Use n1 through n15 for NOI notes and r for rests")
                pitch = 12 * (self.octave + 1) + NOTE_OFFSETS[character]
                if self.position < len(self.text) and self.text[self.position] in "+#-":
                    pitch += -1 if self.text[self.position] == "-" else 1
                    self.position += 1
                if not 0 <= pitch <= 127:
                    raise self.error("Pitch is outside the MIDI note number range")
            denominator = self.number(required=False)
            if denominator is None:
                denominator = self.default_length
        end = self.time + self.duration(denominator)
        event = Note(self.time, end, pitch, noise, line)
        if self.pending_tie:
            previous = self.events[-1]
            if not isinstance(previous, Note) or previous.is_rest or event.is_rest:
                raise self.error("Both ends of a tie must be notes")
            if (previous.pitch, previous.noise) != (event.pitch, event.noise):
                raise self.error("& can only tie notes with the same pitch or noise number")
            previous.end = end
            self.pending_tie = False
        else:
            self.events.append(event)
        self.time = end

    def control(self, line):
        self.skip_space()
        match = re.match(r"[a-zA-Z]+", self.text[self.position:])
        if not match:
            raise self.error("Expected a control name after @, such as @d or @env")
        name = match.group().lower()
        self.position += len(match.group())
        value = self.number()
        if self.pending_tie:
            raise self.error("Cannot change timbre or envelope within a tie")
        if name == "d":
            limit = 1 if self.name == "NOI" else 3
            if self.name == "TRI" or not 0 <= value <= limit:
                raise self.error("@d must be 0 to 3 for square waves or 0 to 1 for NOI; TRI has no duty control")
            self.events.append(Setting("duty", (value,), line))
        elif name == "env":
            self.skip_space()
            if self.position >= len(self.text) or self.text[self.position] != ",":
                raise self.error("Envelope syntax is @env period,step")
            self.position += 1
            step = self.number()
            if self.name == "TRI" or not 0 <= value <= 127 or not 0 <= step <= 15:
                raise self.error("Envelopes apply to square waves and NOI; period must be 0 to 127 and step 0 to 15")
            if (value == 0) != (step == 0):
                raise self.error("Use @env0,0 to disable the envelope; period and step must both be positive to enable it")
            self.events.append(Setting("envelope", (value, step), line))
        else:
            raise self.error("Unsupported control name @" + name)

    def parse(self):
        while True:
            self.skip_space()
            if self.position == len(self.text):
                break
            line = self.text.count("\n", 0, self.position) + 1
            character = self.text[self.position].lower()
            self.position += 1
            if character in "cdefgabrn":
                self.note(character, line)
            elif character in "tolv":
                value = self.number(decimal=character == "t")
                if character == "t":
                    if value <= 0:
                        raise self.error("Tempo must be greater than zero")
                    self.tempo = value
                elif character == "o":
                    if not 0 <= value <= 9:
                        raise self.error("Octave must be between 0 and 9")
                    self.octave = value
                elif character == "l":
                    if not 1 <= value <= 256:
                        raise self.error("Default length denominator must be between 1 and 256")
                    self.default_length = value
                else:
                    if self.name == "TRI" or not 0 <= value <= 15 or self.pending_tie:
                        raise self.error("v applies to square waves and NOI, with values from 0 to 15; volume cannot change within a tie")
                    self.events.append(Setting("volume", (value,), line))
            elif character in "<>":
                self.octave += 1 if character == ">" else -1
                if not 0 <= self.octave <= 9:
                    raise self.error("Octave must be between 0 and 9")
            elif character == "@":
                self.control(line)
            elif character == "&":
                if self.pending_tie or not self.events or not isinstance(self.events[-1], Note) or self.events[-1].is_rest:
                    raise self.error("Expected a note before &")
                self.pending_tie = True
            elif character == "|":
                if self.pending_tie or self.has_loop:
                    raise self.error("Each channel allows one loop point, which cannot occur within a tie")
                self.events.append(LoopPoint(self.time, line))
                self.has_loop = True
            else:
                raise self.error("Unsupported character " + repr(character))
        if self.pending_tie:
            raise self.error("Missing note after &")
        if not any(isinstance(event, Note) for event in self.events):
            raise self.error("Channel needs at least one note or rest")
        return self.events


def parse_mml(text):
    score = Score()
    sections = {}
    current = None
    for line_number, raw in enumerate(text.splitlines(), 1):
        content = raw.split(";", 1)[0].strip()
        if not content:
            continue
        if content.startswith("#title "):
            score.title = content[7:].strip()
            continue
        if content.startswith("#fps "):
            try:
                score.fps = Fraction(content[5:].strip())
            except (ValueError, ZeroDivisionError):
                raise MusicError("Line {}: #fps requires a valid number".format(line_number))
            if not 1 <= score.fps <= 240:
                raise MusicError("#fps must be between 1 and 240")
            continue
        match = SECTION.match(content)
        if match:
            current = match.group(1).upper()
            if current in sections:
                raise MusicError("Duplicate channel section: " + current)
            sections[current] = ["\n" * (line_number - 1), match.group(2), "\n"]
        else:
            if current is None:
                raise MusicError("Line {}: declare SQ1:, SQ2:, TRI:, or NOI: first".format(line_number))
            # Keep blank source lines so error locations refer to the MML file.
            existing_lines = "".join(sections[current]).count("\n")
            sections[current].append("\n" * max(0, line_number - 1 - existing_lines))
            sections[current].extend((content, "\n"))
    for name in CHANNELS:
        if name in sections:
            score.channels[name] = ChannelParser(name, "".join(sections[name])).parse()
    if not score.channels:
        raise MusicError("MML contains no channels")
    return score
