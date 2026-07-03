namespace IamZhuli.Core;

/// <summary>
/// 价格值类型。游戏内所有金额/价格的强类型封装,内部以 decimal 存储。
/// 强类型的目的是防止"价格"与"数量"等不同单位的 decimal 被混用(编译期拦截)。
/// 绝不使用 double/float 表示货币。
/// </summary>
public readonly struct Price : IEquatable<Price>, IComparable<Price>
{
    public decimal Value { get; }

    public Price(decimal value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "价格不能为负。");
        Value = value;
    }

    public static Price Zero => new(0m);

    // —— 隐式转换:方便从 decimal 字面量构造 ——
    public static implicit operator Price(decimal v) => new(v);

    // —— 比较 ——
    public int CompareTo(Price other) => Value.CompareTo(other.Value);
    public bool Equals(Price other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Price p && Equals(p);
    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(Price a, Price b) => a.Value == b.Value;
    public static bool operator !=(Price a, Price b) => a.Value != b.Value;
    public static bool operator <(Price a, Price b) => a.Value < b.Value;
    public static bool operator >(Price a, Price b) => a.Value > b.Value;
    public static bool operator <=(Price a, Price b) => a.Value <= b.Value;
    public static bool operator >=(Price a, Price b) => a.Value >= b.Value;

    public override string ToString() => Value.ToString("F2");
}
