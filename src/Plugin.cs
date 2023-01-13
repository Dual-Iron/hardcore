using BepInEx;
using Menu;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MoreSlugcats;
using Music;
using RWCustom;
using System;
using System.Security.Permissions;
using UnityEngine;

#pragma warning disable CS0618 // Type or member is obsolete
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace Hardcore;

[BepInPlugin("com.dual.hardcore", "Hardcore", "1.0.0")]
sealed class Plugin : BaseUnityPlugin
{
    private SlugcatStats.Name saveNum;

    // Utilities
    private static bool IsCurrentDead(SlugcatSelectMenu self)
    {
        return IsDead(self, self.slugcatPages[self.slugcatPageIndex].slugcatNumber);
    }
    private static bool IsDead(SlugcatSelectMenu self, SlugcatStats.Name character)
    {
        return self.saveGameData.TryGetValue(character, out var data) && data != null && data.redsDeath;
    }
    private static bool IsSlugcatImage(string fileName)
    {
        return fileName.ToLower() is "white slugcat - 2" or "yellow slugcat - 1"
            or "artificer slugcat - 1" or "artificer slugcat - 1 - dark"
            or "gourmand slugcat - 1" or "gourmand slugcat - 1 - dark"
            or "rivulet slugcat - 1" or "rivulet slugcat - 1b" or "rivulet slugcat - 1 - dark"
            or "saint slugcat - 1" or "saint slugcat - 1b" or "saint slugcat - 1 - dark"
            or "spearmaster slugcat - 1" or "spearmaster slugcat - 1 - dark";
    }
    private static void EndGamePermanently(RainWorldGame game)
    {
        if (game.manager.upcomingProcess != null || game.Players[0].realizedCreature is not Player p || game.session is not StoryGameSession sess) {
            return;
        }
        game.manager.musicPlayer?.FadeOutAllSongs(20f);
        p.redsIllness ??= new RedsIllness(p, -1);
        p.redsIllness.fadeOutSlow = true;
        sess.saveState.AppendCycleToStatistics(p, sess, true);
        sess.saveState.deathPersistentSaveData.redsDeath = true;
        game.manager.rainWorld.progression.SaveWorldStateAndProgression(false);
        game.manager.RequestMainProcessSwitch(ProcessManager.ProcessID.Statistics, 10f);
    }

    public void OnEnable()
    {
        On.RainWorld.Update += (o, s) => {
            try { o(s); }
            catch (Exception e) { Logger.LogError(e); }
        };

        // Use new save file to prevent overwriting vanilla
        new Hook(typeof(Options).GetMethod("get_SaveFileName"), GetterSaveFileName);

        // Prevent items from ever respawning
        On.RegionState.ReportConsumedItem += RegionState_ReportConsumedItem;

        // Always use minimum cycle time
        On.RainCycle.ctor += RainCycle_ctor;

        // Red karma meter
        On.HUD.KarmaMeter.Draw += KarmaMeter_Draw;

        // Fix game over behavior
        On.RainWorldGame.GameOver += RainWorldGame_GameOver;
        On.HUD.TextPrompt.Update += TextPrompt_Update;

        // Fix quitting and dying behavior
        On.DeathPersistentSaveData.SaveToString += DeathPersistentSaveData_SaveToString;
        On.Menu.PauseMenu.Update += PauseMenu_Update;

        // Fix saving permanent game overs
        new Hook(typeof(StoryGameSession).GetMethod("get_RedIsOutOfCycles"), GetterRedIsOutOfCycles);
        On.RedsIllness.RedsCycles += RedsIllness_RedsCycles;
        IL.Menu.SlugcatSelectMenu.MineForSaveData += SlugcatSelectMenu_MineForSaveData;

        // Compatibility.
        On.Menu.SlugcatSelectMenu.SlugcatPage.AddImage += SlugcatPage_AddImage;

        // Make all but the hunter disappear when they die on the select screen
        On.Menu.MenuDepthIllustration.GrafUpdate += MenuDepthIllustration_GrafUpdate;

        // Make button become "STATISTICS" button after a slugcat dies
        IL.Menu.SlugcatSelectMenu.StartGame += SlugcatSelectMenu_StartGame;
        On.Menu.SlugcatSelectMenu.UpdateStartButtonText += SlugcatSelectMenu_UpdateStartButtonText;
        On.Menu.StoryGameStatisticsScreen.GetDataFromGame += StoryGameStatisticsScreen_GetDataFromGame;
        On.Menu.StoryGameStatisticsScreen.CommunicateWithUpcomingProcess += StoryGameStatisticsScreen_CommunicateWithUpcomingProcess;
        On.Menu.StoryGameStatisticsScreen.AddBkgIllustration += StoryGameStatisticsScreen_AddBkgIllustration;
    }

    private static string GetterSaveFileName(Func<Options, string> orig, Options self)
    {
        return orig(self) + "_survival";
    }

    private void RegionState_ReportConsumedItem(On.RegionState.orig_ReportConsumedItem orig, RegionState self, int originRoom, int placedObjectIndex, int waitCycles)
    {
        orig(self, originRoom, placedObjectIndex, waitCycles: int.MaxValue);
    }

    private void RainCycle_ctor(On.RainCycle.orig_ctor orig, RainCycle self, World world, float minutes)
    {
        orig(self, world, world.game.rainWorld.setup.cycleTimeMin / 60f);
    }

    private void KarmaMeter_Draw(On.HUD.KarmaMeter.orig_Draw orig, HUD.KarmaMeter self, float timeStacker)
    {
        orig(self, timeStacker);

        if (self.showAsReinforced) {
            self.karmaSprite.color = Color.white;
            self.glowSprite.color = Color.white;
        }
        else {
            const float redness = 0.75f;
            const float brightness = 0.8f;

            Color color;

            color = self.karmaSprite.color;
            color.g = Mathf.Min(color.g, 1 - redness);
            color.b = Mathf.Min(color.b, 1 - redness);
            color.r = Mathf.Min(color.r, brightness);
            self.karmaSprite.color = color;

            color = self.glowSprite.color;
            color.g = Mathf.Min(color.g, 1 - redness);
            color.b = Mathf.Min(color.b, 1 - redness);
            color.r = Mathf.Min(color.r, brightness);
            self.glowSprite.color = color;
        }
    }

    private void RainWorldGame_GameOver(On.RainWorldGame.orig_GameOver orig, RainWorldGame self, Creature.Grasp dependentOnGrasp)
    {
        if (self.session is StoryGameSession sess) {
            if (dependentOnGrasp != null) {
                sess.PlaceKarmaFlowerOnDeathSpot();
                self.manager.musicPlayer?.DeathEvent();
            }
            else {
                EndGamePermanently(self);
            }
            return;
        }
        orig(self, dependentOnGrasp);
    }

    private void TextPrompt_Update(On.HUD.TextPrompt.orig_Update orig, HUD.TextPrompt self)
    {
        const string pretense = "Paused - Warning! Quitting now ";

        orig(self);
        if (self.currentlyShowing == HUD.TextPrompt.InfoID.Paused && !string.IsNullOrEmpty(self.label.text) && self.pausedWarningText) {
            if (self.hud.owner is Player player && player.abstractCreature.world.game.IsStorySession && player.abstractCreature.world.game.clock > 1200) {
                if (player.KarmaIsReinforced)
                    self.label.text = pretense + "will remove your karma flower";
                else if (player.Karma > 0)
                    self.label.text = pretense + "will reset your current karma";
                else
                    self.label.text = pretense + "will permanently end your game";
            }
            else {
                self.label.text = "Paused";
            }
        }
    }

    private string DeathPersistentSaveData_SaveToString(On.DeathPersistentSaveData.orig_SaveToString orig, DeathPersistentSaveData self, bool saveAsIfPlayerDied, bool saveAsIfPlayerQuit)
    {
        if (saveAsIfPlayerQuit) {
            if (saveAsIfPlayerDied) {
                if (self.reinforcedKarma) {
                    self.reinforcedKarma = false;
                    var ret = orig(self, false, false);
                    self.reinforcedKarma = true;
                    return ret;
                }
                if (self.karma > 0) {
                    var tempKarma = self.karma;
                    self.karma = 0;
                    var ret = orig(self, false, false);
                    self.karma = tempKarma;
                    return ret;
                }
                return orig(self, true, true);
            }
            return orig(self, false, false);
        }
        return orig(self, saveAsIfPlayerDied, saveAsIfPlayerQuit);
    }

    private void PauseMenu_Update(On.Menu.PauseMenu.orig_Update orig, PauseMenu self)
    {
        orig(self);

        // Prevent exiting if karma == 0
        if (self.game.IsStorySession && self.game.clock >= 200) {
            self.counter = 0;
        }
    }

    private static bool GetterRedIsOutOfCycles(Func<StoryGameSession, bool> orig, StoryGameSession self)
    {
        return orig(self) || !self.saveState.deathPersistentSaveData.reinforcedKarma;
    }

    private int RedsIllness_RedsCycles(On.RedsIllness.orig_RedsCycles orig, bool extraCycles)
    {
        if (Custom.rainWorld.progression.currentSaveState.deathPersistentSaveData.redsDeath) {
            return int.MinValue;
        }
        return orig(extraCycles);
    }

    private void SlugcatSelectMenu_MineForSaveData(ILContext il)
    {
        ILCursor cursor = new(il);

        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdsfld<SlugcatStats.Name>("Red"))) {
            Logger.LogError("MineForSaveData: Missing instruction 1");
            return;
        }

        // Skip `Name == Red` check to make game add a save data miner for redIsDead
        cursor.Index += 2;
        cursor.Emit(OpCodes.Pop);
        cursor.Emit(OpCodes.Ldc_I4_1);
    }

    // - Select screen -
    private void SlugcatPage_AddImage(On.Menu.SlugcatSelectMenu.SlugcatPage.orig_AddImage orig, SlugcatSelectMenu.SlugcatPage self, bool ascended)
    {
        if (self.menu is SlugcatSelectMenu ssm && IsDead(ssm, self.slugcatNumber)) {
            if (self.slugcatNumber == SlugcatStats.Name.Red)
                ssm.redIsDead = true;
            else if (self.slugcatNumber == MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                ssm.artificerIsDead = true;
            else if (self.slugcatNumber == MoreSlugcatsEnums.SlugcatStatsName.Saint)
                ssm.saintIsDead = true;
            else if (self.slugcatNumber != SlugcatStats.Name.White && self.slugcatNumber != SlugcatStats.Name.Yellow)
                ascended = true;
        }
        orig(self, ascended);
    }

    private void MenuDepthIllustration_GrafUpdate(On.Menu.MenuDepthIllustration.orig_GrafUpdate orig, MenuDepthIllustration self, float timeStacker)
    {
        orig(self, timeStacker);

        if (IsSlugcatImage(self.fileName) && self.menu is SlugcatSelectMenu ssm && self.owner.owner is SlugcatSelectMenu.SlugcatPage sp && IsDead(ssm, sp.slugcatNumber)) {
            self.sprite.scaleX = 0;
        }
    }

    private void SlugcatSelectMenu_StartGame(ILContext il)
    {
        ILCursor cursor = new(il);

        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdsfld<SlugcatStats.Name>("Red"))) {
            Logger.LogError("StartGame: Missing instruction 1");
            return;
        }

        cursor.Index--;
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate(IsDead);
        cursor.Emit(OpCodes.Brtrue, il.Instrs[il.Instrs.Count - 1]);

        static bool IsDead(SlugcatSelectMenu self, SlugcatStats.Name storyGameCharacter)
        {
            if (Plugin.IsDead(self, storyGameCharacter)) {
                self.redSaveState = self.manager.rainWorld.progression.GetOrInitiateSaveState(storyGameCharacter, null, self.manager.menuSetup, false);
                self.manager.RequestMainProcessSwitch(ProcessManager.ProcessID.Statistics);
                self.PlaySound(SoundID.MENU_Switch_Page_Out);
                if (self.manager.musicPlayer?.song is IntroRollMusic) {
                    self.manager.musicPlayer.song.FadeOut(20f);
                }
                return true;
            }
            return false;
        }
    }

    private void SlugcatSelectMenu_UpdateStartButtonText(On.Menu.SlugcatSelectMenu.orig_UpdateStartButtonText orig, SlugcatSelectMenu self)
    {
        orig(self);
        if (!self.restartChecked && IsCurrentDead(self)) {
            self.startButton.menuLabel.text = self.Translate("STATISTICS");
        }
    }

    private void StoryGameStatisticsScreen_GetDataFromGame(On.Menu.StoryGameStatisticsScreen.orig_GetDataFromGame orig, StoryGameStatisticsScreen self, KarmaLadderScreen.SleepDeathScreenDataPackage package)
    {
        orig(self, package);
        saveNum = package.saveState.saveStateNumber;
    }

    private void StoryGameStatisticsScreen_CommunicateWithUpcomingProcess(On.Menu.StoryGameStatisticsScreen.orig_CommunicateWithUpcomingProcess orig, StoryGameStatisticsScreen self, MainLoopProcess nextProcess)
    {
        orig(self, null);
        if (nextProcess is SlugcatSelectMenu ssm) {
            for (int i = 0; i < ssm.slugcatColorOrder.Count; i++) {
                if (ssm.slugcatColorOrder[i] == saveNum) {
                    ssm.slugcatPageIndex = i;
                    break;
                }
            }
            ssm.UpdateSelectedSlugcatInMiscProg();
        }
    }

    private void StoryGameStatisticsScreen_AddBkgIllustration(On.Menu.StoryGameStatisticsScreen.orig_AddBkgIllustration orig, StoryGameStatisticsScreen self)
    {
        SlugcatSelectMenu.SaveGameData saveGameData = SlugcatSelectMenu.MineForSaveData(self.manager, saveNum);
        if (saveGameData != null && saveGameData.ascended) {
            self.scene = new InteractiveMenuScene(self, self.pages[0], MenuScene.SceneID.Red_Ascend);
            self.pages[0].subObjects.Add(self.scene);
        }
        else {
            self.scene = new InteractiveMenuScene(self, self.pages[0], MenuScene.SceneID.RedsDeathStatisticsBkg);
            self.pages[0].subObjects.Add(self.scene);
        }
    }
}
