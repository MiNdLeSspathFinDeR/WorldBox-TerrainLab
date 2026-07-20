using System;
using System.Reflection;
using HarmonyLib;
using NeoModLoader.api;

namespace TerrainLab
{
    public sealed class TerrainLabMod : BasicMod<TerrainLabMod>
    {
        private Harmony _harmony;
        private TerrainLabRuntime _runtime;
        private TerrainLabEditor _editor;
        private TerrainLabUi _ui;

        protected override void OnModLoad()
        {
        }

        public override void Init()
        {
            base.Init();

            try
            {
                _runtime = GetComponent<TerrainLabRuntime>();
                if (_runtime == null)
                {
                    _runtime = gameObject.AddComponent<TerrainLabRuntime>();
                }

                _runtime.Initialize();

                _editor = GetComponent<TerrainLabEditor>();
                if (_editor == null)
                {
                    _editor = gameObject.AddComponent<TerrainLabEditor>();
                }

                _editor.Initialize(_runtime);

                _harmony = new Harmony("Vlad.TerrainLab");
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                _ui = GetComponent<TerrainLabUi>();
                if (_ui == null)
                {
                    _ui = gameObject.AddComponent<TerrainLabUi>();
                }

                _ui.Initialize(GetDeclaration(), _editor);
                LogInfo("TerrainLab 1.18.1 GIS runtime initialized.");
            }
            catch (Exception exception)
            {
                LogError("Failed to initialize TerrainLab UI: " + exception);
                throw;
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
