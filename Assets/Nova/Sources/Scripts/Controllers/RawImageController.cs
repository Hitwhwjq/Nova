using System;
using UnityEngine;
using UnityEngine.UI;

namespace Nova
{
    [RequireComponent(typeof(RawImage))]
    [ExportCustomType]
    public class RawImageController : MonoBehaviour, IRestorable
    {
        public string luaGlobalName;

        private GameState gameState;
        private RawImage image;

        public Material material
        {
            set => image.material = value;
        }

        public Material sharedMaterial => image.material;

        private void Awake()
        {
            gameState = Utils.FindNovaController().GameState;
            image = GetComponent<RawImage>();

            if (!string.IsNullOrEmpty(luaGlobalName))
            {
                LuaRuntime.Instance.BindObject(luaGlobalName, this, "_G");
                gameState.AddRestorable(this);
            }
        }

        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(luaGlobalName))
            {
                gameState.RemoveRestorable(this);
            }
        }

        #region Restoration

        public string restorableName => luaGlobalName;

        [Serializable]
        private class RawImageControllerRestoreData : IRestoreData
        {
            public readonly MaterialData materialData;

            public RawImageControllerRestoreData(RawImageController parent)
            {
                // Material must be RestorableMaterial or DefaultMaterial
                if (parent.sharedMaterial is RestorableMaterial)
                {
                    materialData = RestorableMaterial.GetRestoreData(parent.sharedMaterial);
                }
                else
                {
                    materialData = null;
                }
            }
        }

        public IRestoreData GetRestoreData()
        {
            return new RawImageControllerRestoreData(this);
        }

        public void Restore(IRestoreData restoreData)
        {
            var data = restoreData as RawImageControllerRestoreData;

            // Material must be RestorableMaterial or DefaultMaterial
            if (data.materialData != null)
            {
                MaterialFactory factory = gameObject.Ensure<MaterialPool>().factory;
                material = RestorableMaterial.Restore(data.materialData, factory);
            }
            else
            {
                material = gameObject.Ensure<MaterialPool>().defaultMaterial;
            }
        }

        #endregion
    }
}
