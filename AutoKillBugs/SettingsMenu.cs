using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace AutoKillBugs
{
    /// <summary>
    /// 游戏内设置菜单（默认热键 F9，热键在 <see cref="ModEntry"/> 里）：三个开关 ——
    /// <c>Enabled</c> / <c>OnlyInMines</c> / <c>DelayOneFrame</c>，点一下立即生效并立刻写回 config.json。
    ///
    /// 三条设计约束（每条都能在本机反编译源码里对上原文）：
    /// <list type="bullet">
    /// <item><b>只用游戏原生资源画</b>：面板是 <c>IClickableMenu.drawTextureBox</c>（内部即 <c>Game1.menuTexture</c> 的标准菜单框，
    /// IClickableMenu.cs:950-953）；文字用 <c>Game1.dialogueFont</c> / <c>Game1.smallFont</c>；
    /// 勾选框用 <c>Game1.mouseCursors</c> 上那对矩形 <c>(227,425,9,9)</c> / <c>(236,425,9,9)</c>，与原生
    /// <c>OptionsCheckbox</c>（OptionsCheckbox.cs:14-16、:41）用的**同一对**精灵，缩放同为 4f；
    /// 悬停高亮用 <c>Game1.menuTexture</c> 的按钮底纹。**不新增任何贴图/字体资源文件**。</item>
    /// <item><b>改动立即生效</b>：本菜单持有的是 <c>ModEntry</c> 里那个 <see cref="ModConfig"/> **实例本身**（不是副本），
    /// 而 <c>BugKiller</c> 持有的也正是同一个实例（BugKiller.cs:61、:88），并且它是**每次用到时才读**字段
    /// （例如 BugKiller.cs:138 的 <c>if (!this.Config.DelayOneFrame)</c>），所以这里改一个字段，
    /// 下一次 <c>GameLoop.UpdateTicked</c>（Enabled / OnlyInMines）或下一次击杀判定（DelayOneFrame）立刻按新值走；
    /// 同一次点击里再调 <c>helper.WriteConfig</c> 把内存里的这份配置写回 config.json。
    /// 所以这里**永远不重新 new 一个 ModConfig 赋给 ModEntry**（那会让 BugKiller 继续用旧对象）。</item>
    /// <item><b>两种打开位置</b>：游戏内挂在 <c>Game1.activeClickableMenu</c> 上；标题界面挂在 <c>TitleMenu.subMenu</c> 上。
    /// 标题界面时 <c>Game1.activeClickableMenu</c> 就是那个 TitleMenu 实例本身（Game1.cs:2598
    /// <c>activeClickableMenu = new TitleMenu()</c>），若直接顶掉它，会命中 <c>activeClickableMenu</c> setter 里的
    /// <c>if (_activeClickableMenu is IDisposable ...) disposable.Dispose();</c>（Game1.cs:1687）把标题界面本身销毁掉，
    /// 关闭后也没法恢复 —— 所以标题界面走游戏自己的 subMenu 机制。</item>
    /// </list>
    /// </summary>
    public class SettingsMenu : IClickableMenu
    {
        /*********
        ** 布局常量
        *********/

        /// <summary>面板宽度（像素）。与原生菜单同量级，1280×720 的 UI 视口下两侧仍有余量。</summary>
        private const int MenuWidth = 632;

        /// <summary>标题区高度。</summary>
        private const int HeaderHeight = 108;

        /// <summary>每一行占的高度（含行间距）。</summary>
        private const int RowHeight = 68;

        /// <summary>行与面板左右边缘的间距。</summary>
        private const int RowInset = 24;

        /// <summary>行与行之间留的空隙（高亮框不会贴在一起）。</summary>
        private const int RowGap = 8;

        /// <summary>底部提示区高度。</summary>
        private const int FooterHeight = 64;

        /// <summary>开关项个数：Enabled / OnlyInMines / DelayOneFrame。</summary>
        private const int OptionCount = 3;

        /// <summary>面板总高度（由上面几个常量算出来，改动项数时会自动跟着变）。</summary>
        private const int MenuHeight = HeaderHeight + OptionCount * RowHeight + FooterHeight;

        /// <summary>勾选框精灵：未勾选。与 <c>OptionsCheckbox.sourceRectUnchecked</c> 逐字相同。</summary>
        private static readonly Rectangle UncheckedBoxSource = new Rectangle(227, 425, 9, 9);

        /// <summary>勾选框精灵：已勾选。与 <c>OptionsCheckbox.sourceRectChecked</c> 逐字相同。</summary>
        private static readonly Rectangle CheckedBoxSource = new Rectangle(236, 425, 9, 9);

        /// <summary>勾选框缩放（与 <c>OptionsCheckbox.draw</c> 一致 → 36×36 像素）。</summary>
        private const float BoxScale = 4f;

        /*********
        ** 字段
        *********/

        /// <summary>SMAPI 接口，用来调 <c>WriteConfig</c> 落盘。</summary>
        private readonly IModHelper Helper;

        /// <summary>模组配置对象（与 ModEntry / BugKiller 共用同一个实例）。</summary>
        private readonly ModConfig Config;

        /// <summary>true = 本菜单挂在 <c>TitleMenu.subMenu</c> 上（标题界面）；false = 挂在 <c>Game1.activeClickableMenu</c> 上（游戏内）。</summary>
        private readonly bool TitleScreenMode;

        /// <summary>打开本菜单之前 <c>Game1.activeClickableMenu</c> 里的那个菜单（游戏内打开时恒为 null，见 <see cref="ModEntry"/> 的打开条件）。</summary>
        private readonly IClickableMenu? PreviousMenu;

        /// <summary>三个开关项。</summary>
        private readonly List<ToggleOption> Options;

        /// <summary>当前鼠标悬停在第几项（-1 = 没有）。</summary>
        private int HoveredIndex = -1;

        /*********
        ** 构造与公开成员
        *********/

        /// <summary>菜单是否已经关闭（关闭流程只允许走一次，热键/Esc/右键/关闭按钮都会经过它）。</summary>
        public bool IsClosed { get; private set; }

        /// <summary>构造设置菜单。</summary>
        /// <param name="helper">SMAPI 接口（写 config.json 用）。</param>
        /// <param name="config">模组配置对象（必须是 ModEntry 里那一个实例）。</param>
        /// <param name="titleScreenMode">true = 将挂在 <c>TitleMenu.subMenu</c> 上；false = 将挂在 <c>Game1.activeClickableMenu</c> 上。</param>
        /// <param name="previousMenu">打开前的 <c>Game1.activeClickableMenu</c>（游戏内恒为 null）。</param>
        public SettingsMenu(IModHelper helper, ModConfig config, bool titleScreenMode, IClickableMenu? previousMenu)
            : base(
                x: (Game1.uiViewport.Width - MenuWidth) / 2,
                y: (Game1.uiViewport.Height - MenuHeight) / 2,
                width: MenuWidth,
                height: MenuHeight,
                showUpperRightCloseButton: true   // 用游戏自带的右上角 X（基类会建好、基类 receiveLeftClick 会处理）
            )
        {
            this.Helper = helper;
            this.Config = config;
            this.TitleScreenMode = titleScreenMode;
            this.PreviousMenu = previousMenu;

            // 三项的顺序与 config.json 里的字段顺序一致，方便对照。
            this.Options = new List<ToggleOption>
            {
                new ToggleOption(
                    label: () => "总开关（Enabled）",
                    description: () => "关掉后本模组什么都不做（等价于没装）。",
                    getValue: () => this.Config.Enabled,
                    setValue: value => this.Config.Enabled = value
                ),
                new ToggleOption(
                    label: () => "只在矿井生效（OnlyInMines）",
                    description: () => "只在矿井/骷髅洞穴/采石场矿井/突变虫穴生效。",
                    getValue: () => this.Config.OnlyInMines,
                    setValue: value => this.Config.OnlyInMines = value
                ),
                new ToggleOption(
                    label: () => "延迟一帧击杀（DelayOneFrame）",
                    description: () => "开=掉落全但闪一帧；关=一帧都看不到。",
                    getValue: () => this.Config.DelayOneFrame,
                    setValue: value => this.Config.DelayOneFrame = value
                )
            };
        }

        /// <summary>
        /// 关闭本菜单：热键再按一次 / Esc / 右键 / 右上角 X 都走这里。
        /// 具体的状态恢复见 <see cref="cleanupBeforeExit"/>（基类 <c>exitThisMenu</c> 会调用它）。
        /// </summary>
        public void CloseMenu()
        {
            if (this.IsClosed)
                return;

            this.exitThisMenu();
        }

        /*********
        ** 输入
        *********/

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.IsClosed)
                return;

            // 先让基类处理右上角那个原生关闭按钮（IClickableMenu.cs:744-754：命中就 exitThisMenu()）。
            base.receiveLeftClick(x, y, playSound);
            if (this.IsClosed)
                return;

            for (int i = 0; i < this.Options.Count; i++)
            {
                if (!this.GetRowBounds(i).Contains(x, y))
                    continue;

                this.Toggle(i, playSound);
                return;
            }
        }

        /// <inheritdoc />
        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            // 基类的 receiveRightClick 是空实现（IClickableMenu.cs:766-768），这里补上「右键关闭」。
            if (this.IsClosed)
                return;

            this.exitThisMenu(playSound);
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            if (this.IsClosed)
                return;

            // Esc = 关闭。这里**刻意不调用** base.receiveKeyPress()：
            // 基类那份实现（IClickableMenu.cs:772-785）除了处理「菜单键 → exitThisMenu()」，
            // 还会在 snappyMenus + 手柄模式下把按键转给 applyMovementKey/keyboard 吸附导航；
            // 而本菜单没有任何需要键盘导航的组件（唯一带 bounds 的组件是那个只能用鼠标点的关闭按钮），
            // 少走那条用不到的路径就不会碰到 currentlySnappedComponent 未初始化之类的边角情况。
            // 「菜单键」沿用玩家自己的绑定（默认就是 Esc，见 Game1.options.menuButton）。
            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
                this.exitThisMenu();
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);   // 原生关闭按钮的悬停放大

            this.HoveredIndex = -1;
            for (int i = 0; i < this.Options.Count; i++)
            {
                if (this.GetRowBounds(i).Contains(x, y))
                {
                    this.HoveredIndex = i;
                    break;
                }
            }
        }

        /*********
        ** 绘制
        *********/

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            // 面板：游戏标准的菜单框（内部就是 Game1.menuTexture 的 (0,256,60,60) 九宫格，四角带阴影）。
            IClickableMenu.drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            // 标题
            string title = "AutoKillBugs · 设置";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(
                Game1.dialogueFont,
                title,
                new Vector2(this.xPositionOnScreen + (this.width - titleSize.X) / 2f, this.yPositionOnScreen + 20f),
                Game1.textColor
            );

            // 副标题（说清「改了就生效、已经落盘」）
            string subtitle = "热键 F9 · 改动立即生效并已写回 config.json";
            Vector2 subtitleSize = Game1.smallFont.MeasureString(subtitle);
            b.DrawString(
                Game1.smallFont,
                subtitle,
                new Vector2(this.xPositionOnScreen + (this.width - subtitleSize.X) / 2f, this.yPositionOnScreen + 66f),
                Game1.textColor * 0.7f
            );

            for (int i = 0; i < this.Options.Count; i++)
                this.DrawRow(b, i);

            // 底部提示
            string footer = "Esc 或右键关闭";
            Vector2 footerSize = Game1.smallFont.MeasureString(footer);
            b.DrawString(
                Game1.smallFont,
                footer,
                new Vector2(this.xPositionOnScreen + (this.width - footerSize.X) / 2f, this.yPositionOnScreen + this.height - FooterHeight + 20f),
                Game1.textColor * 0.7f
            );

            // 基类的 draw 只负责右上角关闭按钮（IClickableMenu.cs:827-833），放最后画，保证盖在面板之上。
            base.draw(b);
        }

        /// <summary>画一行开关。</summary>
        /// <param name="b">SpriteBatch。</param>
        /// <param name="index">第几项。</param>
        private void DrawRow(SpriteBatch b, int index)
        {
            Rectangle row = this.GetRowBounds(index);
            ToggleOption option = this.Options[index];
            bool value = option.GetValue();

            // 悬停高亮：还是用 Game1.menuTexture 的按钮底纹（不额外画半透明色块，观感与原生按钮一致）。
            if (this.HoveredIndex == index)
            {
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    row.X,
                    row.Y,
                    row.Width,
                    row.Height,
                    Color.White,
                    1f,
                    drawShadow: false
                );
            }

            // 勾选框：与原生 OptionsCheckbox 同一对精灵、同一缩放。
            b.Draw(
                Game1.mouseCursors,
                new Vector2(row.X + 20, row.Y + (row.Height - 9 * BoxScale) / 2f),
                value ? CheckedBoxSource : UncheckedBoxSource,
                Color.White,
                0f,
                Vector2.Zero,
                BoxScale,
                SpriteEffects.None,
                0.9f
            );

            // 名称 + 说明
            Vector2 labelPosition = new Vector2(row.X + 76, row.Y + 6);
            b.DrawString(Game1.smallFont, option.Label(), labelPosition, Game1.textColor);
            b.DrawString(Game1.smallFont, option.Description(), new Vector2(labelPosition.X, labelPosition.Y + 26), Game1.textColor * 0.65f);

            // 右侧的开/关字样（纯文字，不开贴图；关掉时淡一档，和勾选框一起表达状态）
            string state = value ? "开" : "关";
            Vector2 stateSize = Game1.smallFont.MeasureString(state);
            b.DrawString(
                Game1.smallFont,
                state,
                new Vector2(row.Right - 24 - stateSize.X, row.Y + (row.Height - stateSize.Y) / 2f),
                value ? Game1.textColor : Game1.textColor * 0.55f
            );
        }

        /*********
        ** 内部逻辑
        *********/

        /// <summary>算出一行的矩形（每次现算，这样窗口尺寸变化导致菜单被挪动时也不会错位）。</summary>
        /// <param name="index">第几项。</param>
        private Rectangle GetRowBounds(int index)
        {
            return new Rectangle(
                this.xPositionOnScreen + RowInset,
                this.yPositionOnScreen + HeaderHeight + index * RowHeight,
                this.width - RowInset * 2,
                RowHeight - RowGap
            );
        }

        /// <summary>切换一项，并立刻写回 config.json。</summary>
        /// <param name="index">第几项。</param>
        /// <param name="playSound">是否播放声音。</param>
        private void Toggle(int index, bool playSound)
        {
            ToggleOption option = this.Options[index];
            option.SetValue(!option.GetValue());

            if (playSound)
                Game1.playSound("drumkit6");   // 原生 OptionsCheckbox.receiveLeftClick 用的就是这一声（OptionsCheckbox.cs:29）

            // 立刻落盘。注意：只写 config.json，不碰任何其它文件；
            // WriteConfig 是 SMAPI 的约定接口，写入目标就是本模组文件夹里的 config.json。
            this.Helper.WriteConfig(this.Config);
        }

        /// <summary>
        /// 菜单退出前的收尾（基类 <c>exitThisMenu()</c> → 本方法，见 IClickableMenu.cs:865-893）。
        /// </summary>
        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (this.TitleScreenMode)
            {
                // 标题界面：这里**什么都不用做、也什么都不该做**。
                // TitleMenu.subMenu 的 setter 已经把 TitleMenu.CloseSubMenu 挂到了本菜单的 exitFunction 上
                // （TitleMenu.cs:211-217），基类 exitThisMenu() 末尾会调用它，由游戏自己完成
                // 「回到主标题界面」的收尾（isTransitioningButtons + 音乐切换，TitleMenu.cs:867-873）。
                // 若在这里手动把 TitleMenu.subMenu 置 null，反而会先命中 setter 里的
                // `_subMenu.exitFunction = null`（TitleMenu.cs:204）把回调清掉，标题按钮就再也滑不回来了。
            }
            else if (ReferenceEquals(Game1.activeClickableMenu, this))
            {
                // 游戏内：恢复成打开前的菜单。打开条件决定了这里恒为 null（见 ModEntry.ToggleSettingsMenu），
                // 也就是交回「没有菜单」的正常状态（和原版菜单关掉后一样，游戏照常画世界与 HUD）。
                Game1.activeClickableMenu = this.PreviousMenu;
            }

            this.IsClosed = true;
        }

        /// <summary>一个可点击的开关项：名称、说明、读值、写值。</summary>
        private sealed class ToggleOption
        {
            /// <summary>构造。</summary>
            /// <param name="label">名称文字（每帧现取，便于将来做本地化）。</param>
            /// <param name="description">说明文字。</param>
            /// <param name="getValue">读当前值。</param>
            /// <param name="setValue">写新值。</param>
            public ToggleOption(Func<string> label, Func<string> description, Func<bool> getValue, Action<bool> setValue)
            {
                this.Label = label;
                this.Description = description;
                this.GetValue = getValue;
                this.SetValue = setValue;
            }

            /// <summary>名称文字。</summary>
            public Func<string> Label { get; }

            /// <summary>说明文字。</summary>
            public Func<string> Description { get; }

            /// <summary>读当前值。</summary>
            public Func<bool> GetValue { get; }

            /// <summary>写新值。</summary>
            public Action<bool> SetValue { get; }
        }
    }
}
