namespace DustPlanner.Core.Deckstrings;

/// <summary>
/// Encoder/decoder for Blizzard deckstrings (base64 of unsigned varints):
/// <code>
/// 0x00 | version=1 | format
/// heroes:  n, dbfId×n
/// cards×1: n, dbfId×n
/// cards×2: n, dbfId×n
/// cards×N: n, (dbfId, count)×n
/// [0x01 | sideboards×1: n, (dbfId, owner)×n | sideboards×2: n, (dbfId, owner)×n | sideboards×N: n, (dbfId, count, owner)×n]
/// [0x00 when there are no sideboards; older strings end right after cards×N]
/// </code>
/// Reference: hearthsim.info/docs/deckstrings, python-hearthstone <c>deckstrings.py</c>, HearthDb <c>DeckSerializer</c>.
/// </summary>
public static class DeckstringCodec
{
    public const int Version = 1;

    public static DeckCode Decode(string deckstring)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckstring);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(deckstring.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDeckstringException("Deckstring is not valid base64.", ex);
        }

        var reader = new VarIntReader(bytes);
        if (reader.ReadByte() != 0)
        {
            throw new InvalidDeckstringException("Deckstring does not start with the reserved 0x00 byte.");
        }

        var version = reader.ReadVarInt();
        if (version != Version)
        {
            throw new InvalidDeckstringException($"Unsupported deckstring version {version}.");
        }

        var format = (DeckFormat)reader.ReadVarInt();

        var heroCount = reader.ReadVarInt();
        var heroes = new List<int>(heroCount);
        for (var i = 0; i < heroCount; i++)
        {
            heroes.Add(reader.ReadVarInt());
        }

        var cards = new List<DeckCard>();
        var x1 = reader.ReadVarInt();
        for (var i = 0; i < x1; i++)
        {
            cards.Add(new DeckCard(reader.ReadVarInt(), 1));
        }

        var x2 = reader.ReadVarInt();
        for (var i = 0; i < x2; i++)
        {
            cards.Add(new DeckCard(reader.ReadVarInt(), 2));
        }

        var xn = reader.ReadVarInt();
        for (var i = 0; i < xn; i++)
        {
            var dbfId = reader.ReadVarInt();
            var count = reader.ReadVarInt();
            cards.Add(new DeckCard(dbfId, count));
        }

        var sideboards = new List<SideboardCard>();
        if (!reader.AtEnd && reader.ReadByte() == 1)
        {
            var s1 = reader.ReadVarInt();
            for (var i = 0; i < s1; i++)
            {
                var dbfId = reader.ReadVarInt();
                sideboards.Add(new SideboardCard(dbfId, 1, reader.ReadVarInt()));
            }

            var s2 = reader.ReadVarInt();
            for (var i = 0; i < s2; i++)
            {
                var dbfId = reader.ReadVarInt();
                sideboards.Add(new SideboardCard(dbfId, 2, reader.ReadVarInt()));
            }

            var sn = reader.ReadVarInt();
            for (var i = 0; i < sn; i++)
            {
                var dbfId = reader.ReadVarInt();
                var count = reader.ReadVarInt();
                sideboards.Add(new SideboardCard(dbfId, count, reader.ReadVarInt()));
            }
        }

        heroes.Sort();
        cards.Sort((a, b) => a.DbfId.CompareTo(b.DbfId));
        sideboards.Sort((a, b) => a.OwnerDbfId != b.OwnerDbfId ? a.OwnerDbfId.CompareTo(b.OwnerDbfId) : a.DbfId.CompareTo(b.DbfId));
        return new DeckCode(format, heroes, cards, sideboards);
    }

    /// <summary>Encodes in canonical order. Always writes the trailing sideboard marker byte, like the current client.</summary>
    public static string Encode(DeckCode deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        if (deck.Heroes.Count != 1)
        {
            throw new InvalidDeckstringException($"A deckstring needs exactly one hero, got {deck.Heroes.Count}.");
        }

        var writer = new VarIntWriter();
        writer.WriteByte(0);
        writer.WriteVarInt(Version);
        writer.WriteVarInt((int)deck.Format);

        writer.WriteVarInt(deck.Heroes.Count);
        foreach (var hero in deck.Heroes.OrderBy(h => h))
        {
            writer.WriteVarInt(hero);
        }

        var ordered = deck.Cards.OrderBy(c => c.DbfId).ToList();
        WriteGroup(writer, ordered.Where(c => c.Count == 1), c => writer.WriteVarInt(c.DbfId));
        WriteGroup(writer, ordered.Where(c => c.Count == 2), c => writer.WriteVarInt(c.DbfId));
        WriteGroup(writer, ordered.Where(c => c.Count != 1 && c.Count != 2), c =>
        {
            writer.WriteVarInt(c.DbfId);
            writer.WriteVarInt(c.Count);
        });

        if (deck.Sideboards.Count == 0)
        {
            writer.WriteByte(0);
        }
        else
        {
            writer.WriteByte(1);
            var side = deck.Sideboards.OrderBy(s => s.OwnerDbfId).ThenBy(s => s.DbfId).ToList();
            WriteGroup(writer, side.Where(s => s.Count == 1), s =>
            {
                writer.WriteVarInt(s.DbfId);
                writer.WriteVarInt(s.OwnerDbfId);
            });
            WriteGroup(writer, side.Where(s => s.Count == 2), s =>
            {
                writer.WriteVarInt(s.DbfId);
                writer.WriteVarInt(s.OwnerDbfId);
            });
            WriteGroup(writer, side.Where(s => s.Count != 1 && s.Count != 2), s =>
            {
                writer.WriteVarInt(s.DbfId);
                writer.WriteVarInt(s.Count);
                writer.WriteVarInt(s.OwnerDbfId);
            });
        }

        return Convert.ToBase64String(writer.ToArray());
    }

    private static void WriteGroup<T>(VarIntWriter writer, IEnumerable<T> items, Action<T> write)
    {
        var list = items.ToList();
        writer.WriteVarInt(list.Count);
        foreach (var item in list)
        {
            write(item);
        }
    }

    private ref struct VarIntReader(byte[] bytes)
    {
        private readonly byte[] _bytes = bytes;
        private int _position;

        public readonly bool AtEnd => _position >= _bytes.Length;

        public byte ReadByte()
        {
            if (AtEnd)
            {
                throw new InvalidDeckstringException("Unexpected end of deckstring.");
            }

            return _bytes[_position++];
        }

        public int ReadVarInt()
        {
            var result = 0L;
            var shift = 0;
            while (true)
            {
                var b = ReadByte();
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
                if (shift > 35)
                {
                    throw new InvalidDeckstringException("Varint is too long.");
                }
            }

            if (result > int.MaxValue)
            {
                throw new InvalidDeckstringException("Varint exceeds Int32.");
            }

            return (int)result;
        }
    }

    private sealed class VarIntWriter
    {
        private readonly List<byte> _stream = new(64);

        public void WriteByte(byte value) => _stream.Add(value);

        public void WriteVarInt(int value)
        {
            if (value < 0)
            {
                throw new InvalidDeckstringException("Deckstrings cannot encode negative numbers.");
            }

            var v = (uint)value;
            while (v >= 0x80)
            {
                _stream.Add((byte)(v | 0x80));
                v >>= 7;
            }

            _stream.Add((byte)v);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}

public sealed class InvalidDeckstringException : Exception
{
    public InvalidDeckstringException()
    {
    }

    public InvalidDeckstringException(string message)
        : base(message)
    {
    }

    public InvalidDeckstringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
