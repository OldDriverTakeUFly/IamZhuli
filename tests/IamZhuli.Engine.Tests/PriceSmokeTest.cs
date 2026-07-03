using IamZhuli.Core;

namespace IamZhuli.Engine.Tests;

/// <summary>
/// Smoke 测试:验证 Core → Engine → Tests 引用链路通,且 Price 值类型基本行为正确。
/// 这是 M0 的环境验证;真正的撮合测试在 M1 编写。
/// </summary>
public class PriceSmokeTest
{
    [Fact]
    public void CanCreate_FromDecimalLiteral_ViaImplicitConversion()
    {
        Price p = 10.50m;
        Assert.Equal(10.50m, p.Value);
    }

    [Fact]
    public void Compare_TwoPrices_UsesDecimalOrdering()
    {
        Price low = 9.99m;
        Price high = 10.01m;

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low != high);
    }

    [Fact]
    public void Construct_NegativePrice_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Price(-1m));
    }

    [Fact]
    public void Equality_WorksAsExpected()
    {
        Price a = 10.50m;
        Price b = new(10.50m);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }
}
