# Agent 图形化测试

`node tools/playtest/agent-playtest.mjs`(需 playwright chromium,服务在 localhost:5023)

三层评测:
- A 逻辑点击: 冷启动盲玩完整局(选关→组牌→开局→每段出牌→结束回合→日切),记录点击路径/死点/错误
- B 布局点击: 全量可点元素触达量测(手机44px/桌面24px阈值),输出 issues 清单
- C 视觉信息: 关键节点截图(vision-NN.png)+状态真值(vision-groundtruth.json),
  供视觉问卷"只看图读状态"比对评分(信息显著度审计)

产出: /tmp/playtest/{report.json, vision-*.png, *-groundtruth.json}

已由该 harness 抓到的 bug: Waiting态/api/ai NRE; 组牌模态开局后不关闭遮蔽全操作;
手牌选择器被组牌库污染(.card-item 需限定 #cardHand)。
