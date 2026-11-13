using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Comfort.Common;
using EFT.Hideout;
using EFT.Settings;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace RE4RSaveRoomMusic;

[BepInPlugin(Guid, "RE4RSaveRoomMusic", "1.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.ikta.srhideoutmusic.re4";
    private const string ClipName = "RE4RSaveRoomMusic";
    private const string Remake = "Remake";
    private const string Classic = "Classic";
    private const string SeparateWays = "Separate Ways";
    private static AudioClip replacement;
    private ConfigEntry<string> selectedTrack;
    private static ConfigEntry<int> musicVolume;
    private static ConfigEntry<bool> restartMusicOnReturn;
    private ConfigEntry<int> hideoutSoundsVolume;
    private HideoutMixer mixer;
    private static AudioSource activeBackground;
    private static bool useBackportPlayback;
    private static bool hideoutSoundActive;
    private static bool hasEnteredHideout;
    private static float returnFadeStart;
    private static float returnFadeDuration;
    private static float hideoutVolume;
    private Harmony harmony;

    private void Awake()
    {
        selectedTrack = Config.Bind("Audio", "Save Room Track", Remake,
            new ConfigDescription("Choose the save-room music to play in the hideout.",
                new AcceptableValueList<string>(Remake, Classic, SeparateWays), new DisplayNameAttribute("Track")));
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
            try
            {
                replacement = LoadEmbeddedAudio(selectedTrack.Value);
            }
            catch (Exception exception) when (selectedTrack.Value != Remake)
            {
                Logger.LogWarning($"Could not load {selectedTrack.Value}. Using Remake: {exception.Message}");
                replacement = LoadEmbeddedAudio(Remake);
            }
            useBackportPlayback = Chainloader.PluginInfos.ContainsKey("red.soundenginebackport");
            harmony = new Harmony(Guid);
            mixer = new HideoutMixer(harmony, hideoutSoundsVolume, ApplyVolume, message => Logger.LogWarning(message));
            var target = AccessTools.DeclaredMethod(typeof(HideoutAudioBackground), "UpdateSounds");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(ReplaceBackground)));
            var transition = AccessTools.DeclaredMethod(typeof(GUISounds), "method_11");
            harmony.Patch(transition, prefix: new HarmonyMethod(typeof(Plugin), nameof(RestartOnHideoutEntry)));
            if (useBackportPlayback)
                harmony.Patch(transition, postfix: new HarmonyMethod(typeof(Plugin), nameof(UpdateBackportPlayback)));
            selectedTrack.SettingChanged += OnTrackChanged;
            Logger.LogInfo($"Loaded {replacement.name} ({replacement.length:F2}s)");
        }
        catch (Exception exception)
        {
            harmony?.UnpatchSelf();
            Logger.LogError($"Could not initialize {ClipName}: {exception}");
        }
    }

    private static AudioClip LoadEmbeddedAudio(string track)
    {
        var resourceName = track switch
        {
            Classic => "Classic.wav",
            SeparateWays => "SeparateWays.wav",
            _ => ClipName + ".wav"
        };
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"The embedded audio resource {resourceName} is missing.");
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

                var clip = AudioClip.Create(track, samples.Length / channels, channels, sampleRate, false);
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
        if (useBackportPlayback)
        {
            if (background.isPlaying)
                background.Stop();
            if (activeBackground == null || activeBackground.gameObject != background.gameObject)
            {
                if (activeBackground != null)
                {
                    activeBackground.Stop();
                    Destroy(activeBackground);
                }
                activeBackground = background.gameObject.AddComponent<AudioSource>();
                activeBackground.playOnAwake = false;
                activeBackground.spatialBlend = 0f;
            }
            background = activeBackground;
        }
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

    private void OnTrackChanged(object sender, EventArgs args)
    {
        AudioClip next;
        try
        {
            next = LoadEmbeddedAudio(selectedTrack.Value);
        }
        catch (Exception exception)
        {
            Logger.LogError($"Could not switch to {selectedTrack.Value}: {exception}");
            return;
        }

        var previous = replacement;
        replacement = next;
        if (activeBackground != null)
        {
            var wasPlaying = activeBackground.isPlaying;
            activeBackground.Stop();
            activeBackground.clip = next;
            if (wasPlaying && activeBackground.isActiveAndEnabled)
                activeBackground.Play();
        }
        Destroy(previous);
    }

    private void Update()
    {
        if (returnFadeDuration <= 0f)
            return;

        if (Time.unscaledTime - returnFadeStart >= returnFadeDuration)
            returnFadeDuration = 0f;
        ApplyVolume();
    }

    private static void RestartOnHideoutEntry(GUISounds __instance, bool __0)
    {
        if (!__0)
        {
            returnFadeDuration = 0f;
            return;
        }
        if (__instance.HideoutSoundActive)
            return;

        if (hasEnteredHideout)
        {
            var restart = restartMusicOnReturn.Value;
            returnFadeStart = Time.unscaledTime;
            returnFadeDuration = restart ? 1f : 0.5f;
            ApplyVolume();
            if (restart && activeBackground != null && activeBackground.clip == replacement)
                activeBackground.timeSamples = 0;
        }
        hasEnteredHideout = true;
    }

    private static void UpdateBackportPlayback(bool __0)
    {
        hideoutSoundActive = __0;
        ApplyVolume();
    }

    private static void ApplyVolume()
    {
        if (activeBackground != null)
        {
            var musicGroup = HideoutMixer.MusicGroup;
            if (musicGroup != null && activeBackground.outputAudioMixerGroup != musicGroup)
                activeBackground.outputAudioMixerGroup = musicGroup;
            var gain = HideoutMixer.MusicGain;
            if (useBackportPlayback)
            {
                // SEB mirrors the hideout fade on its own mixer. Read the game setting directly.
                var decibels = hideoutSoundActive && Singleton<SettingsManager>.Instantiated
                    ? Singleton<SettingsManager>.Instance.Sound.Settings.HideoutVolumeValue
                    : -80;
                gain = decibels <= -80 ? 0f : Mathf.Pow(10f, decibels / 20f);
            }
            if (returnFadeDuration > 0f)
                gain *= Mathf.Clamp01((Time.unscaledTime - returnFadeStart) / returnFadeDuration);
            activeBackground.volume = hideoutVolume * (musicVolume.Value / 100f) * gain;
        }
    }

    private void OnDestroy()
    {
        selectedTrack.SettingChanged -= OnTrackChanged;
        musicVolume.SettingChanged -= OnVolumeChanged;
        hideoutSoundsVolume.SettingChanged -= OnHideoutSoundsVolumeChanged;
        harmony?.UnpatchSelf();
        if (useBackportPlayback && activeBackground != null)
            Destroy(activeBackground);
        if (replacement != null)
            Destroy(replacement);
    }
}
