using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace Jumius.VRCLightmapMixer
{
    [AddComponentMenu("Jumius/VRCLightmapMixer/Lightmap Mixer Slider Controller")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class LightmapMixerSliderController : UdonSharpBehaviour
    {
        public LightmapMixer lightmapMixer;
        public Slider mixASlider;
        public Slider mixBSlider;
        public bool initializeSlidersFromMixer = true;
        public bool applyOnStart = true;

        [UdonSynced]
        private float _syncedMixA = 1f;
        [UdonSynced]
        private float _syncedMixB = 1f;

        public void Start()
        {
            if (Networking.IsOwner(gameObject))
            {
                if (initializeSlidersFromMixer && lightmapMixer != null)
                {
                    _syncedMixA = Mathf.Clamp01(lightmapMixer.runtimeMixA);
                    _syncedMixB = Mathf.Clamp01(lightmapMixer.runtimeMixB);
                }
                else
                {
                    _syncedMixA = GetSliderValue(mixASlider, _syncedMixA);
                    _syncedMixB = GetSliderValue(mixBSlider, _syncedMixB);
                }

                ApplySyncedValuesToSliders();

                if (applyOnStart)
                {
                    ApplySyncedValuesToMixer();
                    RequestSerialization();
                }

                return;
            }

            if (initializeSlidersFromMixer && lightmapMixer != null)
            {
                SetSliderValueWithoutNotify(mixASlider, lightmapMixer.runtimeMixA);
                SetSliderValueWithoutNotify(mixBSlider, lightmapMixer.runtimeMixB);
            }
        }

        public void SyncMixerFromSliders()
        {
            if (lightmapMixer == null)
            {
                return;
            }

            float mixA = GetSliderValue(mixASlider, lightmapMixer.runtimeMixA);
            float mixB = GetSliderValue(mixBSlider, lightmapMixer.runtimeMixB);
            SetSyncedMix(mixA, mixB);
        }

        public void SyncSlidersFromMixer()
        {
            if (lightmapMixer == null)
            {
                return;
            }

            SetSliderValueWithoutNotify(mixASlider, lightmapMixer.runtimeMixA);
            SetSliderValueWithoutNotify(mixBSlider, lightmapMixer.runtimeMixB);
        }

        public void SetMixAValue(float value)
        {
            SetSyncedMix(value, GetSliderValue(mixBSlider, _syncedMixB));
        }

        public void SetMixBValue(float value)
        {
            SetSyncedMix(GetSliderValue(mixASlider, _syncedMixA), value);
        }

        public void OnMixASliderChanged()
        {
            SetMixAValue(GetSliderValue(mixASlider, _syncedMixA));
        }

        public void OnMixBSliderChanged()
        {
            SetMixBValue(GetSliderValue(mixBSlider, _syncedMixB));
        }

        public override void OnDeserialization()
        {
            ApplySyncedValuesToMixer();
            ApplySyncedValuesToSliders();
        }

        private void SetSyncedMix(float mixA, float mixB)
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            _syncedMixA = Mathf.Clamp01(mixA);
            _syncedMixB = Mathf.Clamp01(mixB);

            ApplySyncedValuesToMixer();
            ApplySyncedValuesToSliders();
            RequestSerialization();
        }

        private void ApplySyncedValuesToMixer()
        {
            if (lightmapMixer == null)
            {
                return;
            }

            lightmapMixer.SetRuntimeMix(lightmapMixer.runtimeMixUse, _syncedMixA, _syncedMixB);
        }

        private void ApplySyncedValuesToSliders()
        {
            SetSliderValueWithoutNotify(mixASlider, _syncedMixA);
            SetSliderValueWithoutNotify(mixBSlider, _syncedMixB);
        }

        private float GetSliderValue(Slider slider, float fallback)
        {
            if (slider == null)
            {
                return Mathf.Clamp01(fallback);
            }

            return Mathf.Clamp01(slider.value);
        }

        private void SetSliderValueWithoutNotify(Slider slider, float value)
        {
            if (slider == null)
            {
                return;
            }

            slider.SetValueWithoutNotify(Mathf.Clamp01(value));
        }
    }
}
