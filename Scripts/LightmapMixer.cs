using UdonSharp;
using VRC.SDKBase;
using UnityEngine;

namespace Jumius.VRCLightmapMixer
{
    [AddComponentMenu("Jumius/VRCLightmapMixer/Lightmap Mixer")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class LightmapMixer : UdonSharpBehaviour
    {
        private const string LightMapAProperty = "_LightMapA";
        private const string LightMapBProperty = "_LightMapB";
        private const string MixUseProperty = "_UdonMultiLightmapGlobalMixUse";
        private const string MixAProperty = "_UdonMultiLightmapGlobalMixA";
        private const string MixBProperty = "_UdonMultiLightmapGlobalMixB";

        [Header("Replacement")]
        public Shader replacementShader;
        public Texture2D[] lightMapATextures;
        public Texture2D[] lightMapBTextures;

        [Header("Targets")]
        public bool autoCollectRenderersBeforeBake = true;
        public Renderer[] targetRenderers;
        [HideInInspector]
        public int[] targetLightmapIndices;

        [Header("Editor Bake")]
        public GameObject[] activeObjectsForLightmapA;
        public GameObject[] activeObjectsForLightmapB;
        public GameObject[] activeReflectionProbesForLightmapA;
        public GameObject[] activeReflectionProbesForLightmapB;
        public string lightmapAOutputFolder = "Assets/BakeryLightmaps/LightmapA";
        public string lightmapBOutputFolder = "Assets/BakeryLightmaps/LightmapB";
        public bool renderLightProbesAfterLightmaps = true;
        public bool renderReflectionProbesAfterLightmaps = true;
        public bool restoreObjectStatesAfterRendering = true;
        public Material[] emissiveMaterialsForLightmapA;
        public Material[] emissiveMaterialsForLightmapB;
        [HideInInspector]
        public Material[] bakeMaterialParameterMaterials;
        [HideInInspector]
        public string[] bakeMaterialParameterNames;
        [HideInInspector]
        public float[] bakeMaterialParameterValuesForLightmapA;
        [HideInInspector]
        public float[] bakeMaterialParameterValuesForLightmapB;

        [Header("Runtime")]
        public bool applyOnStart = true;
        public bool skipWhenLightmapTextureIsMissing = true;
        public bool updateRuntimeMix = true;
        [Range(0f, 1f)]
        public float runtimeMixUse = 1f;
        [Range(0f, 1f)]
        public float runtimeMixA = 1f;
        [Range(0f, 1f)]
        public float runtimeMixB = 1f;
        [Range(0.01f, 1f)]
        public float runtimeMixStep = 0.1f;
        public bool logRuntimeMixChanges;

        private float _lastRuntimeMixUse = -1f;
        private float _lastRuntimeMixA = -1f;
        private float _lastRuntimeMixB = -1f;
        private Material[] _runtimeEmissiveMaterialsForLightmapA;
        private Material[] _runtimeEmissiveMaterialsForLightmapB;
        private Color[] _runtimeEmissiveBaseColorsForLightmapA;
        private Color[] _runtimeEmissiveBaseColorsForLightmapB;
        private int _runtimeEmissiveMaterialCountA;
        private int _runtimeEmissiveMaterialCountB;
        private bool _runtimeApplyInitialized;
        private bool _runtimeApplyInProgress;

        public void Start()
        {
            EnsureRuntimeApplyInitialized();
            ApplyRuntimeMix();
        }

        public void Update()
        {
            EnsureRuntimeApplyInitialized();

            if (updateRuntimeMix)
            {
                ApplyRuntimeMixIfChanged();
            }

            ApplyRuntimeReflectionProbeIntensities();
            ApplyRuntimeEmissiveMaterials();
        }

        public void Apply()
        {
            _runtimeApplyInitialized = true;

            if (replacementShader == null || targetRenderers == null)
            {
                return;
            }

            PrepareRuntimeEmissiveMaterialTargets(CountTargetMaterialSlots());

            for (int i = 0; i < targetRenderers.Length; i++)
            {
                Renderer targetRenderer = targetRenderers[i];
                if (targetRenderer == null)
                {
                    continue;
                }

                int lightmapIndex = GetStoredLightmapIndex(i);
                if (lightmapIndex < 0)
                {
                    continue;
                }

                Texture2D lightMapA = GetLightmapTexture(lightMapATextures, lightmapIndex);
                Texture2D lightMapB = GetLightmapTexture(lightMapBTextures, lightmapIndex);
                if (skipWhenLightmapTextureIsMissing && (lightMapA == null || lightMapB == null))
                {
                    continue;
                }

                Material[] sourceMaterials = targetRenderer.sharedMaterials;
                Material[] runtimeMaterials = targetRenderer.materials;
                if (runtimeMaterials == null)
                {
                    continue;
                }

                for (int materialIndex = 0; materialIndex < runtimeMaterials.Length; materialIndex++)
                {
                    Material material = runtimeMaterials[materialIndex];
                    if (material == null)
                    {
                        continue;
                    }

                    Material sourceMaterial = GetMaterialAt(sourceMaterials, materialIndex);
                    RegisterRuntimeEmissiveMaterial(material, sourceMaterial);

                    material.shader = replacementShader;
                    if (lightMapA != null)
                    {
                        material.SetTexture(LightMapAProperty, lightMapA);
                    }
                    if (lightMapB != null)
                    {
                        material.SetTexture(LightMapBProperty, lightMapB);
                    }
                }

                targetRenderer.materials = runtimeMaterials;
            }

            ApplyRuntimeEmissiveMaterials();
        }

        private void EnsureRuntimeApplyInitialized()
        {
            if (_runtimeApplyInitialized || _runtimeApplyInProgress || !applyOnStart)
            {
                return;
            }

            _runtimeApplyInProgress = true;
            Apply();
            _runtimeApplyInProgress = false;
        }

        private Material GetMaterialAt(Material[] materials, int index)
        {
            if (materials == null || index < 0 || index >= materials.Length)
            {
                return null;
            }

            return materials[index];
        }

        private int CountTargetMaterialSlots()
        {
            if (targetRenderers == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < targetRenderers.Length; i++)
            {
                Renderer targetRenderer = targetRenderers[i];
                if (targetRenderer == null)
                {
                    continue;
                }

                Material[] materials = targetRenderer.sharedMaterials;
                if (materials != null)
                {
                    count += materials.Length;
                }
            }

            return count;
        }

        private void PrepareRuntimeEmissiveMaterialTargets(int materialSlotCount)
        {
            _runtimeEmissiveMaterialsForLightmapA = new Material[materialSlotCount];
            _runtimeEmissiveMaterialsForLightmapB = new Material[materialSlotCount];
            _runtimeEmissiveBaseColorsForLightmapA = new Color[materialSlotCount];
            _runtimeEmissiveBaseColorsForLightmapB = new Color[materialSlotCount];
            _runtimeEmissiveMaterialCountA = 0;
            _runtimeEmissiveMaterialCountB = 0;
        }

        private void RegisterRuntimeEmissiveMaterial(Material runtimeMaterial, Material sourceMaterial)
        {
            RegisterRuntimeEmissiveMaterial(
                runtimeMaterial,
                sourceMaterial,
                emissiveMaterialsForLightmapA,
                true);

            RegisterRuntimeEmissiveMaterial(
                runtimeMaterial,
                sourceMaterial,
                emissiveMaterialsForLightmapB,
                false);
        }

        private void RegisterRuntimeEmissiveMaterial(
            Material runtimeMaterial,
            Material sourceMaterial,
            Material[] sourceMaterials,
            bool forLightmapA)
        {
            if (runtimeMaterial == null || sourceMaterial == null || !runtimeMaterial.HasProperty("_EmissionColor"))
            {
                return;
            }

            if (FindMaterialIndex(sourceMaterial, sourceMaterials) < 0)
            {
                return;
            }

            Color baseColor = runtimeMaterial.GetColor("_EmissionColor");

            if (forLightmapA)
            {
                AddRuntimeEmissiveMaterialForLightmapA(runtimeMaterial, baseColor);
            }
            else
            {
                AddRuntimeEmissiveMaterialForLightmapB(runtimeMaterial, baseColor);
            }
        }

        private int FindMaterialIndex(Material material, Material[] materials)
        {
            if (material == null || materials == null)
            {
                return -1;
            }

            for (int i = 0; i < materials.Length; i++)
            {
                if (material == materials[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private void AddRuntimeEmissiveMaterialForLightmapA(Material material, Color baseColor)
        {
            if (_runtimeEmissiveMaterialsForLightmapA == null ||
                _runtimeEmissiveBaseColorsForLightmapA == null ||
                _runtimeEmissiveMaterialCountA >= _runtimeEmissiveMaterialsForLightmapA.Length ||
                _runtimeEmissiveMaterialCountA >= _runtimeEmissiveBaseColorsForLightmapA.Length)
            {
                return;
            }

            _runtimeEmissiveMaterialsForLightmapA[_runtimeEmissiveMaterialCountA] = material;
            _runtimeEmissiveBaseColorsForLightmapA[_runtimeEmissiveMaterialCountA] = baseColor;
            _runtimeEmissiveMaterialCountA++;
        }

        private void AddRuntimeEmissiveMaterialForLightmapB(Material material, Color baseColor)
        {
            if (_runtimeEmissiveMaterialsForLightmapB == null ||
                _runtimeEmissiveBaseColorsForLightmapB == null ||
                _runtimeEmissiveMaterialCountB >= _runtimeEmissiveMaterialsForLightmapB.Length ||
                _runtimeEmissiveMaterialCountB >= _runtimeEmissiveBaseColorsForLightmapB.Length)
            {
                return;
            }

            _runtimeEmissiveMaterialsForLightmapB[_runtimeEmissiveMaterialCountB] = material;
            _runtimeEmissiveBaseColorsForLightmapB[_runtimeEmissiveMaterialCountB] = baseColor;
            _runtimeEmissiveMaterialCountB++;
        }

        public void EnableMixedLightmap()
        {
            SetRuntimeMix(1f, 1f, 1f);
        }

        public void EnableLightmapA()
        {
            SetRuntimeMix(1f, 1f, 0f);
        }

        public void EnableLightmapB()
        {
            SetRuntimeMix(1f, 0f, 1f);
        }

        public void DisableAddedLightmaps()
        {
            SetRuntimeMix(0f, 0f, 0f);
        }

        public void ApplyRuntimeMix()
        {
            EnsureRuntimeApplyInitialized();
            SetMix(runtimeMixUse, runtimeMixA, runtimeMixB);
            ApplyRuntimeReflectionProbeIntensities();
            ApplyRuntimeEmissiveMaterials();
            StoreLastRuntimeMix();
        }

        public void ApplyRuntimeReflectionProbeIntensities()
        {
            EnsureRuntimeApplyInitialized();
            SetReflectionProbeIntensities(activeReflectionProbesForLightmapA, runtimeMixA);
            SetReflectionProbeIntensities(activeReflectionProbesForLightmapB, runtimeMixB);
        }

        public void ApplyRuntimeEmissiveMaterials()
        {
            EnsureRuntimeApplyInitialized();
            ApplyRuntimeEmissiveMaterials(_runtimeEmissiveMaterialsForLightmapA, _runtimeEmissiveBaseColorsForLightmapA, _runtimeEmissiveMaterialCountA, runtimeMixA);
            ApplyRuntimeEmissiveMaterials(_runtimeEmissiveMaterialsForLightmapB, _runtimeEmissiveBaseColorsForLightmapB, _runtimeEmissiveMaterialCountB, runtimeMixB);
        }

        public void SetRuntimeMix(float use, float a, float b)
        {
            runtimeMixUse = Clamp01(use);
            runtimeMixA = Clamp01(a);
            runtimeMixB = Clamp01(b);
            ApplyRuntimeMix();
        }

        public void SetRuntimeMixUse(float value)
        {
            runtimeMixUse = Clamp01(value);
            ApplyRuntimeMix();
        }

        public void SetRuntimeMixA(float value)
        {
            runtimeMixA = Clamp01(value);
            ApplyRuntimeMix();
        }

        public void SetRuntimeMixB(float value)
        {
            runtimeMixB = Clamp01(value);
            ApplyRuntimeMix();
        }

        public void IncreaseRuntimeMixA()
        {
            SetRuntimeMixA(runtimeMixA + runtimeMixStep);
        }

        public void DecreaseRuntimeMixA()
        {
            SetRuntimeMixA(runtimeMixA - runtimeMixStep);
        }

        public void IncreaseRuntimeMixB()
        {
            SetRuntimeMixB(runtimeMixB + runtimeMixStep);
        }

        public void DecreaseRuntimeMixB()
        {
            SetRuntimeMixB(runtimeMixB - runtimeMixStep);
        }

        public void SetMix(float use, float a, float b)
        {
            int shaderMixUseId = VRCShader.PropertyToID(MixUseProperty);
            int shaderMixAId = VRCShader.PropertyToID(MixAProperty);
            int shaderMixBId = VRCShader.PropertyToID(MixBProperty);
            VRCShader.SetGlobalFloat(shaderMixUseId, use);
            VRCShader.SetGlobalFloat(shaderMixAId, a);
            VRCShader.SetGlobalFloat(shaderMixBId, b);

            if (logRuntimeMixChanges)
            {
                Debug.Log("[VRCLightmapMixer] Set global mix: use=" + use + ", a=" + a + ", b=" + b);
            }
        }

        private void ApplyRuntimeMixIfChanged()
        {
            runtimeMixUse = Clamp01(runtimeMixUse);
            runtimeMixA = Clamp01(runtimeMixA);
            runtimeMixB = Clamp01(runtimeMixB);

            if (runtimeMixUse == _lastRuntimeMixUse &&
                runtimeMixA == _lastRuntimeMixA &&
                runtimeMixB == _lastRuntimeMixB)
            {
                return;
            }

            ApplyRuntimeMix();
        }

        private void StoreLastRuntimeMix()
        {
            _lastRuntimeMixUse = runtimeMixUse;
            _lastRuntimeMixA = runtimeMixA;
            _lastRuntimeMixB = runtimeMixB;
        }

        private void ApplyRuntimeEmissiveMaterials(Material[] materials, Color[] colors, int count, float multiplier)
        {
            if (materials == null || colors == null)
            {
                return;
            }

            if (count > materials.Length)
            {
                count = materials.Length;
            }

            if (count > colors.Length)
            {
                count = colors.Length;
            }

            for (int i = 0; i < count; i++)
            {
                Material material = materials[i];
                if (material == null || !material.HasProperty("_EmissionColor"))
                {
                    continue;
                }

                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", colors[i] * multiplier);
            }
        }

        private void SetReflectionProbeIntensities(GameObject[] reflectionProbeObjects, float intensity)
        {
            if (reflectionProbeObjects == null)
            {
                return;
            }

            for (int i = 0; i < reflectionProbeObjects.Length; i++)
            {
                if (reflectionProbeObjects[i] == null)
                {
                    continue;
                }

                ReflectionProbe reflectionProbe = reflectionProbeObjects[i].GetComponent<ReflectionProbe>();
                if (reflectionProbe != null)
                {
                    reflectionProbe.intensity = intensity;
                }
            }
        }

        private float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }

        private int GetStoredLightmapIndex(int targetIndex)
        {
            if (targetLightmapIndices == null || targetIndex < 0 || targetIndex >= targetLightmapIndices.Length)
            {
                return -1;
            }

            return targetLightmapIndices[targetIndex];
        }

        private Texture2D GetLightmapTexture(Texture2D[] textures, int lightmapIndex)
        {
            if (textures == null || lightmapIndex < 0 || lightmapIndex >= textures.Length)
            {
                return null;
            }

            return textures[lightmapIndex];
        }
    }
}

