using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Audio;

namespace AutoKillBugs
{
    /// <summary>
    /// 在「本模组自动击杀的那一瞬间」屏蔽怪物音效。
    ///
    /// 为什么需要它：蛆(Grub)与苍蝇(Fly)的 <c>takeDamage</c> 会在死亡时调用
    /// <c>currentLocation.playSound("slimeHit" / "slimedead" / "hitEnemy" / "monsterdead")</c>
    /// （Grub.cs:103/118、Fly.cs:93/100），而这些调用的 position 参数是 null ——
    /// <c>GameLocation.playSound → Game1.sounds.PlayAll → SoundsHelper.GetVolumeForDistance</c> 对 null 位置直接返回
    /// 1.0 音量（SoundsHelper.cs:53-77），也就是说它们无法靠「离玩家远一点」自然衰减，必须显式压掉，
    /// 否则玩家会听到一串「看不到的怪物在死」的音效。
    ///
    /// 做法：给 <c>GameLocation.playSound</c> 装一个 Harmony 前缀，只在 <see cref="Suppress"/> 为 true 时跳过原方法。
    /// <see cref="Suppress"/> 的开启/关闭都被严格夹在 <see cref="Begin"/> 与 <see cref="End"/> 之间，
    /// 而这个窗口里只有本模组自己那一次 <c>damageMonster</c> 调用，因此对玩家正常攻击、其它音效毫无影响。
    /// 补丁安装失败也不会影响击杀与掉落（<see cref="TryInstall"/> 会返回 false 并记一条警告）。
    /// </summary>
    internal static class SoundSuppressor
    {
        /// <summary>是否正处于「屏蔽音效」窗口内。</summary>
        internal static bool Suppress;

        /// <summary>安装音效抑制补丁。</summary>
        /// <param name="monitor">SMAPI 日志接口。</param>
        /// <param name="harmonyId">Harmony 实例 id（用模组的 UniqueID）。</param>
        /// <returns>补丁是否安装成功。</returns>
        public static bool TryInstall(IMonitor monitor, string harmonyId)
        {
            try
            {
                new Harmony(harmonyId).PatchAll(typeof(SoundSuppressor).Assembly);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log(
                    "音效抑制补丁安装失败：自动击杀瞬间仍会播放游戏原生怪物音效，其它功能（立即死亡、掉落、击杀计数）不受影响。"
                    + $"失败原因：{ex}",
                    LogLevel.Warn
                );
                return false;
            }
        }

        /// <summary>进入屏蔽音效窗口（必须与 <see cref="End"/> 成对调用）。</summary>
        public static void Begin()
        {
            Suppress = true;
        }

        /// <summary>退出屏蔽音效窗口。</summary>
        public static void End()
        {
            Suppress = false;
        }
    }

    /// <summary><c>GameLocation.playSound(string, Vector2?, int?, SoundContext)</c> 的前缀补丁：屏蔽窗口内跳过原方法。</summary>
    [HarmonyPatch(
        typeof(GameLocation),
        nameof(GameLocation.playSound),
        new[] { typeof(string), typeof(Vector2?), typeof(int?), typeof(SoundContext) }
    )]
    internal static class SuppressKillSoundPatch
    {
        /// <summary>返回 false 表示不执行原方法（即不播放这次音效）。</summary>
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !SoundSuppressor.Suppress;
        }
    }
}
