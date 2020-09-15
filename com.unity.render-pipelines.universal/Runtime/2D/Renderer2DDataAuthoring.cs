using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Experimental.Rendering.Universal
{
    public partial class Renderer2DData
    {
#if UNITY_EDITOR
        [SerializeField]
        Renderer2DDefaultMaterialType m_DefaultMaterialType = Renderer2DDefaultMaterialType.Lit;

        [SerializeField, Reload("Runtime/Materials/Sprite-Lit-Default.mat")]
        Material m_DefaultCustomMaterial = null;

        [SerializeField, Reload("Runtime/Materials/Sprite-Lit-Default.mat")]
        Material m_DefaultLitMaterial = null;

        [SerializeField, Reload("Runtime/Materials/Sprite-Unlit-Default.mat")]
        Material m_DefaultUnlitMaterial = null;

        [SerializeField, Reload("Runtime/Materials/Sprite-Mask.mat")]
        Material m_DefaultSpriteMaskMaterial = null;

        internal override Shader GetDefaultShader()
        {
            return Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
        }

        internal override Material GetDefaultMaterial(DefaultMaterialType materialType)
        {
            if (materialType == DefaultMaterialType.Sprite || materialType == DefaultMaterialType.Particle)
            {
                if (m_DefaultMaterialType == Renderer2DDefaultMaterialType.Lit)
                    return m_DefaultLitMaterial;
                else if (m_DefaultMaterialType == Renderer2DDefaultMaterialType.Unlit)
                    return m_DefaultUnlitMaterial;
                else
                    return m_DefaultCustomMaterial;
            }
            else if (materialType == DefaultMaterialType.SpriteMask)
            {
                return m_DefaultSpriteMaskMaterial;
            }

            return null;
        }

        private void OnEnableInEditor()
        {
            // Provide a list of suggested texture property names to Sprite Editor via EditorPrefs.
            const string suggestedNamesKey = "SecondarySpriteTexturePropertyNames";
            const string maskTex = "_MaskTex";
            const string normalMap = "_NormalMap";
            string suggestedNamesPrefs = EditorPrefs.GetString(suggestedNamesKey);

            if (string.IsNullOrEmpty(suggestedNamesPrefs))
                EditorPrefs.SetString(suggestedNamesKey, maskTex + "," + normalMap);
            else
            {
                if (!suggestedNamesPrefs.Contains(maskTex))
                    suggestedNamesPrefs += ("," + maskTex);

                if (!suggestedNamesPrefs.Contains(normalMap))
                    suggestedNamesPrefs += ("," + normalMap);

                EditorPrefs.SetString(suggestedNamesKey, suggestedNamesPrefs);
            }

            ResourceReloader.TryReloadAllNullIn(this, UniversalRenderPipelineAsset.packagePath);
            ResourceReloader.TryReloadAllNullIn(m_PostProcessData, UniversalRenderPipelineAsset.packagePath);
        }

        private void Awake()
        {
            if (m_LightBlendStyles != null)
                return;

            m_LightBlendStyles = new Light2DBlendStyle[4];

            for (int i = 0; i < m_LightBlendStyles.Length; ++i)
            {
                m_LightBlendStyles[i].name = "Blend Style " + i;
                m_LightBlendStyles[i].blendMode = Light2DBlendStyle.BlendMode.Multiply;
                m_LightBlendStyles[i].renderTextureScale = 0.5f;
            }
        }
#endif
    }
}
