using System;
using System.Collections.Generic;
using System.IO;
using Jumius.VRCLightmapMixer;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(LightmapMixer))]
public class LightmapMixerEditor : Editor
{
    private enum BakeStep
    {
        Idle,
        RenderingA,
        RenderingB,
        RenderingLightProbes,
        RenderingReflectionProbesA,
        RenderingReflectionProbesB
    }

    private static LightmapMixer activeBakeInstaller;
    private static GlobalObjectId activeBakeInstallerId;
    private static BakeStep activeBakeStep = BakeStep.Idle;
    private static readonly Dictionary<GameObject, bool> savedObjectStates = new Dictionary<GameObject, bool>();
    private static readonly Dictionary<Material, Color> savedEmissionColors = new Dictionary<Material, Color>();
    private static bool pendingCopyAssignA;
    private static int pendingCopyAttempts;
    private static double pendingCopyStartedAt;
    private static double pendingNextCopyAt;
    private static bool pendingLightVolumeCopyAndAssign;
    private static bool pendingLightVolumeAssignA;
    private static int pendingLightVolumeAttempts;
    private static double pendingLightVolumeStartedAt;
    private static double pendingNextLightVolumeCheckAt;
    private static string pendingLightVolumeNextStepLabel;
    private static System.Action pendingLightVolumeNextStep;
    private static bool bakeMonitorActive;
    private static bool bakeMonitorSawInProgress;
    private static bool bakeMonitorLastInProgress;
    private static int bakeMonitorFrames;
    private static int renderPassSequence;
    private static bool reflectionProbesACompleted;
    private static bool reflectionProbesBCompleted;
    private static bool showLightVolumeSettings = true;
    private static bool showBakeMaterialParameterSettings = true;
    private const int MaxBakeStartWaitFrames = 300;
    private const double MaxPendingCopyWaitSeconds = 90.0;
    private const double MaxPendingLightVolumeWaitSeconds = 300.0;
    private const double PendingCopyPollIntervalSeconds = 0.5;
    private const string LightVolumeTextureAProperty = "_TexA";
    private const string LightVolumeTextureBProperty = "_TexB";

    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets))
        {
            return;
        }

        LightmapMixer installer = (LightmapMixer)target;

        serializedObject.Update();
        DrawReplacementAndTargetsSection(installer);
        DrawLightmapSwitchingSection();
        DrawRuntimeSection();
        DrawRenderingSection(installer);
        DrawAdvancedSection();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawReplacementAndTargetsSection(LightmapMixer installer)
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("1. 置き換え用シェーダと対象レンダラー", EditorStyles.boldLabel);
            DrawProperty("replacementShader", "置き換え用シェーダ");
            DrawProperty("autoCollectRenderersBeforeBake", "ベイク時に自動で収集");
            DrawProperty("targetRenderers", "対象レンダラー一覧");

            DrawTargetRendererStatus(installer);
            if (GUILayout.Button("対象レンダラーを収集"))
            {
                serializedObject.ApplyModifiedProperties();
                CollectLightmappedRenderers(installer);
                serializedObject.Update();
            }
        }
    }

    private void DrawLightmapSwitchingSection()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("2. Lightmapごとに切り替えるもの", EditorStyles.boldLabel);
            DrawProperty("activeObjectsForLightmapA", "Lightmap Aで有効化するオブジェクト");
            DrawProperty("activeObjectsForLightmapB", "Lightmap Bで有効化するオブジェクト");
            DrawProperty("activeReflectionProbesForLightmapA", "Lightmap Aでベイクするリフレクションプローブ");
            DrawProperty("activeReflectionProbesForLightmapB", "Lightmap Bでベイクするリフレクションプローブ");
            DrawProperty("emissiveMaterialsForLightmapA", "Lightmap Aで光らせるマテリアル");
            DrawProperty("emissiveMaterialsForLightmapB", "Lightmap Bで光らせるマテリアル");
            DrawLightVolumeSettings();
            DrawBakeMaterialParameters(serializedObject);
        }
    }

    private void DrawLightVolumeSettings()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            Rect foldoutRect = EditorGUILayout.GetControlRect();
            foldoutRect.x += 12f;
            foldoutRect.width -= 12f;
            showLightVolumeSettings = EditorGUI.Foldout(foldoutRect, showLightVolumeSettings, "VRCLightVolume", true);
            if (!showLightVolumeSettings)
            {
                return;
            }

            EditorGUI.indentLevel++;
            DrawProperty("mixedLightVolumeMaterial", "VRCLV混合マテリアル");
            DrawProperty("mixedLightVolumeAtlas", "MixedLightVolumeAtlas");
            DrawProperty("lightVolumeManager", "Light Volume Manager");
            EditorGUI.indentLevel--;
        }
    }

    private void DrawRuntimeSection()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("3. 実行時の調整", EditorStyles.boldLabel);
            DrawProperty("updateRuntimeMix", "Update Runtime Mix");
            DrawProperty("runtimeMixUse", "Runtime Mix Use");
            DrawProperty("runtimeMixA", "Runtime Mix A");
            DrawProperty("runtimeMixB", "Runtime Mix B");
            DrawProperty("runtimeMixStep", "Runtime Mix Step");
        }
    }

    private void DrawRenderingSection(LightmapMixer installer)
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("4. レンダリング", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(activeBakeStep != BakeStep.Idle);
            if (GUILayout.Button("ライトベイクを実行"))
            {
                serializedObject.ApplyModifiedProperties();
                StartLightmapRendering(installer);
                serializedObject.Update();
            }
            EditorGUI.EndDisabledGroup();

            if (activeBakeStep != BakeStep.Idle)
            {
                EditorGUILayout.HelpBox("Bakery rendering is running. Wait for the current render callback to finish.", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("レンダリング結果", EditorStyles.boldLabel);
            DrawProperty("lightMapATextures", "Lightmap A Textures");
            DrawProperty("lightMapBTextures", "Lightmap B Textures");
        }
    }

    private void DrawAdvancedSection()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("5. Advanced Settings", EditorStyles.boldLabel);
            DrawProperty("applyOnStart", "Apply On Start");
            DrawProperty("skipWhenLightmapTextureIsMissing", "Skip When Lightmap Texture Is Missing");
            DrawProperty("lightmapAOutputFolder", "Lightmap A Output Folder");
            DrawProperty("lightmapBOutputFolder", "Lightmap B Output Folder");
            DrawProperty("renderLightProbesAfterLightmaps", "Render Light Probes After Lightmaps");
            DrawProperty("renderReflectionProbesAfterLightmaps", "Render Reflection Probes After Lightmaps");
            DrawProperty("restoreObjectStatesAfterRendering", "Restore Object States After Rendering");
            DrawProperty("logRuntimeMixChanges", "Log Runtime Mix Changes");
        }
    }

    private void DrawProperty(string propertyName, string label)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property == null)
        {
            return;
        }

        if (property.isArray && property.propertyType != SerializedPropertyType.String)
        {
            DrawArrayProperty(property, label, GetArrayElementObjectType(propertyName));
            return;
        }

        EditorGUILayout.PropertyField(property, new GUIContent(label), true);
    }

    private static void DrawArrayProperty(SerializedProperty property, string label, Type objectType)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            Rect foldoutRect = EditorGUILayout.GetControlRect();
            foldoutRect.x += 12f;
            foldoutRect.width -= 12f;
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);
            if (!property.isExpanded)
            {
                return;
            }

            EditorGUI.indentLevel++;
            int newSize = Mathf.Max(0, EditorGUILayout.IntField("Size", property.arraySize));
            if (newSize != property.arraySize)
            {
                property.arraySize = newSize;
            }

            for (int i = 0; i < property.arraySize; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(element, new GUIContent("Element " + i), true);
                    if (GUILayout.Button("-", GUILayout.Width(24)))
                    {
                        bool deleteTwice = element.propertyType == SerializedPropertyType.ObjectReference &&
                            element.objectReferenceValue != null;
                        property.DeleteArrayElementAtIndex(i);
                        if (deleteTwice && i < property.arraySize)
                        {
                            property.DeleteArrayElementAtIndex(i);
                        }
                        break;
                    }
                }
            }

            if (GUILayout.Button("Add Element"))
            {
                property.arraySize++;
            }

            DrawArrayDropArea(property, objectType);
            EditorGUI.indentLevel--;
        }
    }

    private static Type GetArrayElementObjectType(string propertyName)
    {
        switch (propertyName)
        {
            case "targetRenderers":
                return typeof(Renderer);
            case "activeObjectsForLightmapA":
            case "activeObjectsForLightmapB":
            case "activeReflectionProbesForLightmapA":
            case "activeReflectionProbesForLightmapB":
                return typeof(GameObject);
            case "emissiveMaterialsForLightmapA":
            case "emissiveMaterialsForLightmapB":
                return typeof(Material);
            case "lightMapATextures":
            case "lightMapBTextures":
                return typeof(Texture2D);
            default:
                return null;
        }
    }

    private static void DrawArrayDropArea(SerializedProperty property, Type objectType)
    {
        if (objectType == null)
        {
            return;
        }

        Rect dropArea = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, "Drag and drop multiple " + objectType.Name + " objects here", EditorStyles.helpBox);
        Event currentEvent = Event.current;
        if (!dropArea.Contains(currentEvent.mousePosition))
        {
            return;
        }

        if (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform)
        {
            return;
        }

        bool hasValidObject = HasValidDraggedObject(objectType);
        DragAndDrop.visualMode = hasValidObject ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
        if (currentEvent.type == EventType.DragPerform && hasValidObject)
        {
            DragAndDrop.AcceptDrag();
            AddDraggedObjectsToArray(property, objectType);
        }

        currentEvent.Use();
    }

    private static bool HasValidDraggedObject(Type objectType)
    {
        UnityEngine.Object[] draggedObjects = DragAndDrop.objectReferences;
        if (draggedObjects == null)
        {
            return false;
        }

        for (int i = 0; i < draggedObjects.Length; i++)
        {
            if (GetObjectForArray(draggedObjects[i], objectType) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddDraggedObjectsToArray(SerializedProperty property, Type objectType)
    {
        UnityEngine.Object[] draggedObjects = DragAndDrop.objectReferences;
        if (draggedObjects == null)
        {
            return;
        }

        for (int i = 0; i < draggedObjects.Length; i++)
        {
            UnityEngine.Object objectToAdd = GetObjectForArray(draggedObjects[i], objectType);
            if (objectToAdd == null || ArrayContainsObject(property, objectToAdd))
            {
                continue;
            }

            int index = property.arraySize;
            property.arraySize++;
            property.GetArrayElementAtIndex(index).objectReferenceValue = objectToAdd;
        }
    }

    private static UnityEngine.Object GetObjectForArray(UnityEngine.Object draggedObject, Type objectType)
    {
        if (draggedObject == null || objectType == null)
        {
            return null;
        }

        if (objectType.IsInstanceOfType(draggedObject))
        {
            return draggedObject;
        }

        Component component = draggedObject as Component;
        if (objectType == typeof(GameObject) && component != null)
        {
            return component.gameObject;
        }

        GameObject gameObject = draggedObject as GameObject;
        if (gameObject == null)
        {
            return null;
        }

        if (objectType == typeof(Renderer))
        {
            return gameObject.GetComponent<Renderer>();
        }

        return null;
    }

    private static bool ArrayContainsObject(SerializedProperty property, UnityEngine.Object objectToFind)
    {
        if (property == null || objectToFind == null)
        {
            return false;
        }

        for (int i = 0; i < property.arraySize; i++)
        {
            SerializedProperty element = property.GetArrayElementAtIndex(i);
            if (element.propertyType == SerializedPropertyType.ObjectReference &&
                element.objectReferenceValue == objectToFind)
            {
                return true;
            }
        }

        return false;
    }

    private static void DrawTargetRendererStatus(LightmapMixer installer)
    {
        int targetCount = installer.targetRenderers == null ? 0 : installer.targetRenderers.Length;
        int indexCount = installer.targetLightmapIndices == null ? 0 : installer.targetLightmapIndices.Length;
        int unresolvedIndexCount = CountUnresolvedLightmapIndices(installer);
        EditorGUILayout.LabelField("Collected Renderers", targetCount.ToString());
        EditorGUILayout.LabelField("Stored Lightmap Indices", indexCount.ToString());
        EditorGUILayout.LabelField("Pending Lightmap Indices", unresolvedIndexCount.ToString());

        if (targetCount != indexCount)
        {
            EditorGUILayout.HelpBox("Target renderers and stored lightmap indices are out of sync. Collect targets again before entering play mode or uploading.", MessageType.Warning);
        }
        else if (unresolvedIndexCount > 0)
        {
            EditorGUILayout.HelpBox("Some collected renderers do not have a lightmap index yet. This is expected before the first bake. Run LightmapMixer bake and collect again after lightmaps are assigned.", MessageType.Info);
        }
    }

    private static int CountUnresolvedLightmapIndices(LightmapMixer installer)
    {
        if (installer == null || installer.targetLightmapIndices == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < installer.targetLightmapIndices.Length; i++)
        {
            if (installer.targetLightmapIndices[i] < 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void DrawBakeMaterialParameters(SerializedObject serializedObject)
    {
        SerializedProperty materials = serializedObject.FindProperty("bakeMaterialParameterMaterials");
        SerializedProperty names = serializedObject.FindProperty("bakeMaterialParameterNames");
        SerializedProperty valuesA = serializedObject.FindProperty("bakeMaterialParameterValuesForLightmapA");
        SerializedProperty valuesB = serializedObject.FindProperty("bakeMaterialParameterValuesForLightmapB");
        if (materials == null || names == null || valuesA == null || valuesB == null)
        {
            return;
        }

        int count = Mathf.Max(materials.arraySize, names.arraySize, valuesA.arraySize, valuesB.arraySize);
        SetBakeMaterialParameterArraySize(materials, names, valuesA, valuesB, count);

        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            Rect foldoutRect = EditorGUILayout.GetControlRect();
            foldoutRect.x += 12f;
            foldoutRect.width -= 12f;
            showBakeMaterialParameterSettings = EditorGUI.Foldout(
                foldoutRect,
                showBakeMaterialParameterSettings,
                "Lightmapごとにシェーダパラメータを変えるマテリアル",
                true);
            if (!showBakeMaterialParameterSettings)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Material", GUILayout.MinWidth(120));
                EditorGUILayout.LabelField("Shader Parameter", GUILayout.MinWidth(120));
                EditorGUILayout.LabelField("A Value", GUILayout.Width(70));
                EditorGUILayout.LabelField("B Value", GUILayout.Width(70));
                GUILayout.Space(28);
            }

            int removeIndex = -1;
            for (int i = 0; i < count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    SerializedProperty material = materials.GetArrayElementAtIndex(i);
                    SerializedProperty parameterName = names.GetArrayElementAtIndex(i);
                    SerializedProperty valueA = valuesA.GetArrayElementAtIndex(i);
                    SerializedProperty valueB = valuesB.GetArrayElementAtIndex(i);

                    material.objectReferenceValue = EditorGUILayout.ObjectField(material.objectReferenceValue, typeof(Material), false, GUILayout.MinWidth(120));
                    parameterName.stringValue = EditorGUILayout.TextField(parameterName.stringValue, GUILayout.MinWidth(120));
                    valueA.floatValue = EditorGUILayout.FloatField(valueA.floatValue, GUILayout.Width(70));
                    valueB.floatValue = EditorGUILayout.FloatField(valueB.floatValue, GUILayout.Width(70));

                    if (GUILayout.Button("-", GUILayout.Width(24)))
                    {
                        removeIndex = i;
                    }
                }
            }

            if (removeIndex >= 0)
            {
                RemoveBakeMaterialParameterRow(materials, names, valuesA, valuesB, removeIndex);
            }

            if (GUILayout.Button("対象マテリアルを追加"))
            {
                int newIndex = materials.arraySize;
                SetBakeMaterialParameterArraySize(materials, names, valuesA, valuesB, newIndex + 1);
                materials.GetArrayElementAtIndex(newIndex).objectReferenceValue = null;
                names.GetArrayElementAtIndex(newIndex).stringValue = "";
                valuesA.GetArrayElementAtIndex(newIndex).floatValue = 0f;
                valuesB.GetArrayElementAtIndex(newIndex).floatValue = 0f;
            }

            DrawBakeMaterialParameterDropArea(materials, names, valuesA, valuesB);
        }
    }

    private static void DrawBakeMaterialParameterDropArea(
        SerializedProperty materials,
        SerializedProperty names,
        SerializedProperty valuesA,
        SerializedProperty valuesB)
    {
        Rect dropArea = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, "Drag and drop multiple Material objects here", EditorStyles.helpBox);
        Event currentEvent = Event.current;
        if (!dropArea.Contains(currentEvent.mousePosition))
        {
            return;
        }

        if (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform)
        {
            return;
        }

        bool hasValidObject = HasValidDraggedObject(typeof(Material));
        DragAndDrop.visualMode = hasValidObject ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
        if (currentEvent.type == EventType.DragPerform && hasValidObject)
        {
            DragAndDrop.AcceptDrag();
            AddDraggedMaterialsToBakeMaterialParameters(materials, names, valuesA, valuesB);
        }

        currentEvent.Use();
    }

    private static void AddDraggedMaterialsToBakeMaterialParameters(
        SerializedProperty materials,
        SerializedProperty names,
        SerializedProperty valuesA,
        SerializedProperty valuesB)
    {
        UnityEngine.Object[] draggedObjects = DragAndDrop.objectReferences;
        if (draggedObjects == null)
        {
            return;
        }

        for (int i = 0; i < draggedObjects.Length; i++)
        {
            Material material = GetObjectForArray(draggedObjects[i], typeof(Material)) as Material;
            if (material == null || ArrayContainsObject(materials, material))
            {
                continue;
            }

            int newIndex = materials.arraySize;
            SetBakeMaterialParameterArraySize(materials, names, valuesA, valuesB, newIndex + 1);
            materials.GetArrayElementAtIndex(newIndex).objectReferenceValue = material;
            names.GetArrayElementAtIndex(newIndex).stringValue = "";
            valuesA.GetArrayElementAtIndex(newIndex).floatValue = 0f;
            valuesB.GetArrayElementAtIndex(newIndex).floatValue = 0f;
        }
    }

    private static void SetBakeMaterialParameterArraySize(
        SerializedProperty materials,
        SerializedProperty names,
        SerializedProperty valuesA,
        SerializedProperty valuesB,
        int count)
    {
        materials.arraySize = count;
        names.arraySize = count;
        valuesA.arraySize = count;
        valuesB.arraySize = count;
    }

    private static void RemoveBakeMaterialParameterRow(
        SerializedProperty materials,
        SerializedProperty names,
        SerializedProperty valuesA,
        SerializedProperty valuesB,
        int index)
    {
        materials.GetArrayElementAtIndex(index).objectReferenceValue = null;
        materials.DeleteArrayElementAtIndex(index);
        names.DeleteArrayElementAtIndex(index);
        valuesA.DeleteArrayElementAtIndex(index);
        valuesB.DeleteArrayElementAtIndex(index);
    }

    private static void CollectLightmappedRenderers(LightmapMixer installer)
    {
        List<Renderer> targets = new List<Renderer>();
        List<int> lightmapIndices = new List<int>();
        Scene scene = installer.gameObject.scene;
        GameObject[] rootObjects = scene.GetRootGameObjects();

        for (int rootIndex = 0; rootIndex < rootObjects.Length; rootIndex++)
        {
            Renderer[] renderers = rootObjects[rootIndex].GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!ShouldCollectRenderer(renderer))
                {
                    continue;
                }

                targets.Add(renderer);
                lightmapIndices.Add(renderer.lightmapIndex);
            }
        }

        Undo.RecordObject(installer, "Collect scene lightmap material targets");
        installer.targetRenderers = targets.ToArray();
        installer.targetLightmapIndices = lightmapIndices.ToArray();
        EditorUtility.SetDirty(installer);
        PrefabUtility.RecordPrefabInstancePropertyModifications(installer);
    }

    private static bool ShouldCollectRenderer(Renderer renderer)
    {
        if (renderer == null)
        {
            return false;
        }

        if (renderer.lightmapIndex >= 0)
        {
            return true;
        }

        return IsLightmapCandidateRenderer(renderer);
    }

    private static bool IsLightmapCandidateRenderer(Renderer renderer)
    {
        if (renderer == null || !HasAnySharedMaterial(renderer))
        {
            return false;
        }

        MeshRenderer meshRenderer = renderer as MeshRenderer;
        if (meshRenderer == null)
        {
            return false;
        }

        StaticEditorFlags staticFlags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
        if ((staticFlags & StaticEditorFlags.ContributeGI) != 0)
        {
            return true;
        }

        return renderer.gameObject.isStatic;
    }

    private static bool HasAnySharedMaterial(Renderer renderer)
    {
        if (renderer == null || renderer.sharedMaterials == null)
        {
            return false;
        }

        Material[] materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void StartLightmapRendering(LightmapMixer installer)
    {
        if (installer == null)
        {
            return;
        }

        if (activeBakeStep != BakeStep.Idle)
        {
            Debug.LogWarning("Lightmap rendering is already running.");
            return;
        }

        activeBakeInstaller = installer;
        activeBakeInstallerId = GlobalObjectId.GetGlobalObjectIdSlow(installer);
        reflectionProbesACompleted = false;
        reflectionProbesBCompleted = false;
        if (installer.autoCollectRenderersBeforeBake)
        {
            CollectLightmappedRenderers(installer);
        }

        SaveBakeEmissionColors(installer);
        SaveObjectStates(installer);
        RenderLightmapA();
    }

    private static void RenderLightmapA()
    {
        Debug.Log("[VRCLightmapMixer] Step: prepare A objects and A reflection probes for lightmap A.");
        activeBakeStep = BakeStep.RenderingA;
        ApplyBakeObjectStates(activeBakeInstaller, true);
        ApplyReflectionProbeObjectStates(activeBakeInstaller, true);
        ApplyEmissiveMaterials(activeBakeInstaller, true);
        ApplyBakeMaterialParameters(activeBakeInstaller, true);
        LogBakeObjectDiagnostics(activeBakeInstaller);
        ForceBakeryRefresh(activeBakeInstaller);
        Debug.Log("[VRCLightmapMixer] Step: clear current Bakery output before lightmap A.");
        ClearCurrentBakeryOutputLightmaps(activeBakeInstaller);
        SaveActiveScene();
        PrepareLightVolumeAtlasOutputForFullRender(activeBakeInstaller, "lightmap A");

        Debug.Log("[VRCLightmapMixer] Step: render lightmap A.");
        RegisterBakeryFullRenderCallbacks(OnFinishedLightmapA);
        Debug.Log($"[VRCLightmapMixer] Registered Bakery callbacks for {activeBakeStep} pass #{renderPassSequence}.");
        ftRenderLightmap bakeryWindow = GetBakeryWindow();
        Debug.Log($"[VRCLightmapMixer] Calling Bakery RenderButton for {activeBakeStep}. bakeInProgressBefore={ftRenderLightmap.bakeInProgress}");
        bakeryWindow.RenderButton();
        Debug.Log($"[VRCLightmapMixer] Bakery RenderButton returned for {activeBakeStep}. bakeInProgressAfter={ftRenderLightmap.bakeInProgress}");
        StartBakeMonitor();
    }

    private static void OnFinishedLightmapA(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] OnFinishedLightmapA received. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        CompleteCurrentBakePass("Bakery event");
    }

    private static void RenderLightmapB()
    {
        Debug.Log("[VRCLightmapMixer] Step: prepare B objects and B reflection probes for lightmap B.");
        activeBakeStep = BakeStep.RenderingB;
        ApplyBakeObjectStates(activeBakeInstaller, false);
        ApplyReflectionProbeObjectStates(activeBakeInstaller, false);
        ApplyEmissiveMaterials(activeBakeInstaller, false);
        ApplyBakeMaterialParameters(activeBakeInstaller, false);
        LogBakeObjectDiagnostics(activeBakeInstaller);
        ForceBakeryRefresh(activeBakeInstaller);
        Debug.Log("[VRCLightmapMixer] Step: clear current Bakery output before lightmap B.");
        ClearCurrentBakeryOutputLightmaps(activeBakeInstaller);
        SaveActiveScene();
        PrepareLightVolumeAtlasOutputForFullRender(activeBakeInstaller, "lightmap B");

        Debug.Log("[VRCLightmapMixer] Step: render lightmap B.");
        RegisterBakeryFullRenderCallbacks(OnFinishedLightmapB);
        Debug.Log($"[VRCLightmapMixer] Registered Bakery callbacks for {activeBakeStep} pass #{renderPassSequence}.");
        ftRenderLightmap bakeryWindow = GetBakeryWindow();
        Debug.Log($"[VRCLightmapMixer] Calling Bakery RenderButton for {activeBakeStep}. bakeInProgressBefore={ftRenderLightmap.bakeInProgress}");
        bakeryWindow.RenderButton();
        Debug.Log($"[VRCLightmapMixer] Bakery RenderButton returned for {activeBakeStep}. bakeInProgressAfter={ftRenderLightmap.bakeInProgress}");
        StartBakeMonitor();
    }

    private static void OnFinishedLightmapB(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] OnFinishedLightmapB received. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        CompleteCurrentBakePass("Bakery event");
    }

    private static void RegisterBakeryFullRenderCallbacks(System.EventHandler finishedHandler)
    {
        UnregisterBakeryCallbacks();
        renderPassSequence++;
        ftRenderLightmap.OnPreFullRender += OnBakeryPreFullRenderDiagnostic;
        ftRenderLightmap.OnFinishedFullRender += OnBakeryFinishedFullRenderDiagnostic;
        ftRenderLightmap.OnFinishedFullRender += finishedHandler;
    }

    private static void RegisterBakeryReflectionProbeCallbacks(System.EventHandler finishedHandler)
    {
        UnregisterBakeryCallbacks();
        renderPassSequence++;
        ftRenderLightmap.OnFinishedReflectionProbes += OnBakeryFinishedReflectionProbesDiagnostic;
        ftRenderLightmap.OnFinishedReflectionProbes += finishedHandler;
    }

    private static void UnregisterBakeryCallbacks()
    {
        ftRenderLightmap.OnPreFullRender -= OnBakeryPreFullRenderDiagnostic;
        ftRenderLightmap.OnFinishedFullRender -= OnBakeryFinishedFullRenderDiagnostic;
        ftRenderLightmap.OnFinishedFullRender -= OnFinishedLightmapA;
        ftRenderLightmap.OnFinishedFullRender -= OnFinishedLightmapB;
        ftRenderLightmap.OnFinishedFullRender -= OnFinishedLightProbes;
        ftRenderLightmap.OnFinishedReflectionProbes -= OnBakeryFinishedReflectionProbesDiagnostic;
        ftRenderLightmap.OnFinishedReflectionProbes -= OnFinishedReflectionProbesA;
        ftRenderLightmap.OnFinishedReflectionProbes -= OnFinishedReflectionProbesB;
    }

    private static void OnBakeryPreFullRenderDiagnostic(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] Bakery OnPreFullRender received. activeStep={activeBakeStep}, pass=#{renderPassSequence}, sender={(sender == null ? "null" : sender.GetType().Name)}");
    }

    private static void OnBakeryFinishedFullRenderDiagnostic(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] Bakery OnFinishedFullRender received. activeStep={activeBakeStep}, pass=#{renderPassSequence}, bakeInProgress={ftRenderLightmap.bakeInProgress}, sender={(sender == null ? "null" : sender.GetType().Name)}");
    }

    private static void OnBakeryFinishedReflectionProbesDiagnostic(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] Bakery OnFinishedReflectionProbes received. activeStep={activeBakeStep}, pass=#{renderPassSequence}, bakeInProgress={ftRenderLightmap.bakeInProgress}, sender={(sender == null ? "null" : sender.GetType().Name)}");
    }

    private static void StartBakeMonitor()
    {
        EditorApplication.update -= MonitorBakeProgress;
        bakeMonitorActive = true;
        bakeMonitorSawInProgress = ftRenderLightmap.bakeInProgress;
        bakeMonitorLastInProgress = ftRenderLightmap.bakeInProgress;
        bakeMonitorFrames = 0;
        EditorApplication.update += MonitorBakeProgress;
        Debug.Log($"[VRCLightmapMixer] Bake monitor started. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}, sawInProgress={bakeMonitorSawInProgress}");
    }

    private static void StopBakeMonitor()
    {
        EditorApplication.update -= MonitorBakeProgress;
        if (bakeMonitorActive)
        {
            Debug.Log($"[VRCLightmapMixer] Bake monitor stopped. activeStep={activeBakeStep}, frames={bakeMonitorFrames}, sawInProgress={bakeMonitorSawInProgress}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        }
        bakeMonitorActive = false;
    }

    private static void MonitorBakeProgress()
    {
        if (!bakeMonitorActive || activeBakeStep == BakeStep.Idle)
        {
            StopBakeMonitor();
            return;
        }

        if (ftRenderLightmap.bakeInProgress)
        {
            if (!bakeMonitorLastInProgress)
            {
                Debug.Log($"[VRCLightmapMixer] Bake monitor detected bake start. activeStep={activeBakeStep}, frame={bakeMonitorFrames}");
            }

            bakeMonitorLastInProgress = true;
            bakeMonitorSawInProgress = true;
            bakeMonitorFrames++;
            return;
        }

        if (bakeMonitorLastInProgress)
        {
            Debug.Log($"[VRCLightmapMixer] Bake monitor detected bake stop. activeStep={activeBakeStep}, frame={bakeMonitorFrames}, sawInProgress={bakeMonitorSawInProgress}");
        }

        bakeMonitorLastInProgress = false;

        if (IsLightmapRenderStep(activeBakeStep) && HasGeneratedBakeryOutputLightmaps(activeBakeInstaller))
        {
            Debug.Log($"[VRCLightmapMixer] Bake monitor found generated lightmap files without Bakery finish callback. activeStep={activeBakeStep}, frame={bakeMonitorFrames}, sawInProgress={bakeMonitorSawInProgress}");
            CompleteCurrentBakePass("Generated lightmap file monitor");
            return;
        }

        if (bakeMonitorSawInProgress)
        {
            CompleteCurrentBakePass("Bake progress monitor");
            return;
        }

        bakeMonitorFrames++;
        if (bakeMonitorFrames > MaxBakeStartWaitFrames)
        {
            Debug.LogWarning($"[VRCLightmapMixer] Bakery bake did not start. Finishing bake sequence. activeStep={activeBakeStep}, frames={bakeMonitorFrames}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
            FinishBakeSequence();
        }
    }

    private static bool IsLightmapRenderStep(BakeStep step)
    {
        return step == BakeStep.RenderingA || step == BakeStep.RenderingB;
    }

    private static void CompleteCurrentBakePass(string source)
    {
        BakeStep completedStep = activeBakeStep;
        Debug.Log($"[VRCLightmapMixer] CompleteCurrentBakePass entered. completedStep={completedStep}, source={source}, bakeInProgress={ftRenderLightmap.bakeInProgress}, userCanceled={ftRenderLightmap.userCanceled}");
        if (completedStep != BakeStep.RenderingA &&
            completedStep != BakeStep.RenderingB &&
            completedStep != BakeStep.RenderingLightProbes &&
            completedStep != BakeStep.RenderingReflectionProbesA &&
            completedStep != BakeStep.RenderingReflectionProbesB)
        {
            return;
        }

        StopBakeMonitor();
        UnregisterBakeryCallbacks();

        if (!ValidateActiveBake())
        {
            FinishBakeSequence();
            return;
        }

        if (ftRenderLightmap.userCanceled)
        {
            Debug.LogWarning("[VRCLightmapMixer] Bakery bake was canceled. Finishing bake sequence.");
            FinishBakeSequence();
            return;
        }

        Debug.Log($"[VRCLightmapMixer] {completedStep} completed via {source}.");

        if (completedStep == BakeStep.RenderingReflectionProbesA)
        {
            reflectionProbesACompleted = true;
        }
        else if (completedStep == BakeStep.RenderingReflectionProbesB)
        {
            reflectionProbesBCompleted = true;
        }

        if (completedStep == BakeStep.RenderingA)
        {
            ScheduleCopyAfterBake(true);
            return;
        }

        if (completedStep == BakeStep.RenderingB)
        {
            ScheduleCopyAfterBake(false);
            return;
        }

        if (completedStep == BakeStep.RenderingLightProbes)
        {
            ScheduleWaitForLightVolumeAtlasAfterFullRender(false, true, RenderReflectionProbesAOrRenderLightmapB, "reflection probes A or lightmap B");
            return;
        }

        if (completedStep == BakeStep.RenderingReflectionProbesA)
        {
            ScheduleNextBakeStep(RenderLightmapB, "lightmap B");
            return;
        }

        FinishBakeSequence(true);
    }

    private static void ScheduleNextBakeStep(System.Action action, string label)
    {
        Debug.Log($"[VRCLightmapMixer] Scheduling next bake step after Bakery callback/update has unwound: {label}");
        EditorApplication.delayCall += () =>
        {
            if (!ValidateActiveBake())
            {
                FinishBakeSequence();
                return;
            }

            Debug.Log($"[VRCLightmapMixer] Starting scheduled bake step: {label}");
            action();
        };
    }

    private static void ScheduleCopyAfterBake(bool assignA)
    {
        pendingCopyAssignA = assignA;
        pendingCopyAttempts = 0;
        pendingCopyStartedAt = EditorApplication.timeSinceStartup;
        pendingNextCopyAt = pendingCopyStartedAt;
        EditorApplication.delayCall -= CopyAfterBakeOutputIsReady;
        EditorApplication.update -= PollCopyAfterBakeOutput;
        EditorApplication.update += PollCopyAfterBakeOutput;
        Debug.Log($"[VRCLightmapMixer] Waiting for generated lightmap {(assignA ? "A" : "B")} files. outputFolder={(assignA ? activeBakeInstaller.lightmapAOutputFolder : activeBakeInstaller.lightmapBOutputFolder)}");
        LogCurrentBakeryOutputFiles(activeBakeInstaller);
    }

    private static void PollCopyAfterBakeOutput()
    {
        if (EditorApplication.timeSinceStartup < pendingNextCopyAt)
        {
            return;
        }

        pendingNextCopyAt = EditorApplication.timeSinceStartup + PendingCopyPollIntervalSeconds;
        CopyAfterBakeOutputIsReady();
    }

    private static void CopyAfterBakeOutputIsReady()
    {
        if (!ValidateActiveBake())
        {
            FinishBakeSequence(true);
            return;
        }

        Texture2D[] textures = CopyGeneratedBakeryLightmapsFromCurrentOutput(
            activeBakeInstaller,
            pendingCopyAssignA ? activeBakeInstaller.lightmapAOutputFolder : activeBakeInstaller.lightmapBOutputFolder,
            pendingCopyAttempts == 0);

        if (!HasAnyTexture(textures) &&
            EditorApplication.timeSinceStartup - pendingCopyStartedAt < MaxPendingCopyWaitSeconds)
        {
            pendingCopyAttempts++;
            if (pendingCopyAttempts % 10 == 0)
            {
                Debug.Log($"[VRCLightmapMixer] Still waiting for generated lightmap {(pendingCopyAssignA ? "A" : "B")} files. attempts={pendingCopyAttempts}");
                LogCurrentBakeryOutputFiles(activeBakeInstaller);
            }

            return;
        }

        EditorApplication.update -= PollCopyAfterBakeOutput;
        EditorApplication.delayCall -= CopyAfterBakeOutputIsReady;

        if (!HasAnyTexture(textures))
        {
            Debug.LogError($"[VRCLightmapMixer] Generated lightmaps were not found after retrying for lightmap {(pendingCopyAssignA ? "A" : "B")}. Stopping without assigning Bakery storage because it may contain stale lightmaps from the previous pass.");
            FinishBakeSequence();
            return;
        }

        AssignLightmapTextures(activeBakeInstaller, pendingCopyAssignA, textures);
        Debug.Log($"[VRCLightmapMixer] Assigned lightmap {(pendingCopyAssignA ? "A" : "B")} textures. textureSlots={textures.Length}, hasAny={HasAnyTexture(textures)}");

        bool assignA = pendingCopyAssignA;
        ScheduleWaitForLightVolumeAtlasAfterFullRender(true, assignA, () => ContinueAfterLightmapAssignment(assignA), $"continue after lightmap {(assignA ? "A" : "B")} VRCLightVolumes atlas");
    }

    private static void ContinueAfterLightmapAssignment(bool assignA)
    {
        if (!ValidateActiveBake())
        {
            FinishBakeSequence(true);
            return;
        }

        if (assignA)
        {
            if (activeBakeInstaller.autoCollectRenderersBeforeBake)
            {
                CollectLightmappedRenderers(activeBakeInstaller);
            }

            if (activeBakeInstaller.renderLightProbesAfterLightmaps)
            {
                RenderLightProbes();
            }
            else
            {
                RenderReflectionProbesAOrRenderLightmapB();
            }
            return;
        }

        if (activeBakeInstaller.autoCollectRenderersBeforeBake)
        {
            CollectLightmappedRenderers(activeBakeInstaller);
        }

        Debug.Log("[VRCLightmapMixer] Lightmap B assignment completed. Moving to reflection probe B step or finish.");
        RenderReflectionProbesBOrFinish();
    }

    private static void RenderLightProbes()
    {
        Debug.Log("[VRCLightmapMixer] Step: keep A objects and A reflection probes for light probes.");
        activeBakeStep = BakeStep.RenderingLightProbes;
        ApplyBakeObjectStates(activeBakeInstaller, true);
        ApplyReflectionProbeObjectStates(activeBakeInstaller, true);
        ApplyEmissiveMaterials(activeBakeInstaller, true);
        ApplyBakeMaterialParameters(activeBakeInstaller, true);
        LogBakeObjectDiagnostics(activeBakeInstaller);
        ForceBakeryRefresh(activeBakeInstaller);
        SaveActiveScene();
        PrepareLightVolumeAtlasOutputForFullRender(activeBakeInstaller, "light probes");

        Debug.Log("[VRCLightmapMixer] Step: render light probes.");
        RegisterBakeryFullRenderCallbacks(OnFinishedLightProbes);
        Debug.Log($"[VRCLightmapMixer] Registered Bakery callbacks for {activeBakeStep} pass #{renderPassSequence}.");
        ftRenderLightmap bakeryWindow = GetBakeryWindow();
        Debug.Log($"[VRCLightmapMixer] Calling Bakery RenderLightProbesButton for {activeBakeStep}. bakeInProgressBefore={ftRenderLightmap.bakeInProgress}");
        bakeryWindow.RenderLightProbesButton();
        Debug.Log($"[VRCLightmapMixer] Bakery RenderLightProbesButton returned for {activeBakeStep}. bakeInProgressAfter={ftRenderLightmap.bakeInProgress}");
        StartBakeMonitor();
    }

    private static void OnFinishedLightProbes(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] OnFinishedLightProbes received. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        CompleteCurrentBakePass("Bakery event");
    }

    private static void RenderReflectionProbesAOrRenderLightmapB()
    {
        if (!activeBakeInstaller.renderReflectionProbesAfterLightmaps)
        {
            Debug.Log("[VRCLightmapMixer] Step: reflection probes A skipped.");
            RenderLightmapB();
            return;
        }

        if (HasAnyObject(activeBakeInstaller.activeReflectionProbesForLightmapA))
        {
            RenderReflectionProbesA();
            return;
        }

        reflectionProbesACompleted = true;
        Debug.Log("[VRCLightmapMixer] Step: no reflection probes A are assigned.");
        RenderLightmapB();
    }

    private static void RenderReflectionProbesA()
    {
        Debug.Log("[VRCLightmapMixer] Step: prepare A objects and A reflection probes for reflection probes A.");
        activeBakeStep = BakeStep.RenderingReflectionProbesA;
        ApplyBakeObjectStates(activeBakeInstaller, true);
        ApplyReflectionProbeObjectStates(activeBakeInstaller, true);
        ApplyEmissiveMaterials(activeBakeInstaller, true);
        ApplyBakeMaterialParameters(activeBakeInstaller, true);
        LogBakeObjectDiagnostics(activeBakeInstaller);
        ForceBakeryRefresh(activeBakeInstaller);
        SaveActiveScene();

        Debug.Log("[VRCLightmapMixer] Step: render reflection probes A.");
        RegisterBakeryReflectionProbeCallbacks(OnFinishedReflectionProbesA);
        Debug.Log($"[VRCLightmapMixer] Registered Bakery callbacks for {activeBakeStep} pass #{renderPassSequence}.");
        ftRenderLightmap bakeryWindow = GetBakeryWindow();
        Debug.Log($"[VRCLightmapMixer] Calling Bakery RenderReflectionProbesButton for {activeBakeStep}. bakeInProgressBefore={ftRenderLightmap.bakeInProgress}");
        bakeryWindow.RenderReflectionProbesButton();
        Debug.Log($"[VRCLightmapMixer] Bakery RenderReflectionProbesButton returned for {activeBakeStep}. bakeInProgressAfter={ftRenderLightmap.bakeInProgress}");
        StartBakeMonitor();
    }

    private static void OnFinishedReflectionProbesA(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] OnFinishedReflectionProbesA received. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        CompleteCurrentBakePass("Bakery event");
    }

    private static void RenderReflectionProbesBOrFinish()
    {
        Debug.Log($"[VRCLightmapMixer] RenderReflectionProbesBOrFinish entered. renderReflectionProbesAfterLightmaps={activeBakeInstaller.renderReflectionProbesAfterLightmaps}, hasBReflectionProbeObjects={HasAnyObject(activeBakeInstaller.activeReflectionProbesForLightmapB)}");
        if (!activeBakeInstaller.renderReflectionProbesAfterLightmaps)
        {
            reflectionProbesBCompleted = true;
            Debug.Log("[VRCLightmapMixer] Step: reflection probes B skipped.");
            FinishBakeSequence(true);
            return;
        }

        if (HasAnyObject(activeBakeInstaller.activeReflectionProbesForLightmapB))
        {
            RenderReflectionProbesB();
            return;
        }

        reflectionProbesBCompleted = true;
        Debug.Log("[VRCLightmapMixer] Step: no reflection probes B are assigned.");
        FinishBakeSequence(true);
    }

    private static void RenderReflectionProbesB()
    {
        Debug.Log("[VRCLightmapMixer] Step: prepare B objects and B reflection probes for reflection probes B.");
        activeBakeStep = BakeStep.RenderingReflectionProbesB;
        ApplyBakeObjectStates(activeBakeInstaller, false);
        ApplyReflectionProbeObjectStates(activeBakeInstaller, false);
        ApplyEmissiveMaterials(activeBakeInstaller, false);
        ApplyBakeMaterialParameters(activeBakeInstaller, false);
        LogBakeObjectDiagnostics(activeBakeInstaller);
        ForceBakeryRefresh(activeBakeInstaller);
        SaveActiveScene();

        Debug.Log("[VRCLightmapMixer] Step: render reflection probes B.");
        RegisterBakeryReflectionProbeCallbacks(OnFinishedReflectionProbesB);
        Debug.Log($"[VRCLightmapMixer] Registered Bakery callbacks for {activeBakeStep} pass #{renderPassSequence}.");
        ftRenderLightmap bakeryWindow = GetBakeryWindow();
        Debug.Log($"[VRCLightmapMixer] Calling Bakery RenderReflectionProbesButton for {activeBakeStep}. bakeInProgressBefore={ftRenderLightmap.bakeInProgress}");
        bakeryWindow.RenderReflectionProbesButton();
        Debug.Log($"[VRCLightmapMixer] Bakery RenderReflectionProbesButton returned for {activeBakeStep}. bakeInProgressAfter={ftRenderLightmap.bakeInProgress}");
        StartBakeMonitor();
    }

    private static void OnFinishedReflectionProbesB(object sender, System.EventArgs e)
    {
        Debug.Log($"[VRCLightmapMixer] OnFinishedReflectionProbesB received. activeStep={activeBakeStep}, bakeInProgress={ftRenderLightmap.bakeInProgress}");
        CompleteCurrentBakePass("Bakery event");
    }

    private static void FinishBakeSequence()
    {
        FinishBakeSequence(false);
    }

    private static void FinishBakeSequence(bool showCompletionDialog)
    {
        RestoreBakeEmissionColors();

        if (activeBakeInstaller != null && activeBakeInstaller.restoreObjectStatesAfterRendering)
        {
            RestoreObjectStates();
        }

        if (activeBakeInstaller != null && reflectionProbesACompleted && reflectionProbesBCompleted)
        {
            EnableConfiguredReflectionProbeObjects(activeBakeInstaller);
        }

        if (showCompletionDialog)
        {
            AssignMixedLightVolumeAtlasToManager(activeBakeInstaller);
        }

        SaveActiveScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[VRCLightmapMixer] Lightmap rendering completed.");
        StopBakeMonitor();
        UnregisterBakeryCallbacks();
        EditorApplication.delayCall -= ShowBakeCompletedDialog;
        if (showCompletionDialog)
        {
            EditorApplication.delayCall += ShowBakeCompletedDialog;
        }

        EditorApplication.delayCall -= CopyAfterBakeOutputIsReady;
        EditorApplication.update -= PollCopyAfterBakeOutput;
        ClearPendingLightVolumeWait();
        savedObjectStates.Clear();
        savedEmissionColors.Clear();
        reflectionProbesACompleted = false;
        reflectionProbesBCompleted = false;
        activeBakeInstaller = null;
        activeBakeStep = BakeStep.Idle;
    }

    private static void ShowBakeCompletedDialog()
    {
        EditorUtility.DisplayDialog("VRCLightmapMixer", "ライトベイクが完了しました。", "OK");
    }

    private static bool ValidateActiveBake()
    {
        if (activeBakeInstaller != null)
        {
            return true;
        }

        UnityEngine.Object resolvedObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(activeBakeInstallerId);
        activeBakeInstaller = resolvedObject as LightmapMixer;
        if (activeBakeInstaller != null)
        {
            Debug.Log("[VRCLightmapMixer] Recovered LightmapMixer reference after bake.");
            return true;
        }

        Debug.LogWarning($"[VRCLightmapMixer] Active LightmapMixer reference is missing. id={activeBakeInstallerId}");
        return false;
    }

    private static ftRenderLightmap GetBakeryWindow()
    {
        return (ftRenderLightmap)EditorWindow.GetWindow(typeof(ftRenderLightmap));
    }

    private static void SaveActiveScene()
    {
        if (activeBakeInstaller != null)
        {
            Scene scene = activeBakeInstaller.gameObject.scene;
            MarkDirtyAndSaveSceneIfPossible(scene);
            return;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        MarkDirtyAndSaveSceneIfPossible(activeScene);
    }

    private static void MarkDirtyAndSaveSceneIfPossible(Scene scene)
    {
        if (!scene.IsValid())
        {
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (string.IsNullOrEmpty(scene.path))
        {
            Debug.LogWarning("[VRCLightmapMixer] Scene has no saved path. Skipping automatic scene save to avoid opening a save dialog.");
            return;
        }

        EditorSceneManager.SaveScene(scene);
    }

    private static void SaveObjectStates(LightmapMixer installer)
    {
        savedObjectStates.Clear();
        AddObjectStates(installer.activeReflectionProbesForLightmapA);
        AddObjectStates(installer.activeReflectionProbesForLightmapB);
    }

    private static void AddObjectStates(GameObject[] objects)
    {
        if (objects == null)
        {
            return;
        }

        for (int i = 0; i < objects.Length; i++)
        {
            GameObject obj = objects[i];
            if (obj == null || savedObjectStates.ContainsKey(obj))
            {
                continue;
            }

            savedObjectStates.Add(obj, obj.activeSelf);
        }
    }

    private static void SaveBakeEmissionColors(LightmapMixer installer)
    {
        savedEmissionColors.Clear();
        if (installer == null)
        {
            return;
        }

        SaveBakeEmissionColors(installer.emissiveMaterialsForLightmapA);
        SaveBakeEmissionColors(installer.emissiveMaterialsForLightmapB);

        Debug.Log($"[VRCLightmapMixer] Saved {savedEmissionColors.Count} emissive material color(s) for bake.");
    }

    private static void SaveBakeEmissionColors(Material[] materials)
    {
        if (materials == null)
        {
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null || savedEmissionColors.ContainsKey(material))
            {
                continue;
            }

            if (!material.HasProperty("_EmissionColor"))
            {
                Debug.LogWarning($"[VRCLightmapMixer] Material '{material.name}' does not have _EmissionColor. It will be skipped for emissive bake control.");
                continue;
            }

            savedEmissionColors.Add(material, material.GetColor("_EmissionColor"));
        }
    }

    private static void RestoreBakeEmissionColors()
    {
        foreach (KeyValuePair<Material, Color> savedEmissionColor in savedEmissionColors)
        {
            Material material = savedEmissionColor.Key;
            if (material == null || !material.HasProperty("_EmissionColor"))
            {
                continue;
            }

            Undo.RecordObject(material, "Restore VRCLightmapMixer emissive material");
            material.SetColor("_EmissionColor", savedEmissionColor.Value);
            EditorUtility.SetDirty(material);
        }
    }

    private static void ApplyBakeObjectStates(LightmapMixer installer, bool renderA)
    {
        Debug.Log(renderA
            ? "[VRCLightmapMixer] State: ActiveObjectsForLightmapA=ON, ActiveObjectsForLightmapB=OFF."
            : "[VRCLightmapMixer] State: ActiveObjectsForLightmapA=OFF, ActiveObjectsForLightmapB=ON.");
        SetObjectsActive(installer.activeObjectsForLightmapA, false);
        SetObjectsActive(installer.activeObjectsForLightmapB, false);
        SetObjectsActive(renderA ? installer.activeObjectsForLightmapA : installer.activeObjectsForLightmapB, true);
    }

    private static void ApplyReflectionProbeObjectStates(LightmapMixer installer, bool renderA)
    {
        Debug.Log(renderA
            ? "[VRCLightmapMixer] State: ActiveReflectionProbesForLightmapA=ON, ActiveReflectionProbesForLightmapB=OFF."
            : "[VRCLightmapMixer] State: ActiveReflectionProbesForLightmapA=OFF, ActiveReflectionProbesForLightmapB=ON.");
        SetObjectsActive(installer.activeReflectionProbesForLightmapA, false);
        SetObjectsActive(installer.activeReflectionProbesForLightmapB, false);
        SetObjectsActive(renderA ? installer.activeReflectionProbesForLightmapA : installer.activeReflectionProbesForLightmapB, true);
    }

    private static void ApplyEmissiveMaterials(LightmapMixer installer, bool renderA)
    {
        if (installer == null)
        {
            return;
        }

        ApplyEmissiveMaterials(installer.emissiveMaterialsForLightmapA, renderA ? 1f : 0f, renderA ? "A" : "B");
        ApplyEmissiveMaterials(installer.emissiveMaterialsForLightmapB, renderA ? 0f : 1f, renderA ? "A" : "B");
    }

    private static void ApplyEmissiveMaterials(Material[] materials, float multiplier, string bakeLabel)
    {
        if (materials == null)
        {
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                continue;
            }

            Undo.RecordObject(material, "Apply VRCLightmapMixer emissive material");
            material.EnableKeyword("_EMISSION");

            if (!material.HasProperty("_EmissionColor"))
            {
                Debug.LogWarning($"[VRCLightmapMixer] Material '{material.name}' does not have _EmissionColor. Emission color was not changed.");
                continue;
            }

            Color emissionColor;
            if (!savedEmissionColors.TryGetValue(material, out emissionColor))
            {
                emissionColor = material.GetColor("_EmissionColor");
                savedEmissionColors[material] = emissionColor;
                Debug.LogWarning($"[VRCLightmapMixer] Emission color for material '{material.name}' was not saved at bake start. Current _EmissionColor will be used as the base value.");
            }

            material.SetColor("_EmissionColor", emissionColor * multiplier);
            EditorUtility.SetDirty(material);
            Debug.Log($"[VRCLightmapMixer] Emissive material: {material.name}._EmissionColor={bakeLabel}:{multiplier}");
        }
    }

    private static void ApplyBakeMaterialParameters(LightmapMixer installer, bool renderA)
    {
        if (installer == null || installer.bakeMaterialParameterMaterials == null)
        {
            return;
        }

        int count = installer.bakeMaterialParameterMaterials.Length;
        for (int i = 0; i < count; i++)
        {
            Material material = installer.bakeMaterialParameterMaterials[i];
            string parameterName = GetArrayValue(installer.bakeMaterialParameterNames, i);
            if (material == null || string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            float value = renderA
                ? GetArrayValue(installer.bakeMaterialParameterValuesForLightmapA, i)
                : GetArrayValue(installer.bakeMaterialParameterValuesForLightmapB, i);

            Undo.RecordObject(material, "Apply VRCLightmapMixer bake material parameter");
            if (!material.HasProperty(parameterName))
            {
                Debug.LogWarning($"[VRCLightmapMixer] Material '{material.name}' does not have shader parameter '{parameterName}'. Value will still be set on the material.");
            }

            material.SetFloat(parameterName, value);
            EditorUtility.SetDirty(material);
            Debug.Log($"[VRCLightmapMixer] Material parameter: {material.name}.{parameterName}={(renderA ? "A" : "B")}:{value}");
        }
    }

    private static string GetArrayValue(string[] values, int index)
    {
        if (values == null || index < 0 || index >= values.Length)
        {
            return "";
        }

        return values[index];
    }

    private static float GetArrayValue(float[] values, int index)
    {
        if (values == null || index < 0 || index >= values.Length)
        {
            return 0f;
        }

        return values[index];
    }

    private static void SetObjectsActive(GameObject[] objects, bool active)
    {
        if (objects == null)
        {
            return;
        }

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null)
            {
                objects[i].SetActive(active);
                EditorUtility.SetDirty(objects[i]);
            }
        }
    }

    private static void LogBakeObjectDiagnostics(LightmapMixer installer)
    {
        LogBakeObjectGroupDiagnostics("A", installer.activeObjectsForLightmapA);
        LogBakeObjectGroupDiagnostics("B", installer.activeObjectsForLightmapB);
    }

    private static void ForceBakeryRefresh(LightmapMixer installer)
    {
        ftRenderLightmap.forceRefresh = true;

        ftLightmapsStorage storage = FindBakeryStorage(installer.gameObject.scene);
        if (storage == null)
        {
            Debug.LogWarning("[VRCLightmapMixer] Bakery storage was not found. Cannot set renderSettingsForceRefresh.");
            return;
        }

        storage.renderSettingsForceRefresh = true;
        storage.Init(true);
        EditorUtility.SetDirty(storage);
        Debug.Log("[VRCLightmapMixer] Bakery force refresh enabled for the next bake.");
    }

    private static void LogBakeObjectGroupDiagnostics(string label, GameObject[] objects)
    {
        int objectCount = 0;
        int activeRootCount = 0;
        int lightCount = 0;
        int activeLightCount = 0;
        int enabledActiveLightCount = 0;
        int bakedLightCount = 0;
        int mixedLightCount = 0;
        int realtimeLightCount = 0;

        if (objects != null)
        {
            for (int i = 0; i < objects.Length; i++)
            {
                GameObject obj = objects[i];
                if (obj == null)
                {
                    continue;
                }

                objectCount++;
                if (obj.activeSelf)
                {
                    activeRootCount++;
                }

                Light[] lights = obj.GetComponentsInChildren<Light>(true);
                for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
                {
                    Light light = lights[lightIndex];
                    if (light == null)
                    {
                        continue;
                    }

                    lightCount++;
                    if (light.gameObject.activeInHierarchy)
                    {
                        activeLightCount++;
                        if (light.enabled)
                        {
                            enabledActiveLightCount++;
                        }
                    }

                    if (light.lightmapBakeType == LightmapBakeType.Baked)
                    {
                        bakedLightCount++;
                    }
                    else if (light.lightmapBakeType == LightmapBakeType.Mixed)
                    {
                        mixedLightCount++;
                    }
                    else
                    {
                        realtimeLightCount++;
                    }
                }
            }
        }

        Debug.Log($"[VRCLightmapMixer] Diagnostics: ActiveObjectsForLightmap{label}: objects={objectCount}, activeSelfObjects={activeRootCount}, lights={lightCount}, activeLights={activeLightCount}, enabledActiveLights={enabledActiveLightCount}, baked={bakedLightCount}, mixed={mixedLightCount}, realtime={realtimeLightCount}.");
    }

    private static void RestoreObjectStates()
    {
        foreach (KeyValuePair<GameObject, bool> savedState in savedObjectStates)
        {
            if (savedState.Key != null)
            {
                savedState.Key.SetActive(savedState.Value);
            }
        }
    }

    private static void EnableConfiguredReflectionProbeObjects(LightmapMixer installer)
    {
        SetObjectsActive(installer.activeReflectionProbesForLightmapA, true);
        SetObjectsActive(installer.activeReflectionProbesForLightmapB, true);
    }

    private static bool HasAnyObject(GameObject[] objects)
    {
        if (objects == null)
        {
            return false;
        }

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void LogCurrentBakeryOutputFiles(LightmapMixer installer)
    {
        if (installer == null)
        {
            Debug.Log("[VRCLightmapMixer] Bakery output diagnostics: installer is null.");
            return;
        }

        string sceneNamePrefix = installer.gameObject.scene.name + "_";
        List<string> sourceFolders = GetCurrentLightVolumeOutputFolders(installer);
        for (int i = 0; i < sourceFolders.Count; i++)
        {
            string sourceFolder = NormalizeAssetFolder(sourceFolders[i]);
            string absoluteSourceFolder = AssetFolderToAbsolutePath(sourceFolder);
            if (!Directory.Exists(absoluteSourceFolder))
            {
                Debug.Log($"[VRCLightmapMixer] Bakery output diagnostics: folder missing: {sourceFolder}");
                continue;
            }

            string[] filePaths = Directory.GetFiles(absoluteSourceFolder, "*_final.*", SearchOption.TopDirectoryOnly);
            List<string> matchingFiles = new List<string>();
            for (int fileIndex = 0; fileIndex < filePaths.Length; fileIndex++)
            {
                if (IsMetaFile(filePaths[fileIndex]))
                {
                    continue;
                }

                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePaths[fileIndex]);
                if (!fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) ||
                    fileNameWithoutExtension.IndexOf("_final", System.StringComparison.Ordinal) < 0 ||
                    GetLightmapIndexFromFileName(fileNameWithoutExtension) < 0)
                {
                    continue;
                }

                matchingFiles.Add(Path.GetFileName(filePaths[fileIndex]));
            }

            int sampleCount = Mathf.Min(matchingFiles.Count, 8);
            string sample = sampleCount == 0 ? "" : string.Join(", ", matchingFiles.GetRange(0, sampleCount).ToArray());
            Debug.Log($"[VRCLightmapMixer] Bakery output diagnostics: folder={sourceFolder}, matchingFinalFiles={matchingFiles.Count}, sample=[{sample}]");
        }
    }

    private static bool HasGeneratedBakeryOutputLightmaps(LightmapMixer installer)
    {
        if (installer == null)
        {
            return false;
        }

        string sceneNamePrefix = installer.gameObject.scene.name + "_";
        List<string> sourceFolders = GetCurrentBakeryOutputFolders(installer);
        for (int i = 0; i < sourceFolders.Count; i++)
        {
            string sourceFolder = NormalizeAssetFolder(sourceFolders[i]);
            string absoluteSourceFolder = AssetFolderToAbsolutePath(sourceFolder);
            if (!Directory.Exists(absoluteSourceFolder))
            {
                continue;
            }

            string[] filePaths = Directory.GetFiles(absoluteSourceFolder, "*_final.*", SearchOption.TopDirectoryOnly);
            for (int fileIndex = 0; fileIndex < filePaths.Length; fileIndex++)
            {
                if (IsMetaFile(filePaths[fileIndex]))
                {
                    continue;
                }

                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePaths[fileIndex]);
                if (fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) &&
                    fileNameWithoutExtension.IndexOf("_final", System.StringComparison.Ordinal) >= 0 &&
                    GetLightmapIndexFromFileName(fileNameWithoutExtension) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Texture2D[] CopyGeneratedBakeryLightmapsFromCurrentOutput(LightmapMixer installer, string outputFolder, bool logMissingFiles)
    {
        EnsureAssetFolder(outputFolder);

        List<string> sourceFolders = GetCurrentBakeryOutputFolders(installer);
        for (int i = 0; i < sourceFolders.Count; i++)
        {
            Texture2D[] generatedTextures = CopyGeneratedBakeryLightmaps(installer, sourceFolders[i], outputFolder, logMissingFiles);
            if (HasAnyTexture(generatedTextures))
            {
                return generatedTextures;
            }
        }

        return new Texture2D[0];
    }

    private static void ClearCurrentBakeryOutputLightmaps(LightmapMixer installer)
    {
        if (installer == null)
        {
            return;
        }

        string sceneNamePrefix = installer.gameObject.scene.name + "_";
        string lightmapAOutputFolder = NormalizeAssetFolder(installer.lightmapAOutputFolder);
        string lightmapBOutputFolder = NormalizeAssetFolder(installer.lightmapBOutputFolder);
        List<string> sourceFolders = GetCurrentBakeryOutputFolders(installer);

        for (int i = 0; i < sourceFolders.Count; i++)
        {
            string sourceFolder = NormalizeAssetFolder(sourceFolders[i]);
            if (sourceFolder == lightmapAOutputFolder || sourceFolder == lightmapBOutputFolder)
            {
                continue;
            }

            DeleteGeneratedLightmapsInFolder(sourceFolder, sceneNamePrefix);
            DeleteGeneratedLightVolumeAssetsInFolder(sourceFolder, sceneNamePrefix);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static Texture2D[] CopyGeneratedBakeryLightmaps(LightmapMixer installer, string sourceFolder, string outputFolder, bool logMissingFiles)
    {
        sourceFolder = NormalizeAssetFolder(sourceFolder);
        outputFolder = NormalizeAssetFolder(outputFolder);
        EnsureAssetFolder(outputFolder);
        AssetDatabase.Refresh();

        string absoluteSourceFolder = AssetFolderToAbsolutePath(sourceFolder);
        if (!Directory.Exists(absoluteSourceFolder))
        {
            if (logMissingFiles)
            {
                Debug.LogWarning($"[VRCLightmapMixer] Bakery output folder does not exist: {sourceFolder}");
            }

            return new Texture2D[0];
        }

        string sceneNamePrefix = installer.gameObject.scene.name + "_";
        string[] filePaths = Directory.GetFiles(absoluteSourceFolder, "*_final.*", SearchOption.TopDirectoryOnly);
        List<string> sourcePaths = new List<string>();

        for (int i = 0; i < filePaths.Length; i++)
        {
            if (IsMetaFile(filePaths[i]))
            {
                continue;
            }

            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePaths[i]);
            if (!fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) ||
                fileNameWithoutExtension.IndexOf("_final", System.StringComparison.Ordinal) < 0)
            {
                continue;
            }

            int lightmapIndex = GetLightmapIndexFromFileName(fileNameWithoutExtension);
            if (lightmapIndex < 0)
            {
                continue;
            }

            sourcePaths.Add(AbsolutePathToAssetPath(filePaths[i]));
        }

        if (sourcePaths.Count == 0)
        {
            if (logMissingFiles)
            {
                Debug.LogWarning($"[VRCLightmapMixer] No generated lightmaps matching {sceneNamePrefix}LM*_final were found in {sourceFolder}.");
            }

            return new Texture2D[0];
        }

        if (sourceFolder != outputFolder)
        {
            DeleteGeneratedLightmapsInFolder(outputFolder, sceneNamePrefix);
        }

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            CopyLightmapAsset(sourcePaths[i], AssetPathCombine(outputFolder, Path.GetFileName(sourcePaths[i])));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[VRCLightmapMixer] Copied {sourcePaths.Count} generated lightmap texture(s) from {sourceFolder} to {outputFolder}.");

        Texture2D[] textures = LoadLightmapsFromFolder(outputFolder);
        if (!HasAnyTexture(textures))
        {
            if (logMissingFiles)
            {
                Debug.LogWarning($"[VRCLightmapMixer] Copied lightmaps were not imported yet: {outputFolder}");
            }

            return new Texture2D[0];
        }

        return textures;
    }

    private static void CopyAndAssignLightVolumeOutput(bool assignA)
    {
        if (!ValidateActiveBake())
        {
            return;
        }

        string outputFolder = assignA ? activeBakeInstaller.lightmapAOutputFolder : activeBakeInstaller.lightmapBOutputFolder;
        Debug.Log($"[VRCLightmapMixer] Step: copy VRCLightVolumes atlas for {(assignA ? "A" : "B")}. outputFolder={outputFolder}");
        CopyGeneratedLightVolumesFromCurrentOutput(activeBakeInstaller, outputFolder, true);
        AssignLightVolumeAtlas(activeBakeInstaller, assignA);
    }

    private static void ScheduleWaitForLightVolumeAtlasAfterFullRender(bool copyAndAssign, bool assignA, System.Action nextStep, string nextStepLabel)
    {
        if (!ValidateActiveBake())
        {
            FinishBakeSequence();
            return;
        }

        if (!ShouldHandleLightVolumeAtlas(activeBakeInstaller))
        {
            if (nextStep != null)
            {
                ScheduleNextBakeStep(nextStep, nextStepLabel);
            }

            return;
        }

        pendingLightVolumeCopyAndAssign = copyAndAssign;
        pendingLightVolumeAssignA = assignA;
        pendingLightVolumeAttempts = 0;
        pendingLightVolumeStartedAt = EditorApplication.timeSinceStartup;
        pendingNextLightVolumeCheckAt = pendingLightVolumeStartedAt;
        pendingLightVolumeNextStep = nextStep;
        pendingLightVolumeNextStepLabel = nextStepLabel;

        EditorApplication.update -= PollLightVolumeAtlasAfterFullRender;
        EditorApplication.update += PollLightVolumeAtlasAfterFullRender;
        Debug.Log($"[VRCLightmapMixer] Waiting for VRCLightVolumes LightVolumeAtlas.asset after Bakery full render. copyAndAssign={copyAndAssign}, assign={(assignA ? "A" : "B")}");
        LogCurrentLightVolumeAtlasCandidates(activeBakeInstaller);
    }

    private static void PollLightVolumeAtlasAfterFullRender()
    {
        if (EditorApplication.timeSinceStartup < pendingNextLightVolumeCheckAt)
        {
            return;
        }

        pendingNextLightVolumeCheckAt = EditorApplication.timeSinceStartup + PendingCopyPollIntervalSeconds;

        if (!ValidateActiveBake())
        {
            ClearPendingLightVolumeWait();
            FinishBakeSequence(true);
            return;
        }

        string sourcePath = FindGeneratedLightVolumeAtlasPath(activeBakeInstaller);
        if (!string.IsNullOrEmpty(sourcePath) && AssetDatabase.LoadAssetAtPath<Texture>(sourcePath) != null)
        {
            Debug.Log($"[VRCLightmapMixer] VRCLightVolumes LightVolumeAtlas.asset is ready: {sourcePath}");
            CompletePendingLightVolumeWait();
            return;
        }

        pendingLightVolumeAttempts++;
        if (pendingLightVolumeAttempts % 10 == 0)
        {
            Debug.Log($"[VRCLightmapMixer] Still waiting for VRCLightVolumes LightVolumeAtlas.asset. attempts={pendingLightVolumeAttempts}");
            LogCurrentLightVolumeAtlasCandidates(activeBakeInstaller);
        }

        if (EditorApplication.timeSinceStartup - pendingLightVolumeStartedAt < MaxPendingLightVolumeWaitSeconds)
        {
            return;
        }

        Debug.LogWarning("[VRCLightmapMixer] VRCLightVolumes LightVolumeAtlas.asset was not generated before timeout. Continuing bake sequence without updating VRCLightVolumes atlas.");
        CompletePendingLightVolumeWait(false);
    }

    private static void CompletePendingLightVolumeWait(bool copyIfRequested = true)
    {
        bool copyAndAssign = pendingLightVolumeCopyAndAssign && copyIfRequested;
        bool assignA = pendingLightVolumeAssignA;
        System.Action nextStep = pendingLightVolumeNextStep;
        string nextStepLabel = pendingLightVolumeNextStepLabel;

        ClearPendingLightVolumeWait();

        if (copyAndAssign)
        {
            CopyAndAssignLightVolumeOutput(assignA);
        }

        if (nextStep != null)
        {
            ScheduleNextBakeStep(nextStep, nextStepLabel);
        }
    }

    private static void ClearPendingLightVolumeWait()
    {
        EditorApplication.update -= PollLightVolumeAtlasAfterFullRender;
        pendingLightVolumeCopyAndAssign = false;
        pendingLightVolumeAssignA = false;
        pendingLightVolumeAttempts = 0;
        pendingLightVolumeStartedAt = 0;
        pendingNextLightVolumeCheckAt = 0;
        pendingLightVolumeNextStep = null;
        pendingLightVolumeNextStepLabel = null;
    }

    private static bool ShouldHandleLightVolumeAtlas(LightmapMixer installer)
    {
        return installer != null && installer.mixedLightVolumeMaterial != null;
    }

    private static void PrepareLightVolumeAtlasOutputForFullRender(LightmapMixer installer, string stepLabel)
    {
        if (!ShouldHandleLightVolumeAtlas(installer))
        {
            return;
        }

        DeleteCurrentLightVolumeAtlasCandidates(installer);
        Debug.Log($"[VRCLightmapMixer] Cleared VRCLightVolumes LightVolumeAtlas.asset before {stepLabel}.");
    }

    private static void CopyGeneratedLightVolumesFromCurrentOutput(LightmapMixer installer, string outputFolder, bool logMissingFiles)
    {
        if (installer == null)
        {
            return;
        }

        outputFolder = NormalizeAssetFolder(outputFolder);
        EnsureAssetFolder(outputFolder);
        AssetDatabase.Refresh();

        CopyGeneratedLightVolumeAtlas(installer, outputFolder, logMissingFiles);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void CopyGeneratedLightVolumeAtlas(LightmapMixer installer, string outputFolder, bool logMissingFiles)
    {
        string sourcePath = FindGeneratedLightVolumeAtlasPath(installer);
        if (string.IsNullOrEmpty(sourcePath))
        {
            if (logMissingFiles)
            {
                Debug.Log("[VRCLightmapMixer] VRCLightVolumes LightVolumeAtlas.asset was not found. Skipping light volume atlas copy.");
            }

            return;
        }

        string destinationPath = AssetPathCombine(outputFolder, "LightVolumeAtlas.asset");
        if (CopyAssetFile(sourcePath, destinationPath))
        {
            Debug.Log($"[VRCLightmapMixer] Copied VRCLightVolumes atlas from {sourcePath} to {destinationPath}.");
        }
    }

    private static string FindGeneratedLightVolumeAtlasPath(LightmapMixer installer)
    {
        if (installer == null)
        {
            return null;
        }

        List<string> candidates = GetCurrentLightVolumeAtlasCandidatePaths(installer);
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = NormalizeAssetPath(candidates[i]);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null ||
                File.Exists(AssetPathToAbsolutePath(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<string> GetCurrentLightVolumeAtlasCandidatePaths(LightmapMixer installer)
    {
        List<string> candidates = new List<string>();
        List<string> sourceFolders = GetCurrentLightVolumeOutputFolders(installer);
        for (int i = 0; i < sourceFolders.Count; i++)
        {
            AddPathCandidate(candidates, AssetPathCombine(sourceFolders[i], "LightVolumeAtlas.asset"));
        }

        return candidates;
    }

    private static void DeleteCurrentLightVolumeAtlasCandidates(LightmapMixer installer)
    {
        List<string> candidates = GetCurrentLightVolumeAtlasCandidatePaths(installer);
        bool deletedAny = false;

        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = NormalizeAssetPath(candidates[i]);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null)
            {
                deletedAny = AssetDatabase.DeleteAsset(candidate) || deletedAny;
                continue;
            }

            string absolutePath = AssetPathToAbsolutePath(candidate);
            if (File.Exists(absolutePath))
            {
                DeletePhysicalAssetFile(candidate);
                deletedAny = true;
            }
        }

        if (deletedAny)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    private static void LogCurrentLightVolumeAtlasCandidates(LightmapMixer installer)
    {
        List<string> candidates = GetCurrentLightVolumeAtlasCandidatePaths(installer);
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = NormalizeAssetPath(candidates[i]);
            bool assetExists = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null;
            bool fileExists = File.Exists(AssetPathToAbsolutePath(candidate));
            Debug.Log($"[VRCLightmapMixer] VRCLightVolumes atlas candidate: {candidate}, assetExists={assetExists}, fileExists={fileExists}");
        }
    }

    private static void AddPathCandidate(List<string> candidates, string path)
    {
        if (candidates == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = NormalizeAssetPath(path);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] == path)
            {
                return;
            }
        }

        candidates.Add(path);
    }

    private static bool CopyAssetFile(string sourcePath, string destinationPath)
    {
        sourcePath = NormalizeAssetPath(sourcePath);
        destinationPath = NormalizeAssetPath(destinationPath);
        EnsureAssetFolder(GetAssetFolder(destinationPath));

        if (sourcePath == destinationPath)
        {
            AssetDatabase.ImportAsset(sourcePath);
            return true;
        }

        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath) != null)
        {
            AssetDatabase.DeleteAsset(destinationPath);
        }
        else
        {
            DeletePhysicalAssetFile(destinationPath);
        }

        if (File.Exists(AssetPathToAbsolutePath(sourcePath)))
        {
            AssetDatabase.ImportAsset(sourcePath);
        }

        if (AssetDatabase.CopyAsset(sourcePath, destinationPath))
        {
            AssetDatabase.ImportAsset(destinationPath);
            return true;
        }

        string absoluteSourcePath = AssetPathToAbsolutePath(sourcePath);
        string absoluteDestinationPath = AssetPathToAbsolutePath(destinationPath);
        if (!File.Exists(absoluteSourcePath))
        {
            Debug.LogWarning($"[VRCLightmapMixer] Source asset does not exist: {sourcePath}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absoluteDestinationPath));
        File.Copy(absoluteSourcePath, absoluteDestinationPath, true);
        AssetDatabase.ImportAsset(destinationPath);
        return true;
    }

    private static bool HasAnyTexture(Texture2D[] textures)
    {
        if (textures == null)
        {
            return false;
        }

        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void DeleteGeneratedLightmapsInFolder(string folderPath, string sceneNamePrefix)
    {
        folderPath = NormalizeAssetFolder(folderPath);
        string absoluteFolderPath = AssetFolderToAbsolutePath(folderPath);
        if (!Directory.Exists(absoluteFolderPath))
        {
            return;
        }

        string[] filePaths = Directory.GetFiles(absoluteFolderPath, "*_final.*", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < filePaths.Length; i++)
        {
            if (IsMetaFile(filePaths[i]))
            {
                DeleteOrphanGeneratedMetaFile(filePaths[i], sceneNamePrefix);
                continue;
            }

            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePaths[i]);
            if (fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) &&
                fileNameWithoutExtension.IndexOf("_final", System.StringComparison.Ordinal) >= 0 &&
                GetLightmapIndexFromFileName(fileNameWithoutExtension) >= 0)
            {
                string assetPath = AbsolutePathToAssetPath(filePaths[i]);
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    DeletePhysicalAssetFile(assetPath);
                }
            }
        }
    }

    private static void DeleteGeneratedLightVolumeAssetsInFolder(string folderPath, string sceneNamePrefix)
    {
        folderPath = NormalizeAssetFolder(folderPath);
        string absoluteFolderPath = AssetFolderToAbsolutePath(folderPath);
        if (!Directory.Exists(absoluteFolderPath))
        {
            return;
        }

        string[] filePaths = Directory.GetFiles(absoluteFolderPath, "*.asset", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < filePaths.Length; i++)
        {
            if (IsMetaFile(filePaths[i]))
            {
                DeleteOrphanGeneratedLightVolumeMetaFile(filePaths[i], sceneNamePrefix);
                continue;
            }

            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePaths[i]);
            if (!IsGeneratedLightVolumeAssetName(fileNameWithoutExtension, sceneNamePrefix))
            {
                continue;
            }

            string assetPath = AbsolutePathToAssetPath(filePaths[i]);
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                DeletePhysicalAssetFile(assetPath);
            }
        }
    }

    private static bool IsGeneratedLightVolumeAssetName(string fileNameWithoutExtension, string sceneNamePrefix)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension) ||
            fileNameWithoutExtension.IndexOf("Light Volume", System.StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) ||
            fileNameWithoutExtension.StartsWith("Light Volume", System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMetaFile(string path)
    {
        return string.Equals(Path.GetExtension(path), ".meta", System.StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteOrphanGeneratedMetaFile(string metaPath, string sceneNamePrefix)
    {
        string assetPathWithoutMeta = metaPath.Substring(0, metaPath.Length - ".meta".Length);
        if (File.Exists(assetPathWithoutMeta))
        {
            return;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(assetPathWithoutMeta);
        if (!fileNameWithoutExtension.StartsWith(sceneNamePrefix, System.StringComparison.Ordinal) ||
            fileNameWithoutExtension.IndexOf("_final", System.StringComparison.Ordinal) < 0 ||
            GetLightmapIndexFromFileName(fileNameWithoutExtension) < 0)
        {
            return;
        }

        File.Delete(metaPath);
    }

    private static void DeleteOrphanGeneratedLightVolumeMetaFile(string metaPath, string sceneNamePrefix)
    {
        string assetPathWithoutMeta = metaPath.Substring(0, metaPath.Length - ".meta".Length);
        if (File.Exists(assetPathWithoutMeta))
        {
            return;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(assetPathWithoutMeta);
        if (!IsGeneratedLightVolumeAssetName(fileNameWithoutExtension, sceneNamePrefix))
        {
            return;
        }

        File.Delete(metaPath);
    }

    private static Texture2D[] CopyStoredBakeryLightmaps(LightmapMixer installer, string outputFolder)
    {
        ftLightmapsStorage storage = FindBakeryStorage(installer.gameObject.scene);
        if (storage == null || storage.maps == null)
        {
            Debug.LogWarning("[VRCLightmapMixer] Bakery lightmap storage was not found. No textures were assigned.");
            return new Texture2D[0];
        }

        EnsureAssetFolder(outputFolder);
        Texture2D[] copiedTextures = new Texture2D[storage.maps.Count];

        for (int i = 0; i < storage.maps.Count; i++)
        {
            Texture2D sourceTexture = storage.maps[i];
            if (sourceTexture == null)
            {
                continue;
            }

            string sourcePath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(sourcePath))
            {
                continue;
            }

            copiedTextures[i] = CopyLightmapAsset(sourcePath, AssetPathCombine(outputFolder, Path.GetFileName(sourcePath)));
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return copiedTextures;
    }

    private static Texture2D CopyLightmapAsset(string sourcePath, string destinationPath)
    {
        sourcePath = NormalizeAssetPath(sourcePath);
        destinationPath = NormalizeAssetPath(destinationPath);
        if (sourcePath == destinationPath)
        {
            AssetDatabase.ImportAsset(sourcePath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
        }

        if (AssetDatabase.LoadAssetAtPath<Texture2D>(destinationPath) != null)
        {
            AssetDatabase.DeleteAsset(destinationPath);
        }
        else
        {
            DeletePhysicalAssetFile(destinationPath);
        }

        if (File.Exists(AssetPathToAbsolutePath(sourcePath)))
        {
            AssetDatabase.ImportAsset(sourcePath);
        }

        if (AssetDatabase.CopyAsset(sourcePath, destinationPath))
        {
            AssetDatabase.ImportAsset(destinationPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(destinationPath);
        }

        string absoluteSourcePath = AssetPathToAbsolutePath(sourcePath);
        string absoluteDestinationPath = AssetPathToAbsolutePath(destinationPath);
        if (!File.Exists(absoluteSourcePath))
        {
            Debug.LogWarning($"[VRCLightmapMixer] Source lightmap does not exist: {sourcePath}");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absoluteDestinationPath));
        File.Copy(absoluteSourcePath, absoluteDestinationPath, true);
        AssetDatabase.ImportAsset(destinationPath);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(destinationPath);
    }

    private static List<string> GetCurrentBakeryOutputFolders(LightmapMixer installer)
    {
        List<string> folders = new List<string>();
        AddBakeryOutputFolder(folders, ftRenderLightmap.outputPathFull);

        ftLightmapsStorage storage = FindBakeryStorage(installer.gameObject.scene);
        if (storage != null)
        {
            AddBakeryOutputFolder(folders, storage.renderSettingsOutPath);
        }

        AddBakeryOutputFolder(folders, ftRenderLightmap.outputPath);
        AddBakeryOutputFolder(folders, "BakeryLightmaps");
        return folders;
    }

    private static List<string> GetCurrentLightVolumeOutputFolders(LightmapMixer installer)
    {
        List<string> folders = new List<string>();
        if (installer != null)
        {
            Scene scene = installer.gameObject.scene;
            string sceneName = scene.name;
            string scenePath = scene.path;

            if (!string.IsNullOrEmpty(scenePath))
            {
                string sceneFolder = NormalizeAssetFolder(Path.GetDirectoryName(scenePath));
                AddBakeryOutputFolder(folders, sceneFolder + "/" + sceneName + "/VRCLightVolumes");
                AddBakeryOutputFolder(folders, sceneFolder + "/" + sceneName);
                AddBakeryOutputFolder(folders, sceneFolder + "/VRCLightVolumes");
                AddBakeryOutputFolder(folders, sceneFolder);
            }

            AddBakeryOutputFolder(folders, "Assets/Scenes/" + sceneName + "/VRCLightVolumes");
            AddBakeryOutputFolder(folders, "Assets/Scenes/" + sceneName);
        }

        List<string> bakeryOutputFolders = GetCurrentBakeryOutputFolders(installer);
        for (int i = 0; i < bakeryOutputFolders.Count; i++)
        {
            AddBakeryOutputFolder(folders, bakeryOutputFolders[i]);
        }

        return folders;
    }

    private static void AddBakeryOutputFolder(List<string> folders, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        folder = NormalizeAssetFolder(folder);
        for (int i = 0; i < folders.Count; i++)
        {
            if (folders[i] == folder)
            {
                return;
            }
        }

        folders.Add(folder);
    }

    private static void AssignLightmapTextures(LightmapMixer installer, bool assignA, Texture2D[] textures)
    {
        if (installer == null)
        {
            return;
        }

        Undo.RecordObject(installer, assignA ? "Assign lightmap A textures" : "Assign lightmap B textures");
        if (assignA)
        {
            installer.lightMapATextures = textures ?? new Texture2D[0];
        }
        else
        {
            installer.lightMapBTextures = textures ?? new Texture2D[0];
        }

        EditorUtility.SetDirty(installer);
        PrefabUtility.RecordPrefabInstancePropertyModifications(installer);
    }

    private static void AssignLightVolumeAtlas(LightmapMixer installer, bool assignA)
    {
        if (installer == null || installer.mixedLightVolumeMaterial == null)
        {
            return;
        }

        string outputFolder = assignA ? installer.lightmapAOutputFolder : installer.lightmapBOutputFolder;
        Texture lightVolumeAtlas = LoadLightVolumeAtlasFromFolder(outputFolder);
        if (lightVolumeAtlas == null)
        {
            Debug.LogWarning($"[VRCLightmapMixer] VRCLightVolumes atlas was not found in {NormalizeAssetFolder(outputFolder)}. Mixed light volume material was not updated.");
            return;
        }

        string propertyName = assignA ? LightVolumeTextureAProperty : LightVolumeTextureBProperty;
        Material material = installer.mixedLightVolumeMaterial;
        Undo.RecordObject(material, assignA ? "Assign light volume atlas A" : "Assign light volume atlas B");

        if (!material.HasProperty(propertyName))
        {
            Debug.LogWarning($"[VRCLightmapMixer] Mixed light volume material '{material.name}' does not have shader property '{propertyName}'.");
        }

        material.SetTexture(propertyName, lightVolumeAtlas);
        EditorUtility.SetDirty(material);
        Debug.Log($"[VRCLightmapMixer] Assigned VRCLightVolumes atlas {(assignA ? "A" : "B")} to {material.name}.{propertyName}: {AssetDatabase.GetAssetPath(lightVolumeAtlas)}");
    }

    private static void AssignMixedLightVolumeAtlasToManager(LightmapMixer installer)
    {
        if (installer == null || installer.lightVolumeManager == null || installer.mixedLightVolumeAtlas == null)
        {
            return;
        }

        SerializedObject serializedManager = new SerializedObject(installer.lightVolumeManager);
        SerializedProperty atlasProperty = serializedManager.FindProperty("LightVolumeAtlas");
        if (atlasProperty != null && atlasProperty.propertyType == SerializedPropertyType.ObjectReference)
        {
            atlasProperty.objectReferenceValue = installer.mixedLightVolumeAtlas;
            serializedManager.ApplyModifiedProperties();
            EditorUtility.SetDirty(installer.lightVolumeManager);
            PrefabUtility.RecordPrefabInstancePropertyModifications(installer.lightVolumeManager);
            Debug.Log($"[VRCLightmapMixer] Assigned mixed VRCLightVolumes atlas to Light Volume Manager: {AssetDatabase.GetAssetPath(installer.mixedLightVolumeAtlas)}");
            return;
        }

        System.Reflection.FieldInfo atlasField = installer.lightVolumeManager.GetType().GetField(
            "LightVolumeAtlas",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        if (atlasField != null && typeof(Texture).IsAssignableFrom(atlasField.FieldType))
        {
            atlasField.SetValue(installer.lightVolumeManager, installer.mixedLightVolumeAtlas);
            EditorUtility.SetDirty(installer.lightVolumeManager);
            PrefabUtility.RecordPrefabInstancePropertyModifications(installer.lightVolumeManager);
            Debug.Log($"[VRCLightmapMixer] Assigned mixed VRCLightVolumes atlas to Light Volume Manager by reflection: {AssetDatabase.GetAssetPath(installer.mixedLightVolumeAtlas)}");
            return;
        }

        Debug.LogWarning("[VRCLightmapMixer] Light Volume Manager does not have a Texture field named LightVolumeAtlas. MixedLightVolumeAtlas was not assigned.");
    }

    private static Texture LoadLightVolumeAtlasFromFolder(string folderPath)
    {
        string assetPath = AssetPathCombine(folderPath, "LightVolumeAtlas.asset");
        return AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
    }

    private static Texture2D[] LoadLightmapsFromFolder(string folderPath)
    {
        folderPath = NormalizeAssetFolder(folderPath);
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            Debug.LogWarning($"[VRCLightmapMixer] Lightmap folder does not exist: {folderPath}");
            return new Texture2D[0];
        }

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
        List<Texture2D> unordered = new List<Texture2D>();
        List<int> parsedIndices = new List<int>();
        bool hasParsedIndex = false;

        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null)
            {
                continue;
            }

            unordered.Add(texture);
            int lightmapIndex = GetLightmapIndexFromFileName(Path.GetFileNameWithoutExtension(assetPath));
            parsedIndices.Add(lightmapIndex);
            hasParsedIndex |= lightmapIndex >= 0;
        }

        if (!hasParsedIndex)
        {
            unordered.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
            return unordered.ToArray();
        }

        int maxIndex = -1;
        for (int i = 0; i < parsedIndices.Count; i++)
        {
            if (parsedIndices[i] > maxIndex)
            {
                maxIndex = parsedIndices[i];
            }
        }

        Texture2D[] textures = new Texture2D[maxIndex + 1];
        for (int i = 0; i < unordered.Count; i++)
        {
            int lightmapIndex = parsedIndices[i];
            if (lightmapIndex >= 0)
            {
                textures[lightmapIndex] = unordered[i];
            }
        }

        return textures;
    }

    private static int GetLightmapIndexFromFileName(string fileName)
    {
        int lmIndex = IndexAfterToken(fileName, "_LM");
        if (lmIndex >= 0)
        {
            return lmIndex;
        }

        return IndexAfterToken(fileName, "_LMA");
    }

    private static int IndexAfterToken(string fileName, string token)
    {
        int tokenIndex = fileName.IndexOf(token, System.StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return -1;
        }

        int start = tokenIndex + token.Length;
        int end = start;
        while (end < fileName.Length && char.IsDigit(fileName[end]))
        {
            end++;
        }

        if (end == start)
        {
            return -1;
        }

        int result;
        return int.TryParse(fileName.Substring(start, end - start), out result) ? result : -1;
    }

    private static ftLightmapsStorage FindBakeryStorage(Scene scene)
    {
        GameObject[] rootObjects = scene.GetRootGameObjects();
        for (int i = 0; i < rootObjects.Length; i++)
        {
            ftLightmapsStorage storage = rootObjects[i].GetComponentInChildren<ftLightmapsStorage>(true);
            if (storage != null)
            {
                return storage;
            }
        }

        return null;
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        folderPath = NormalizeAssetFolder(folderPath);
        Directory.CreateDirectory(AssetFolderToAbsolutePath(folderPath));
        AssetDatabase.Refresh();

        if (AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string[] parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }

    private static string NormalizeAssetFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "Assets/BakeryLightmaps";
        }

        folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (folderPath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
        {
            folderPath = "Assets" + folderPath.Substring(dataPath.Length);
        }

        if (!folderPath.StartsWith("Assets/") && folderPath != "Assets")
        {
            folderPath = "Assets/" + folderPath;
        }

        return folderPath;
    }

    private static string NormalizeAssetPath(string assetPath)
    {
        assetPath = assetPath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (assetPath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
        {
            assetPath = "Assets" + assetPath.Substring(dataPath.Length);
        }

        return assetPath;
    }

    private static string AssetFolderToAbsolutePath(string folderPath)
    {
        folderPath = NormalizeAssetFolder(folderPath);
        if (folderPath == "Assets")
        {
            return Application.dataPath;
        }

        return Path.Combine(Application.dataPath, folderPath.Substring("Assets/".Length));
    }

    private static string AssetPathToAbsolutePath(string assetPath)
    {
        assetPath = NormalizeAssetPath(assetPath);
        if (assetPath == "Assets")
        {
            return Application.dataPath;
        }

        return Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
    }

    private static string AbsolutePathToAssetPath(string absolutePath)
    {
        absolutePath = absolutePath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (!absolutePath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
        {
            return absolutePath;
        }

        return "Assets" + absolutePath.Substring(dataPath.Length);
    }

    private static void DeletePhysicalAssetFile(string assetPath)
    {
        string absolutePath = AssetPathToAbsolutePath(assetPath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        string metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private static string AssetPathCombine(string folderPath, string fileName)
    {
        return NormalizeAssetFolder(folderPath) + "/" + fileName;
    }

    private static string GetAssetFolder(string assetPath)
    {
        assetPath = NormalizeAssetPath(assetPath);
        int slashIndex = assetPath.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return "Assets";
        }

        return assetPath.Substring(0, slashIndex);
    }
}

