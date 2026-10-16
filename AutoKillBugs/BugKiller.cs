using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace AutoKillBugs
{
    /// <summary>
    /// 把蛆(Grub)与苍蝇(Fly)按游戏原生击杀流程杀死。
    ///
    /// 关键点：本类只调用 <c>GameLocation.damageMonster(...)</c> —— 也就是玩家挥武器或扔炸弹命中怪物时，
    /// 游戏自己走的那一个入口。该方法在怪物血量归零时会调用 <c>GameLocation.onMonsterKilled(...)</c>，从而完整触发：
    /// <list type="bullet">
    /// <item><c>Game1.stats.monsterKilled(monster.Name)</c> → 探险家公会「杀死洞穴昆虫」进度（Grub / Fly / Bug 合计 80 只）</item>
    /// <item><c>GameLocation.monsterDrop(...)</c> → 按 Data/Monsters 原生掉落表掉落（虫肉、古代种子、钻石、五彩碎片等）</item>
    /// <item>任务 Quest.OnMonsterSlain、特殊订单、附魔「杀戮」、窃贼戒指、经验值等全部原生钩子</item>
    /// <item>最后才由游戏自己执行 <c>GameLocation.characters.Remove(monster)</c>（GameLocation.cs:4883）</item>
    /// </list>
    /// 直接删除怪物对象、或只把 Health 置 0，都拿不到上面任何一项：那种写法会被
    /// <c>GameLocation.updateCharacters</c> 静默移除（无掉落、无计数、无经验）。
    ///
    /// 击杀时机由 <see cref="ModConfig.DelayOneFrame"/> 选择，两种模式走的是同一条 <see cref="Kill"/>：
    /// <list type="bullet">
    /// <item><b>延迟一帧</b>（默认）：先登记进 <see cref="PendingTargets"/>，下一个 tick 才杀。每只怪都会先执行一次
    /// <c>Monster.update()</c>，于是 <c>Monster.InitializeForLocation(...)</c>（Monster.cs:677-683 → 476-520）
    /// 会追加那部分按难度计算的额外掉落（钻石 72 / 五彩碎片 74 等）。代价是虫子会闪一帧。</item>
    /// <item><b>当帧击杀</b>：在怪物加入 <c>characters</c> 的同一 tick 内击杀，一帧都不会被绘制；但「在 updateCharacters 之后
    /// 才加入」的怪物（典型：蛹化蛆孵化出的苍蝇，Grub.cs:234）从未被 update 过，拿不到上述加成掉落。</item>
    /// </list>
    /// </summary>
    internal sealed class BugKiller
    {
        /*********
        ** 常量
        *********/

        /// <summary>
        /// 击杀伤害。取一个远高于任何 Grub/Fly 血量上限的值即可，但**不能**用 int.MaxValue：
        /// GameLocation.damageMonster 内部会执行 <c>Game1.random.Next(minDamage, maxDamage + 1)</c>（GameLocation.cs:4695），
        /// maxDamage = int.MaxValue 时 +1 溢出成 int.MinValue，直接抛 ArgumentOutOfRangeException。
        /// </summary>
        private const int KillDamage = 999999;

        /// <summary>
        /// 命中精度。damageMonster 会把它除以 10 后传给 <c>Monster.takeDamage(...)</c> 的 addedPrecision 参数（GameLocation.cs:4728），
        /// 传 10 → addedPrecision = 1.0 → Grub/Fly 的 missChance 被判成 <c>missChance - missChance * 1.0 = 0</c>，
        /// 永远不会「未命中」（否则被怪物的闪避率挡下来就没有掉落与计数了）。
        /// </summary>
        private const int HitPrecision = 10;

        /*********
        ** 字段
        *********/

        /// <summary>日志接口。</summary>
        private readonly IMonitor Monitor;

        /// <summary>本模组配置（决定击杀时机等）。</summary>
        private readonly ModConfig Config;

        /// <summary>
        /// 延迟一帧模式下的待处理名单：本轮登记、下一轮击杀。
        /// 每轮都会先清空再按当前地点的 <c>characters</c> 重建，因此不可能无界增长，也不会重复登记同一只怪。
        /// </summary>
        private readonly List<Monster> PendingTargets = new();

        /// <summary>本次游戏会话累计自动击杀数量（只用于日志）。</summary>
        private int SessionKills;

        /// <summary>是否已经输出过「非主机端不处理」的提示。</summary>
        private bool LoggedNonHost;

        /// <summary>是否已经输出过「未找到玩家」的提示。</summary>
        private bool LoggedNoPlayer;

        /*********
        ** 构造
        *********/

        /// <summary>构造一个击杀器。</summary>
        /// <param name="monitor">SMAPI 日志接口。</param>
        /// <param name="config">本模组配置（击杀时机来自其中的 <see cref="ModConfig.DelayOneFrame"/>）。</param>
        public BugKiller(IMonitor monitor, ModConfig config)
        {
            this.Monitor = monitor;
            this.Config = config;
        }

        /*********
        ** 公开方法
        *********/

        /// <summary>把指定地点里的全部目标怪物（Grub / Fly 及其强化变体）按原生流程击杀。</summary>
        /// <param name="location">要扫描的地点，通常是 <c>Game1.currentLocation</c>。</param>
        /// <returns>本次实际击杀的数量（延迟一帧模式下，返回的是「本次杀掉的上轮登记数」，当轮新出现的怪要等下一轮）。</returns>
        /// <remarks>
        /// 击杀时机由 <see cref="ModConfig.DelayOneFrame"/> 决定，两条分支最终都只走 <see cref="Kill"/> → <c>GameLocation.damageMonster(...)</c>。
        /// </remarks>
        public int KillTargetsInLocation(GameLocation location)
        {
            if (location?.characters is null)
            {
                this.DiscardPendingTargets();
                return 0;
            }

            // 怪物在多人游戏里由主机持有，客机端改 characters 会造成不同步。
            if (!Game1.IsMasterGame)
            {
                if (!this.LoggedNonHost)
                {
                    this.LoggedNonHost = true;
                    this.Monitor.Log("当前不是游戏主机（多人游戏客机端），本模组不做任何处理。", LogLevel.Warn);
                }

                this.DiscardPendingTargets();
                return 0;
            }

            // onMonsterKilled 与 monsterDrop 都会用到玩家对象：
            // monsterDrop 里取 who.StandingPixel、damageMonster 里取 who.hasBuff(...)（GameLocation.cs:4684，无 null 保护），
            // 没有玩家就没有合法的「等价玩家击杀」，此时什么都不做。
            if (Game1.player is null)
            {
                if (!this.LoggedNoPlayer)
                {
                    this.LoggedNoPlayer = true;
                    this.Monitor.Log("尚未取到玩家对象（Game1.player 为 null），本次跳过自动击杀。", LogLevel.Debug);
                }

                this.DiscardPendingTargets();
                return 0;
            }

            // 模式二：当帧击杀（DelayOneFrame=false，与 1.0.0 完全一致）。本模式下待处理名单恒为空，顺手清一次以防任何残留。
            if (!this.Config.DelayOneFrame)
            {
                this.DiscardPendingTargets();
                return this.KillNow(location);
            }

            // 模式一：延迟一帧。
            // 先把上一轮登记的杀掉 —— 它们至少已经跑完一次 Monster.update()（Monster.cs:677-683），
            // InitializeForLocation 因此生效，加成掉落不会丢；再重新登记本轮在场的目标，交给下一轮处理。
            int killed = this.KillPendingTargets(location);
            this.RegisterPendingTargets(location);
            return killed;
        }

        /// <summary>丢弃全部待处理记录。</summary>
        /// <remarks>
        /// 所有早退分支（模组关闭、世界未就绪、地点不在生效范围、非主机端、没有玩家对象）都必须调用它：
        /// 否则玩家一旦离开矿井或关掉模组，上一轮登记的对象会一直留在集合里。
        /// </remarks>
        public void DiscardPendingTargets()
        {
            if (this.PendingTargets.Count == 0)
                return;

            int count = this.PendingTargets.Count;
            this.PendingTargets.Clear();
            this.Monitor.Log($"已丢弃 {count} 条待处理击杀记录（当前不需要处理它们，属于正常清理）。", LogLevel.Trace);
        }

        /*********
        ** 私有方法
        *********/

        /// <summary>当帧击杀模式：把当前地点的目标全部立刻击杀。</summary>
        /// <param name="location">要扫描的地点。</param>
        /// <returns>本次实际击杀的数量。</returns>
        private int KillNow(GameLocation location)
        {
            // 先收集成列表再动手：damageMonster 成功击杀时会由游戏自己修改 location.characters，不能在枚举期间改。
            List<Monster> targets = new();
            foreach (NPC npc in location.characters)
            {
                if (npc is Monster monster && IsTargetMonster(monster))
                    targets.Add(monster);
            }

            int killed = 0;
            foreach (Monster monster in targets)
            {
                if (this.TryKill(location, monster))
                    killed++;
            }

            return killed;
        }

        /// <summary>延迟一帧模式：击杀上一轮登记下来的目标。</summary>
        /// <param name="location">当前地点（<c>Game1.currentLocation</c>）。</param>
        /// <returns>本次实际击杀的数量。</returns>
        private int KillPendingTargets(GameLocation location)
        {
            if (this.PendingTargets.Count == 0)
                return 0;

            int killed = 0;
            foreach (Monster monster in this.PendingTargets)
            {
                // 登记与击杀之间隔了一整个 tick，对象可能已经失效（被玩家或别的路径打死、被移出 characters、玩家已换层），
                // 因此每一只都必须重新校验，绝不盲目动手。
                if (!IsStillKillableIn(location, monster))
                    continue;

                if (this.TryKill(location, monster))
                    killed++;
            }

            // 无论杀了多少只都清空：下一轮完全按当前 characters 重建，集合大小恒等于「当前地点里还没死掉的目标数」。
            this.PendingTargets.Clear();
            return killed;
        }

        /// <summary>把当前地点里在场的目标怪物登记为「下一轮击杀」。</summary>
        /// <param name="location">当前地点。</param>
        /// <remarks>
        /// 先清空再重建，因此：集合大小有界（≤ 当前地点的怪物数），同一只怪在一轮里只会出现一次，
        /// 上一轮已杀掉/已离开的引用也不会被继续保留。
        /// </remarks>
        private void RegisterPendingTargets(GameLocation location)
        {
            this.PendingTargets.Clear();

            foreach (NPC npc in location.characters)
            {
                if (npc is Monster monster && IsTargetMonster(monster) && monster.Health > 0)
                    this.PendingTargets.Add(monster);
            }
        }

        /// <summary>判断登记过的怪物是否仍然可以由本模组安全击杀。</summary>
        /// <param name="location">当前地点。</param>
        /// <param name="monster">之前登记下来的怪物。</param>
        private static bool IsStillKillableIn(GameLocation location, Monster monster)
        {
            // 已经被别的路径打死（或血量已归零）→ 跳过，绝不做第二次结算（否则会多算击杀数、多掉一次掉落）。
            if (monster.Health <= 0)
                return false;

            // 玩家这一帧内换层/换地点时，登记的怪物还指向旧地点：只处理仍属于当前地点的对象，避免跨地点误杀。
            if (monster.currentLocation is not null && !ReferenceEquals(monster.currentLocation, location))
                return false;

            // 必须仍在当前地点的 characters 里（被游戏移除过的对象不会出现在这里）。
            return location.characters.Contains(monster);
        }

        /// <summary>击杀一只怪物，并把任何意外限制在日志里（绝不把异常抛回游戏循环）。</summary>
        /// <param name="location">怪物所在地点。</param>
        /// <param name="monster">要击杀的怪物。</param>
        /// <returns>是否已成功击杀。</returns>
        private bool TryKill(GameLocation location, Monster monster)
        {
            try
            {
                return this.Kill(location, monster);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"自动击杀 {monster.Name} 时出现异常（已跳过这只）：{ex}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>判断是否为需要处理的目标怪物：蛆(Grub)、苍蝇(Fly)，含危险矿井的强化变体。</summary>
        /// <param name="monster">待判断的怪物。</param>
        /// <remarks>
        /// 1.6.15 的游戏数据里并不存在独立的「突变蛆 / 突变苍蝇」怪物类，也不存在独立的 Name：
        /// 突变虫穴（BugLand）与危险矿井里的 Grub/Fly 就是同一个类，只是构造时传 <c>hard: true</c>
        /// （见 <c>Grub(Vector2, bool)</c> / <c>Fly(Vector2, bool)</c>，BugLand.cs:101、Grub.cs:227/234、GameLocation.cs:13739 都这样生成）。
        /// 它们经过 <c>Monster.parseMonsterInfo("Grub"/"Fly")</c> 得到的 Name 依旧是 "Grub"/"Fly"，
        /// 因此按类型判断即可覆盖全部变体，而且与游戏自身在 <c>BugLand.DayUpdate</c> 里的写法（<c>npc is Grub || npc is Fly</c>）完全一致。
        /// </remarks>
        private static bool IsTargetMonster(Monster monster)
        {
            return monster is Grub || monster is Fly;
        }

        /// <summary>用游戏原生流程击杀一只怪物。</summary>
        /// <param name="location">怪物所在地点。</param>
        /// <param name="monster">要击杀的怪物。</param>
        /// <returns>是否已成功击杀。</returns>
        private bool Kill(GameLocation location, Monster monster)
        {
            // 已经有别的路径把它打死/移走了，就不再重复处理。
            if (monster.Health <= 0)
                return false;

            // 蛹化中的蛆在 Grub.takeDamage 里会直接 return 0（免疫伤害，Grub.cs:104-109），
            // 必须先解除蛹化状态才杀得死；若不解除，本模组会每帧徒劳地重击它，白刷音效直到它变成苍蝇。
            // 这是本模组对怪物状态唯一的改动，只作用于马上要被击杀的那一只，且击杀失败时会原样恢复。
            Grub? grub = monster as Grub;
            bool wasPupating = false;
            if (grub is not null && grub.pupating.Value)
            {
                wasPupating = true;
                grub.pupating.Value = false;
            }

            // 记录击杀前的临时特效数量，用于稍后抹掉这次死亡特效（用户对蛆/苍蝇的视听表现敏感）。
            int spritesBefore = location.temporarySprites.Count;

            // 音效抑制窗口：只覆盖这次 damageMonster 调用（Grub/Fly 会在其中播放 slimeHit/hitEnemy/slimedead/monsterdead）。
            SoundSuppressor.Begin();
            try
            {
                // 命中区域只取怪物包围盒的中心 1 像素：
                // damageMonster 是按矩形相交判定命中的（Monster.TakesDamageFromHitbox → GetBoundingBox().Intersects(area)，Monster.cs:994），
                // 传整只怪的包围盒会把恰好重叠在同一格的其它怪物一起打死；取中心点可让误伤概率降到最低。
                Rectangle box = monster.GetBoundingBox();
                Rectangle hitbox = new(box.Center.X, box.Center.Y, 1, 1);

                location.damageMonster(
                    hitbox,
                    KillDamage,
                    KillDamage,
                    isBomb: false,
                    knockBackModifier: 0f,
                    addedPrecision: HitPrecision,
                    critChance: 0f,
                    critMultiplier: 1f,
                    triggerMonsterInvincibleTimer: false,
                    who: Game1.player,
                    isProjectile: true
                );
            }
            finally
            {
                SoundSuppressor.End();

                // 没打死（例如血量被别的 Mod 改得比 KillDamage 还高）时恢复蛹化状态，下一帧继续尝试。
                if (monster.Health > 0 && wasPupating)
                    grub!.pupating.Value = true;
            }

            if (monster.Health > 0)
                return false;

            this.SuppressDeathEffects(location, monster, spritesBefore);

            this.SessionKills++;
            this.Monitor.Log(
                $"已自动击杀 {monster.Name}{((monster.isHardModeMonster.Value) ? "（强化变体）" : "")}"
                + $"（地点 {location.NameOrUniqueName}，本次会话累计 {this.SessionKills} 只）：走的是游戏原生击杀流程，"
                + "掉落物、经验与击杀计数均已按玩家击杀计入。",
                LogLevel.Debug
            );

            return true;
        }

        /// <summary>抹掉这只怪物刚产生的死亡特效与伤害数字（不影响掉落物与击杀计数）。</summary>
        /// <param name="location">怪物所在地点。</param>
        /// <param name="monster">刚被击杀的怪物。</param>
        /// <param name="spritesBefore">调用 damageMonster 之前的临时特效数量。</param>
        /// <remarks>
        /// 死亡特效的来源：Grub/Fly 的 takeDamage 死亡分支会调用 <c>Utility.makeTemporarySpriteJuicier(...)</c>
        /// （Grub.cs:118 / Fly.cs:100）把爆浆粒子追加到 <c>location.temporarySprites</c> 末尾；
        /// 伤害数字则是 damageMonster 自己生成的一个 <c>Debris</c>（GameLocation.cs:4748）。
        /// 掉落物也是 Debris，但它的 <c>toHover</c> 为 null，只有伤害数字的 toHover 指向被打的怪物
        /// （Debris.cs:124 字段，只有 Debris.cs:368 那个构造器会赋值），所以下面的过滤不会误删掉落物。
        /// </remarks>
        private void SuppressDeathEffects(GameLocation location, Monster monster, int spritesBefore)
        {
            try
            {
                // 1) 死亡粒子：移除击杀之后新出现的那些即可，
                //    既不会误删玩家自己或其它怪物的特效，也不影响掉落（掉落物是 Debris，不是临时特效）。
                TemporaryAnimatedSpriteList sprites = location.temporarySprites;
                while (sprites.Count > spritesBefore)
                    sprites.RemoveAt(sprites.Count - 1);

                // 2) 飘出的伤害数字（玩家会看到的那串「999999」）。
                location.debris.RemoveWhere(d => d.toHover == monster);
            }
            catch (Exception ex)
            {
                // 特效抑制只是锦上添花，失败绝不能影响已经完成的击杀。
                this.Monitor.Log($"抑制死亡特效时出错（掉落与击杀计数不受影响）：{ex}", LogLevel.Trace);
            }
        }
    }
}
