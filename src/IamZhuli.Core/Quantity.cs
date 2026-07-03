namespace IamZhuli.Core;

/// <summary>
/// 数量值类型(单位:手)。强类型防止与价格/金额混用。
/// 游戏量级用 int 足够(单股流通盘通常远小于 int.MaxValue)。
/// </summary>
public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
{
    public int Value { get; }

    public Quantity(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "数量不能为负。");
        Value = value;
    }

    public static Quantity Zero => new(0);

    public bool IsZero => Value == 0;

    public static implicit operator Quantity(int v) => new(v);

    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);
    public bool Equals(Quantity other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Quantity q && Equals(q);
    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(Quantity a, Quantity b) => a.Value == b.Value;
    public static bool operator !=(Quantity a, Quantity b) => a.Value != b.Value;
    public static bool operator <(Quantity a, Quantity b) => a.Value < b.Value;
    public static bool operator >(Quantity a, Quantity b) => a.Value > b.Value;
    public static bool operator <=(Quantity a, Quantity b) => a.Value <= b.Value;
    public static bool operator >=(Quantity a, Quantity b) => a.Value >= b.Value;

    public static Quantity operator -(Quantity a, Quantity b)
    {
        int r = a.Value - b.Value;
        if (r < 0) throw new InvalidOperationException("数量相减为负。");
        return new Quantity(r);
    }

    public override string ToString() => $"{Value}手";
}
