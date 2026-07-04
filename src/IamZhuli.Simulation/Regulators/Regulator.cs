using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Regulators;

/// <summary>监管惩罚等级。</summary>
public enum PenaltyLevel { None, Inquiry, Warning, Investigation, ForcedLiquidation }

/// <summary>监管状态快照(供 UI/DTO)。</summary>
public sealed class RegulatorStatus
{
    public decimal Heat { get; init; }          // 关注值 0~100
    public PenaltyLevel Penalty { get; init; }
    public decimal CashFine { get; init; }      // 本次罚款金额(元)
    public bool IsFailed => Penalty == PenaltyLevel.ForcedLiquidation;
    public string LatestEvent { get; init; } = "";
}

/// <summary>
/// 监管系统。监测玩家操纵行为,累积"监管关注值",触发惩罚阶梯。
/// 监测项:对倒、虚假挂单(大单挂短时撤)、异常波动、大单占比、频繁撤单。
/// 关注值随时间衰减(给玩家"蛰伏冷却"空间)。
/// </summary>
public sealed class Regulator
{
    private readonly ParticipantId _player;
    private decimal _heat;
    private readonly List<string> _recentEvents = new();

    /// <summary>配置:各项操纵的关注值增幅系数(可按难度调)。</summary>
    public RegulatorConfig Config { get; set; } = new();

    public decimal Heat => Math.Min(100m, _heat);
    public PenaltyLevel CurrentPenalty => Heat switch
    {
        >= 100 => PenaltyLevel.ForcedLiquidation,
        >= 80 => PenaltyLevel.Investigation,
        >= 60 => PenaltyLevel.Warning,
        >= 30 => PenaltyLevel.Inquiry,
        _ => PenaltyLevel.None
    };
    public IReadOnlyList<string> RecentEvents => _recentEvents;

    public Regulator(ParticipantId player) => _player = player;

    /// <summary>每笔成交:检测对倒、大单占比。</summary>
    public void OnTrade(Trade trade, bool isPlayerInvolved)
    {
        if (!isPlayerInvolved) return;
        // 对倒:玩家既是 taker 又是 maker(自买自卖)
        if (trade.TakerId.Equals(_player) && trade.MakerId.Equals(_player))
        {
            AddHeat(Config.WashTradeHeat, $"对倒: {trade.Quantity}@{trade.Price}");
        }
        // 大单占比:单笔成交量大(>阈值)且玩家参与
        else if (trade.Quantity.Value >= Config.LargeOrderThreshold)
        {
            AddHeat(Config.LargeOrderHeat, $"大单成交: {trade.Quantity}@{trade.Price}");
        }
    }

    /// <summary>玩家下单时:记录(供虚假挂单/频繁撤单检测)。</summary>
    public void OnOrderPlaced(ParticipantId who, OrderRequest req)
    {
        if (!who.Equals(_player)) return;
        // 限价大单挂簿(可能是虚假挂单),记录待观察
        if (req.Type == OrderType.Limit && req.Quantity.Value >= Config.SpoofOrderThreshold)
        {
            _pendingBigOrders[req.Side == Side.Buy ? ++_buySeq : ++_sellSeq] = (req.Price, req.Quantity, _tickCounter);
        }
    }

    /// <summary>玩家撤单:检测虚假挂单(大单短时挂又撤)、频繁撤单。</summary>
    public void OnOrderCancelled(ParticipantId who, Price orderPrice, Quantity qty, long placedTick)
    {
        if (!who.Equals(_player)) return;
        _cancelCount++;
        // 虚假挂单:大单挂了又撤
        if (qty.Value >= Config.SpoofOrderThreshold)
        {
            long age = _tickCounter - placedTick;
            if (age <= Config.SpoofWindowTicks)
                AddHeat(Config.SpoofHeat, $"虚假挂单: {qty}@{orderPrice}(挂{age}tick即撤)");
        }
        // 频繁撤单:累计撤单数超阈值
        if (_cancelCount >= Config.FrequentCancelThreshold && _cancelCount % Config.FrequentCancelThreshold == 0)
            AddHeat(Config.FrequentCancelHeat, $"频繁撤单(累计{_cancelCount}次)");
    }

    /// <summary>每 tick:异常波动检测 + 关注值衰减。</summary>
    public void OnTick(decimal? priceChangeRatio)
    {
        _tickCounter++;
        // 异常波动:单 tick 价格变动超阈值
        if (priceChangeRatio is { } r && Math.Abs(r) >= Config.VolatilityThreshold)
            AddHeat(Config.VolatilityHeat, $"异常波动: {r:P1}");

        // 关注值衰减(每 DecayIntervalTicks 衰减 DecayAmount)
        if (_tickCounter % Config.DecayIntervalTicks == 0 && _heat > 0)
            _heat = Math.Max(0, _heat - Config.DecayAmount);
    }

    private void AddHeat(decimal amount, string reason)
    {
        _heat = Math.Min(100m, _heat + amount);
        var penalty = CurrentPenalty;
        _recentEvents.Insert(0, $"[关注值{_heat:F0}% {penalty}] {reason}");
        if (_recentEvents.Count > 20) _recentEvents.RemoveAt(_recentEvents.Count - 1);
    }

    public RegulatorStatus GetStatus() => new()
    {
        Heat = Heat,
        Penalty = CurrentPenalty,
        CashFine = CurrentPenalty switch
        {
            PenaltyLevel.Warning => Config.WarningFine,
            PenaltyLevel.Investigation => Config.InvestigationFine,
            _ => 0
        },
        LatestEvent = _recentEvents.Count > 0 ? _recentEvents[0] : ""
    };

    private int _cancelCount;
    private long _tickCounter;
    private long _buySeq, _sellSeq;
    private readonly Dictionary<long, (Price Price, Quantity Qty, long Tick)> _pendingBigOrders = new();

    public void Reset()
    {
        _heat = 0; _cancelCount = 0; _tickCounter = 0;
        _recentEvents.Clear(); _pendingBigOrders.Clear();
    }
}

/// <summary>监管参数(可按关卡难度调)。</summary>
public sealed class RegulatorConfig
{
    public decimal WashTradeHeat { get; set; } = 15m;        // 对倒:大幅
    public decimal SpoofHeat { get; set; } = 8m;             // 虚假挂单:中
    public decimal VolatilityHeat { get; set; } = 6m;        // 异常波动:中
    public decimal LargeOrderHeat { get; set; } = 2m;        // 大单占比:小
    public decimal FrequentCancelHeat { get; set; } = 3m;    // 频繁撤单:小

    public int LargeOrderThreshold { get; set; } = 2000;     // 大单阈值(手)
    public int SpoofOrderThreshold { get; set; } = 1500;     // 虚假挂单识别的大单阈值(手)
    public int SpoofWindowTicks { get; set; } = 10;          // 挂单后多久撤算"虚假"(tick)
    public int FrequentCancelThreshold { get; set; } = 8;    // 累计撤单多少次触发
    public decimal VolatilityThreshold { get; set; } = 0.03m;// 单tick价格变动超3%

    public int DecayIntervalTicks { get; set; } = 20;        // 每20tick衰减一次
    public decimal DecayAmount { get; set; } = 1.5m;         // 每次衰减1.5%

    public decimal WarningFine { get; set; } = 500_000m;     // 警告罚款(元)
    public decimal InvestigationFine { get; set; } = 5_000_000m; // 立案罚款
}
