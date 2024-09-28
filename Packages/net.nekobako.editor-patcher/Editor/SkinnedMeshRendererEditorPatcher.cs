using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditorInternal;
using UnityEngine;
using HarmonyLib;

namespace net.nekobako.EditorPatcher.Editor
{
    internal static class SkinnedMeshRendererEditorPatcher
    {
        private const string k_PatchId = "net.nekobako.editor-patcher.skinned-mesh-renderer-editor-patcher";
        private const string k_MenuPath = "Tools/Editor Patcher/Skinned Mesh Renderer Editor";

        private static readonly Type s_TargetType = AccessTools.TypeByName("UnityEditor.SkinnedMeshRendererEditor");
        private static readonly Type s_GUIViewType = AccessTools.TypeByName("UnityEditor.GUIView");
        private static Dictionary<UnityEditor.Editor, BlendShapesDrawer> s_BlendShapesDrawers = new Dictionary<UnityEditor.Editor, BlendShapesDrawer>();

        private class BlendShape
        {
            public readonly int Index = 0;
            public readonly string Name = string.Empty;
            public readonly GUIContent Content = null;
            public readonly float MinWeight = 0.0f;
            public readonly float MaxWeight = 0.0f;

            public BlendShape(Mesh mesh, int index)
            {
                Index = index;
                Name = mesh.GetBlendShapeName(index);
                Content = new GUIContent(Name);
                MinWeight = 0.0f;
                MaxWeight = 0.0f;
                for (var i = 0; i < mesh.GetBlendShapeFrameCount(index); i++)
                {
                    var weight = mesh.GetBlendShapeFrameWeight(index, i);
                    MinWeight = Mathf.Min(weight, MinWeight);
                    MaxWeight = Mathf.Max(weight, MaxWeight);
                }
            }
        }

        private class BlendShapeGroup
        {
            public readonly string Name = string.Empty;
            public readonly List<BlendShape> BlendShapes = new List<BlendShape>();

            public BlendShapeGroup(string name)
            {
                Name = name;
            }
        }

        private class BlendShapesDrawer : TreeView
        {
            private const string k_DefaultGroupName = "Default";
            private const string k_GroupNamePattern = @"^(?:\W|\p{Pc}){3,}(.*?)(?:\W|\p{Pc}){3,}$";
            private const int k_RowHeight = 24;
            private const int k_LineHeight = 22;

            private static readonly GUIContent s_PropertyContent = new GUIContent("BlendShapes");
            private static readonly GUIContent s_ClampWeightsInfoContent = Traverse.Create(s_TargetType)
                .Type("Styles")
                .Field("legacyClampBlendShapeWeightsInfo")
                .GetValue<GUIContent>();
            private static readonly GUIStyle s_HeaderStyle = new GUIStyle("RL Header")
            {
                fixedHeight = k_LineHeight + 8,
                padding = new RectOffset(6, 6, 4, 4),
            };
            private static readonly GUIStyle s_BackgroundStyle = new GUIStyle("RL Background")
            {
                fixedHeight = 0,
                padding = new RectOffset(6, 6, 4, 4),
            };
            private static readonly GUIStyle s_SearchFieldStyle = new GUIStyle("SearchTextField")
            {
                fixedHeight = 0,
                margin = new RectOffset(2, 2, 2, 2),
            };
            private static readonly GUIStyle s_SearchFieldCancelButtonStyle = new GUIStyle("SearchCancelButton")
            {
                fixedHeight = 0,
                margin = new RectOffset(2, 2, 2, 2),
            };
            private static readonly GUIStyle s_SearchFieldCancelButtonEmptyStyle = new GUIStyle("SearchCancelButtonEmpty")
            {
                fixedHeight = 0,
                margin = new RectOffset(2, 2, 2, 2),
            };
            private static readonly GUIStyle s_PopupStyle = new GUIStyle("MiniPopup")
            {
                fixedHeight = 0,
                margin = new RectOffset(2, 2, 2, 2),
            };
            private static readonly GUIStyle s_ToggleStyle = new GUIStyle("LargeButton")
            {
                fixedHeight = 0,
                margin = new RectOffset(2, 2, 2, 2),
            };
            private static readonly GUIStyle s_SliderStyle = new GUIStyle()
            {
                overflow = new RectOffset(0, 0, (2 - k_RowHeight) / 2, (2 - k_RowHeight) / 2),
            };
            private static readonly GUIStyle s_SliderThumbStyle = new GUIStyle("HorizontalSliderThumb")
            {
                margin = new RectOffset(0, 0, (k_RowHeight - 10) / 2, (k_RowHeight - 10) / 2),
            };
            private static readonly GUIStyle s_SliderThumbExtentStyle = new GUIStyle("HorizontalSliderThumbExtent")
            {
                margin = new RectOffset(0, 0, (k_RowHeight - 10) / 2, (k_RowHeight - 10) / 2),
            };
            private static readonly GUIStyle s_SliderNumberFieldStyle = new GUIStyle("TextField")
            {
                fixedHeight = k_LineHeight,
                alignment = TextAnchor.MiddleLeft,
            };

            private class Item : TreeViewItem
            {
                public readonly BlendShape BlendShape = null;

                public Item(BlendShape shape) : base(shape.Index, 0, shape.Name)
                {
                    BlendShape = shape;
                }
            }

            private readonly List<BlendShapeGroup> m_BlendShapeGroups = new List<BlendShapeGroup>();
            private readonly SearchField m_SearchField = new SearchField();

            private SerializedProperty m_Property = null;
            private Mesh m_Mesh = null;
            private string m_SearchText = string.Empty;
            private string[] m_GroupNames = Array.Empty<string>();
            private int m_GroupMask = ~0;
            private bool m_ShowZero = true;

            public BlendShapesDrawer() : base(new TreeViewState())
            {
                rowHeight = k_RowHeight;
                useScrollView = false;
#if UNITY_2022_1_OR_NEWER
                enableItemHovering = true;
#endif
            }

            public void Draw(SerializedProperty property)
            {
                var rect = EditorGUILayout.GetControlRect();
                var content = EditorGUI.BeginProperty(rect, TempContent.Text("BlendShapes"), property);

                property.isExpanded = EditorGUI.BeginFoldoutHeaderGroup(rect, property.isExpanded, content);
                EditorGUI.EndFoldoutHeaderGroup();

                EditorGUI.EndProperty();

                if (!property.isExpanded)
                {
                    return;
                }

                if (property.serializedObject.targetObjects.Length > 1)
                {
                    GUILayout.Label("Multi-object editing not supported.", EditorStyles.helpBox);
                    return;
                }

                if (PlayerSettings.legacyClampBlendShapeWeights)
                {
                    EditorGUILayout.HelpBox(s_ClampWeightsInfoContent.text, MessageType.Info);
                }

                m_Property = property;

                var renderer = property.serializedObject.targetObject as SkinnedMeshRenderer;
                var mesh = renderer.sharedMesh;
                if (mesh != m_Mesh)
                {
                    m_Mesh = mesh;

                    UpdateBlendShapeGroups();
                    Reload();
                }

                using (new EditorGUILayout.HorizontalScope(s_HeaderStyle))
                {
                    EditorGUI.BeginChangeCheck();

                    rect = EditorGUILayout.GetControlRect(GUILayout.MinWidth(k_LineHeight), GUILayout.ExpandHeight(true));
                    m_SearchText = m_SearchField.OnGUI(rect, m_SearchText, s_SearchFieldStyle, s_SearchFieldCancelButtonStyle, s_SearchFieldCancelButtonEmptyStyle);

                    m_GroupMask = EditorGUILayout.MaskField(m_GroupMask, m_GroupNames, s_PopupStyle, GUILayout.Width(100), GUILayout.ExpandHeight(true));

                    m_ShowZero = GUILayout.Toggle(m_ShowZero, "0", s_ToggleStyle, GUILayout.Width(k_LineHeight), GUILayout.ExpandHeight(true));

                    if (EditorGUI.EndChangeCheck())
                    {
                        Reload();
                    }
                }

                using (new EditorGUILayout.HorizontalScope(s_BackgroundStyle))
                {
                    if (rootItem.children.Count > 0)
                    {
                        Traverse.Create<EditorGUI>()
                            .Property("lineHeight")
                            .SetValue(k_RowHeight);

                        rect = EditorGUILayout.GetControlRect(false, totalHeight - 3);
                        rect.min -= new Vector2(5, 2);
                        rect.max += new Vector2(5, 1);
                        OnGUI(rect);

                        Traverse.Create<EditorGUI>()
                            .Property("lineHeight")
                            .SetValue(EditorGUIUtility.singleLineHeight);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("List is Empty", GUILayout.Height(k_RowHeight - 3));
                    }
                }

                EditorGUILayout.Space();
            }

            private void UpdateBlendShapeGroups()
            {
                m_BlendShapeGroups.Clear();
                m_BlendShapeGroups.Add(new BlendShapeGroup(k_DefaultGroupName));

                for (var i = 0; m_Mesh != null && i < m_Mesh.blendShapeCount; i++)
                {
                    var shape = new BlendShape(m_Mesh, i);
                    var match = Regex.Match(shape.Name, k_GroupNamePattern);
                    if (match.Success)
                    {
                        m_BlendShapeGroups.Add(new BlendShapeGroup(match.Groups[1].Value));
                    }

                    m_BlendShapeGroups.Last().BlendShapes.Add(shape);
                }

                m_GroupNames = m_BlendShapeGroups
                    .Select(x => x.Name)
                    .ToArray();
                m_GroupMask = ~0;
            }

            protected override TreeViewItem BuildRoot()
            {
                var renderer = m_Property.serializedObject.targetObject as SkinnedMeshRenderer;

                return new TreeViewItem(-1, -1)
                {
                    children = m_BlendShapeGroups
                        .Where((x, i) => (m_GroupMask & 1 << i) != 0)
                        .SelectMany(x => x.BlendShapes)
                        .Where(x => m_ShowZero || m_Mesh != null && x.Index < m_Mesh.blendShapeCount && renderer.GetBlendShapeWeight(x.Index) != 0.0f)
                        .Where(x => m_SearchText.Split().All(y => x.Name.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(x => new Item(x))
                        .ToList<TreeViewItem>(),
                };
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var item = args.item as Item;
                var shape = item.BlendShape;

                var rect = args.rowRect;
                rect.min += new Vector2(5, 0);
                rect.max -= new Vector2(5, 0);

                if (shape.Index < m_Property.arraySize)
                {
                    var prop = m_Property.GetArrayElementAtIndex(shape.Index);
                    var content = EditorGUI.BeginProperty(rect, shape.Content, prop);

                    EditorGUI.BeginChangeCheck();

                    var value = Traverse.Create<EditorGUI>()
                        .Method(nameof(EditorGUI.Slider), rect, content, prop.floatValue, shape.MinWeight, shape.MaxWeight, float.MinValue, float.MaxValue,
                            s_SliderNumberFieldStyle, s_SliderStyle, s_SliderThumbStyle, Texture2D.linearGrayTexture, s_SliderThumbExtentStyle)
                        .GetValue<float>();

                    if (EditorGUI.EndChangeCheck())
                    {
                        prop.floatValue = value;
                    }

                    EditorGUI.EndProperty();
                }
                else
                {
                    EditorGUI.BeginChangeCheck();

                    var value = Traverse.Create<EditorGUI>()
                        .Method(nameof(EditorGUI.Slider), rect, shape.Content, 0.0f, shape.MinWeight, shape.MaxWeight, float.MinValue, float.MaxValue,
                            s_SliderNumberFieldStyle, s_SliderStyle, s_SliderThumbStyle, Texture2D.linearGrayTexture, s_SliderThumbExtentStyle)
                        .GetValue<float>();

                    if (EditorGUI.EndChangeCheck())
                    {
                        m_Property.arraySize = m_Mesh != null ? m_Mesh.blendShapeCount : 0;
                        m_Property.GetArrayElementAtIndex(shape.Index).floatValue = value;
                    }
                }

                if (Event.current.type == EventType.MouseDown && args.rowRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                }
            }
        }

        private static bool IsEnabled
        {
            get => EditorPrefs.GetBool(k_MenuPath);
            set => EditorPrefs.SetBool(k_MenuPath, value);
        }

        [MenuItem(k_MenuPath, true)]
        private static bool ValidateEnabled()
        {
            Menu.SetChecked(k_MenuPath, IsEnabled);
            return true;
        }

        [MenuItem(k_MenuPath, false)]
        private static void ToggleEnabled()
        {
            IsEnabled = !IsEnabled;
            InternalEditorUtility.RepaintAllViews();
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            var harmony = new Harmony(k_PatchId);

            var onBlendShapeUI = AccessTools.Method(s_TargetType, "OnBlendShapeUI");
            harmony.Patch(onBlendShapeUI, new HarmonyMethod(typeof(SkinnedMeshRendererEditorPatcher), nameof(OnBlendShapeUI)));

            AssemblyReloadEvents.beforeAssemblyReload += () => harmony.UnpatchAll();
        }

        private static bool OnBlendShapeUI(UnityEditor.Editor __instance, SerializedProperty ___m_BlendShapeWeights)
        {
            if (!IsEnabled)
            {
                return true;
            }

            // Workaround for errors caused by TreeView.enableItemHovering = true
            if (Traverse.Create(s_GUIViewType)
                .Property("current")
                .GetValue() == null)
            {
                return true;
            }

            if (s_BlendShapesDrawers.Any(x => x.Key == null))
            {
                s_BlendShapesDrawers = s_BlendShapesDrawers
                    .Where(x => x.Key != null)
                    .ToDictionary(x => x.Key, x => x.Value);
            }

            if (!s_BlendShapesDrawers.TryGetValue(__instance, out var drawer))
            {
                s_BlendShapesDrawers.Add(__instance, drawer = new BlendShapesDrawer());
            }

            drawer.Draw(___m_BlendShapeWeights);

            return false;
        }
    }
}
