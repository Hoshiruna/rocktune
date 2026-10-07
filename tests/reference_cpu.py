# SPDX-License-Identifier: MIT
"""Small instruction interpreter for the supplied sound driver's research.

This is not a console emulator: it has flat memory and no hardware or cycle model.
"""

OPCODES = {}
for row in """
01 ORA ix;05 ORA zp;09 ORA im;0D ORA ab;11 ORA iy;15 ORA zx;19 ORA ay;1D ORA ax
21 AND ix;25 AND zp;29 AND im;2D AND ab;31 AND iy;35 AND zx;39 AND ay;3D AND ax
41 EOR ix;45 EOR zp;49 EOR im;4D EOR ab;51 EOR iy;55 EOR zx;59 EOR ay;5D EOR ax
61 ADC ix;65 ADC zp;69 ADC im;6D ADC ab;71 ADC iy;75 ADC zx;79 ADC ay;7D ADC ax
81 STA ix;85 STA zp;8D STA ab;91 STA iy;95 STA zx;99 STA ay;9D STA ax
A1 LDA ix;A5 LDA zp;A9 LDA im;AD LDA ab;B1 LDA iy;B5 LDA zx;B9 LDA ay;BD LDA ax
C1 CMP ix;C5 CMP zp;C9 CMP im;CD CMP ab;D1 CMP iy;D5 CMP zx;D9 CMP ay;DD CMP ax
E1 SBC ix;E5 SBC zp;E9 SBC im;ED SBC ab;F1 SBC iy;F5 SBC zx;F9 SBC ay;FD SBC ax
06 ASL zp;0A ASL ac;0E ASL ab;16 ASL zx;1E ASL ax
26 ROL zp;2A ROL ac;2E ROL ab;36 ROL zx;3E ROL ax
46 LSR zp;4A LSR ac;4E LSR ab;56 LSR zx;5E LSR ax
66 ROR zp;6A ROR ac;6E ROR ab;76 ROR zx;7E ROR ax
86 STX zp;8E STX ab;96 STX zy;84 STY zp;8C STY ab;94 STY zx
A2 LDX im;A6 LDX zp;AE LDX ab;B6 LDX zy;BE LDX ay
A0 LDY im;A4 LDY zp;AC LDY ab;B4 LDY zx;BC LDY ax
C6 DEC zp;CE DEC ab;D6 DEC zx;DE DEC ax
E6 INC zp;EE INC ab;F6 INC zx;FE INC ax
C0 CPY im;C4 CPY zp;CC CPY ab;E0 CPX im;E4 CPX zp;EC CPX ab
24 BIT zp;2C BIT ab;4C JMP ab;6C JMP in;20 JSR ab
10 BPL re;30 BMI re;50 BVC re;70 BVS re;90 BCC re;B0 BCS re;D0 BNE re;F0 BEQ re
08 PHP no;28 PLP no;48 PHA no;68 PLA no;60 RTS no
18 CLC no;38 SEC no;58 CLI no;78 SEI no;B8 CLV no;D8 CLD no;F8 SED no
88 DEY no;98 TYA no;A8 TAY no;C8 INY no;E8 INX no;CA DEX no
8A TXA no;9A TXS no;AA TAX no;BA TSX no;EA NOP no
""".strip().splitlines():
    for entry in row.split(";"):
        code, mnemonic, mode = entry.split()
        OPCODES[int(code, 16)] = (mnemonic, mode)


class Cpu6502:
    def __init__(self, memory):
        self.memory = bytearray(memory)
        self.a = self.x = self.y = self.pc = 0
        self.sp = 0xFD
        self.p = 0x20
        self.before_step = None

    def flag(self, mask, condition):
        self.p = (self.p | mask) if condition else (self.p & ~mask)

    def nz(self, value):
        value &= 0xFF
        self.flag(2, value == 0)
        self.flag(0x80, value & 0x80)
        return value

    def byte(self):
        value = self.memory[self.pc]
        self.pc = (self.pc + 1) & 0xFFFF
        return value

    def word(self):
        return self.byte() | self.byte() << 8

    def read_word(self, address, zero_page=False):
        following = (address + 1) & (0xFF if zero_page else 0xFFFF)
        return self.memory[address] | self.memory[following] << 8

    def push(self, value):
        self.memory[0x100 + self.sp] = value & 0xFF
        self.sp = (self.sp - 1) & 0xFF

    def pop(self):
        self.sp = (self.sp + 1) & 0xFF
        return self.memory[0x100 + self.sp]

    def address(self, mode):
        if mode == "im":
            address = self.pc
            self.byte()
            return address
        if mode in ("zp", "zx", "zy", "ix", "iy"):
            base = self.byte()
            if mode == "zp":
                return base
            if mode in ("zx", "zy"):
                return (base + (self.x if mode == "zx" else self.y)) & 0xFF
            if mode == "ix":
                return self.read_word((base + self.x) & 0xFF, True)
            return (self.read_word(base, True) + self.y) & 0xFFFF
        base = self.word()
        if mode == "in":
            following = (base & 0xFF00) | ((base + 1) & 0xFF)
            return self.memory[base] | self.memory[following] << 8
        return (base + (self.x if mode == "ax" else self.y if mode == "ay" else 0)) & 0xFFFF

    def step(self):
        if self.before_step:
            self.before_step(self)
        instruction_address = self.pc
        opcode = self.byte()
        if opcode not in OPCODES:
            raise ValueError("Unsupported opcode {:02X} at {:04X}".format(opcode, instruction_address))
        name, mode = OPCODES[opcode]
        if mode == "re":
            displacement = self.byte()
            mask, wanted = {"BPL": (128, False), "BMI": (128, True),
                            "BVC": (64, False), "BVS": (64, True),
                            "BCC": (1, False), "BCS": (1, True),
                            "BNE": (2, False), "BEQ": (2, True)}[name]
            if bool(self.p & mask) == wanted:
                self.pc = (self.pc + (displacement if displacement < 128 else displacement - 256)) & 0xFFFF
            return
        if mode == "no":
            if name == "RTS":
                self.pc = (self.pop() | self.pop() << 8) + 1
                self.pc &= 0xFFFF
            elif name in ("PHA", "PHP"):
                self.push(self.a if name == "PHA" else self.p | 0x30)
            elif name == "PLA":
                self.a = self.nz(self.pop())
            elif name == "PLP":
                self.p = self.pop() | 0x20
            elif name in ("CLC", "SEC", "CLI", "SEI", "CLV", "CLD", "SED"):
                mask = {"CLC": 1, "SEC": 1, "CLI": 4, "SEI": 4,
                        "CLV": 64, "CLD": 8, "SED": 8}[name]
                self.flag(mask, name in ("SEC", "SEI", "SED"))
            elif name in ("INX", "DEX", "INY", "DEY"):
                register = "x" if name.endswith("X") else "y"
                value = getattr(self, register) + (1 if name.startswith("IN") else -1)
                setattr(self, register, self.nz(value))
            elif name != "NOP":
                source, target = {"TXA": ("x", "a"), "TYA": ("y", "a"),
                                  "TAX": ("a", "x"), "TAY": ("a", "y"),
                                  "TXS": ("x", "sp"), "TSX": ("sp", "x")}[name]
                value = getattr(self, source)
                setattr(self, target, value if name == "TXS" else self.nz(value))
            return
        address = None if mode == "ac" else self.address(mode)
        if name in ("JMP", "JSR"):
            if name == "JSR":
                self.push((self.pc - 1) >> 8)
                self.push(self.pc - 1)
            self.pc = address
            return
        value = self.a if mode == "ac" else self.memory[address]
        if name in ("LDA", "LDX", "LDY"):
            setattr(self, name[-1].lower(), self.nz(value))
        elif name in ("STA", "STX", "STY"):
            self.memory[address] = getattr(self, name[-1].lower())
        elif name in ("ORA", "AND", "EOR"):
            result = self.a | value if name == "ORA" else self.a & value if name == "AND" else self.a ^ value
            self.a = self.nz(result)
        elif name in ("ADC", "SBC"):
            operand = value if name == "ADC" else value ^ 0xFF
            result = self.a + operand + (self.p & 1)
            self.flag(64, (~(self.a ^ operand) & (self.a ^ result)) & 128)
            self.flag(1, result > 255)
            self.a = self.nz(result)
        elif name in ("CMP", "CPX", "CPY"):
            register = self.a if name == "CMP" else self.x if name == "CPX" else self.y
            self.flag(1, register >= value)
            self.nz(register - value)
        elif name == "BIT":
            self.flag(2, self.a & value == 0)
            self.flag(128, value & 128)
            self.flag(64, value & 64)
        else:
            carry = self.p & 1
            if name in ("ASL", "ROL"):
                self.flag(1, value & 128)
                result = (value << 1) | (carry if name == "ROL" else 0)
            elif name in ("LSR", "ROR"):
                self.flag(1, value & 1)
                result = (value >> 1) | (carry << 7 if name == "ROR" else 0)
            elif name in ("INC", "DEC"):
                result = value + (1 if name == "INC" else -1)
            else:
                raise ValueError("Unimplemented instruction: " + name)
            result = self.nz(result)
            if mode == "ac":
                self.a = result
            else:
                self.memory[address] = result

    def call(self, address, a=0, y=0, instruction_limit=100000):
        self.a, self.y, self.pc = a, y, address
        self.push(0x7F)
        self.push(0xFE)
        for count in range(instruction_limit):
            if self.pc == 0x7FFF:
                return count
            self.step()
        raise ValueError("Sound routine did not return within instruction limit")
