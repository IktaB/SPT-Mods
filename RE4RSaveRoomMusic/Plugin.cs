using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using EFT.Hideout;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace RE4RSaveRoomMusic;

[BepInPlugin(Guid, "RE4RSaveRoomMusic", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.ikta.srhideoutmusic.re4";
    private const string ClipName = "RE4RSaveRoomMusic";
    private static AudioClip replacement;
    private static ConfigEntry<int> musicVolume;
    private static ConfigEntry<bool> restartMusicOnReturn;
    private ConfigEntry<int> hideoutSoundsVolume;
    private HideoutMixer mixer;
    private static AudioSource activeBackground;
    private static float hideoutVolume;
    private Harmony harmony;

    private void Awake()
    {
        musicVolume = Config.Bind("Audio", "Music Volume", 75,
            new ConfigDescription("Save room music volume as a percentage of the game's hideout volume.",
                new AcceptableValueRange<int>(0, 100)));
        musicVolume.SettingChanged += OnVolumeChanged;
        hideoutSoundsVolume = Config.Bind("Audio", "Ambience", 50,
            new ConfigDescription("Volume of hideout ambience and station sounds. Footsteps are unchanged.",
                new AcceptableValueRange<int>(0, 100), new DisplayNameAttribute("Ambience Volume")));
        hideoutSoundsVolume.SettingChanged += OnHideoutSoundsVolumeChanged;
        restartMusicOnReturn = Config.Bind("Audio", "Restart Music on Return", false,
            "Restart the save-room track from the beginning when returning to the hideout from the menu.");
    }

    private void Start()
    {
        try
        {
            replacement = LoadEmbeddedAudio();
            harmony = new Harmony(Guid);
            mixer = new HideoutMixer(harmony, hideoutSoundsVolume, ApplyVolume, message => Logger.LogWarning(message));
            var target = AccessTools.DeclaredMethod(typeof(HideoutAudioBackground), "UpdateSounds");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(ReplaceBackground)));
            var transition = AccessTools.DeclaredMethod(typeof(GUISounds), "method_11");
            harmony.Patch(transition, prefix: new HarmonyMethod(typeof(Plugin), nameof(RestartOnHideoutEntry)));
            Logger.LogInfo($"Loaded embedded {ClipName} ({replacement.length:F2}s)");
        }
        catch (Exception exception)
        {
            harmony?.UnpatchSelf();
            Logger.LogError($"Could not initialize {ClipName}: {exception}");
        }
    }

    private static AudioClip LoadEmbeddedAudio()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ClipName + ".wav")
            ?? throw new InvalidDataException("The embedded audio resource is missing.");
        using var reader = new BinaryReader(stream);
        // Skip the RIFF/WAVE header and any metadata chunks.
        stream.Position = 12;
        int channels = 0, sampleRate = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var size = reader.ReadInt32();
            var next = stream.Position + size + (size & 1);
            if (size < 0 || next > stream.Length)
                break;
            if (id == 0x20746D66 && size >= 16) // fmt
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                stream.Position += 6;
                var bits = reader.ReadUInt16();
                if (format != 1 || bits != 16 || channels == 0 || sampleRate <= 0)
                    break;
            }
            else if (id == 0x61746164 && channels > 0 && size > 0 && size % (2 * channels) == 0) // data
            {
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = reader.ReadInt16() / 32768f;

                var clip = AudioClip.Create(ClipName, samples.Length / channels, channels, sampleRate, false);
                if (clip.SetData(samples, 0))
                    return clip;
                Destroy(clip);
                break;
            }
            stream.Position = next;
        }
        throw new InvalidDataException("Expected a 16-bit PCM WAV resource.");
    }

    private static bool ReplaceBackground(
        AudioSource ____hollowAudioSource,
        List<AudioSource> ____inhabitedAudioSources,
        float ____masterVolume,
        bool ____started)
    {
        if (replacement == null || ____hollowAudioSource == null)
            return true;

        foreach (var source in ____inhabitedAudioSources)
        {
            if (source != null && source != ____hollowAudioSource && source.isPlaying)
                source.Stop();
        }

        var background = ____hollowAudioSource;
        activeBackground = background;
        hideoutVolume = ____masterVolume;
        ApplyVolume();
        background.loop = true;
        if (background.clip != replacement)
        {
            background.Stop();
            background.clip = replacement;
        }

        if (____started && background.isActiveAndEnabled && !background.isPlaying)
            background.Play();

        return false;
    }

    private void OnVolumeChanged(object sender, EventArgs args) => ApplyVolume();
    private void OnHideoutSoundsVolumeChanged(object sender, EventArgs args) => mixer?.ApplySoundsVolume();

    private static void RestartOnHideoutEntry(GUISounds __instance, bool __0)
    {
        if (__0 && !__instance.HideoutSoundActive && restartMusicOnReturn.Value
            && activeBackground != null && activeBackground.clip == replacement)
        {
            activeBackground.timeSamples = 0;
        }
    }

    private static void ApplyVolume()
    {
        if (activeBackground != null)
        {
            var musicGroup = HideoutMixer.MusicGroup;
            if (musicGroup != null && activeBackground.outputAudioMixerGroup != musicGroup)
                activeBackground.outputAudioMixerGroup = musicGroup;
            activeBackground.volume = hideoutVolume * (musicVolume.Value / 100f) * HideoutMixer.MusicGain;
        }
    }

    private void OnDestroy()
    {
        musicVolume.SettingChanged -= OnVolumeChanged;
        hideoutSoundsVolume.SettingChanged -= OnHideoutSoundsVolumeChanged;
        harmony?.UnpatchSelf();
        if (replacement != null)
            Destroy(replacement);
    }
}
