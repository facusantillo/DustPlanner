namespace DustPlanner.Core.Rules;

/// <summary>What kind of copy a collection slot holds. <see cref="Unknown"/> copies never get a dust value. Serialized as snake_case strings via <see cref="Json.CoreJson"/>.</summary>
public enum PremiumKind
{
    Normal,
    Golden,
    Signature,
    Diamond,
    Unknown,
}

/// <summary>Maps HDT's count-array indexes (0..3) to <see cref="PremiumKind"/>, from <c>data/premium-slots.json</c>.</summary>
public sealed class PremiumSlotMap
{
    private readonly PremiumKind[] _slots;

    public PremiumSlotMap(IReadOnlyList<PremiumKind> slots, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = slots.ToArray();
        Confirmed = confirmed;
    }

    /// <summary>Default, conservative mapping: 0 = normal, 1 = golden, 2 and 3 unknown.</summary>
    public static PremiumSlotMap Default { get; } = new(new[] { PremiumKind.Normal, PremiumKind.Golden, PremiumKind.Unknown, PremiumKind.Unknown }, confirmed: false);

    public bool Confirmed { get; }

    public int SlotCount => _slots.Length;

    public PremiumKind KindOf(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot] : PremiumKind.Unknown;

    public int? SlotOf(PremiumKind kind)
    {
        var index = Array.IndexOf(_slots, kind);
        return index < 0 ? null : index;
    }

    public static PremiumSlotMap Load(string? overrideDirectory = null)
    {
        var file = GameDataFiles.Read<SlotFile>(GameDataFiles.PremiumSlotsFile, overrideDirectory);
        var slots = new List<PremiumKind>();
        for (var i = 0; i < 4; i++)
        {
            var name = file.Slots.GetValueOrDefault(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? "unknown";
            slots.Add(Enum.TryParse<PremiumKind>(name, ignoreCase: true, out var kind) ? kind : PremiumKind.Unknown);
        }

        return new PremiumSlotMap(slots, file.Confirmed);
    }

    private sealed class SlotFile
    {
        public bool Confirmed { get; set; }

        public Dictionary<string, string> Slots { get; set; } = new();
    }
}
