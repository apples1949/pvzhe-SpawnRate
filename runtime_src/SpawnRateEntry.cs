using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「出怪倍率」Mod —— 托管运行时入口。
///
/// ── 需求（2026-10-20 用户）──────────────────────────────────────
/// 1. 战斗设置页（`BattleOption`）里**兼容其他 Mod** 增加一个按钮。
/// 2. 点按钮 → 弹出面板；面板里：
///    · 下拉栏选「大于 / 小于 / 等于 1 倍」；
///    · 滑动条选具体倍率：**0.1~1 倍共 10 档**、**1~10 倍共 10 档**；
///    · 点「确定」把倍率套到**当前关卡**的出怪设置上。
///
/// ── 解包源码依据（全部实查）──────────────────────────────────────
/// · 一场战斗的出怪数据 = `TowerDefenseBattleFeatureWave.config`
///   （`TowerDefenseLevelWaveManagerConfig`，类型 `Registry/Battle/Feature/Wave/Resource/`）。
/// · `TowerDefenseBattleFeatureWave.Init()` **每场战斗 `new` 一份 config 再 `Init(data)`**
///   ⇒ 在这里改 `config.wave[*].spawn[*].num` **只影响本场战斗**，不会污染关卡资源/存档。
/// · `SpawnZombie(waveId, dynamic)`（:850）里的取数逻辑：
///     · 非动态波：`foreach (spawn in wave.spawn) for (m = 0; m < spawn.num; m++) …`
///       ⇒ **`spawn.num` 就是该波该种僵尸的数量**；
///     · 动态波（`dynamic == true` 或 `wave.dynamic != null` 或生存）：
///       `int num = currentSpawnPoint; if (wave.dynamic != null) num += wave.dynamic.points;`
///       然后按 `GetWavePointCost()` 从 `wave.dynamic.zombiePool` 里抽到点数用光
///       ⇒ **动态出怪的量由"波次点数"决定**，所以要同时按比例缩放
///       `currentDynamic.startingPoints` / `pointIncrementPerWave`（`StartWave` 里赋初值、
///       `NextWave` 里逐波累加）和每波的 `wave.dynamic.points`。
/// · 因此本 Mod 的缩放口径：
///     · 每个**波次**统计原始出怪总量 T（所有 `spawn.num` 之和）；
///     · 目标总量 = round(T × 倍率)（T&gt;0 时至少留 1 只，避免整关没怪）；
///     · 用**最大余数法**按原始占比把目标量分回各个 `spawn.num`
///       ⇒ 僵尸**种类比例不变**，只是整体变多/变少（不会出现"每种都减半后反而变多"）；
///     · 动态点数（&gt;0 的才缩放）同步乘倍率。
///
/// ── 铁律 ─────────────────────────────────────────────────────────
/// · 入口三回调一律 try/catch（抛出 = 整包回滚）。
/// · 每帧逻辑挂 `SceneTree.process_frame`（本程序集没有 Godot 源码生成器，
///   自定义 Node 的 `_Process` 不会被调用）。
/// · 设置页按钮走共享组件 `BattleOptionColumns`（多 Mod 兼容，不跟原生列抢位置）。
/// </summary>
public sealed partial class SpawnRateEntry : IXWModRuntimeEntry
{
	// ================================================================ 常量

	private const string KMultKind = "multKind";
	private const string KMultIndex = "multIndex";
	private const string KEnableLog = "enableLog";

	private const string OptionColumnName = "ModSpawnRateCol";
	private const string OptBtnName = "ModSpawnRateButton";
	private const string PanelLayerName = "ModSpawnRatePanelLayer";
	private const string PanelRootName = "ModSpawnRatePanel";

	/// <summary>扫描节流。</summary>
	private const int OptionScanStride = 6;

	// ================================================================ 运行状态

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _started;
	private bool _faultReported;
	private bool _optFaultReported;
	private long _frame;

	// 面板里的选择（持久化到宿主配置；点「确定」前不生效）
	private string _kind = "gt";       // gt / lt / eq
	private int _index = 3;            // 1..10

	// 已生效的倍率（只在当前关卡内有效）
	private double _activeMult = 1.0;
	private string _activeLevelKey;
	private ulong _appliedFeatureId;

	// 原始值快照（按 Resource 实例 id 存；config 每场战斗新建 ⇒ 换关清空）
	private readonly Dictionary<ulong, int> _origNums = new Dictionary<ulong, int>();
	private readonly Dictionary<ulong, int> _origLines = new Dictionary<ulong, int>();
	private readonly Dictionary<ulong, int> _origWavePoints = new Dictionary<ulong, int>();
	private readonly Dictionary<ulong, int> _origStartPoints = new Dictionary<ulong, int>();
	private readonly Dictionary<ulong, int> _origIncrement = new Dictionary<ulong, int>();

	/// <summary>
	/// **本 Mod 自己塞进 `wave.spawn` 的临时条目**（多倍时用来"摊到多行"、以及大波追加旗帜僵尸）。
	/// 每次重新套用/恢复前必须先整批移除，否则会越叠越多、还会被当成"原始条目"二次缩放。
	/// </summary>
	private readonly List<(Godot.Collections.Array<TowerDefenseLevelSpawnConfig> arr, TowerDefenseLevelSpawnConfig cfg)> _extras
		= new List<(Godot.Collections.Array<TowerDefenseLevelSpawnConfig>, TowerDefenseLevelSpawnConfig)>();

	// 面板控件
	private BattlePanelKit.Panel _panel;
	private OptionButton _ddKind;
	private HSlider _slider;
	private Label _lbValue;
	private Label _lbStatus;
	private bool _panelSync;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			BattlePanelKit.Prefix = "[SpawnRate] ";
			BattlePanelKit.LogEnabled = BattlePanelKit.ReadBool(context, KEnableLog, false);
			ReloadSettings();
			BattlePanelKit.Info("初始化完成；PackageRoot=" + ((context == null) ? "<null>" : context.PackageRoot));
		}
		catch (Exception ex)
		{
			SafeWarn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				BattlePanelKit.Warn("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tickCallable);
			_started = true;
			try
			{
				if (_context?.Settings != null) { _context.Settings.Changed += OnSettingsChanged; }
			}
			catch { }
			// ★ 不受 enableLog 门控：每次启动只打 1 行，用来确认"包真的加载且入口跑起来了"。
			try
			{
				GD.Print("[SpawnRate] v1.0.1 ready（出怪倍率面板；enableLog="
					+ (BattlePanelKit.LogEnabled ? "on" : "off") + "）");
			}
			catch { }
			BattlePanelKit.Info("已挂载 process_frame（维护设置页按钮 / 倍率跨关卡复位）。");
		}
		catch (Exception ex)
		{
			SafeWarn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			try
			{
				if (_context?.Settings != null) { _context.Settings.Changed -= OnSettingsChanged; }
			}
			catch { }
			// 卸载前把当前关卡的出怪恢复原样（否则玩家会以为关卡坏了）
			try { RestoreCurrentBattle(); } catch { }
			BattlePanelKit.DestroyPanel(ref _panel);
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
			}
		}
		catch (Exception ex)
		{
			SafeWarn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 配置

	private void ReloadSettings()
	{
		try
		{
			if (_context == null) { return; }
			string k = BattlePanelKit.ReadString(_context, KMultKind, "gt");
			if (k != "gt" && k != "lt" && k != "eq") { k = "gt"; }
			_kind = k;
			_index = BattlePanelKit.ReadInt(_context, KMultIndex, 3, 1, 10);
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try
		{
			ReloadSettings();
			BattlePanelKit.LogEnabled = BattlePanelKit.ReadBool(_context, KEnableLog, false);
		}
		catch { }
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		try
		{
			_frame++;
			if ((_frame % OptionScanStride) == 0) { EnsureOptionButton(); }
			MaintainApplication();
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				SafeWarn("每帧异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private void EnsureOptionButton()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
			// 设置页 `pasue=true` ⇒ 平时可用 `tree.Paused` 当廉价判据；
			// ⚠️ 多人模式下 `BattleOption._Ready` 会把 pasue 置 false 并解除暂停
			//    （BattleOption.cs:60-67）⇒ 只判 Paused 会让按钮在联机时永不注入。
			if (!_tree.Paused && !IsMultiplayerMode()) { return; }
			List<Node> opts = new List<Node>(4);
			BattlePanelKit.CollectOptionDialogs(_tree, opts);
			for (int i = 0; i < opts.Count; i++)
			{
				Node opt = opts[i];
				if (opt == null || !GodotObject.IsInstanceValid(opt)) { continue; }
				// ★ 2026-10-24 用户要求：与「时停·卡槽触发面板 / CD开关设置面板 / 改键面板 / 波次栏位置」
				//   **竖排在同一列**（`ModCol4`）。该列已存在 ⇒ 只加按钮，不新增列宽。
				VBoxContainer col = BattleOptionColumns.GetOrCreateColumn(opt,
					BattleOptionColumns.TimeStopColName, BattleOptionColumns.TimeStopColMinWidth);
				if (col == null || !GodotObject.IsInstanceValid(col)) { continue; }
				if (col.FindChild(OptBtnName, true, false) != null) { continue; }   // 幂等

				// 旧版留在原生列工具行里的同名按钮先清掉，避免两个入口
				Button stale = opt.FindChild(OptBtnName, true, false) as Button;
				if (stale != null && GodotObject.IsInstanceValid(stale))
				{
					try { stale.GetParent()?.RemoveChild(stale); } catch { }
					try { stale.QueueFree(); } catch { }
				}

				Button b = new Button();
				b.Name = OptBtnName;
				b.Text = "出怪倍率";
				b.TooltipText = "修改当前关卡的出怪倍率（大于 / 小于 / 等于 1 倍，10 档滑动条）";
				b.AddThemeFontSizeOverride("font_size", 14);
				b.CustomMinimumSize = new Vector2(0f, 40f);
				b.ProcessMode = Node.ProcessModeEnum.Always;
				BattleOptionColumns.ApplyDarkButtonStyle(b);
				b.Pressed += () =>
				{
					try { OpenPanel(); }
					catch (Exception exOpen) { SafeWarn("打开出怪倍率面板异常（已吞）：" + exOpen.Message); }
				};
				col.AddChild(b, false, Node.InternalMode.Disabled);
				try { BattleOptionColumns.NormalizeOrder(opt); } catch { }
				BattlePanelKit.Info("已在战斗设置页 ModCol4 列注入按钮：出怪倍率");
			}
		}
		catch (Exception ex)
		{
			if (!_optFaultReported)
			{
				_optFaultReported = true;
				SafeWarn("设置页按钮注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private static bool IsMultiplayerMode()
	{
		try { return Global.IsMultiplayerMode; }
		catch { return false; }
	}

	/// <summary>多人模式只有主机能改（客户端改了也不会同步，还可能两端计数不一致）。</summary>
	private static bool IsMultiplayerClient()
	{
		try { return Global.IsMultiplayerMode && !MultiPlayerManager.IsHost; }
		catch { return false; }
	}

	/// <summary>
	/// 维持"已生效倍率"：
	///  · 换了**关卡** ⇒ 复位（倍率只对当前关卡生效）；
	///  · 同一关卡但**战斗实例换了**（继续存档 / 重开本关；config 是新建的）⇒ 重新套用。
	/// </summary>
	private void MaintainApplication()
	{
		try
		{
			if (_activeLevelKey == null) { return; }
			TowerDefenseBattleFeatureWave wave = TowerDefenseBattleFeatureWave.Instance;
			if (wave == null || !GodotObject.IsInstanceValid(wave)) { return; }

			string key = CurrentLevelKey();
			// ★ 只在"两边都拿得到关卡 key"且确实不同 时才判换关 ——
			//   否则读不到 key（主菜单/加载中）会被误判成换关，把刚设的倍率清掉。
			if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(_activeLevelKey)
				&& !string.Equals(key, _activeLevelKey, StringComparison.Ordinal))
			{
				BattlePanelKit.Info("换关（" + _activeLevelKey + " → " + key + "）⇒ 出怪倍率复位为 1×。");
				_activeMult = 1.0;
				_activeLevelKey = null;
				_appliedFeatureId = 0UL;
				ClearSnapshots();
				RefreshStatus();
				return;
			}
			if (wave.GetInstanceId() != _appliedFeatureId)
			{
				BattlePanelKit.Info("检测到新的战斗实例（继续/重开同一关卡）⇒ 重新套用倍率 " + Fmt(_activeMult) + "×。");
				ClearSnapshots();
				ApplyInternal(wave, _activeMult, 1.0, out string msg);
				_appliedFeatureId = wave.GetInstanceId();
				BattlePanelKit.Info(msg);
				RefreshStatus();
			}
		}
		catch { }
	}

	private static string CurrentLevelKey()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return null; }
			TowerDefenseLevelBaseConfig cfg = mgr.currentLevelConfig;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg)) { return null; }
			return cfg.name;
		}
		catch { return null; }
	}

	private void ClearSnapshots()
	{
		_origNums.Clear();
		_origWavePoints.Clear();
		_origStartPoints.Clear();
		_origIncrement.Clear();
	}

	// ================================================================ 套用倍率

	/// <summary>把当前面板选择套到当前关卡（确定按钮）。</summary>
	private bool ApplySelection(out string message)
	{
		message = "";
		try
		{
			if (IsMultiplayerClient())
			{
				message = "多人模式下只有主机能修改出怪倍率。";
				return false;
			}
			TowerDefenseBattleFeatureWave wave = TowerDefenseBattleFeatureWave.Instance;
			if (wave == null || !GodotObject.IsInstanceValid(wave) || wave.config == null)
			{
				message = "请先进入关卡，再设置出怪倍率。";
				return false;
			}
			double mult = SelectedValue();
			string key = CurrentLevelKey();
			// ★ 只有"战斗实例换了"才清快照（那种情况下 config 是新建的、id 全新）；
			//   同一场战斗里反复点确定**绝不能清** —— 否则会把已经缩放过的 num 当成原始值，
			//   再乘一次倍率就变成叠乘（3× 之后再选 0.5× 会得到 1.5× 而不是 0.5×）。
			bool sameInstance = (wave.GetInstanceId() == _appliedFeatureId);
			if (!sameInstance) { ClearSnapshots(); }
			// fromMult：同一场战斗里"上一次已生效的倍率"（用于把运行时预算 currentSpawnPoint 按比例换算）
			double fromMult = sameInstance ? _activeMult : 1.0;
			bool ok = ApplyInternal(wave, mult, fromMult, out message);
			if (!ok) { return false; }
			_activeMult = mult;
			_activeLevelKey = key ?? "";
			_appliedFeatureId = wave.GetInstanceId();
			return true;
		}
		catch (Exception ex)
		{
			message = "应用倍率异常：" + ex.Message;
			return false;
		}
	}

	/// <summary>恢复当前关卡为原始出怪（等于 1 倍 / 卸载时）。</summary>
	private void RestoreCurrentBattle()
	{
		try
		{
			TowerDefenseBattleFeatureWave wave = TowerDefenseBattleFeatureWave.Instance;
			if (wave == null || !GodotObject.IsInstanceValid(wave)) { return; }
			ApplyInternal(wave, 1.0, (_activeMult > 0.0) ? _activeMult : 1.0, out _);
			_activeMult = 1.0;
			_activeLevelKey = null;
			_appliedFeatureId = 0UL;
		}
		catch { }
	}

	/// <summary>
	/// 核心：按倍率重算当前战斗的 `config`。
	/// `mult == 1.0` 时把一切恢复成快照里的原始值。
	/// </summary>
	private bool ApplyInternal(TowerDefenseBattleFeatureWave wave, double mult, double fromMult, out string message)
	{
		message = "";
		int waveCount = 0;
		long origTotal = 0;
		long newTotal = 0;
		int spreadExtra = 0;      // 被"摊到多行"的额外只数（用于汇报）
		try
		{
			if (wave == null || !GodotObject.IsInstanceValid(wave) || wave.config == null)
			{
				message = "当前没有可修改的关卡配置。";
				return false;
			}
			TowerDefenseLevelWaveManagerConfig config = wave.config;
			Godot.Collections.Array<TowerDefenseLevelWaveConfig> waves = config.wave;
			if (waves == null || waves.Count == 0)
			{
				message = "当前关卡的波次数据为空，无法修改出怪倍率。";
				return false;
			}

			// ★ 先清掉上一轮由本 Mod 塞进去的临时条目（"摊到多行"的克隆、大波追加的旗帜僵尸），
			//   否则反复点确定会越叠越多，还会被当成原始条目二次缩放。
			RemoveExtras();

			// ── ① 每个波次的显式出怪 `spawn[*].num` ──
			for (int i = 0; i < waves.Count; i++)
			{
				TowerDefenseLevelWaveConfig w = waves[i];
				if (w == null || !GodotObject.IsInstanceValid(w)) { continue; }
				Godot.Collections.Array<TowerDefenseLevelSpawnConfig> arr = w.spawn;
				if (arr == null || arr.Count == 0) { continue; }
				waveCount++;

				int n = arr.Count;
				int[] orig = new int[n];
				int[] origLine = new int[n];
				double sum = 0.0;
				for (int j = 0; j < n; j++)
				{
					TowerDefenseLevelSpawnConfig sp = arr[j];
					if (sp == null || !GodotObject.IsInstanceValid(sp)) { continue; }
					ulong id = sp.GetInstanceId();
					if (!_origNums.TryGetValue(id, out int o))
					{
						o = sp.num;
						_origNums[id] = o;
						_origLines[id] = sp.line;      // 原始车道（-1 = 游戏自己选行）
					}
					orig[j] = o;
					origLine[j] = _origLines.TryGetValue(id, out int ln) ? ln : sp.line;
					if (o > 0) { sum += o; }
				}
				if (sum <= 0.0) { continue; }
				origTotal += (long)sum;

				// ★ 小于 1 倍时用 **向下取整**（floor），保证"实际出怪不高于标称倍率"；
				//   大于等于 1 倍用四舍五入。两者都保证每波有怪时至少留 1 只（否则整波空掉）。
				int target;
				if (mult >= 1.0)
				{
					target = (int)Math.Round(sum * mult, MidpointRounding.AwayFromZero);
				}
				else
				{
					target = (int)Math.Floor(sum * mult + 1e-9);
				}
				if (target < 0) { target = 0; }
				if (mult > 0.0 && target < 1) { target = 1; }
				newTotal += target;

				int[] alloc = AllocateProportional(orig, sum, target);
				for (int j = 0; j < n; j++)
				{
					TowerDefenseLevelSpawnConfig sp = arr[j];
					if (sp == null || !GodotObject.IsInstanceValid(sp)) { continue; }
					int want = alloc[j];
					int keepLine = origLine[j];
					if (sp.line != keepLine) { sp.line = keepLine; }   // 恢复原始车道（可能被上一轮改过）

					if (want <= orig[j])
					{
						// 变少 / 不变：就留在原来那一条车道
						if (sp.num != want) { sp.num = want; }
						continue;
					}

					// ★ 变多：**别全堆在原来那一行**
					//   · 原条目保留"原有的那几只"在它自己的车道（尊重关卡设计，比如水池行）；
					//   · 多出来的那部分做成**同种僵尸的克隆条目、车道设为 -1**
					//     —— `SpawnZombie` 里 `line == -1` 的分支会**逐只挑当前僵尸数最少的车道**
					//     （`SpawnZombie:932-951` 的 num5/array2 循环），于是自动平均摊到多行。
					if (sp.num != orig[j]) { sp.num = orig[j]; }
					if (keepLine == -1)
					{
						// 本来就是"自动选行" ⇒ 直接加数量，游戏自己会摊开
						sp.num = want;
						continue;
					}
					int extra = want - orig[j];
					TowerDefenseLevelSpawnConfig clone = null;
					try { clone = sp.Duplicate(false) as TowerDefenseLevelSpawnConfig; } catch { }
					if (clone == null)
					{
						sp.num = want;      // 复制失败就退回"同一行加满"（宁可挤一行也不能丢怪）
						continue;
					}
					clone.line = -1;
					clone.num = extra;
					arr.Add(clone);
					_extras.Add((arr, clone));
					spreadExtra += extra;
				}
			}

			// ── ①b 大波旗帜僵尸：按倍数**追加**，且同样分散到不同行 ──
			//   游戏本体在 `SpawnZombie:891-915` 里对每个大波**硬编码只加 1 只**（没有数量字段），
			//   所以只能"额外追加"：倍数 3× ⇒ 再补 2 只，条目 `line = -1` 由游戏摊到最少僵尸的行。
			try
			{
				int flagWaves = 0;
				int flagAdded = 0;
				if (config != null && config.flagZombieUse && !string.IsNullOrEmpty(config.flagZombie)
					&& config.flagWaveInterval > 0 && mult > 1.0)
				{
					int flagPerWave = Math.Max(0, (int)Math.Round(mult, MidpointRounding.AwayFromZero) - 1);
					if (flagPerWave > 0)
					{
						for (int i = 0; i < waves.Count; i++)
						{
							if (((i + 1) % config.flagWaveInterval) != 0) { continue; }
							TowerDefenseLevelWaveConfig w = waves[i];
							if (w == null || !GodotObject.IsInstanceValid(w) || w.spawn == null) { continue; }
							TowerDefenseLevelSpawnConfig fz = new TowerDefenseLevelSpawnConfig();
							fz.zombie = config.flagZombie;
							fz.line = -1;
							fz.num = flagPerWave;
							w.spawn.Add(fz);
							_extras.Add((w.spawn, fz));
							flagWaves++;
							flagAdded += flagPerWave;
						}
					}
				}
				if (flagAdded > 0)
				{
					BattlePanelKit.Info("大波旗帜僵尸追加：" + flagWaves + " 个大波 × " + (flagAdded / Math.Max(1, flagWaves))
						+ " 只 = " + flagAdded + " 只（line=-1，由游戏摊到各行）。");
				}
			}
			catch (Exception ex)
			{
				SafeWarn("追加旗帜僵尸失败（已吞）：" + ex.Message);
			}

			// ── ② 每波自带的动态点数（wave.dynamic.points）──
			for (int i = 0; i < waves.Count; i++)
			{
				TowerDefenseLevelWaveConfig w = waves[i];
				if (w == null || !GodotObject.IsInstanceValid(w)) { continue; }
				TowerDefenseLevelSpawnDynamicConfig dyn = w.dynamic;
				if (dyn == null || !GodotObject.IsInstanceValid(dyn)) { continue; }
				ulong id = dyn.GetInstanceId();
				if (!_origWavePoints.TryGetValue(id, out int o)) { o = dyn.points; _origWavePoints[id] = o; }
				if (o > 0)
				{
					int want = ScaleUp(o, mult);
					if (dyn.points != want) { dyn.points = want; }
				}
			}

			// ── ③ 动态出怪的"波次点数"（startingPoints / pointIncrementPerWave）──
			TowerDefenseLevelDynamicConfig cur = wave.currentDynamic;
			if (cur != null && GodotObject.IsInstanceValid(cur))
			{
				ulong id = cur.GetInstanceId();
				if (!_origStartPoints.TryGetValue(id, out int s0)) { s0 = cur.startingPoints; _origStartPoints[id] = s0; }
				if (!_origIncrement.TryGetValue(id, out int i0)) { i0 = cur.pointIncrementPerWave; _origIncrement[id] = i0; }
				if (s0 > 0)
				{
					int want = ScaleUp(s0, mult);
					if (cur.startingPoints != want) { cur.startingPoints = want; }
				}
				if (i0 > 0)
				{
					int want = ScaleUp(i0, mult);
					if (cur.pointIncrementPerWave != want) { cur.pointIncrementPerWave = want; }
				}
				// ★ `currentSpawnPoint` 是**运行时累计预算**（StartWave 赋初值、NextWave 累加）。
				//   不能直接乘倍率（反复点确定会叠乘），也不能不管（中途改倍率时"基数"仍是旧值）。
				//   做法：按 **新倍率 / 上次已生效倍率** 的比例换算一次 ⇒ 与"整体缩放"严格等价，
				//   且反复点确定（3× → 5× → 0.5×）不会失真。
				if (fromMult > 0.0 && Math.Abs(mult - fromMult) > 1e-9)
				{
					int oldPoints = wave.currentSpawnPoint;
					if (oldPoints != 0)
					{
						int newPoints = (int)Math.Round(oldPoints * mult / fromMult, MidpointRounding.AwayFromZero);
						if (newPoints != oldPoints) { wave.currentSpawnPoint = newPoints; }
					}
				}
			}

			message = (Math.Abs(mult - 1.0) < 1e-9)
				? ("已恢复当前关卡的原始出怪（共 " + waveCount + " 波，原始 " + origTotal + " 只）。")
				: ("已把当前关卡出怪设为 " + Fmt(mult) + "×（共 " + waveCount + " 波，原始 " + origTotal
					+ " 只 → 约 " + newTotal + " 只"
					+ ((spreadExtra > 0) ? ("；其中 " + spreadExtra + " 只已拆成「自动选行」条目，会平均摊到多行") : "")
					+ "）。从下一波开始生效。");
			BattlePanelKit.Info(message);
			return true;
		}
		catch (Exception ex)
		{
			message = "修改出怪失败：" + ex.Message;
			return false;
		}
	}

	/// <summary>
	/// 移除上一轮由本 Mod 塞进 `wave.spawn` 的临时条目。
	/// 必须在每次重算/恢复之前调用（幂等）：否则克隆会越叠越多，且会被当成"原始条目"再乘一次。
	/// 只做 `Array.Remove`，不 `Free`（Resource 是 RefCounted，交回引擎回收）。
	/// </summary>
	private void RemoveExtras()
	{
		try
		{
			for (int i = 0; i < _extras.Count; i++)
			{
				Godot.Collections.Array<TowerDefenseLevelSpawnConfig> arr = _extras[i].arr;
				TowerDefenseLevelSpawnConfig cfg = _extras[i].cfg;
				try
				{
					if (arr != null && cfg != null && GodotObject.IsInstanceValid(cfg))
					{
						arr.Remove(cfg);
					}
				}
				catch { }
			}
		}
		catch { }
		_extras.Clear();
	}

	/// <summary>正数按倍率缩放（四舍五入，至少 1）。</summary>
	private static int ScaleUp(int original, double mult)
	{
		if (original <= 0) { return original; }
		int v = (int)Math.Round(original * mult, MidpointRounding.AwayFromZero);
		if (v < 1) { v = 1; }
		return v;
	}

	/// <summary>
	/// 把目标总量按原始占比分配到各项（**最大余数法**）。
	/// 好处：僵尸种类比例不变，且各项之和**严格等于** target
	/// （逐个四舍五入会出现 10 项 ×0.1 倍结果反而比原来多的坑）。
	/// </summary>
	private static int[] AllocateProportional(int[] orig, double sum, int target)
	{
		int n = orig.Length;
		int[] alloc = new int[n];
		if (n == 0 || sum <= 0.0) { return alloc; }
		double[] frac = new double[n];
		int used = 0;
		for (int i = 0; i < n; i++)
		{
			if (orig[i] <= 0) { frac[i] = -1.0; continue; }
			double exact = orig[i] * target / sum;
			int fl = (int)Math.Floor(exact);
			alloc[i] = fl;
			frac[i] = exact - fl;
			used += fl;
		}
		int left = target - used;
		while (left > 0)
		{
			int best = -1;
			double bestFrac = -1.0;
			for (int i = 0; i < n; i++)
			{
				if (frac[i] < 0.0) { continue; }
				if (frac[i] > bestFrac)
				{
					bestFrac = frac[i];
					best = i;
				}
			}
			if (best < 0) { break; }
			alloc[best]++;
			frac[best] = -1.0;   // 每项最多补 1，避免同一项被反复加
			left--;
		}
		return alloc;
	}

	private static string Fmt(double mult)
	{
		return mult.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>面板当前选择对应的倍率值。</summary>
	private double SelectedValue()
	{
		if (_kind == "lt") { return Math.Round(0.1 * Mathf.Clamp(_index, 1, 10), 1); }
		if (_kind == "gt") { return Mathf.Clamp(_index, 1, 10); }
		return 1.0;
	}

	// ================================================================ 面板

	private void OpenPanel()
	{
		try
		{
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null) { return; }
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root))
			{
				BuildPanel(root);
			}
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root)) { return; }
			SyncPanelFromConfig();
			RefreshStatus();
			_panel.Root.Visible = true;
			BattlePanelKit.Info("已打开「出怪倍率」面板；当前选择 = " + Fmt(SelectedValue()) + "×");
		}
		catch (Exception ex)
		{
			SafeWarn("打开面板异常（已吞）：" + ex.Message);
		}
	}

	private void ClosePanel()
	{
		try
		{
			if (_panel != null && _panel.Root != null && GodotObject.IsInstanceValid(_panel.Root))
			{
				_panel.Root.Visible = false;
			}
		}
		catch { }
	}

	private void BuildPanel(Node root)
	{
		try
		{
			BattlePanelKit.Panel p = BattlePanelKit.CreatePanel(root, PanelLayerName, PanelRootName, 470f, "出怪倍率");
			if (p == null) { return; }
			VBoxContainer body = p.Body;
			const float innerW = 430f;

			BattlePanelKit.AddLine(body, "先选倍率方向，再拖滑动条，最后点「确定」。", 13,
				new Color(0.74f, 0.80f, 0.88f, 1f), innerW);

			BattlePanelKit.AddLine(body, "倍率方向", 14, new Color(0.82f, 0.92f, 0.86f, 1f), innerW, wrap: false);
			// 顺序按用户描述：大于 → 小于 → 等于
			string[] labels = { "大于 1 倍（出怪变多）", "小于 1 倍（出怪变少）", "等于 1 倍（恢复原样）" };
			_ddKind = BattlePanelKit.AddDropdown(body, labels, KindIndex(_kind), OnKindChanged);

			HSlider sl = BattlePanelKit.AddSliderRow(body, "倍率（10 档）", 1, 10, 1, Mathf.Clamp(_index, 1, 10),
				OnSliderChanged, out _lbValue, innerW);
			_slider = sl;

			_lbStatus = BattlePanelKit.AddLine(body, "", 13, new Color(0.86f, 0.92f, 0.70f, 1f), innerW);

			BattlePanelKit.AddLine(body,
				"说明：按每一波的出怪总量等比例放大/缩小，僵尸种类比例不变；动态出怪（波次点数）同倍率缩放。"
				+ "只影响当前关卡，已生成的波次不变；换关自动恢复原倍率。",
				12, new Color(0.70f, 0.76f, 0.84f, 1f), innerW);

			HBoxContainer footer = new HBoxContainer();
			footer.AddThemeConstantOverride("separation", 8);
			body.AddChild(footer, false, Node.InternalMode.Disabled);
			BattlePanelKit.AddButton(footer, "确定", OnConfirm);
			BattlePanelKit.AddButton(footer, "关闭", ClosePanel);

			_panel = p;
			SyncPanelFromConfig();
			p.Root.Visible = false;
		}
		catch (Exception ex)
		{
			SafeWarn("创建面板异常（已吞）：" + ex.Message);
		}
	}

	private static int KindIndex(string kind)
	{
		if (kind == "lt") { return 1; }
		if (kind == "eq") { return 2; }
		return 0;
	}

	private static string KindValue(int idx)
	{
		if (idx == 1) { return "lt"; }
		if (idx == 2) { return "eq"; }
		return "gt";
	}

	private void SyncPanelFromConfig()
	{
		try
		{
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root)) { return; }
			_panelSync = true;
			_index = Mathf.Clamp(_index, 1, 10);
			if (_ddKind != null && GodotObject.IsInstanceValid(_ddKind)) { _ddKind.Selected = KindIndex(_kind); }
			if (_slider != null && GodotObject.IsInstanceValid(_slider))
			{
				_slider.SetValueNoSignal(_index);
				_slider.Editable = (_kind != "eq");
			}
			UpdateValueLabel();
			_panelSync = false;
		}
		catch { _panelSync = false; }
	}

	private void OnKindChanged(int idx)
	{
		try
		{
			if (_panelSync) { return; }
			_kind = KindValue(idx);
			if (_slider != null && GodotObject.IsInstanceValid(_slider)) { _slider.Editable = (_kind != "eq"); }
			UpdateValueLabel();
		}
		catch { }
	}

	private void OnSliderChanged(double v)
	{
		try
		{
			_index = Mathf.Clamp(Mathf.RoundToInt((float)v), 1, 10);
			UpdateValueLabel();
		}
		catch { }
	}

	private void UpdateValueLabel()
	{
		try
		{
			if (_lbValue == null || !GodotObject.IsInstanceValid(_lbValue)) { return; }
			double value = SelectedValue();
			_lbValue.Text = Fmt(value) + "×";
		}
		catch { }
	}

	private void OnConfirm()
	{
		try
		{
			// 先落库选择（下次打开面板/重进游戏仍是上次的选择）
			Dictionary<string, object> changes = new Dictionary<string, object>(2);
			changes[KMultKind] = _kind;
			changes[KMultIndex] = Mathf.Clamp(_index, 1, 10);
			BattlePanelKit.Save(_context, changes);

			if (ApplySelection(out string msg))
			{
				RefreshStatus();
				BattlePanelKit.Info(msg);
				ClosePanel();
			}
			else
			{
				if (_lbStatus != null && GodotObject.IsInstanceValid(_lbStatus)) { _lbStatus.Text = msg; }
				SafeWarn(msg);
			}
		}
		catch (Exception ex)
		{
			SafeWarn("确定按钮异常（已吞）：" + ex.Message);
		}
	}

	private void RefreshStatus()
	{
		try
		{
			if (_lbStatus == null || !GodotObject.IsInstanceValid(_lbStatus)) { return; }
			if (_activeLevelKey == null || Math.Abs(_activeMult - 1.0) < 1e-9)
			{
				_lbStatus.Text = "当前关卡：原始出怪（1×）。";
			}
			else
			{
				_lbStatus.Text = "当前关卡已生效：" + Fmt(_activeMult) + "×（换关自动恢复）。";
			}
		}
		catch { }
	}

	private void SafeWarn(string msg)
	{
		try { GD.PrintErr("[SpawnRate] " + msg); } catch { }
	}
}
