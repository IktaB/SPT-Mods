using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using EFT.Settings.Sound;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace RE4RSaveRoomMusic;

internal sealed class HideoutMixer
{
    private const string HideoutParameter = "HideoutVolume";
    private const float Silent = -80f;
    private static HideoutMixer instance;
    private readonly ConfigEntry<int> soundsVolume;
    private readonly Action updateMusic;
    private readonly Action<string> warn;
    private AudioMixer gameMixer;
    private float nativeLevel = Silent;

    public static AudioMixerGroup MusicGroup => instance?.musicGroup;
    public static float MusicGain => MusicGroup == null ? 1f : DecibelsToGain(instance.nativeLevel);
    private AudioMixerGroup musicGroup;

    public HideoutMixer(Harmony harmony, ConfigEntry<int> soundsVolume, Action updateMusic, Action<string> warn)
    {
        this.soundsVolume = soundsVolume;
        this.updateMusic = updateMusic;
        this.warn = warn;
        instance = this;

        var audioTweens = Assembly.Load("DOTween.Modules").GetType("DG.Tweening.DOTweenModuleAudio", true);
        var floatTween = AccessTools.Inner(audioTweens, "<>c__DisplayClass2_0");
        harmony.Patch(AccessTools.DeclaredMethod(floatTween, "<DOSetFloat>b__0"),
            transpiler: new HarmonyMethod(typeof(HideoutMixer), nameof(ReplaceMixerCalls)));
        harmony.Patch(AccessTools.DeclaredMethod(floatTween, "<DOSetFloat>b__1"),
            transpiler: new HarmonyMethod(typeof(HideoutMixer), nameof(ReplaceMixerCalls)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(SoundSettingsController), "AdjustSoundValue"),
            transpiler: new HarmonyMethod(typeof(HideoutMixer), nameof(ReplaceMixerCalls)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(GUISounds), "method_11"),
            prefix: new HarmonyMethod(typeof(HideoutMixer), nameof(CaptureMixer)));
    }

    public void ApplySoundsVolume()
    {
        if (musicGroup != null)
            gameMixer.SetFloat(HideoutParameter, AdjustedLevel());
    }

    private float AdjustedLevel()
    {
        if (soundsVolume.Value == 0)
            return Silent;
        return Mathf.Max(Silent, nativeLevel + 20f * Mathf.Log10(soundsVolume.Value / 100f));
    }

    private static void CaptureMixer(GUISounds __instance)
    {
        var mixer = __instance.MasterMixer;
        if (mixer == null || mixer == instance.gameMixer)
            return;

        instance.gameMixer = mixer;
        instance.musicGroup = Array.Find(mixer.FindMatchingGroups("Master"), group => group.name == "Master");
        if (instance.musicGroup == null)
        {
            instance.warn("Master audio group not found. The Ambience slider is unavailable.");
            return;
        }

        if (!mixer.GetFloat(HideoutParameter, out instance.nativeLevel))
            instance.nativeLevel = Silent;
        instance.updateMusic();
    }

    private static float DecibelsToGain(float level) =>
        level <= Silent ? 0f : Mathf.Pow(10f, level / 20f);

    private static bool ReadLevel(AudioMixer mixer, string parameter, out float level)
    {
        if (parameter != HideoutParameter || mixer != instance.gameMixer || MusicGroup == null)
            return mixer.GetFloat(parameter, out level);

        // Native fades must start from the unscaled value even when the mod slider is at zero.
        level = instance.nativeLevel;
        return true;
    }

    private static bool WriteLevel(AudioMixer mixer, string parameter, float level)
    {
        if (parameter != HideoutParameter || mixer != instance.gameMixer || MusicGroup == null)
            return mixer.SetFloat(parameter, level);

        instance.nativeLevel = level;
        instance.updateMusic();
        return mixer.SetFloat(parameter, instance.AdjustedLevel());
    }

    private static IEnumerable<CodeInstruction> ReplaceMixerCalls(IEnumerable<CodeInstruction> instructions)
    {
        var get = AccessTools.DeclaredMethod(typeof(AudioMixer), nameof(AudioMixer.GetFloat));
        var set = AccessTools.DeclaredMethod(typeof(AudioMixer), nameof(AudioMixer.SetFloat));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(get))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(HideoutMixer), nameof(ReadLevel));
            }
            else if (instruction.Calls(set))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(HideoutMixer), nameof(WriteLevel));
            }
            yield return instruction;
        }
    }
}
