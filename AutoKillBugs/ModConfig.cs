namespace AutoKillBugs
{
    /// <summary>
    /// 本模组的配置文件（<c>Mods/AutoKillBugs/config.json</c>）。
    /// 用的是 SMAPI 的 ModConfig 模式：文件不存在时 SMAPI 会自动按下面的默认值生成一份，
    /// 改完保存后重进游戏（或重新读档）即可生效。
    /// </summary>
    public class ModConfig
    {
        /// <summary>
        /// 模组总开关。设为 <c>false</c> 时本模组什么都不做（等价于没装）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 是否只在矿井类地点生效：矿井、骷髅洞穴、采石场矿井（含危险矿井）以及突变虫穴。
        /// 设为 <c>false</c> 则对所有地点生效（例如别的 Mod 添加的地下城）。
        /// </summary>
        public bool OnlyInMines { get; set; } = true;

        /// <summary>
        /// 是否「延迟一帧击杀」。两种模式的取舍：
        /// <list type="bullet">
        /// <item><c>true</c>（默认）：怪物出现后先登记，<b>下一个</b>游戏 tick 才击杀。这样每只怪都至少执行过一次
        /// <c>Monster.update()</c>，而该方法的第一件事就是 <c>Monster.InitializeForLocation(...)</c>
        /// （Monster.cs:677-683 → Monster.cs:476-520），它会按难度追加额外掉落（钻石 72 / 五彩碎片 74 等，概率 0.001 + 难度×0.001），
        /// 这部分掉落只有在「怪物真的被 update 过一次」之后才存在。代价：虫子会在画面上出现约一帧（1/60 秒）。</item>
        /// <item><c>false</c>：在怪物加入 <c>GameLocation.characters</c> 的同一 tick 内击杀，怪物一帧都不会被绘制；
        /// 但在 <c>updateCharacters</c> 之后才加入的怪物（典型：蛹化蛆孵化出的苍蝇，Grub.cs:234）从未被 update 过，
        /// 因此拿不到上面那部分加成掉落。</item>
        /// </list>
        /// 两种模式都只调用 <c>GameLocation.damageMonster(...)</c>，掉落物、经验值与击杀计数完全一致。
        /// </summary>
        public bool DelayOneFrame { get; set; } = true;
    }
}
