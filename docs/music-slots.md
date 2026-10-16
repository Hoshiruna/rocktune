# Music IDs and Slot Capacities

These slots apply to the supported Japanese Rockman 2 ROM. Music IDs start at
**0** and can be passed to `patch --track` as decimal or `0x` hexadecimal values.
IDs `0x21` and above are sound effects and cannot be selected for music replacement.

All address ranges below are **inclusive**. File offsets include the 16-byte
iNES header. Capacity is the maximum compiled music size, including its song
header, channel streams and modulation table. It is not the size of the whole
NSF file. Smaller valid songs are allowed; oversized songs are rejected before
writing.

**IDs 15 and 24..32 share one slot. Replacing any one changes all of them.**
There are 33 music IDs backed by 24 distinct slots.

| ID (decimal) | ID (hex) | CPU address range | ROM file offset range | Capacity (bytes) | Shared data |
| --- | --- | --- | --- | ---: | --- |
| 0 | `0x00` | `$8AD6-$8E1C` | `$30AE6-$30E2C` | 839 | - |
| 1 | `0x01` | `$8E1D-$90C7` | `$30E2D-$310D7` | 683 | - |
| 2 | `0x02` | `$90C8-$9486` | `$310D8-$31496` | 959 | - |
| 3 | `0x03` | `$9487-$9697` | `$31497-$316A7` | 529 | - |
| 4 | `0x04` | `$9698-$9A41` | `$316A8-$31A51` | 938 | - |
| 5 | `0x05` | `$9A42-$9E47` | `$31A52-$31E57` | 1030 | - |
| 6 | `0x06` | `$9E48-$A190` | `$31E58-$321A0` | 841 | - |
| 7 | `0x07` | `$A191-$A4A7` | `$321A1-$324B7` | 791 | - |
| 8 | `0x08` | `$A4A8-$A95F` | `$324B8-$3296F` | 1208 | - |
| 9 | `0x09` | `$A960-$AB9F` | `$32970-$32BAF` | 576 | - |
| 10 | `0x0A` | `$ABA0-$AC57` | `$32BB0-$32C67` | 184 | - |
| 11 | `0x0B` | `$AC58-$AE13` | `$32C68-$32E23` | 444 | - |
| 12 | `0x0C` | `$AE14-$AEF3` | `$32E24-$32F03` | 224 | - |
| 13 | `0x0D` | `$AEF4-$B1B3` | `$32F04-$331C3` | 704 | - |
| 14 | `0x0E` | `$B1B4-$B356` | `$331C4-$33366` | 419 | - |
| 15 | `0x0F` | `$B357-$B3D4` | `$33367-$333E4` | 126 | IDs 24..32 |
| 16 | `0x10` | `$B3D5-$B4A5` | `$333E5-$334B5` | 209 | - |
| 17 | `0x11` | `$B4A6-$B552` | `$334B6-$33562` | 173 | - |
| 18 | `0x12` | `$B553-$B58D` | `$33563-$3359D` | 59 | - |
| 19 | `0x13` | `$B58E-$B6DB` | `$3359E-$336EB` | 334 | - |
| 20 | `0x14` | `$B6DC-$B9E9` | `$336EC-$339F9` | 782 | - |
| 21 | `0x15` | `$B9EA-$BA51` | `$339FA-$33A61` | 104 | - |
| 22 | `0x16` | `$BA52-$BAEC` | `$33A62-$33AFC` | 155 | - |
| 23 | `0x17` | `$BAED-$BB21` | `$33AFD-$33B31` | 53 | - |
| 24 | `0x18` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 25 | `0x19` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 26 | `0x1A` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 27 | `0x1B` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 28 | `0x1C` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 29 | `0x1D` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 30 | `0x1E` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 31 | `0x1F` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |
| 32 | `0x20` | `$B357-$B3D4` | `$33367-$333E4` | 126 | ID 15 and IDs 24..32 |

Slot ends use the next distinct music or sound-effect address. The final
physical music slot (`0x17`) ends immediately before sound effect `0x21`.
Aliased IDs do not provide additional storage.
