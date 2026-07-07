"""matplotlib 绘图共享配置:中文字体 + 无头模式。

所有绘图脚本在 import matplotlib 后、画图前 import 本模块,
即可解决中文乱码(系统需装 Noto Sans CJK,本机已有)。
"""
import matplotlib
matplotlib.use("Agg")   # 无头模式,直接存图不开窗
import matplotlib.pyplot as plt

# 中文字体:优先用系统的 Noto Sans CJK SC(本机已装),找不到则回退默认。
plt.rcParams["font.sans-serif"] = ["Noto Sans CJK SC", "Noto Sans CJK JP",
                                   "DejaVu Sans"]
# 负号显示正常(默认被 font.sans-serif 影响)
plt.rcParams["axes.unicode_minus"] = False
