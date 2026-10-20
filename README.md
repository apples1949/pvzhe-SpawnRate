# 出怪倍率（SpawnRate）v1.0.0

## 需求 → 实现对照

| 用户需求 | 实现 |
|---|---|
| 战斗设置页里，**兼容其他 Mod** 增加一个按钮 | 共享组件 `BattleOptionColumns.GetOrCreateColumn(..., "ModSpawnRateCol", 220)` 新建本 Mod 独占列（不挤游戏原生列），幂等注入按钮 `ModSpawnRateButton`「出怪倍率 · 设置」 |
| 点按钮**显示一个面板** | `CanvasLayer(150) → CenterContainer(FullRect, Ignore) → PanelContainer(Stop)`，全 `ProcessMode.Always`（设置页把树暂停了也能点） |
| 面板里下拉栏选择「**大于 / 小于 / 等于 1**」 | `OptionButton`：`大于 1 倍（出怪变多）` / `小于 1 倍（出怪变少）` / `等于 1 倍（恢复原样）` |
| 滑动栏设置倍率：**0.1~1 倍共 10 档**、**1~10 倍共 10 档** | 一个 `HSlider`（min=1,max=10,step=1 ⇒ **10 个档位**），按方向映射：`lt` ⇒ 0.1/0.2/…/1.0；`gt` ⇒ 1/2/…/10；`eq` ⇒ 固定 1.0（滑条置灰） |
| 设置完成后点「确定」 | 「确定」= 写配置 + **立即套用到当前关卡** + 关闭面板；「关闭」= 只收起 |

## 倍率怎么落到出怪上（判据全部来自解包源码）

`TowerDefenseBattleFeatureWave.Init()` **每场战斗都 `new` 一份 `TowerDefenseLevelWaveManagerConfig`**
⇒ 改 `config` 只影响本场，不污染关卡资源 / 存档 / 下一关。

1. **显式出怪**（绝大多数波次）：`SpawnZombie()` 里
   `foreach (spawn in wave.spawn) for (m = 0; m < spawn.num; m++) …`
   ⇒ 直接决定该波该种僵尸数量。本 Mod 按**每一波**统计原始总量 `T`，
   目标量 `round(T × 倍率)`（`T>0` 时至少 1 只），再用**最大余数法**按原始占比分回各 `spawn.num`
   ⇒ 僵尸**种类比例不变**，且不会出现"每种都 ×0.1 四舍五入成 1 只 ⇒ 反而变多"的坑。
2. **动态出怪**（`dynamic=true` / `wave.dynamic != null` / 生存）：
   `int num = currentSpawnPoint; if (wave.dynamic != null) num += wave.dynamic.points;`
   再按 `GetWavePointCost()` 从 `wave.dynamic.zombiePool` 抽到点数用光
   ⇒ 同步按倍率缩放**正数**的 `wave.dynamic.points`、`currentDynamic.startingPoints`、
   `currentDynamic.pointIncrementPerWave`（负值保持原样 —— 那些是"扣点数"的难度变体，
   乘倍率会把方向反过来）。
3. 不覆盖的路径（有意）：`config.flagZombie` 大波旗子僵尸（固定 1 只）、`gridSpawn`（无数量字段）、
   生存模式的 `survivalRunner.point`（由生存轮次驱动）。

## 快照 / 复位语义（防"叠乘"）

- `_origNums`（按 `TowerDefenseLevelSpawnConfig` 实例 id）、`_origWavePoints`、`_origStartPoints`、
  `_origIncrement` 保存**原始值**。
- **同一场战斗反复点确定不会叠乘**（始终从原始值重算）；只有"战斗实例换了"（继续存档 / 重开本关，
  config 被重建）才清空快照并**自动重新套用**。
- **换关自动复位**（倍率只对当前关卡生效）；卸载 Mod 时 `Shutdown()` 会先把当前关卡恢复原样。
- 选「等于 1 倍」并确定 = 立即恢复原始出怪。

## 配置项（选项 → Mod 管理 → 出怪倍率 → 配置）

| key | 说明 |
|---|---|
| `multKind` | `gt` / `lt` / `eq`（面板下拉的持久化镜像） |
| `multIndex` | 1~10 档位 |
| `enableLog` | 诊断日志（默认关） |

> 面板里的选择在点「确定」**之前不会生效**；配置项只是记住上次的选择。

## 构建 / 装机

```bash
cd mods/SpawnRate
python build_and_install.py            # 只编译
python build_and_install.py --install  # 编译 + 打包 dist/SpawnRate.pmod + 装机 + 清 ModsCache/spawnrate
```

- 程序集身份名：`JTYSpawnRate`（安卓要求全局唯一），包内路径固定 `Runtime/ModAssembly.dll`
- 共享组件：`../BattleOptionColumns.cs`、`../BattlePanelKit.cs`

## 已知副作用 / 未验证项

- 已生成（正在出怪）的那一波不会被改；从**下一波**开始生效。
- 动态出怪的 `currentSpawnPoint` 是运行时累计值，**不直接改**（改了会破坏增量语义）；
  它按 `startingPoints + n × pointIncrementPerWave` 走，两项都已按倍率缩放 ⇒ 等价于整体缩放。
- 多人模式下出怪由 Host 决定；本 Mod 改的是 Host 侧的 `config`，客户端不套用。
- 实机验收：进关卡 → 战斗设置页 → 点按钮 → 选 3× 确定 → 观察第 2 波起数量 → 换关确认复位。
