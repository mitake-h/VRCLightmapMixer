using UnityEditor;
using UnityEngine;
using System.Collections;
using System.IO;

public class LightmapMaterialReplacer : EditorWindow {
    public Shader replacementShader;
    public Texture2D[] lightMapATextures;
    public Texture2D[] lightMapBTextures;
    public bool isTestMode;

    [MenuItem("Tools/Lightmap Material Replacer")]
    public static void ShowWindow() {
        GetWindow<LightmapMaterialReplacer>("Lightmap Material Replacer");
    }

    private void OnGUI() {
        GUILayout.Label("Replacement Settings", EditorStyles.boldLabel);
        replacementShader = EditorGUILayout.ObjectField("Replacement Shader", replacementShader, typeof(Shader), false) as Shader;

        GUILayout.Label("Light Map A Textures", EditorStyles.boldLabel);
        int lightMapACount = EditorGUILayout.IntField("Count", lightMapATextures != null ? lightMapATextures.Length : 0);
        if (lightMapATextures == null || lightMapATextures.Length != lightMapACount) {
            lightMapATextures = new Texture2D[lightMapACount];
        }
        for (int i = 0; i < lightMapACount; i++) {
            lightMapATextures[i] = EditorGUILayout.ObjectField($"Texture A {i}", lightMapATextures[i], typeof(Texture2D), false) as Texture2D;
        }

        GUILayout.Label("Light Map B Textures", EditorStyles.boldLabel);
        int lightMapBCount = EditorGUILayout.IntField("Count", lightMapBTextures != null ? lightMapBTextures.Length : 0);
        if (lightMapBTextures == null || lightMapBTextures.Length != lightMapBCount) {
            lightMapBTextures = new Texture2D[lightMapBCount];
        }
        for (int i = 0; i < lightMapBCount; i++) {
            lightMapBTextures[i] = EditorGUILayout.ObjectField($"Texture B {i}", lightMapBTextures[i], typeof(Texture2D), false) as Texture2D;
        }

        isTestMode = EditorGUILayout.Toggle("Test Mode", isTestMode);

        if (GUILayout.Button("Replace Materials")) {
            ReplaceMaterials();
        }

        if (GUILayout.Button("Enable Mixed Lightmap")) {
            SetBrightness(1, 1, 1);
        }
        if (GUILayout.Button("Enable Mixed Lightmap A")) {
            SetBrightness(1, 1, 0);
        }
        if (GUILayout.Button("Enable Mixed Lightmap B")) {
            SetBrightness(1, 0, 1);
        }
        if (GUILayout.Button("Enable Mixed Lightmap but A,B = 0,0")) {
            SetBrightness(1, 0, 0);
        }
        if (GUILayout.Button("Disable Mixed Lightmap")) {
            SetBrightness(0, 0, 0);
        }

        if (GUILayout.Button("Render Lightmap")) {
            RenderLightmap();
        }
    }

    private void SetBrightness(float use, float a, float b) {
        Shader.SetGlobalFloat("_UdonMultiLightmapGlobalMixUse", use);
        Shader.SetGlobalFloat("_UdonMultiLightmapGlobalMixA", a);
        Shader.SetGlobalFloat("_UdonMultiLightmapGlobalMixB", b);
    }

    private void ReplaceMaterials() {
        GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
        foreach (GameObject obj in allObjects) {
            if (obj.isStatic) {
                ProcessRenderer(obj.GetComponent<Renderer>(), obj);
            }
        }

        if (!isTestMode) {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    private void ProcessRenderer(Renderer renderer, GameObject obj) {
        if (renderer == null) return;

        var materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++) {
            Material originalMaterial = materials[i];

            if (originalMaterial == null || originalMaterial.name == "Default-Material" || renderer.lightmapIndex < 0) {
                if (isTestMode) {
                    Debug.Log($"Skipping null or default material on object: {obj.name}");
                }
                continue;
            }

            string newMaterialName = "LM" + renderer.lightmapIndex + "_" + originalMaterial.name;
            if (originalMaterial.name.StartsWith("LM")) {
                newMaterialName = "LM" + renderer.lightmapIndex + "_" + originalMaterial.name.Substring(4);
            }

            {
                string originalMaterialPath = AssetDatabase.GetAssetPath(originalMaterial);
                string newMaterialPath = System.IO.Path.GetDirectoryName(originalMaterialPath) + "/" + newMaterialName + ".mat";

                Material newMaterial = AssetDatabase.LoadAssetAtPath<Material>(newMaterialPath);
                if (newMaterial == null) {
                    if (isTestMode) {
                        Debug.Log($"Would create new material: {newMaterialPath}");
                        Debug.Log($"Would set shader: {replacementShader.name}");
                    } else {
                        newMaterial = new Material(originalMaterial);
                        newMaterial.shader = replacementShader;
                        AssetDatabase.CreateAsset(newMaterial, newMaterialPath);
                    }
                }

                Debug.Log($"{obj.name}.mat[{i}] -> {newMaterialPath}");
                if (!isTestMode) {
                    materials[i] = newMaterial;
                }
            }

            int lightmapIndex = renderer.lightmapIndex;
            if (lightmapIndex >= 0 && lightmapIndex < lightMapATextures.Length && lightmapIndex < lightMapBTextures.Length) {
                if (isTestMode) {
                    Debug.Log($"Would set _LightMapA to texture: {lightMapATextures[lightmapIndex].name}");
                    Debug.Log($"Would set _LightMapB to texture: {lightMapBTextures[lightmapIndex].name}");
                } else {
                    materials[i].SetTexture("_LightMapA", lightMapATextures[lightmapIndex]);
                    materials[i].SetTexture("_LightMapB", lightMapBTextures[lightmapIndex]);
                }
            }
        }

        if (!isTestMode) {
            renderer.sharedMaterials = materials;
        }
    }

    // ----- ----- ----- ----- ----- ----- ----- ----- ----- ----- ----- ----- ----- ----- -----
    // ぷらね邸の一括ベイク

    private void RenderLightmap() {
        Debug.Log("--STEP1--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));

        // - Lightings/Baked/EnableOnBakeをオン
        GameObject.Find("Lightings/Baked/EnableOnBake").SetActive(true);

        // - RoomLightsをオフ、DomeLightsをオン
        GameObject.Find("Lightings/Baked/RoomLights").SetActive(false);
        GameObject.Find("Lightings/Baked/DomeLights").SetActive(true);

        // - 3_DomeScreenの照明を1に
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightWhite", 1.0f);
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightBlue", 0.0f);

        // - 部屋照明のエミッションを0に
        /*
        var roomLightEmissiveMaterials = FindObjectOfType<MyPlanetariumController>().roomLightEmissiveMaterials;
        foreach (var mat in roomLightEmissiveMaterials) {
            mat.EnableKeyword("_EMISSION");
            Color emissionColor = mat.GetColor("_EmissionColorSaved");
            mat.SetColor("_EmissionColor", emissionColor * 0);
        }
        */

        // - ベイク実行
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        ftRenderLightmap.OnFinishedFullRender += RenderLightmapDomeFinished;
        bakeryRender.RenderButton();
        // （続く）
    }
    private void RenderLightmapDomeFinished(object sender, System.EventArgs e) {
        Debug.Log("--STEP2--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));
        ftRenderLightmap.OnFinishedFullRender -= RenderLightmapDomeFinished;

        //  →　生成されたライトマップをLightmap_DomeLightに移動
        {
            string pathFrom = Application.dataPath + "/BakeryLightmaps/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "_LM0_final.hdr";
            string pathTo = Application.dataPath + "/BakeryLightmaps/LightMap_DomeLight/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "_LM0_final.hdr";
            var fiFrom = new System.IO.FileInfo(pathFrom);
            var fiTo = new System.IO.FileInfo(pathTo);
            if (fiFrom.Exists) {
                if (fiTo.Exists) {
                    fiTo.Delete();
                }
                fiFrom.MoveTo(pathTo);
            } else {
                Debug.Log("Lightmap doesn't exist");
            }
        }

        {
            string pathFrom = Application.dataPath + "/Scenes/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "/LightVolumeAtlas.asset";
            string pathTo = Application.dataPath + "/BakeryLightmaps/LightMap_DomeLight/LightVolumeAtlas.asset";
            var fiFrom = new System.IO.FileInfo(pathFrom);
            var fiTo = new System.IO.FileInfo(pathTo);
            if (fiFrom.Exists) {
                if (fiTo.Exists) {
                    fiTo.Delete();
                }
                fiFrom.MoveTo(pathTo);
            } else {
                Debug.Log("Lightvolume doesn't exist");
            }
        }

        // - DomeLightsをオフ、RoomLightsをオン
        GameObject.Find("Lightings/Baked/RoomLights").SetActive(true);
        GameObject.Find("Lightings/Baked/DomeLights").SetActive(false);

        // - 部屋照明のエミッションを1に
        /*
        var roomLightEmissiveMaterials = FindObjectOfType<MyPlanetariumController>().roomLightEmissiveMaterials;
        foreach (var mat in roomLightEmissiveMaterials) {
            mat.EnableKeyword("_EMISSION");
            Color emissionColor = mat.GetColor("_EmissionColorSaved");
            mat.SetColor("_EmissionColor", emissionColor * 1);
        }
        */

        // - ベイク実行
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        ftRenderLightmap.OnFinishedFullRender += RenderLightmapRoomFinished;
        bakeryRender.RenderButton();
    }

    private void RenderLightmapRoomFinished(object sender, System.EventArgs e) {
        Debug.Log("--STEP3--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));
        ftRenderLightmap.OnFinishedFullRender -= RenderLightmapRoomFinished;

        // →　生成されたライトマップをLightmap_RoomLightに移動
        {
            string pathFrom = Application.dataPath + "/BakeryLightmaps/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "_LM0_final.hdr";
            string pathTo = Application.dataPath + "/BakeryLightmaps/LightMap_RoomLight/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "_LM0_final.hdr";
            var fiFrom = new System.IO.FileInfo(pathFrom);
            var fiTo = new System.IO.FileInfo(pathTo);
            if (fiFrom.Exists) {
                if (fiTo.Exists) { fiTo.Delete(); }
                fiFrom.MoveTo(pathTo);
            } else {
                Debug.Log("Lightmap doesn't exist");
            }
        }

        {
            string pathFrom = Application.dataPath + "/Scenes/" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "/LightVolumeAtlas.asset";
            string pathTo = Application.dataPath + "/BakeryLightmaps/LightMap_RoomLight/LightVolumeAtlas.asset";
            var fiFrom = new System.IO.FileInfo(pathFrom);
            var fiTo = new System.IO.FileInfo(pathTo);
            if (fiFrom.Exists) {
                if (fiTo.Exists) {
                    fiTo.Delete();
                }
                fiFrom.MoveTo(pathTo);
            } else {
                Debug.Log("Lightvolume doesn't exist");
            }
        }

        // ライトプローブのベイク
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        ftRenderLightmap.OnFinishedFullRender += RenderLightprobeFinished;
        bakeryRender.RenderLightProbesButton();
        // （続く）
    }

    private void RenderLightprobeFinished(object sender, System.EventArgs e) {
        Debug.Log("--STEP4--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));
        ftRenderLightmap.OnFinishedFullRender -= RenderLightprobeFinished;

        // ----- ----- ----- ----- ----- ----- ----- ----- ----- -----
        // リフレクションプローブのベイク

        // - ReflectionProbeRoomをオン、ReflectionProbeDomeをオフ
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeRoom").SetActive(true);
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeDome").SetActive(false);

        // - ライトマップをRoom = 1, Dome = 0に
        SetBrightness(1, 1, 0);

        // - 3_DomeScreenの照明を0に
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightWhite", 0.0f);
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightBlue", 0.0f);

        // - Render ReflectionProbeを実行
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        ftRenderLightmap.OnFinishedReflectionProbes += RenderReflectionProbeRoomFinished;
        bakeryRender.RenderReflectionProbesButton();
    }

    private void RenderReflectionProbeRoomFinished(object sender, System.EventArgs e) {
        Debug.Log("--STEP5--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));
        ftRenderLightmap.OnFinishedReflectionProbes -= RenderReflectionProbeRoomFinished;

        // - ReflectionProbeDomeをオン、ReflectionProbeRoomをオフ
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeRoom").SetActive(false);
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeDome").SetActive(true);

        // - ライトマップをRoom = 0, Dome = 1に
        SetBrightness(1, 0, 1);

        // - 3_DomeScreenの白照明を1に
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightWhite", 1.0f);

        // - Render ReflectionProbeを実行
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        ftRenderLightmap.OnFinishedReflectionProbes += RenderReflectionProbeDomeFinished;
        bakeryRender.RenderReflectionProbesButton();
    }

    private void RenderReflectionProbeDomeFinished(object sender, System.EventArgs e) {
        Debug.Log("--STEP6--");

        var bakeryRender = (ftRenderLightmap)GetWindow(typeof(ftRenderLightmap));
        ftRenderLightmap.OnFinishedReflectionProbes -= RenderReflectionProbeDomeFinished;

        // 後片付け

        // - Lightings/Baked/EnableOnBakeをオフ
        GameObject.Find("Lightings/Baked/EnableOnBake").SetActive(false);

        // - RoomLightsをオン、DomeLightsをオフ
        GameObject.Find("Lightings/Baked/RoomLights").SetActive(true);
        GameObject.Find("Lightings/Baked/DomeLights").SetActive(false);

        // - ライトマップをRoom = 1, Dome = 1に
        SetBrightness(1, 1, 1);

        // - 3_DomeScreenの照明を0に
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightWhite", 0.0f);
        GameObject.Find("PlanetariumProjector/3_DomeScreen").GetComponent<Renderer>().sharedMaterial.SetFloat("_ScreenLightBlue", 0.0f);

        // リフレクションプローブを両方ON
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeRoom").SetActive(true);
        GameObject.Find("Lightings/ReflectionProbes/ReflectionProbeDome").SetActive(true);

        Debug.Log("Render PLANETEI completed!");
    }

}