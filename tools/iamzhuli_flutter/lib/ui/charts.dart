// 轻量图表: K线蜡烛+分时折线(CustomPainter自绘,零依赖;对齐Web版数据通道)
import 'package:flutter/material.dart';

class CandleChart extends StatelessWidget {
  final List<dynamic> candles;   // {day,open,high,low,close,volume}
  final double? lastPrice;
  const CandleChart({super.key, required this.candles, this.lastPrice});

  @override
  Widget build(BuildContext context) {
    if (candles.isEmpty) return const Center(child: Text('暂无K线', style: TextStyle(color: Colors.grey)));
    final lows = candles.map((c) => (c['low'] as num).toDouble()).reduce(_min);
    final highs = candles.map((c) => (c['high'] as num).toDouble()).reduce(_max);
    final range = (highs - lows) <= 0 ? 1.0 : highs - lows;
    return LayoutBuilder(builder: (context, box) {
      final w = box.maxWidth, h = box.maxHeight;
      final n = candles.length;
      final slot = w / n;
      final bodyW = slot * 0.62;
      // 价格标签
      final labelStyle = const TextStyle(fontSize: 9, color: Color(0xFF8892A0));
      return Stack(children: [
        CustomPaint(
          size: Size(w, h),
          painter: _CandlePainter(List.generate(n, (i) {
            final c = candles[i] as Map<String, dynamic>;
            return _C((c['open'] as num).toDouble(), (c['high'] as num).toDouble(),
                (c['low'] as num).toDouble(), (c['close'] as num).toDouble(),
                slot * i + slot / 2, slot, bodyW, h, lows, range);
          }),
              last: lastPrice, low: lows, range: range),
        ),
        // 右侧价格轴(现价+高低)
        Positioned(right: 2, top: 2, child: Text(highs.toStringAsFixed(2), style: labelStyle)),
        Positioned(right: 2, bottom: 2, child: Text(lows.toStringAsFixed(2), style: labelStyle)),
        if (lastPrice != null)
          Positioned(
            right: 2,
            top: (1 - (lastPrice! - lows) / range) * (h - 18),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
              decoration: BoxDecoration(color: const Color(0xFFEF5350), borderRadius: BorderRadius.circular(3)),
              child: Text(lastPrice!.toStringAsFixed(2), style: const TextStyle(fontSize: 10, color: Colors.white, fontWeight: FontWeight.bold)),
            ),
          ),
      ]);
    });
  }

  static double _min(double a, double b) => a < b ? a : b;
  static double _max(double a, double b) => a > b ? a : b;
}

class _C {
  final double o, hi, lo, c, cx, slot, bodyW, h, lo0, range;
  _C(this.o, this.hi, this.lo, this.c, this.cx, this.slot, this.bodyW, this.h, this.lo0, this.range);
  double y(double p) => (1 - (p - lo0) / range) * (h - 8) + 4;
}

class _CandlePainter extends CustomPainter {
  final List<_C> cs;
  final double? last;
  final double low, range;
  _CandlePainter(this.cs, {required this.last, required this.low, required this.range});

  @override
  void paint(Canvas canvas, Size size) {
    const up = Color(0xFFEF5350), down = Color(0xFF26A69A);
    for (final c in cs) {
      final color = c.c >= c.o ? up : down;
      final paint = Paint()..color = color..strokeWidth = 1..style = PaintingStyle.stroke;
      // 影线
      canvas.drawLine(Offset(c.cx, c.y(c.hi)), Offset(c.cx, c.y(c.lo)), Paint()..color = color..strokeWidth = 1);
      // 实体
      final top = c.y(c.o > c.c ? c.o : c.c);
      final bot = c.y(c.o > c.c ? c.c : c.o);
      canvas.drawRect(Rect.fromLTWH(c.cx - c.bodyW / 2, top, c.bodyW, (bot - top).clamp(1.0, double.infinity)), Paint()..color = color);
      // 昨收虚线
    }
    if (last != null && cs.isNotEmpty) {
      final c0 = cs.first;
      final y = c0.y(last!);
      final p = Paint()..color = const Color(0xFFEF5350)..strokeWidth = 1..style = PaintingStyle.stroke;
      // 简化虚线: 段落
      for (double x = 0; x < size.width; x += 12) {
        canvas.drawLine(Offset(x, y), Offset(x + 6, y), Paint()..color = const Color(0x88EF5350)..strokeWidth = 1);
      }
    }
  }

  @override
  bool shouldRepaint(covariant _CandlePainter old) => true;
}

/// 分时图: 折线+昨收基准+填充
class TimeshareChart extends StatelessWidget {
  final List<dynamic> points;   // {tickOfDay,price,cumVolume}
  final double prevClose;
  const TimeshareChart({super.key, required this.points, required this.prevClose});

  @override
  Widget build(BuildContext context) {
    if (points.isEmpty) return const Center(child: Text('等待开盘…', style: TextStyle(color: Colors.grey)));
    return CustomPaint(
      painter: _LinePainter(points.map((p) => (
        (p['tickOfDay'] as num).toDouble(),
        (p['price'] as num).toDouble(),
      )).toList(), prevClose),
      child: const SizedBox.expand(),
    );
  }
}

class _LinePainter extends CustomPainter {
  final List<(double, double)> pts;
  final double prev;
  _LinePainter(this.pts, this.prev);

  @override
  void paint(Canvas canvas, Size size) {
    if (pts.length < 2) return;
    double lo = prev, hi = prev;
    for (final (_, p) in pts) { if (p < lo) lo = p; if (p > hi) hi = p; }
    final pad = (hi - lo).abs() * 0.1 + 0.02;
    lo -= pad; hi += pad;
    final range = hi - lo;
    Offset xy(double x, double p) => Offset(x / pts.last.$1 * size.width, (1 - (p - lo) / range) * (size.height - 8) + 4);
    // 基准线
    final baseY = xy(0, prev).dy;
    canvas.drawLine(Offset(0, baseY), Offset(size.width, baseY), Paint()..color = const Color(0x668892A0)..strokeWidth = 1);
    // 填充
    final path = Path()..moveTo(0, baseY);
    for (final (t, p) in pts) path.lineTo(xy(t, p).dx, xy(t, p).dy);
    path.lineTo(size.width, baseY); path.close();
    canvas.drawPath(path, Paint()..color = const Color(0x22EF5350));
    // 折线
    final line = Paint()..color = const Color(0xFFEF5350)..strokeWidth = 2..style = PaintingStyle.stroke;
    final lp = Path()..moveTo(xy(pts[0].$1, pts[0].$2).dx, xy(pts[0].$1, pts[0].$2).dy);
    for (final (t, p) in pts) lp.lineTo(xy(t, p).dx, xy(t, p).dy);
    canvas.drawPath(lp, line);
  }

  @override
  bool shouldRepaint(covariant _LinePainter old) => true;
}
