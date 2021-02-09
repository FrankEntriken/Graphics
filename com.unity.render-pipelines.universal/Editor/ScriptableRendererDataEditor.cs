using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting.APIUpdating;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal
{
    static class RemoveRendererDataDialogText
    {
        public static readonly string title = "Remove Renderer Data from URP Asset";
        public static readonly string message = "This change might change the renderer data list entries";
        public static readonly string proceed = "Proceed";
        public static readonly string ok = "Ok";
        public static readonly string cancel = "Cancel";
    }

    public class AssignToRendererDataWindow : EditorWindow
    {
        public static AssignToRendererDataWindow Instance { get; private set; }
        public static bool RunOnce;
        static ScriptableRendererData rendererData;

        public static void ShowWindow(ScriptableRendererData renderer)
        {
            // This is the renderer data that we care about
            rendererData = renderer;
            // Get existing open window or if none, make a new one:
            AssignToRendererDataWindow window = (AssignToRendererDataWindow)GetWindow(typeof(AssignToRendererDataWindow));
            window.titleContent = new GUIContent("Assign to Render Pipeline Asset");
            Init();
            window.Show();
        }

        void OnEnable()
        {
            Instance = this;
        }

        static List<UniversalRenderPipelineAsset> rpaList = new List<UniversalRenderPipelineAsset>();
        Dictionary<UniversalRenderPipelineAsset, bool> rpaDict = new Dictionary<UniversalRenderPipelineAsset, bool>();
        static void Init()
        {
            RunOnce = false;
            rpaList = GetAllUniversalRenderPipelineAssets();
        }

        void OnGUI()
        {
            // This should only run once to populate the assigned list
            if (!RunOnce)
            {
                RunOnce = true;
                GetAllURPAssetsAssignedToDict();
            }

            foreach (UniversalRenderPipelineAsset urpAsset in rpaDict.Keys.ToArray())
            {
                float width = position.width - 25f;
                EditorGUIUtility.labelWidth = width;
                GUIContent label = new GUIContent(urpAsset.name, AssetDatabase.GetAssetPath(urpAsset));
                rpaDict[urpAsset] = EditorGUILayout.Toggle(label, rpaDict[urpAsset]);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Confirm"))
            {
                UpdateRendererAndRPAssets();
                Close();
            }
            else if (GUILayout.Button("Cancel"))
            {
                Close();
            }
            GUILayout.EndHorizontal();
        }

        static List<UniversalRenderPipelineAsset> GetAllUniversalRenderPipelineAssets()
        {
            List<UniversalRenderPipelineAsset> rpaList = new List<UniversalRenderPipelineAsset>();
            var rpAssets = AssetDatabase.FindAssets("t:RenderPipelineAsset");
            foreach (string asset in rpAssets)
            {
                var path = AssetDatabase.GUIDToAssetPath(asset);
                UniversalRenderPipelineAsset urpAsset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path) as UniversalRenderPipelineAsset;
                if (urpAsset != null)
                {
                    rpaList.Add(urpAsset);
                }
            }

            return rpaList;
        }

        void GetAllURPAssetsAssignedToDict()
        {
            foreach (var asset in rpaList)
            {
                var renderers = asset.RendererDataList;
                rpaDict[asset] = false;
                foreach (var renderer in renderers)
                {
                    if (rendererData == renderer)
                    {
                        rpaDict[asset] = true;
                    }
                }
            }
        }

        void UpdateRendererAndRPAssets()
        {
            foreach (UniversalRenderPipelineAsset renderPipelineAsset in rpaDict.Keys)
            {
                if (rpaDict[renderPipelineAsset])
                {
                    renderPipelineAsset.AddRendererToRendererDataList(rendererData);
                }
                else
                {
                    // If this returns true, then we can remove an entry and should tell the user that the list will change.
                    if (renderPipelineAsset.CanRemoveFromRendererDataList(rendererData))
                    {
                        if (EditorUtility.DisplayDialog(RemoveRendererDataDialogText.title, RemoveRendererDataDialogText.message, RemoveRendererDataDialogText.proceed, RemoveRendererDataDialogText.cancel))
                        {
                            renderPipelineAsset.RemoveRendererFromRendererDataList(rendererData);
                        }
                    }
                }
            }
        }

        // void AssignRendererToRPAsset(string rpName)
        // {
        //     foreach (var asset in rpAssets)
        //     {
        //         if (asset.name == rpName)
        //         {
        //             Debug.Log("Adding");
        //             asset.AddRendererToRendererDataList(target as ScriptableRendererData);
        //             return;
        //         }
        //     }
        // }
    }

    [CustomEditor(typeof(ScriptableRendererData), true)]
    [MovedFrom("UnityEditor.Rendering.LWRP")] public class ScriptableRendererDataEditor : Editor
    {
        class Styles
        {
            public static readonly GUIContent RenderFeatures =
                new GUIContent("Renderer Features",
                    "Features to include in this renderer.\nTo add or remove features, use the plus and minus at the bottom of this box.");

            public static readonly GUIContent PassNameField =
                new GUIContent("Name", "Render pass name. This name is the name displayed in Frame Debugger.");

            public static readonly GUIContent MissingFeature = new GUIContent("Missing RendererFeature",
                "Missing reference, due to compilation issues or missing files. you can attempt auto fix or choose to remove the feature.");

            public static GUIStyle BoldLabelSimple;

            static Styles()
            {
                BoldLabelSimple = new GUIStyle(EditorStyles.label);
                BoldLabelSimple.fontStyle = FontStyle.Bold;
            }
        }

        private SerializedProperty m_RendererFeatures;
        private SerializedProperty m_RendererFeaturesMap;
        private SerializedProperty m_FalseBool;
        [SerializeField] private bool falseBool = false;
        List<Editor> m_Editors = new List<Editor>();

        string[] options = {};
        private void OnEnable()
        {
            m_RendererFeatures = serializedObject.FindProperty(nameof(ScriptableRendererData.m_RendererFeatures));
            m_RendererFeaturesMap = serializedObject.FindProperty(nameof(ScriptableRendererData.m_RendererFeatureMap));
            var editorObj = new SerializedObject(this);
            m_FalseBool = editorObj.FindProperty(nameof(falseBool));
            UpdateEditorList();
        }

        protected override void OnHeaderGUI()
        {
            base.OnHeaderGUI();
            // New button in header to assign asset
            if (GUILayout.Button("Assign to Render Pipeline Asset..."))
            {
                AssignToRendererDataWindow.ShowWindow(target as ScriptableRendererData);
            }
            // Need to add some padding here because the addressables tickbox has taken up ome space so it squishes this button otherwise
            GUILayout.Space(10f);
        }

        private void OnDisable()
        {
            ClearEditorsList();
        }

        public override void OnInspectorGUI()
        {
            if (m_RendererFeatures == null)
                OnEnable();
            else if (m_RendererFeatures.arraySize != m_Editors.Count)
                UpdateEditorList();

            serializedObject.Update();
            DrawRendererFeatureList();
        }

        private void DrawRendererFeatureList()
        {
            EditorGUILayout.LabelField(Styles.RenderFeatures, EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (m_RendererFeatures.arraySize == 0)
            {
                EditorGUILayout.HelpBox("No Renderer Features added", MessageType.Info);
            }
            else
            {
                //Draw List
                CoreEditorUtils.DrawSplitter();
                for (int i = 0; i < m_RendererFeatures.arraySize; i++)
                {
                    SerializedProperty renderFeaturesProperty = m_RendererFeatures.GetArrayElementAtIndex(i);
                    DrawRendererFeature(i, ref renderFeaturesProperty);
                    CoreEditorUtils.DrawSplitter();
                }
            }
            EditorGUILayout.Space();

            //Add renderer
            if (GUILayout.Button("Add Renderer Feature", EditorStyles.miniButton))
            {
                AddPassMenu();
            }
        }

        private void DrawRendererFeature(int index, ref SerializedProperty renderFeatureProperty)
        {
            Object rendererFeatureObjRef = renderFeatureProperty.objectReferenceValue;
            if (rendererFeatureObjRef != null)
            {
                bool hasChangedProperties = false;
                string title = ObjectNames.GetInspectorTitle(rendererFeatureObjRef);

                // Get the serialized object for the editor script & update it
                Editor rendererFeatureEditor = m_Editors[index];
                SerializedObject serializedRendererFeaturesEditor = rendererFeatureEditor.serializedObject;
                serializedRendererFeaturesEditor.Update();

                // Foldout header
                EditorGUI.BeginChangeCheck();
                SerializedProperty activeProperty = serializedRendererFeaturesEditor.FindProperty("m_Active");
                bool displayContent = CoreEditorUtils.DrawHeaderToggle(title, renderFeatureProperty, activeProperty, pos => OnContextClick(pos, index));
                hasChangedProperties |= EditorGUI.EndChangeCheck();

                // ObjectEditor
                if (displayContent)
                {
                    EditorGUI.BeginChangeCheck();
                    SerializedProperty nameProperty = serializedRendererFeaturesEditor.FindProperty("m_Name");
                    nameProperty.stringValue = ValidateName(EditorGUILayout.DelayedTextField(Styles.PassNameField, nameProperty.stringValue));
                    if (EditorGUI.EndChangeCheck())
                    {
                        hasChangedProperties = true;

                        // We need to update sub-asset name
                        rendererFeatureObjRef.name = nameProperty.stringValue;
                        AssetDatabase.SaveAssets();

                        // Triggers update for sub-asset name change
                        ProjectWindowUtil.ShowCreatedAsset(target);
                    }

                    EditorGUI.BeginChangeCheck();
                    rendererFeatureEditor.OnInspectorGUI();
                    hasChangedProperties |= EditorGUI.EndChangeCheck();

                    EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
                }

                // Apply changes and save if the user has modified any settings
                if (hasChangedProperties)
                {
                    serializedRendererFeaturesEditor.ApplyModifiedProperties();
                    serializedObject.ApplyModifiedProperties();
                    ForceSave();
                }
            }
            else
            {
                CoreEditorUtils.DrawHeaderToggle(Styles.MissingFeature, renderFeatureProperty, m_FalseBool, pos => OnContextClick(pos, index));
                m_FalseBool.boolValue = false; // always make sure false bool is false
                EditorGUILayout.HelpBox(Styles.MissingFeature.tooltip, MessageType.Error);
                if (GUILayout.Button("Attempt Fix", EditorStyles.miniButton))
                {
                    ScriptableRendererData data = target as ScriptableRendererData;
                    data.ValidateRendererFeatures();
                }
            }
        }

        private void OnContextClick(Vector2 position, int id)
        {
            var menu = new GenericMenu();

            if (id == 0)
                menu.AddDisabledItem(EditorGUIUtility.TrTextContent("Move Up"));
            else
                menu.AddItem(EditorGUIUtility.TrTextContent("Move Up"), false, () => MoveComponent(id, -1));

            if (id == m_RendererFeatures.arraySize - 1)
                menu.AddDisabledItem(EditorGUIUtility.TrTextContent("Move Down"));
            else
                menu.AddItem(EditorGUIUtility.TrTextContent("Move Down"), false, () => MoveComponent(id, 1));

            menu.AddSeparator(string.Empty);
            menu.AddItem(EditorGUIUtility.TrTextContent("Remove"), false, () => RemoveComponent(id));

            menu.DropDown(new Rect(position, Vector2.zero));
        }

        private void AddPassMenu()
        {
            GenericMenu menu = new GenericMenu();
            TypeCache.TypeCollection types = TypeCache.GetTypesDerivedFrom<ScriptableRendererFeature>();
            foreach (Type type in types)
            {
                var data = target as ScriptableRendererData;
                if (data.DuplicateFeatureCheck(type))
                {
                    continue;
                }

                string path = GetMenuNameFromType(type);
                menu.AddItem(new GUIContent(path), false, AddComponent, type.Name);
            }
            menu.ShowAsContext();
        }

        private void AddComponent(object type)
        {
            serializedObject.Update();

            ScriptableObject component = CreateInstance((string)type);
            component.name = $"New{(string)type}";
            Undo.RegisterCreatedObjectUndo(component, "Add Renderer Feature");

            // Store this new effect as a sub-asset so we can reference it safely afterwards
            // Only when we're not dealing with an instantiated asset
            if (EditorUtility.IsPersistent(target))
            {
                AssetDatabase.AddObjectToAsset(component, target);
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out var guid, out long localId);

            // Grow the list first, then add - that's how serialized lists work in Unity
            m_RendererFeatures.arraySize++;
            SerializedProperty componentProp = m_RendererFeatures.GetArrayElementAtIndex(m_RendererFeatures.arraySize - 1);
            componentProp.objectReferenceValue = component;

            // Update GUID Map
            m_RendererFeaturesMap.arraySize++;
            SerializedProperty guidProp = m_RendererFeaturesMap.GetArrayElementAtIndex(m_RendererFeaturesMap.arraySize - 1);
            guidProp.longValue = localId;
            UpdateEditorList();
            serializedObject.ApplyModifiedProperties();

            // Force save / refresh
            if (EditorUtility.IsPersistent(target))
            {
                ForceSave();
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void RemoveComponent(int id)
        {
            SerializedProperty property = m_RendererFeatures.GetArrayElementAtIndex(id);
            Object component = property.objectReferenceValue;
            property.objectReferenceValue = null;

            Undo.SetCurrentGroupName(component == null ? "Remove Renderer Feature" : $"Remove {component.name}");

            // remove the array index itself from the list
            m_RendererFeatures.DeleteArrayElementAtIndex(id);
            m_RendererFeaturesMap.DeleteArrayElementAtIndex(id);
            UpdateEditorList();
            serializedObject.ApplyModifiedProperties();

            // Destroy the setting object after ApplyModifiedProperties(). If we do it before, redo
            // actions will be in the wrong order and the reference to the setting object in the
            // list will be lost.
            if (component != null)
            {
                Undo.DestroyObjectImmediate(component);
            }

            // Force save / refresh
            ForceSave();
        }

        private void MoveComponent(int id, int offset)
        {
            Undo.SetCurrentGroupName("Move Render Feature");
            serializedObject.Update();
            m_RendererFeatures.MoveArrayElement(id, id + offset);
            m_RendererFeaturesMap.MoveArrayElement(id, id + offset);
            UpdateEditorList();
            serializedObject.ApplyModifiedProperties();

            // Force save / refresh
            ForceSave();
        }

        private string GetMenuNameFromType(Type type)
        {
            var path = type.Name;
            if (type.Namespace != null)
            {
                if (type.Namespace.Contains("Experimental"))
                    path += " (Experimental)";
            }

            // Inserts blank space in between camel case strings
            return Regex.Replace(Regex.Replace(path, "([a-z])([A-Z])", "$1 $2", RegexOptions.Compiled),
                "([A-Z])([A-Z][a-z])", "$1 $2", RegexOptions.Compiled);
        }

        private string ValidateName(string name)
        {
            name = Regex.Replace(name, @"[^a-zA-Z0-9 ]", "");
            return name;
        }

        private void UpdateEditorList()
        {
            ClearEditorsList();
            for (int i = 0; i < m_RendererFeatures.arraySize; i++)
            {
                m_Editors.Add(CreateEditor(m_RendererFeatures.GetArrayElementAtIndex(i).objectReferenceValue));
            }
        }

        //To avoid leaking memory we destroy editors when we clear editors list
        private void ClearEditorsList()
        {
            for (int i = m_Editors.Count - 1; i >= 0; --i)
            {
                DestroyImmediate(m_Editors[i]);
            }
            m_Editors.Clear();
        }

        private void ForceSave()
        {
            EditorUtility.SetDirty(target);
        }
    }
}
