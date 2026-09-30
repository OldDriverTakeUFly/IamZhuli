using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IamZhuli.Presenter;

/// <summary>
/// SaveData ↔ JSON 手写通道:零依赖(netstandard2.1/Unity Mono 通用)、decimal 精确无损。
/// 输出与 ASP.NET System.Text.Json 序列化同一记录的字节格式兼容(PascalCase 键、
/// 裸数字 decimal)——Web 壳落盘的存档文件与本地存档可互换读取。
/// 字段增删须同步改 Writer/Binder 两处,xUnit 黄金回环测试把关。
/// </summary>
public static class SaveJson
{
    private static readonly CultureInfo I = CultureInfo.InvariantCulture;

    // ════════════════ 序列化 ════════════════

    public static string Serialize(SaveData s)
    {
        var sb = new StringBuilder(2048);
        sb.Append('{');
        K(sb, "LevelId"); Str(sb, s.LevelId);
        K(sb, "Day"); sb.Append(s.Day.ToString(I));
        K(sb, "LastPrice"); Dec(sb, s.LastPrice);
        K(sb, "PreviousClose"); Dec(sb, s.PreviousClose);
        K(sb, "Player"); Acct(sb, s.Player);
        K(sb, "Ai"); Acct(sb, s.Ai);
        K(sb, "Institution"); Acct(sb, s.Institution);
        K(sb, "AiState"); Str(sb, s.AiState);
        K(sb, "InstRiskLevel"); Str(sb, s.InstRiskLevel);
        K(sb, "Cards");
        sb.Append('{');
        K(sb, "Hand"); StrArr(sb, s.Cards.Hand);
        K(sb, "Draw"); StrArr(sb, s.Cards.Draw);
        K(sb, "Discard"); StrArr(sb, s.Cards.Discard);
        K(sb, "Energy"); sb.Append(s.Cards.Energy.ToString(I));
        K(sb, "Turn"); sb.Append(s.Cards.Turn.ToString(I));
        K(sb, "CustomDeck"); sb.Append(s.Cards.CustomDeck ? "true" : "false");
        sb.Append('}');
        K(sb, "Intent");
        if (s.Intent is null) sb.Append("null"); else Str(sb, s.Intent);
        K(sb, "Heat"); Dec(sb, s.Heat);
        K(sb, "InfoHeat"); Dec(sb, s.InfoHeat);
        K(sb, "ShortablePool"); sb.Append(s.ShortablePool.ToString(I));
        K(sb, "TotalShortable"); sb.Append(s.TotalShortable.ToString(I));
        K(sb, "Sentiment");
        sb.Append('{');
        K(sb, "Value"); Dec(sb, s.Sentiment.Value);
        K(sb, "Target"); Dec(sb, s.Sentiment.Target);
        K(sb, "Confidence"); Dec(sb, s.Sentiment.Confidence);
        K(sb, "Herd"); Dec(sb, s.Sentiment.Herd);
        K(sb, "Bias"); Dec(sb, s.Sentiment.Bias);
        sb.Append('}');
        K(sb, "WaterArmy");
        sb.Append('{');
        K(sb, "Level"); sb.Append(s.WaterArmy.Level.ToString(I));
        K(sb, "Active"); sb.Append(s.WaterArmy.Active ? "true" : "false");
        K(sb, "ActiveDays"); sb.Append(s.WaterArmy.ActiveDays.ToString(I));
        sb.Append('}');
        K(sb, "News");
        sb.Append('[');
        for (int i = 0; i < s.News.Count; i++)
        {
            var n = s.News[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            K(sb, "Type"); Str(sb, n.Type);
            K(sb, "Headline"); Str(sb, n.Headline);
            K(sb, "Impact"); Dec(sb, n.Impact);
            K(sb, "RemainingTicks"); sb.Append(n.RemainingTicks.ToString(I));
            sb.Append('}');
        }
        sb.Append(']');
        K(sb, "Candles");
        sb.Append('[');
        for (int i = 0; i < s.Candles.Count; i++)
        {
            var c = s.Candles[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            K(sb, "Day"); sb.Append(c.Day.ToString(I));
            K(sb, "Open"); Dec(sb, c.Open);
            K(sb, "High"); Dec(sb, c.High);
            K(sb, "Low"); Dec(sb, c.Low);
            K(sb, "Close"); Dec(sb, c.Close);
            K(sb, "Volume"); sb.Append(c.Volume.ToString(I));
            sb.Append('}');
        }
        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    private static void Acct(StringBuilder sb, AccountSave a)
    {
        sb.Append('{');
        K(sb, "Cash"); Dec(sb, a.Cash);
        K(sb, "Available"); sb.Append(a.Available.ToString(I));
        K(sb, "T1Locked"); sb.Append(a.T1Locked.ToString(I));
        K(sb, "AvgCost"); Dec(sb, a.AvgCost);
        K(sb, "ShortQty"); sb.Append(a.ShortQty.ToString(I));
        K(sb, "ShortCost"); Dec(sb, a.ShortCost);
        K(sb, "Margin"); Dec(sb, a.Margin);
        K(sb, "Bought"); sb.Append(a.Bought.ToString(I));
        K(sb, "Sold"); sb.Append(a.Sold.ToString(I));
        sb.Append('}');
    }

    /// <summary>写键名 + 冒号(键名是 ASCII 标识符,无需转义)。
    /// 仅在"上一个值结束"后补逗号——数组/对象开括号([ 或 {)后不加。</summary>
    private static void K(StringBuilder sb, string key)
    {
        char last = sb[sb.Length - 1];
        if (last != '{' && last != '[') sb.Append(',');
        sb.Append('"').Append(key).Append("\":");
    }

    private static void Dec(StringBuilder sb, decimal d) => sb.Append(d.ToString(I));

    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", I));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void StrArr(StringBuilder sb, List<string> arr)
    {
        sb.Append('[');
        for (int i = 0; i < arr.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Str(sb, arr[i]);
        }
        sb.Append(']');
    }

    // ════════════════ 解析 ════════════════

    public static SaveData Parse(string json)
    {
        var p = new Parser(json);
        var root = p.ParseNode();
        p.SkipWs();
        if (!p.AtEnd) throw p.Err("尾部有多余字符");
        var o = root.AsObject("存档根");
        return new SaveData
        {
            LevelId = o.Str("LevelId"),
            Day = o.Int("Day"),
            LastPrice = o.Dec("LastPrice"),
            PreviousClose = o.Dec("PreviousClose"),
            Player = Acct(o, "Player"),
            Ai = Acct(o, "Ai"),
            Institution = Acct(o, "Institution"),
            AiState = o.Str("AiState"),
            InstRiskLevel = o.Str("InstRiskLevel"),
            Cards = ParseCards(o),
            Intent = o.Has("Intent") && !o.IsNull("Intent") ? o.Str("Intent") : null,
            Heat = o.Dec("Heat"),
            InfoHeat = o.Dec("InfoHeat"),
            ShortablePool = o.Int("ShortablePool"),
            TotalShortable = o.Int("TotalShortable"),
            Sentiment = ParseSentiment(o),
            WaterArmy = ParseWaterArmy(o),
            News = ParseNews(o),
            Candles = ParseCandles(o),
        };
    }

    private static AccountSave Acct(Node o, string key)
    {
        var a = o.Get(key).AsObject(key);
        return new AccountSave(a.Dec("Cash"), a.Int("Available"), a.Int("T1Locked"), a.Dec("AvgCost"),
            a.Int("ShortQty"), a.Dec("ShortCost"), a.Dec("Margin"), a.Int("Bought"), a.Int("Sold"));
    }

    private static CardEngineSave ParseCards(Node o)
    {
        var c = o.Get("Cards").AsObject("Cards");
        return new CardEngineSave(
            c.Get("Hand").AsArray("Hand").StrList(),
            c.Get("Draw").AsArray("Draw").StrList(),
            c.Get("Discard").AsArray("Discard").StrList(),
            c.Int("Energy"), c.Int("Turn"), c.Bool("CustomDeck"));
    }

    private static SentimentSave ParseSentiment(Node o)
    {
        var s = o.Get("Sentiment").AsObject("Sentiment");
        return new SentimentSave(s.Dec("Value"), s.Dec("Target"), s.Dec("Confidence"), s.Dec("Herd"), s.Dec("Bias"));
    }

    private static WaterArmySave ParseWaterArmy(Node o)
    {
        var w = o.Get("WaterArmy").AsObject("WaterArmy");
        return new WaterArmySave(w.Int("Level"), w.Bool("Active"), w.Int("ActiveDays"));
    }

    private static List<SavedNewsDto> ParseNews(Node o)
    {
        var arr = o.Get("News").AsArray("News").Items ?? new List<Node>();
        var list = new List<SavedNewsDto>(arr.Count);
        foreach (var n in arr)
        {
            var x = n.AsObject("News[]");
            list.Add(new SavedNewsDto(x.Str("Type"), x.Str("Headline"), x.Dec("Impact"), x.Int("RemainingTicks")));
        }
        return list;
    }

    private static List<CandleDto> ParseCandles(Node o)
    {
        var arr = o.Get("Candles").AsArray("Candles").Items ?? new List<Node>();
        var list = new List<CandleDto>(arr.Count);
        foreach (var c in arr)
        {
            var x = c.AsObject("Candles[]");
            list.Add(new CandleDto(x.Int("Day"), x.Dec("Open"), x.Dec("High"), x.Dec("Low"), x.Dec("Close"), x.Int("Volume")));
        }
        return list;
    }

    // ── 轻量 DOM ──

    private enum Kind { Object, Array, String, Number, Bool, Null }

    private sealed class Node
    {
        public Kind K;
        public Dictionary<string, Node>? Obj;
        public List<Node>? Items;
        public string? Text;      // 字符串值或数字原文

        public Node Get(string key)
        {
            if (K != Kind.Object || Obj == null || !Obj.TryGetValue(key, out var v))
                throw new FormatException($"缺少字段 {key}");
            return v;
        }
        public bool Has(string key) => K == Kind.Object && Obj != null && Obj.ContainsKey(key);
        public bool IsNull(string key) => Get(key).K == Kind.Null;
        public Node AsObject(string what)
        {
            if (K != Kind.Object) throw new FormatException($"{what} 应为对象");
            return this;
        }
        public Node AsArray(string what)
        {
            if (K != Kind.Array) throw new FormatException($"{what} 应为数组");
            return this;
        }
        public string Str(string key)
        {
            var v = Get(key);
            if (v.K != Kind.String) throw new FormatException($"字段 {key} 应为字符串");
            return v.Text!;
        }
        public int Int(string key)
        {
            var v = Get(key);
            if (v.K != Kind.Number || !int.TryParse(v.Text, NumberStyles.Integer, I, out var n))
                throw new FormatException($"字段 {key} 应为整数");
            return n;
        }
        public decimal Dec(string key)
        {
            var v = Get(key);
            if (v.K != Kind.Number || !decimal.TryParse(v.Text, NumberStyles.Number, I, out var d))
                throw new FormatException($"字段 {key} 应为数字");
            return d;
        }
        public bool Bool(string key)
        {
            var v = Get(key);
            if (v.K != Kind.Bool) throw new FormatException($"字段 {key} 应为布尔");
            return v.Text == "true";
        }
        public List<string> StrList()
        {
            var items = Items ?? throw new FormatException("字符串数组未初始化");
            var list = new List<string>(items.Count);
            foreach (var it in items)
                if (it.K == Kind.String) list.Add(it.Text!);
                else throw new FormatException("字符串数组元素应为字符串");
            return list;
        }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            if (string.IsNullOrEmpty(s)) throw new FormatException("空存档");
            _s = s;
        }

        public bool AtEnd => _i >= _s.Length;

        public Node ParseNode()
        {
            SkipWs();
            if (AtEnd) throw Err("意外结束");
            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return new Node { K = Kind.String, Text = ParseString() };
                case 't': Expect("true"); return new Node { K = Kind.Bool, Text = "true" };
                case 'f': Expect("false"); return new Node { K = Kind.Bool, Text = "false" };
                case 'n': Expect("null"); return new Node { K = Kind.Null };
                default: return ParseNumber();
            }
        }

        private Node ParseObject()
        {
            _i++;   // {
            var node = new Node { K = Kind.Object, Obj = new Dictionary<string, Node>() };
            SkipWs();
            if (!AtEnd && _s[_i] == '}') { _i++; return node; }
            while (true)
            {
                SkipWs();
                if (AtEnd || _s[_i] != '"') throw Err("对象键应为字符串");
                string key = ParseString();
                SkipWs();
                if (AtEnd || _s[_i] != ':') throw Err("缺少冒号");
                _i++;
                node.Obj[key] = ParseNode();
                SkipWs();
                if (AtEnd) throw Err("对象未闭合");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == '}') { _i++; return node; }
                throw Err("对象内非法字符");
            }
        }

        private Node ParseArray()
        {
            _i++;   // [
            var node = new Node { K = Kind.Array, Items = new List<Node>() };
            SkipWs();
            if (!AtEnd && _s[_i] == ']') { _i++; return node; }
            while (true)
            {
                node.Items.Add(ParseNode());
                SkipWs();
                if (AtEnd) throw Err("数组未闭合");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; return node; }
                throw Err("数组内非法字符");
            }
        }

        private string ParseString()
        {
            _i++;   // "
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                char c = _s[_i];
                if (c == '"') { _i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); _i++; continue; }
                _i++;
                if (AtEnd) throw Err("转义意外结束");
                switch (_s[_i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (_i + 4 >= _s.Length) throw Err("\\u 转义不完整");
                        var hex = _s.Substring(_i + 1, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, I, out var code)) throw Err("\\u 转义非法");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default: throw Err("非法转义");
                }
                _i++;
            }
            throw Err("字符串未闭合");
        }

        private Node ParseNumber()
        {
            int start = _i;
            while (!AtEnd)
            {
                char c = _s[_i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
                else break;
            }
            if (_i == start) throw Err("非法数字");
            return new Node { K = Kind.Number, Text = _s.Substring(start, _i - start) };
        }

        private void Expect(string word)
        {
            if (_i + word.Length > _s.Length || _s.Substring(_i, word.Length) != word)
                throw Err($"应为 {word}");
            _i += word.Length;
        }

        public void SkipWs()
        {
            while (!AtEnd && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
        }

        public FormatException Err(string msg) => new($"{msg}(位置 {_i})");
    }
}
