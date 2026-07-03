namespace IamZhuli.Core;

/// <summary>
/// 订单唯一标识。强类型 ID,避免 long 到处裸用。
/// </summary>
public readonly record struct OrderId(long Value)
{
    public override string ToString() => $"#{Value}";
}

/// <summary>
/// 市场参与者唯一标识(玩家 / AI 主力 / 散户群体)。
/// </summary>
public readonly record struct ParticipantId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// 成交记录唯一标识。
/// </summary>
public readonly record struct TradeId(long Value);
