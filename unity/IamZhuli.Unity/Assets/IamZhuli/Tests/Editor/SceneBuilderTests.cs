using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace IamZhuli.Tests
{
    /// <summary>
    /// SceneBuilder 场景搭建测试:一键生成后 GameHost/MainUI/UIDocument 全部就位且资产已接线。
    /// </summary>
    public sealed class SceneBuilderTests
    {
        [Test]
        public void SceneBuilder_ProducesWiredScene()
        {
            EditorTools.SceneBuilder.Build(interactive: false);

            Assert.IsTrue(File.Exists("Assets/Scenes/Main.unity"), "场景文件应保存");

            var host = GameObject.Find("GameHost");
            Assert.IsNotNull(host, "场景应有 GameHost");
            Assert.IsNotNull(host.GetComponent<GameHost>(), "GameHost 应挂 GameHost 脚本");

            var ui = GameObject.Find("MainUI");
            Assert.IsNotNull(ui, "场景应有 MainUI");
            Assert.IsNotNull(ui.GetComponent<MainUI>(), "MainUI 应挂 MainUI 脚本");
            var doc = ui.GetComponent<UIDocument>();
            Assert.IsNotNull(doc, "MainUI 应挂 UIDocument");
            Assert.IsNotNull(doc.panelSettings, "UIDocument 应接线 PanelSettings");
            Assert.IsNotNull(doc.visualTreeAsset, "UIDocument 应接线 Main.uxml");
            Assert.IsNotNull(doc.panelSettings.themeStyleSheet, "PanelSettings 应有运行时主题");

            // 清理:回到空场景,避免影响后续测试
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
