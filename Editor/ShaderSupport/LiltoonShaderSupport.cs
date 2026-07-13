#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using nadena.dev.ndmf.proto;
using UnityEditor;
using UnityEngine;
#if MA_LILTOON_PRESENT
using lilToon;
#endif
using Material = UnityEngine.Material;
using Texture = UnityEngine.Texture;
using TextureFormat = UnityEngine.TextureFormat;

namespace nadena.dev.ndmf.platform.resonite
{
    [SuppressMessage("ReSharper", "Unity.PreferAddressByIdToGraphicsParams")]
    internal partial class LiltoonShaderSupport : GenericShaderTranslator
    {
        public LiltoonShaderSupport(TextureAssetImporter textureImporter) : base(textureImporter)
        {
        }
        
        #if MA_LILTOON_PRESENT

        public override bool TryTranslateMaterial(Material material, out proto.Material? protoMat)
        {
            protoMat = null;
            if (!IsLiltoonShader(material.shader)) return false;

            if (material.shader.name.Contains("FakeShadow"))
            {
                protoMat = new()
                {
                    Category = MaterialCategory.FakeShadow
                };
                return true;
            }

            // Clone material as the bake operations are destructive
            material = new Material(material);
            _tempObjects.Add(material);
            
            Material = material;
            foreach (var field in _matPropFields)
            {
                ((lilMaterialProperty)field.GetValue(this)).Bind(this);
            }

            if (!base.TryTranslateMaterial(material, out protoMat)) return false;
            
            if (smoothnessTex.textureValue != null || metallicGlossMap.textureValue != null || reflectionColorTex.textureValue != null)
            {
                BakeMetallicMap(material, protoMat);
            }

            protoMat.Metallic = metallic.floatValue;
            if (material.GetFloat("_ApplyReflection") == 0)
            {
                protoMat.Smoothness = 0;
            }
            else
            {
                protoMat.Smoothness = smoothness.floatValue;
            }
            protoMat.Reflectivity = reflectance.floatValue;

            TranslateShadowSettings(material, protoMat);
            TranslateOutlineSettings(material, protoMat);

            // Update culling/etc settings based on liltoon config
            // TODO: lilToonMulti

            var hasCustomVRCOverride = !string.IsNullOrEmpty(material.GetTag("VRCFallback", false));

            if (!hasCustomVRCOverride)
            {
                if (material.shader.name.Contains("Cutout")) protoMat.BlendMode = BlendMode.Cutout;
                else if (material.shader.name.Contains("Transparent")) protoMat.BlendMode = BlendMode.Alpha;
                else protoMat.BlendMode = BlendMode.Opaque;
            }

            switch ((int)Math.Round(material.GetFloat("_Cull")))
            {
                case 1: protoMat.CullMode = CullMode.Front; break;
                case 2: protoMat.CullMode = CullMode.Back; break;
                default: /* case 0: */ protoMat.CullMode = CullMode.None; break;
            }

            return true;
        }

        private const int ShadowRampWidth = 128;
        private const int ShadowRampGradientHeight = 128;

        private void TranslateShadowSettings(Material material, proto.Material protoMat)
        {
            var shadowEnabled = useShadow.floatValue > 0.5f;
            var maskTexture = shadowEnabled ? shadowStrengthMask.textureValue : null;

            Texture2D ramp;
            if (shadowEnabled)
            {
                ramp = lilToon2Ramp.Convert(material, ShadowRampWidth);
                _tempObjects.Add(ramp);
                if (maskTexture != null)
                {
                    ramp = ApplyShadowStrengthGradient(ramp);
                }
            }
            else
            {
                ramp = CreateSolidWhiteRamp();
            }

            ramp.name = $"{material.name}_ShadowRamp";
            ramp.wrapMode = TextureWrapMode.Clamp;

            if (textureImporter(ramp, null, out var rampId, out _))
            {
                protoMat.ShadowRamp = rampId;
            }

            // An explicit null keeps the backend from binding this field to the shared exemplar.
            protoMat.ShadowRampMask = new() { Id = 0 };
            if (maskTexture != null && textureImporter(maskTexture, maskTexture, out var maskId, out _))
            {
                protoMat.ShadowRampMask = maskId;
            }
        }

        // XiexeToon samples the ramp vertically by ShadowRampMask.R (1.0 = top row). Blending a
        // vertical white gradient (opaque at the bottom row) makes mask values below 1.0
        // progressively disable the shadow, matching liltoon's _ShadowStrengthMask semantics.
        private Texture2D ApplyShadowStrengthGradient(Texture2D baseRamp)
        {
            var srcPixels = baseRamp.GetPixels32();
            var dstPixels = new Color32[ShadowRampWidth * ShadowRampGradientHeight];

            for (var y = 0; y < ShadowRampGradientHeight; y++)
            {
                var whiteAlpha = 255 - Mathf.RoundToInt(y * 255f / (ShadowRampGradientHeight - 1));
                for (var x = 0; x < ShadowRampWidth; x++)
                {
                    dstPixels[y * ShadowRampWidth + x] = BlendTowardsWhite(srcPixels[x], whiteAlpha);
                }
            }

            var dst = new Texture2D(ShadowRampWidth, ShadowRampGradientHeight, TextureFormat.RGBA32, false, false);
            _tempObjects.Add(dst);
            dst.SetPixels32(dstPixels);
            dst.Apply();
            return dst;
        }

        private static Color32 BlendTowardsWhite(Color32 src, int alpha)
        {
            return new Color32(
                (byte)(src.r + ((255 - src.r) * alpha + 127) / 255),
                (byte)(src.g + ((255 - src.g) * alpha + 127) / 255),
                (byte)(src.b + ((255 - src.b) * alpha + 127) / 255),
                255
            );
        }

        private Texture2D CreateSolidWhiteRamp()
        {
            var tex = new Texture2D(ShadowRampWidth, 16, TextureFormat.RGBA32, false, false);
            _tempObjects.Add(tex);
            var pixels = new Color32[ShadowRampWidth * 16];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        private void TranslateOutlineSettings(Material material, proto.Material protoMat)
        {
            var shaderName = material.shader.name;
            var outlineEnabled = lilShaderUtils.IsMultiShaderName(shaderName)
                ? material.GetFloatSafe("_UseOutline", 0f).Value > 0.5f
                : lilShaderUtils.IsOutlineShaderName(shaderName);

            protoMat.Outline = outlineEnabled ? ToonOutlineMode.ToonOutlineLit : ToonOutlineMode.ToonOutlineNone;
            protoMat.OutlineMask = new() { Id = 0 };

            if (!outlineEnabled) return;

            var color = outlineColor.colorValue.ToRPC();
            color.Profile = ColorProfile.SRgb;
            protoMat.OutlineColor = color;
            // XiexeToon's OutlineWidth takes the same 0-1 scale value as liltoon's _OutlineWidth
            // slider (no 0.01 object-space conversion; calibrated visually in Resonite)
            protoMat.OutlineWidth = outlineWidth.floatValue * 1.0f;

            var widthMask = outlineWidthMask.textureValue;
            if (widthMask != null && textureImporter(widthMask, widthMask, out var maskId, out _))
            {
                protoMat.OutlineMask = maskId;
            }
        }

        private void BakeMetallicMap(Material material, proto.Material protoMat)
        {
            Material tmpMat = new Material(Shader.Find("Hidden/NDMF/BakeMetallicGlossReflectionMap"));
            _tempObjects.Add(tmpMat);

            tmpMat.SetTexture("_Smoothness", smoothnessTex.textureValue);
            tmpMat.SetTexture("_Metallic", metallicGlossMap.textureValue);
            tmpMat.SetTexture("_ReflectionColorTex", reflectionColorTex.textureValue);
            tmpMat.SetTextureOffset("_Smoothness", material.GetTextureOffset(smoothnessTex.propertyName));
            tmpMat.SetTextureScale("_Smoothness", material.GetTextureScale(smoothnessTex.propertyName));
            tmpMat.SetTextureOffset("_Metallic", material.GetTextureOffset(metallicGlossMap.propertyName));
            tmpMat.SetTextureScale("_Metallic", material.GetTextureScale(metallicGlossMap.propertyName));
            tmpMat.SetTextureOffset("_ReflectionColorTex", material.GetTextureOffset(reflectionColorTex.propertyName));
            tmpMat.SetTextureScale("_ReflectionColorTex", material.GetTextureScale(reflectionColorTex.propertyName));
            tmpMat.SetColor("_ReflectionColor", reflectionColor.colorValue);

            Texture2D? newTex = null;
            RunBake(ref newTex, tmpMat, smoothnessTex.textureValue, metallicGlossMap.textureValue,
                reflectionColorTex.textureValue);
            newTex.name = $"{material.name}_MetallicGlossReflectionMap";
            if (textureImporter(newTex, null, out var id, out _))
            {
                protoMat.SmoothnessMetallicReflectionMap = id;
            }
            _tempObjects.Add(newTex);
        }

        protected override bool GetOrBakeMainTexture(Material mat, out Texture? mainTex, out Texture? importerReference, out Vector2 scale,
            out Vector2 offset)
        {
            importerReference = this.mainTex.textureValue;
            scale = offset = Vector2.zero;
            
            mainTex = AutoBakeMainTexture(mat);
            if (mainTex == null) return false;
            
            if (mainTex != null && importerReference != null) mainTex.name = importerReference.name;
            scale = mat.GetTextureScale(this.mainTex.propertyName);
            offset = mat.GetTextureOffset(this.mainTex.propertyName);

            var alphaMask = mat.GetTexture("_AlphaMask");
            if (alphaMask != null)
            {
                var newTex = BakeMainTexAlpha(mat, mainTex, alphaMask);
                if (newTex != null)
                {
                    mainTex = newTex;
                    mainTex.name = alphaMask.name;
                }
            }

            return mainTex != null;
        }

        protected override bool GetMatcapTexture(Material mat, out Texture? matcapTex, out Texture? importerReference)
        {
            // If a matcap mask is enabled, we can't replicate it with XSToon, so just disable matcap entirely
            if (mat.HasTexture("_MatCapBlendMask") && mat.GetTexture("_MatCapBlendMask") != null)
            {
                matcapTex = importerReference = null;
                return false;
            }
            
            return base.GetMatcapTexture(mat, out matcapTex, out importerReference);
        }
        #endif
    }
}