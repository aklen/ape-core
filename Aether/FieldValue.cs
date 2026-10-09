namespace Ape.Core.Aether;

/// <summary>One stored field payload. Text is an LWW label. Fixed is a raw contribution, not a saturated sum.</summary>
public readonly record struct FieldValue(long Fixed, string? Text)
{
    public static FieldValue FixedPoint(long value) => new(value, null);

    public static FieldValue Label(string value) => new(0, value ?? throw new ArgumentNullException(nameof(value)));

    public bool IsText => Text is not null;
}
