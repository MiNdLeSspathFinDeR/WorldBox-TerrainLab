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

                _harmony = new Harmony("Vlad.TerrainLab");
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                _ui = GetComponent<TerrainLabUi>();
                if (_ui == null)
                {
                    _ui = gameObject.AddComponent<TerrainLabUi>();
                }

                _ui.Initialize(GetDeclaration());
                LogInfo("GIS window and WBXGEO persistence initialized.");
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
