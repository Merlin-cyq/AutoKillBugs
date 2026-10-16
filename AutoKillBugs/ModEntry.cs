using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Integrations.GenericModConfigMenu;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace AutoKillBugs
{
    /// <summary>
    /// 模组入口：挂到游戏循环上，让矿井/骷髅洞穴/突变虫穴里的蛆(Grub)与苍蝇(Fly)在出现后立刻死亡。
    ///
    /// 时间点选择说明（为什么不选其它事件）：
    /// <list type="bullet">
    /// <item><c>GameLoop.UpdateTicked</c>（本模组采用）：SMAPI 的 <c>SCore.OnPlayerInstanceUpdating</c> 里，
    /// <c>runUpdate()</c>（真正的 <c>Game1.Update</c>，怪物就是在其中被生成并加入 <c>GameLocation.characters</c>）在 SCore.cs:913，
    /// 而 <c>UpdateTicked</c> 在同一方法的 SCore.cs:920 —— 紧跟其后、仍在本次 <c>Game1.Tick</c> 之内，也就是还在这一帧的 Draw 之前。
    /// 因此在这里击杀，怪物**在它出现的那一帧就已经被移出 characters**，玩家一帧都看不到。
    /// （配置 <c>DelayOneFrame=true</c> 时，本模组刻意把击杀推迟到下一个 <c>UpdateTicked</c>，
    /// 代价是怪物会被绘制一帧 —— 为的是换取 <c>Monster.InitializeForLocation</c> 追加的加成掉落，
    /// 取舍说明见 <see cref="ModConfig.DelayOneFrame"/>。）</item>
    /// <item><c>World.NpcListChanged</c>：SMAPI 对 GameLocation.characters 变化的通知，本模组不采用它作为主路径。
    /// 它在 SCore.cs:643 刷新快照、SCore.cs:803-805 派发，全部发生在同一次 tick 的 <c>runUpdate()</c> **之前**，
    /// 因此「在 runUpdate 里被生成」的怪物要到下一个 tick 才被报告 —— 那时它已经至少被绘制过一帧。
    /// 换句话说它天生比 UpdateTicked 晚一帧，无法满足「看不到贴图」。</item>
    /// <item><c>GameLoop.SaveLoaded</c>：只在读档时触发一次，完全覆盖不了随时发生的刷怪。</item>
    /// <item><c>World.NPCAdded / NPCRemoved</c>：SMAPI 4.5.2 里**不存在**这两个事件（已核实 StardewModdingAPI.xml，已被 NpcListChanged 取代）。</item>
    /// </list>
    /// 一律不使用「直接删除对象 / 隐藏贴图 / 改刷怪表」的做法，因为那样不会触发游戏原生的击杀结算。
    /// </summary>
    public class ModEntry : Mod
    {
        /*********
        ** 字段
        *********/

        /// <summary>目标怪物的检查与击杀逻辑。</summary>
        private BugKiller Killer = null!;

        /// <summary>本模组配置（config.json）。</summary>
        private ModConfig Config = null!;

        /// <summary>是否已经提示过「此地点不在生效范围」。</summary>
        private bool LoggedSkippedLocation;

        /// <summary>
        /// 打开/关闭本模组设置菜单的热键（本模组唯一占用的按键）。
        /// 三项设置本身不占按键：在菜单里点、直接改 config.json、或用 GMCM 都能调。
        /// </summary>
        private const SButton SettingsHotkey = SButton.F9;

        /*********
        ** 公开方法
        *********/

        /// <summary>模组入口，由 SMAPI 调用。</summary>
        /// <param name="helper">SMAPI 提供的模组接口。</param>
        public override void Entry(IModHelper helper)
        {
            // 首次运行时 SMAPI 会按 ModConfig 的默认值自动生成 config.json。
            this.Config = helper.ReadConfig<ModConfig>();
            this.Killer = new BugKiller(this.Monitor, this.Config);

            // 只为一个「自动击杀瞬间不播放怪物音效」的前缀补丁使用 Harmony；安装失败也不影响主线功能。
            bool soundSuppressorReady = SoundSuppressor.TryInstall(this.Monitor, this.ModManifest.UniqueID);

            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;

            // 两个设置入口：F9 菜单（Input.ButtonPressed）、GMCM 对接（GameLaunched，见该方法的注释）。
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;

            this.Monitor.Log(
                "AutoKillBugs 已载入：蛆(Grub)/苍蝇(Fly)（含危险矿井与突变虫穴的强化变体）一出现就按游戏原生击杀流程死亡，"
                + "掉落物、经验与探险家公会「杀死洞穴昆虫」击杀计数均按玩家击杀计入。"
                + $"当前配置 Enabled={this.Config.Enabled}、OnlyInMines={this.Config.OnlyInMines}、DelayOneFrame={this.Config.DelayOneFrame}"
                + $"（{(this.Config.DelayOneFrame ? "延迟一帧击杀：每只怪都会先跑一次 update，加成掉落完整，虫子会闪一帧" : "当帧击杀：一帧都看不到，但可能缺少 InitializeForLocation 的加成掉落")}）"
                + $"，击杀音效抑制={(soundSuppressorReady ? "已启用" : "未启用")}。"
                + $"按 {SettingsHotkey} 打开设置菜单（标题界面与存档里都能开）。",
                LogLevel.Info
            );
        }

        /*********
        ** 私有方法
        *********/

        /// <summary>游戏状态每更新一次（约每秒 60 次）调用一次。</summary>
        /// <param name="sender">事件发送者。</param>
        /// <param name="e">事件参数。</param>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // 世界还没就绪（标题画面、读档过程中）时不做任何事。
            // 注意：所有早退分支都要顺手丢弃待处理击杀记录，否则集合会一直留着上一轮登记的对象。
            if (!this.Config.Enabled || !Context.IsWorldReady)
            {
                this.Killer.DiscardPendingTargets();
                return;
            }

            GameLocation? location = Game1.currentLocation;
            if (location is null)
            {
                this.Killer.DiscardPendingTargets();
                return;
            }

            // 只处理玩家所在地点：别处（例如已经离开的矿层）的怪物不会被玩家看见，留给游戏自己处理即可。
            if (this.Config.OnlyInMines && !IsMineOrBugLand(location))
            {
                if (!this.LoggedSkippedLocation)
                {
                    this.LoggedSkippedLocation = true;
                    this.Monitor.Log(
                        $"当前地点（{location.NameOrUniqueName}）不在矿井范围内，按配置 OnlyInMines=true 跳过；"
                        + "想让全地图生效，把 config.json 里的 OnlyInMines 改成 false 即可。",
                        LogLevel.Debug
                    );
                }

                this.Killer.DiscardPendingTargets();
                return;
            }

            // 单人游戏与多人主机都由这里处理（客机端会在 BugKiller 内部拒绝），
            // 因为怪物在多人游戏里由主机持有，客机端改 characters 会造成不同步。
            this.Killer.KillTargetsInLocation(location);
        }

        /// <summary>判断地点是否属于「矿井类」：矿井 / 骷髅洞穴 / 采石场矿井（含危险矿井）以及突变虫穴。</summary>
        /// <param name="location">待判断的地点。</param>
        /// <remarks>
        /// 为什么把 BugLand 也算进来：突变虫穴（Mutant Bug Lair）的类 <c>BugLand : GameLocation</c>（BugLand.cs:9）
        /// **不是** MineShaft 的子类，但它正是突变蛆/突变苍蝇的老家（BugLand.cs:101 与 GameLocation.cs:13739 在那里生成 hard: true 的 Grub/Fly），
        /// 用户要的「蛆/苍蝇及其变体」必须包含它。
        /// </remarks>
        private static bool IsMineOrBugLand(GameLocation location)
        {
            return location is MineShaft || location is BugLand;
        }

        /*********
        ** 设置入口之一：F9 游戏内设置菜单
        *********/

        /// <summary>玩家按下某个按键时调用（设置菜单的打开/关闭都走这里）。</summary>
        /// <param name="sender">事件发送者。</param>
        /// <param name="e">事件参数。</param>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // 1) 热键：打开设置菜单；菜单已经开着时再按 = 关掉。
            if (e.Button == SettingsHotkey)
            {
                // 让游戏这一帧不要再看到这个按键（SMAPI 4.x 的抑制方式是 helper.Input.Suppress，
                // ButtonPressedEventArgs 本身没有 SuppressButton 方法 —— 已按 SMAPI 4.5.2 的 InputHelper.cs:49 核实）。
                this.Helper.Input.Suppress(SettingsHotkey);
                this.ToggleSettingsMenu();
                return;
            }

            // 2) 标题界面的 Esc 兜底。标题界面时本模组的菜单挂在 TitleMenu.subMenu 上（原因见 SettingsMenu 的类注释），
            //    而 TitleMenu.receiveKeyPress 在把按键转给 subMenu 之前会**先把玩家的菜单键（默认 Esc）排除掉**
            //    —— TitleMenu.cs:740 `if (!Game1.options.doesInputListContain(Game1.options.menuButton, key) && ShouldAllowInteraction())`，
            //    所以那条路收不到 Esc，这里用 SMAPI 的输入事件补上，效果与游戏内按 Esc 一致。
            //    （游戏内不需要这一步：那时菜单就是 Game1.activeClickableMenu，Esc 由游戏自己转发进来。）
            if (e.Button == SButton.Escape && TitleMenu.subMenu is SettingsMenu titleScreenMenu)
            {
                this.Helper.Input.Suppress(SButton.Escape);
                titleScreenMenu.CloseMenu();
            }
        }

        /// <summary>打开或关闭设置菜单（多次按热键 = 开关切换）。</summary>
        private void ToggleSettingsMenu()
        {
            // 已经开着 → 关掉。
            SettingsMenu? open = GetOpenSettingsMenu();
            if (open is not null)
            {
                open.CloseMenu();
                return;
            }

            // 只有「当前没有别的菜单」时才抢焦点，避免打断背包/箱子/对话/事件演出：
            // ① 游戏内：Game1.activeClickableMenu == null。
            if (Game1.activeClickableMenu is null)
            {
                Game1.activeClickableMenu = new SettingsMenu(this.Helper, this.Config, titleScreenMode: false, previousMenu: null);
                return;
            }

            // ② 标题界面：activeClickableMenu 就是那个 TitleMenu 实例本身（Game1.cs:2598），并且它自己没挂着别的子菜单
            //    （「读取存档」「关于」等原生菜单也占用 TitleMenu.subMenu，别顶掉它们）。
            if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is null)
            {
                TitleMenu.subMenu = new SettingsMenu(this.Helper, this.Config, titleScreenMode: true, previousMenu: null);
            }
        }

        /// <summary>找出当前正开着的设置菜单（没有就返回 null）。</summary>
        private static SettingsMenu? GetOpenSettingsMenu()
        {
            if (Game1.activeClickableMenu is SettingsMenu inGameMenu)
                return inGameMenu;

            if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is SettingsMenu titleScreenMenu)
                return titleScreenMenu;

            return null;
        }

        /*********
        ** 设置入口之二：GMCM（Generic Mod Config Menu）对接（可选依赖）
        *********/

        /// <summary>所有 Mod 都载入完成后调用一次（GMCM 的注册要等到这时才能稳定拿到 API）。</summary>
        /// <param name="sender">事件发送者。</param>
        /// <param name="e">事件参数。</param>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            this.RegisterGenericModConfigMenu();
        }

        /// <summary>
        /// 把三项设置注册进 GMCM。GMCM 是**可选**依赖：玩家没装时 <c>GetApi</c> 按 SMAPI 的约定返回 null，
        /// 这里直接静默返回 —— 不抛异常、不记 Error、不影响本模组加载（少了这条入口而已，F9 菜单照常可用）。
        /// </summary>
        private void RegisterGenericModConfigMenu()
        {
            // 类型用的是 SMAPI 4.5.2 自带的 StardewModdingAPI.Integrations.GenericModConfigMenu.IGenericModConfigMenuApi，
            // 不需要额外引用任何第三方 DLL。
            IGenericModConfigMenuApi? api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api is null)
                return;

            // reset：GMCM 的「Reset」按钮 → 恢复成 ModConfig 的默认值（三项都是 true）。
            // save：GMCM 的「Save」按钮 → 写回 config.json。
            // titleScreenOnly: false = 标题界面的 Mod Options 里能改，游戏中也同样能改。
            api.Register(this.ModManifest, reset: this.ResetConfigToDefaults, save: this.SaveConfig, titleScreenOnly: false);

            api.AddSectionTitle(this.ModManifest, () => "AutoKillBugs（蛆/苍蝇出现即死）");
            api.AddBoolOption(
                this.ModManifest,
                getValue: () => this.Config.Enabled,
                setValue: value => this.Config.Enabled = value,
                name: () => "总开关（Enabled）",
                tooltip: () => "关掉后本模组什么都不做（等价于没装）。"
            );
            api.AddBoolOption(
                this.ModManifest,
                getValue: () => this.Config.OnlyInMines,
                setValue: value => this.Config.OnlyInMines = value,
                name: () => "只在矿井生效（OnlyInMines）",
                tooltip: () => "只在矿井/骷髅洞穴/采石场矿井/突变虫穴生效；关掉则对所有地点生效。"
            );
            api.AddBoolOption(
                this.ModManifest,
                getValue: () => this.Config.DelayOneFrame,
                setValue: value => this.Config.DelayOneFrame = value,
                name: () => "延迟一帧击杀（DelayOneFrame）",
                tooltip: () => "开=每只虫先跑一次 update，加成掉落完整但会闪一帧；关=一帧都看不到，可能少拿加成掉落。"
            );

            this.Monitor.Log("已检测到 Generic Mod Config Menu：三项设置已注册到它的配置界面（标题界面的 Mod Options 里可见）。", LogLevel.Info);
        }

        /// <summary>把三项设置恢复成默认值（供 GMCM 的 Reset 用）。</summary>
        /// <remarks>
        /// 默认值直接取自 <see cref="ModConfig"/> 的属性初始化（三项都是 true），避免在这里重复写一遍常量。
        /// 关键：这里**只改字段、不换对象** —— BugKiller 持有的是同一个 ModConfig 实例（BugKiller.cs:61、:88），
        /// 一旦把 this.Config 换成新对象，击杀逻辑就再也看不到这里的改动了。
        /// </remarks>
        private void ResetConfigToDefaults()
        {
            ModConfig defaults = new ModConfig();

            this.Config.Enabled = defaults.Enabled;
            this.Config.OnlyInMines = defaults.OnlyInMines;
            this.Config.DelayOneFrame = defaults.DelayOneFrame;
        }

        /// <summary>把内存里的配置写回 config.json（供 GMCM 的 Save 用；F9 菜单里是自己直接调 helper.WriteConfig）。</summary>
        private void SaveConfig()
        {
            this.Helper.WriteConfig(this.Config);
        }
    }
}
