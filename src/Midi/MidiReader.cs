// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text;
using RockTune.Domain;

namespace RockTune.Midi;

/// <summary>Reads synchronous Standard MIDI Files with PPQN timing.</summary>
public static class MidiReader
{
    public static MidiSong Read(string path)
    {
        var input = new MidiBytes(File.ReadAllBytes(path));
        if (input.Tag() != "MThd")
        {
            throw new MusicException("Cannot read MIDI: missing MThd header");
        }
        var header = input.Chunk();
        var format = header.Word();
        var trackCount = header.Word();
        var division = header.Word();
        if (format is not (0 or 1))
        {
            throw new MusicException("Only synchronous MIDI formats 0 and 1 are supported");
        }
        if (trackCount == 0 || (format == 0 && trackCount != 1))
        {
            throw new MusicException("MIDI needs tracks; format 0 must contain exactly one track");
        }
        if ((division & 0x8000) != 0 || division == 0)
        {
            throw new MusicException("SMPTE MIDI timing is not supported; use PPQN timing");
        }
        var tracks = new List<MidiTrack>();
        var changes = new List<(long Tick, int Tempo)>();
        while (tracks.Count < trackCount)
        {
            var tag = input.Tag();
            var chunk = input.Chunk();
            if (tag != "MTrk")
            {
                continue;
            }
            var builder = new TrackBuilder(tracks.Count + 1);
            var trackTempos = new Dictionary<long, int>();
            long tick = 0;
            var runningStatus = 0;
            while (!chunk.AtEnd)
            {
                tick = checked(tick + chunk.VariableLength());
                var first = chunk.Byte();
                var status = first;
                int? firstData = null;
                if (first < 0x80)
                {
                    if (runningStatus == 0)
                    {
                        throw new MusicException("Cannot read MIDI: data byte without running status");
                    }
                    status = runningStatus;
                    firstData = first;
                }
                if (status == 0xFF)
                {
                    runningStatus = 0;
                    var type = chunk.DataByte();
                    var payload = chunk.Read(chunk.VariableLength());
                    if (type == 0x2F)
                    {
                        if (payload.Length != 0)
                        {
                            throw new MusicException("Cannot read MIDI: invalid end-of-track event");
                        }
                        break;
                    }
                    if (type == 0x03 && builder.Track.Name.Length == 0)
                    {
                        builder.Track.Name = Encoding.Latin1.GetString(payload);
                    }
                    if (type == 0x51)
                    {
                        if (payload.Length != 3)
                        {
                            throw new MusicException("Cannot read MIDI: tempo event must have three bytes");
                        }
                        trackTempos[tick] = (payload[0] << 16) | (payload[1] << 8) | payload[2];
                    }
                }
                else if (status is 0xF0 or 0xF7)
                {
                    runningStatus = 0;
                    chunk.Read(chunk.VariableLength());
                }
                else if (status is >= 0x80 and < 0xF0)
                {
                    runningStatus = status;
                    var data1 = firstData ?? chunk.DataByte();
                    var kind = status & 0xF0;
                    var data2 = kind is 0xC0 or 0xD0 ? 0 : chunk.DataByte();
                    builder.Message(tick, kind, status & 15, data1, data2);
                }
                else
                {
                    throw new MusicException($"Cannot read MIDI: unsupported status ${status:X2}");
                }
            }
            tracks.Add(builder.Finish(tick));
            changes.AddRange(trackTempos.Select(item => (item.Key, item.Value)));
        }
        return new MidiSong(System.IO.Path.GetFullPath(path), format, tracks, new TempoMap(division, changes));
    }

    private sealed class MidiBytes(byte[] data, int start = 0, int? end = null)
    {
        private int position = start;
        private readonly int limit = end ?? data.Length;
        public bool AtEnd => position == limit;

        public byte[] Read(int count)
        {
            Require(count);
            var result = data.AsSpan(position, count).ToArray();
            position += count;
            return result;
        }
        private void Require(int count)
        {
            if (count < 0 || count > limit - position)
            {
                throw new MusicException("Cannot read MIDI: truncated chunk or event");
            }
        }
        public int Byte()
        {
            Require(1);
            return data[position++];
        }
        public int DataByte()
        {
            var value = Byte();
            if (value >= 128)
            {
                throw new MusicException("Cannot read MIDI: expected a 7-bit data byte");
            }
            return value;
        }
        public int Word() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
        public string Tag() => Encoding.ASCII.GetString(Read(4));
        public MidiBytes Chunk()
        {
            var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(Read(4)));
            Require(count);
            var result = new MidiBytes(data, position, position + count);
            position += count;
            return result;
        }
        public int VariableLength()
        {
            var value = 0;
            for (var index = 0; index < 4; index++)
            {
                var next = Byte();
                value = (value << 7) | (next & 0x7F);
                if ((next & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new MusicException("Cannot read MIDI: variable-length value exceeds four bytes");
        }
    }

    private sealed class TrackBuilder(int number)
    {
        public MidiTrack Track { get; } = new(number, "", 0);
        private readonly Dictionary<(int Channel, int Pitch), Queue<(long Tick, int Velocity)>> active = [];
        private readonly List<(int Pitch, long Tick, int Velocity)>[] sustained = Enumerable.Range(0, 16)
            .Select(_ => new List<(int Pitch, long Tick, int Velocity)>()).ToArray();
        private readonly bool[] pedal = new bool[16];

        private void Complete(int channel, int pitch, (long Tick, int Velocity) onset, long tick) =>
            Track.Notes.Add(new MidiNote(onset.Tick, tick, pitch, onset.Velocity, channel));

        private void EndSustain(int channel, long tick)
        {
            foreach (var note in sustained[channel])
            {
                Complete(channel, note.Pitch, (note.Tick, note.Velocity), tick);
            }
            sustained[channel].Clear();
        }

        public void Message(long tick, int kind, int channel, int data1, int data2)
        {
            var key = (channel, data1);
            if (kind == 0x90 && data2 != 0)
            {
                if (!active.TryGetValue(key, out var queue))
                {
                    queue = new Queue<(long Tick, int Velocity)>();
                    active[key] = queue;
                }
                queue.Enqueue((tick, data2));
            }
            else if (kind is 0x80 or 0x90)
            {
                if (!active.TryGetValue(key, out var queue) || queue.Count == 0)
                {
                    Track.Warnings.Add($"Unmatched note-off at tick {tick}: channel {channel + 1}, pitch {data1}");
                    return;
                }
                var onset = queue.Dequeue();
                if (pedal[channel])
                {
                    sustained[channel].Add((data1, onset.Tick, onset.Velocity));
                }
                else
                {
                    Complete(channel, data1, onset, tick);
                }
            }
            else if (kind == 0xC0)
            {
                Track.Programs.Add(new Report { ["tick"] = tick, ["channel"] = channel + 1, ["program"] = data1 + 1 });
            }
            else if (kind == 0xE0)
            {
                Track.Bends.Add(new Report { ["tick"] = tick, ["channel"] = channel + 1, ["value"] = ((data2 << 7) | data1) - 8192 });
            }
            else if (kind == 0xB0)
            {
                Track.Controls.Add(new Report { ["tick"] = tick, ["channel"] = channel + 1, ["control"] = data1, ["value"] = data2 });
                if (data1 == 64)
                {
                    pedal[channel] = data2 >= 64;
                    if (!pedal[channel])
                    {
                        EndSustain(channel, tick);
                    }
                }
                else if (data1 is 120 or 123)
                {
                    if (data1 == 120)
                    {
                        EndSustain(channel, tick);
                    }
                    foreach (var (noteKey, queue) in active.Where(item => item.Key.Channel == channel))
                    {
                        while (queue.Count > 0)
                        {
                            var onset = queue.Dequeue();
                            if (data1 == 123 && pedal[channel])
                            {
                                sustained[channel].Add((noteKey.Pitch, onset.Tick, onset.Velocity));
                            }
                            else
                            {
                                Complete(channel, noteKey.Pitch, onset, tick);
                            }
                        }
                    }
                }
            }
        }

        public MidiTrack Finish(long tick)
        {
            Track.EndTick = tick;
            foreach (var (key, queue) in active)
            {
                foreach (var onset in queue)
                {
                    Complete(key.Channel, key.Pitch, onset, tick);
                    Track.Warnings.Add($"Missing note-off: pitch {key.Pitch} closed at track end");
                }
            }
            for (var channel = 0; channel < 16; channel++)
            {
                if (sustained[channel].Count > 0)
                {
                    Track.Warnings.Add("Sustain pedal held at track end; sustained notes closed there");
                    EndSustain(channel, tick);
                }
            }
            var sorted = Track.Notes.OrderBy(note => note.StartTick).ThenBy(note => note.Pitch).ThenBy(note => note.EndTick).ToArray();
            Track.Notes.Clear();
            Track.Notes.AddRange(sorted);
            return Track;
        }
    }
}
