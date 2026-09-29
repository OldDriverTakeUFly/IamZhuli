using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace IamZhuli.Unity.EditorTools
{
    /// <summary>
    /// 一键搭建场景:生成 PanelSettings(含默认主题)+ GameHost + UIDocument(Main.uxml) + MainUI,
    /// 保存为 Assets/Scenes/Main.unity。绕开手写 scene YAML——打开 Unity 后菜单点一次即可开局。
    /// </summary>
    public static class SceneBuilder
    {
        private const string UxmlPath = "Assets/IamZhuli/UI/Main.uxml";
        private const string UssPath = "Assets/IamZhuli/UI/Theme.uss";
        private const string PanelPath = "Assets/IamZhuli/UI/PanelSettings.asset";
        private const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("我是主力/一键搭建场景")]
        public static void Build() => Build(interactive: true);

        /// <summary>搭建场景。interactive=false 供测试调用(不弹窗)。</summary>
        public static void Build(bool interactive)
        {
            // 1. PanelSettings(不存在则创建,挂默认运行时主题)
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                // Unity 6 包内资源路径已变,UnityDefaultRuntimeTheme 不再可寻——
                // 自生成等价 tss(内容与 Unity 自动生成的完全一致)
                var themePath = "Assets/IamZhuli/UI/UnityDefaultRuntimeTheme.tss";
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (theme == null)
                {
                    File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
                    AssetDatabase.ImportAsset(themePath);
                    theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                }
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                if (theme != null) panel.themeStyleSheet = theme;
                else Debug.LogWarning("未能生成默认运行时主题,PanelSettings 主题为空(UI 可能无样式)");
                AssetDatabase.CreateAsset(panel, PanelPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                // 取已导入落盘的实例再接线,避免场景保存时引用解析为空(fileID:0);
                // 极端时序下导入未完成则保留内存实例,至少保证当帧赋值非空
                var imported = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
                if (imported != null) panel = imported;
            }

            // 2. 资产校验
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null) { Debug.LogError("缺少 " + UxmlPath); return; }
            if (AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath) == null)
                Debug.LogWarning("缺少 " + UssPath + ",界面将无主题样式");

            // 3. 校验逻辑层 DLL(Plugins 下自动引用)
            try
            {
                var probe = typeof(IamZhuli.Presenter.GameCore);
                Debug.Log("逻辑层 DLL 就绪: GameCore@" + probe.Assembly.GetName().Name);
            }
            catch (System.Exception)
            {
                Debug.LogError("未找到 IamZhuli.Presenter.GameCore——请先在项目根目录执行 bash unity/build-dlls.sh");
                return;
            }

            // 4. 新场景(自带相机/光照;UI Toolkit 全屏 UI 不依赖相机,保留无妨)
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // 5. GameHost(游戏桥) + MainUI(界面控制器)
            var hostObj = new GameObject("GameHost");
            hostObj.AddComponent<GameHost>();

            var uiObj = new GameObject("MainUI");
            var doc = uiObj.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = uxml;
            // Unity 6 编辑器(NewScene/batchmode 下)UIDocument 属性赋值会被预览层丢弃,
            // 兜底直写序列化字段(字段名以场景 YAML 为准:sourceAsset 即 visualTreeAsset)
            var so = new SerializedObject(doc);
            var pPanel = so.FindProperty("m_PanelSettings");
            if (pPanel != null && pPanel.objectReferenceValue == null)
                pPanel.objectReferenceValue = panel;
            var pSource = so.FindProperty("sourceAsset");
            if (pSource != null && pSource.objectReferenceValue == null)
                pSource.objectReferenceValue = uxml;
            so.ApplyModifiedPropertiesWithoutUndo();
            uiObj.AddComponent<MainUI>();

            // 6. 保存
            if (!Directory.Exists("Assets/Scenes")) Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Unity 6:NewScene 状态下 UIDocument 的属性赋值会被编辑器预览层丢弃;
            // 且 OpenScene 触发的资产卸载会把无引用的 panel 实例原生销毁(C# 侧变假 null)。
            // 因此重开场景后一律从磁盘重新加载资产实例再补赋值,二次保存落盘序列化引用。
            var reopened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var panelLive = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            var uxmlLive = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var d in docs)
            {
                if (d.panelSettings == null && panelLive != null) d.panelSettings = panelLive;
                if (d.visualTreeAsset == null && uxmlLive != null) d.visualTreeAsset = uxmlLive;
            }
            EditorSceneManager.SaveScene(reopened, ScenePath);
            scene = reopened;
            if (interactive)
                EditorUtility.DisplayDialog("我是主力", "场景搭建完成:\n" + ScenePath + "\n\n点击 ▶ Play 开始游戏(先选关卡)。", "好");
            Debug.Log("场景搭建完成: " + ScenePath);
        }
    }
}
