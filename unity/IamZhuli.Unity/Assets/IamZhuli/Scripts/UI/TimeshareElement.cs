using System.Collections.Generic;
using IamZhuli.Presenter;
using UnityEngine;
using UnityEngine.UIElements;

namespace IamZhuli.Unity
{
    /// <summary>
    /// 分时图元素(UXML 可用):价格折线 + 昨收基准线 + 成交量柱。
    /// 自绘 Mesh(generateVisualContent),无第三方依赖。
    /// 数据由 MainUI 每次快照推入(SetData),UI Toolkit 自动触发重绘。
    /// </summary>
    // Unity 6 移除了 UxmlFactory<T> 泛型注册,改用 [UxmlElement](默认元素名=类名,
    // 与现有 UXML 的 <TimeshareElement/> 兼容);2022.3 分支保留经典 UxmlFactory。
#if UNITY_6000_0_OR_NEWER
    [UxmlElement]
#endif
    public sealed partial class TimeshareElement : VisualElement
    {
#if !UNITY_6000_0_OR_NEWER
        public new class UxmlFactory : UxmlFactory<TimeshareElement> { }
#endif

        private static readonly Color LineUp = new(0.937f, 0.325f, 0.314f);      // 红(涨)
        private static readonly Color LineDown = new(0.149f, 0.651f, 0.604f);    // 绿(跌)
        private static readonly Color Baseline = new(0.42f, 0.45f, 0.50f, 0.6f);
        private static readonly Color VolUp = new(0.937f, 0.325f, 0.314f, 0.55f);
        private static readonly Color VolDown = new(0.149f, 0.651f, 0.604f, 0.55f);

        private readonly List<TimesharePointDto> _points = new();
        private decimal _prevClose;

        public TimeshareElement()
        {
            generateVisualContent += OnGenerate;
        }

        /// <summary>推入最新数据(全量替换)。prevClose=昨收基准线。</summary>
        public void SetData(IReadOnlyList<TimesharePointDto> points, decimal prevClose)
        {
            _points.Clear();
            if (points != null) _points.AddRange(points);
            _prevClose = prevClose;
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext ctx)
        {
            float w = contentRect.width;
            float h = contentRect.height;
            if (w < 10f || h < 10f) return;

            float volH = h * 0.2f;          // 底部量区
            float priceH = h - volH - 4f;   // 价格区
            float topPad = 6f, botPad = 2f;
            float chartH = priceH - topPad - botPad;

            // 昨收基准线
            if (_points.Count > 0 || _prevClose > 0m)
            {
                decimal min = _prevClose, max = _prevClose;
                decimal lastVol = 0m;
                foreach (var p in _points)
                {
                    if (p.Price < min) min = p.Price;
                    if (p.Price > max) max = p.Price;
                    if (p.CumVolume - lastVol > 0m) lastVol = p.CumVolume - lastVol;
                    lastVol = p.CumVolume;
                }
                decimal range = max - min;
                if (range <= 0m) { range = System.Math.Max(_prevClose * 0.01m, 0.01m); min -= range / 2; max += range / 2; }
                else { decimal pad = range * 0.1m; min -= pad; max += pad; range = max - min; }

                // 基准线(昨收)
                float baseY = topPad + chartH * (1f - (float)((_prevClose - min) / range));
                DrawRect(ctx, 0f, baseY - 0.5f, w, 1f, Baseline);

                // 价格折线(线宽2px的四边形条带)
                if (_points.Count >= 2)
                {
                    bool up = _points[_points.Count - 1].Price >= _prevClose;
                    Color lineColor = up ? LineUp : LineDown;
                    float xStep = w / (float)System.Math.Max(_points.Count - 1, 1);
                    for (int i = 0; i < _points.Count - 1; i++)
                    {
                        float x0 = i * xStep, x1 = (i + 1) * xStep;
                        float y0 = Y(_points[i].Price), y1 = Y(_points[i + 1].Price);
                        // 线段四边形(法向扩 1px)
                        Vector2 a = new(x0, y0), b = new(x1, y1);
                        Vector2 dir = (b - a).normalized;
                        Vector2 normal = new(-dir.y, dir.x);
                        if (normal.sqrMagnitude < 1e-6f) normal = Vector2.up;
                        Vector2 o = normal * 1f;
                        // 线段四边形(法向扩 1px)。顶点顺序必须与 DrawRect 一致
                        // (y-down 顺时针),逆时针会被 Unity 6 UI Toolkit 整面剔除
                        DrawQuad(ctx, a - o, b - o, b + o, a + o, lineColor);
                    }
                    float Y(decimal price) => topPad + chartH * (1f - (float)((price - min) / range));
                }

                // 成交量柱(按相邻差分,无差分时用累计量比例)
                decimal maxVol = 1m;
                for (int i = 1; i < _points.Count; i++)
                {
                    decimal v = _points[i].CumVolume - _points[i - 1].CumVolume;
                    if (v > maxVol) maxVol = v;
                }
                float bw = w / (float)System.Math.Max(_points.Count, 1) * 0.7f;
                for (int i = 0; i < _points.Count; i++)
                {
                    decimal v = i == 0 ? _points[0].CumVolume : _points[i].CumVolume - _points[i - 1].CumVolume;
                    if (v <= 0m) continue;
                    float bh = volH * (float)(v / maxVol);
                    bool volUp = i == 0 || _points[i].Price >= _points[i - 1].Price;
                    float cx = w * (i + 0.5f) / (float)System.Math.Max(_points.Count, 1);
                    DrawRect(ctx, cx - bw / 2f, h - bh, bw, bh, volUp ? VolUp : VolDown);
                }
            }

            void DrawQuad(MeshGenerationContext c, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color color)
            {
                var mesh = c.Allocate(4, 6);
                // Vertex 无 zClose 字段;UI Toolkit 2D 网格顶点统一落在 z=0 平面
                mesh.SetNextVertex(new Vertex { position = new Vector3(p0.x, p0.y, 0f), tint = color });
                mesh.SetNextVertex(new Vertex { position = new Vector3(p1.x, p1.y, 0f), tint = color });
                mesh.SetNextVertex(new Vertex { position = new Vector3(p2.x, p2.y, 0f), tint = color });
                mesh.SetNextVertex(new Vertex { position = new Vector3(p3.x, p3.y, 0f), tint = color });
                mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
                mesh.SetNextIndex(0); mesh.SetNextIndex(2); mesh.SetNextIndex(3);
            }

            void DrawRect(MeshGenerationContext c, float x, float y, float rw, float rh, Color color)
                => DrawQuad(c, new Vector2(x, y), new Vector2(x + rw, y), new Vector2(x + rw, y + rh), new Vector2(x, y + rh), color);
        }
    }
}
