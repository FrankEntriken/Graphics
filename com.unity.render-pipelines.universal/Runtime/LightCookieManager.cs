//#define VERBOSE_DEBUG

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.Experimental.Rendering;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal
{
    internal class LightCookieManager : IDisposable
    {
        static class ShaderProperty
        {
            public static readonly int mainLightTexture = Shader.PropertyToID("_MainLightCookieTexture");
            public static readonly int mainLightWorldToLight = Shader.PropertyToID("_MainLightWorldToLight");
            public static readonly int mainLightCookieTextureFormat = Shader.PropertyToID("_MainLightCookieTextureFormat");

            public static readonly int additionalLightsCookieAtlasTexture = Shader.PropertyToID("_AdditionalLightsCookieAtlasTexture");
            public static readonly int additionalLightsCookieAtlasTextureFormat = Shader.PropertyToID("_AdditionalLightsCookieAtlasTextureFormat");

            public static readonly int additionalLightsCookieEnableBits = Shader.PropertyToID("_AdditionalLightsCookieEnableBits");

            public static readonly int additionalLightsCookieAtlasUVRectBuffer = Shader.PropertyToID("_AdditionalLightsCookieAtlasUVRectBuffer");
            public static readonly int additionalLightsCookieAtlasUVRects = Shader.PropertyToID("_AdditionalLightsCookieAtlasUVRects");

            // TODO: these should be generic light property
            public static readonly int additionalLightsWorldToLightBuffer = Shader.PropertyToID("_AdditionalLightsWorldToLightBuffer");
            public static readonly int additionalLightsLightTypeBuffer = Shader.PropertyToID("_AdditionalLightsLightTypeBuffer");

            public static readonly int additionalLightsWorldToLights = Shader.PropertyToID("_AdditionalLightsWorldToLights");
            public static readonly int additionalLightsLightTypes = Shader.PropertyToID("_AdditionalLightsLightTypes");
        }

        private enum LightCookieShaderFormat
        {
            RGB = 0,
            Alpha = 1,
            Red = 2
        }

        public struct Settings
        {
            public struct AtlasSettings
            {
                public Vector2Int resolution;
                public GraphicsFormat format;
                public bool useMips;

                public bool isPow2 => Mathf.IsPowerOfTwo(resolution.x) && Mathf.IsPowerOfTwo(resolution.y);
                public bool isSquare => resolution.x == resolution.y;
            }

            public AtlasSettings atlas;
            public int maxAdditionalLights;        // UniversalRenderPipeline.maxVisibleAdditionalLights;
            public float cubeOctahedralSizeScale;  // Cube octahedral projection size scale.
            public bool useStructuredBuffer;       // RenderingUtils.useStructuredBuffer

            public static Settings GetDefault()
            {
                Settings s;
                s.atlas.resolution = new Vector2Int(1024, 1024);
                s.atlas.format = GraphicsFormat.R8G8B8A8_SRGB;
                s.atlas.useMips = false; // TODO: set to true, make sure they work proper first! Disable them for now...
                s.maxAdditionalLights = UniversalRenderPipeline.maxVisibleAdditionalLights;

                // (Scale * W * Scale * H) / (6 * WH) == (Scale^2 / 6)
                // 1: 1/6 = 16%, 2: 4/6 = 66%, 4: 16/6 == 266% of cube pixels
                // 100% cube pixels == sqrt(6) ~= 2.45f --> 2.5;
                s.cubeOctahedralSizeScale = s.atlas.useMips && s.atlas.isPow2 ? 2.0f : 2.5f;
                s.useStructuredBuffer = RenderingUtils.useStructuredBuffer;
                return s;
            }
        }

        private struct Sorting
        {
            public static void QuickSort<T>(T[] data, Func<T, T, int> compare)
            {
                QuickSort<T>(data, 0, data.Length - 1, compare);
            }

            // A non-allocating predicated sub-array quick sort.
            // NOTE: Similar to UnityEngine.Rendering.CoreUnsafeUtils.QuickSort in CoreUnsafeUtils.cs,
            // se should see if these could be merged in the future.
            // For example: Sorting.QuickSort(test, 0, test.Length - 1, (int a, int b) => a - b);
            public static void QuickSort<T>(T[] data, int start, int end, Func<T, T, int> compare)
            {
                int diff = end - start;
                if (diff < 1)
                    return;
                if (diff < 8)
                {
                    InsertionSort(data, start, end, compare);
                    return;
                }

                Assertions.Assert.IsTrue((uint)start < data.Length);
                Assertions.Assert.IsTrue((uint)end   < data.Length); // end == inclusive

                // For Recursion
                if (start < end)
                {
                    int pivot = Partition<T>(data, start, end, compare);

                    if (pivot >= 1)
                        QuickSort<T>(data, start, pivot, compare);

                    if (pivot + 1 < end)
                        QuickSort<T>(data, pivot + 1, end, compare);
                }
            }

            static T Median3Pivot<T>(T[] data, int start, int pivot, int end, Func<T, T, int> compare)
            {
                void Swap(int a, int b)
                {
                    var tmp = data[a];
                    data[a] = data[b];
                    data[b] = tmp;
                }

                if (compare(data[end], data[start]) < 0) Swap(start, end);
                if (compare(data[pivot], data[start]) < 0) Swap(start, pivot);
                if (compare(data[end], data[pivot]) < 0) Swap(pivot, end);
                return data[pivot];
            }

            static int Partition<T>(T[] data, int start, int end, Func<T, T, int> compare)
            {
                int diff = end - start;
                int pivot = start + diff / 2;

                //var pivotValue = data[pivot];
                var pivotValue = Median3Pivot(data, start, pivot, end, compare);

                while (true)
                {
                    while (compare(data[start], pivotValue) < 0) ++start;
                    while (compare(data[end], pivotValue) > 0) --end;

                    if (start >= end)
                    {
                        return end;
                    }

                    var tmp = data[start];
                    data[start++] = data[end];
                    data[end--] = tmp;
                }
            }

            // A non-allocating predicated sub-array insertion sort.
            // Stable
            static public void InsertionSort<T>(T[] data, int start, int end, Func<T, T, int> compare)
            {
                Assertions.Assert.IsTrue((uint)start < data.Length);
                Assertions.Assert.IsTrue((uint)end < data.Length);

                for (int i = start + 1; i < end + 1; i++)
                {
                    var iData = data[i];
                    int j = i - 1;
                    while (j >= 0 && compare(iData, data[j]) < 0)
                    {
                        data[j + 1] = data[j];
                        j--;
                    }
                    data[j + 1] = iData;
                }
            }
        }

        private struct LightCookieMapping
        {
            public ushort visibleLightIndex; // Index into visible light (src)
            public ushort lightBufferIndex;  // Index into light shader data buffer (dst)
            public Light legacyLight; // Cached legacy light for the visibleLightIndex to avoid multiple copies on get from native array

            public static Func<LightCookieMapping, LightCookieMapping, int> s_CompareByCookieSize = (LightCookieMapping a, LightCookieMapping b) =>
            {
                int a2 = a.legacyLight.cookie.width * a.legacyLight.cookie.height;
                int b2 = b.legacyLight.cookie.width * b.legacyLight.cookie.height;
                return b2 - a2;
            };

            public static Func<LightCookieMapping, LightCookieMapping, int> s_CompareByBufferIndex = (LightCookieMapping a, LightCookieMapping b) =>
            {
                return a.lightBufferIndex - b.lightBufferIndex;
            };
        }

        private readonly struct WorkSlice<T>
        {
            private readonly T[] m_Data;
            private readonly int m_Start;
            private readonly int m_Length;

            public WorkSlice(T[] src, int srcLen = -1) : this(src, 0, srcLen) {}

            public WorkSlice(T[] src, int srcStart, int srcLen = -1)
            {
                m_Data = src;
                m_Start = srcStart;
                m_Length = (srcLen < 0) ? src.Length : Math.Min(srcLen, src.Length);
                Assertions.Assert.IsTrue(m_Start + m_Length <= capacity);
            }

            public T this[int index]
            {
                get => m_Data[m_Start + index];
                set => m_Data[m_Start + index] = value;
            }

            public int length => m_Length;
            public int capacity => m_Data.Length;

            public void Sort(Func<T, T, int> compare)
            {
                if (m_Length > 1)
                    Sorting.QuickSort(m_Data, m_Start, m_Start + m_Length - 1, compare);
            }
        }

        // Persistent work/temp memory of [] data.
        private class WorkMemory
        {
            public LightCookieMapping[] lightMappings;
            public Vector4[] uvRects;

            public void Resize(int size)
            {
                if (size <= lightMappings?.Length)
                    return;

                // Avoid allocs on every tiny size change.
                size = Math.Max(size, ((size + 15) / 16) * 16);

                lightMappings = new LightCookieMapping[size];
                uvRects = new Vector4[size];
            }
        }

        private struct ShaderBitArray
        {
            const int k_BitsPerElement = 32;
            const int k_ElementShift = 5;
            const int k_ElementMask = (1 << k_ElementShift) - 1;

            private float[] m_Data;

            public int elemLength => m_Data == null ? 0 : m_Data.Length;
            public int bitCapacity => elemLength * k_BitsPerElement;
            public float[] data => m_Data;

            public void Resize(int bitCount)
            {
                if (bitCapacity > bitCount)
                    return;

                int newElemCount = ((bitCount + (k_BitsPerElement - 1)) / k_BitsPerElement);
                if (newElemCount == m_Data?.Length)
                    return;

                var newData = new float[newElemCount];
                if (m_Data != null)
                {
                    for (int i = 0; i < m_Data.Length; i++)
                        newData[i] = m_Data[i];
                }
                m_Data = newData;
            }

            public void Clear()
            {
                for (int i = 0; i < m_Data.Length; i++)
                    m_Data[i] = 0;
            }

            private void GetElementIndexAndBitOffset(int index, out int elemIndex, out int bitOffset)
            {
                elemIndex = index >> k_ElementShift;
                bitOffset = index & k_ElementMask;
            }

            public bool this[int index]
            {
                get
                {
                    GetElementIndexAndBitOffset(index, out var elemIndex, out var bitOffset);

                    unsafe
                    {
                        fixed(float* floatData = m_Data)
                        {
                            uint* uintElem = (uint*)&floatData[elemIndex];
                            bool val = ((*uintElem) & (1u << bitOffset)) != 0u;
                            return val;
                        }
                    }
                }
                set
                {
                    GetElementIndexAndBitOffset(index, out var elemIndex, out var bitOffset);
                    unsafe
                    {
                        fixed(float* floatData = m_Data)
                        {
                            uint* uintElem = (uint*)&floatData[elemIndex];
                            if (value == true)
                                *uintElem = (*uintElem) | (1u << bitOffset);
                            else
                                *uintElem = (*uintElem) & ~(1u << bitOffset);
                        }
                    }
                }
            }

            public override string ToString()
            {
                unsafe
                {
                    Debug.Assert(bitCapacity < 4096, "Bit string too long! It was truncated!");
                    int len = Math.Min(bitCapacity, 4096);
                    byte* buf = stackalloc byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        buf[i] = (byte)(this[i] ? '1' : '0');
                    }

                    return new string((sbyte*)buf, 0, len, System.Text.Encoding.UTF8);
                }
            }
        }

        /// Must match light data layout.
        private class LightCookieShaderData : IDisposable
        {
            int  m_Size = 0;
            bool m_UseStructuredBuffer;

            // Shader data CPU arrays, used to upload the data to GPU
            Matrix4x4[] m_WorldToLightCpuData;
            Vector4[] m_AtlasUVRectCpuData;
            float[] m_LightTypeCpuData;
            ShaderBitArray m_CookieEnableBitsCpuData;

            // Compute buffer counterparts for the CPU data
            ComputeBuffer m_WorldToLightBuffer;    // TODO: WorldToLight matrices should be general property of lights!!
            ComputeBuffer m_AtlasUVRectBuffer;
            ComputeBuffer m_LightTypeBuffer;

            public Matrix4x4[] worldToLights  => m_WorldToLightCpuData;
            public ShaderBitArray cookieEnableBits  => m_CookieEnableBitsCpuData;
            public Vector4[] atlasUVRects   => m_AtlasUVRectCpuData;
            public float[] lightTypes     => m_LightTypeCpuData;

            public LightCookieShaderData(int size, bool useStructuredBuffer)
            {
                m_UseStructuredBuffer = useStructuredBuffer;
                Resize(size);
            }

            public void Dispose()
            {
                if (m_UseStructuredBuffer)
                {
                    m_WorldToLightBuffer?.Dispose();
                    m_AtlasUVRectBuffer?.Dispose();
                    m_LightTypeBuffer?.Dispose();
                }
            }

            public void Resize(int size)
            {
                if (size <= m_Size)
                    return;

                if (m_Size > 0)
                    Dispose();

                m_WorldToLightCpuData = new Matrix4x4[size];
                m_AtlasUVRectCpuData = new Vector4[size];
                m_LightTypeCpuData = new float[size];
                m_CookieEnableBitsCpuData.Resize(size);

                if (m_UseStructuredBuffer)
                {
                    m_WorldToLightBuffer = new ComputeBuffer(size, Marshal.SizeOf<Matrix4x4>());
                    m_AtlasUVRectBuffer = new ComputeBuffer(size, Marshal.SizeOf<Vector4>());
                    m_LightTypeBuffer = new ComputeBuffer(size, Marshal.SizeOf<float>());
                }

                m_Size = size;
            }

            public void Apply(CommandBuffer cmd)
            {
                if (m_UseStructuredBuffer)
                {
                    m_WorldToLightBuffer.SetData(m_WorldToLightCpuData);
                    m_AtlasUVRectBuffer.SetData(m_AtlasUVRectCpuData);
                    m_LightTypeBuffer.SetData(m_LightTypeCpuData);

                    cmd.SetGlobalBuffer(ShaderProperty.additionalLightsWorldToLightBuffer, m_WorldToLightBuffer);
                    cmd.SetGlobalBuffer(ShaderProperty.additionalLightsCookieAtlasUVRectBuffer, m_AtlasUVRectBuffer);
                    cmd.SetGlobalBuffer(ShaderProperty.additionalLightsLightTypeBuffer, m_LightTypeBuffer);
                }
                else
                {
                    cmd.SetGlobalMatrixArray(ShaderProperty.additionalLightsWorldToLights, m_WorldToLightCpuData);
                    cmd.SetGlobalVectorArray(ShaderProperty.additionalLightsCookieAtlasUVRects, m_AtlasUVRectCpuData);
                    cmd.SetGlobalFloatArray(ShaderProperty.additionalLightsLightTypes, m_LightTypeCpuData);
                }

                cmd.SetGlobalFloatArray(ShaderProperty.additionalLightsCookieEnableBits, m_CookieEnableBitsCpuData.data);
            }
        }

        // i.e. (0, 1) uv == (-0.5, 0.5) world area instead of the (0,1) world area.
        static readonly Matrix4x4 s_DirLightProj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);

        Texture2DAtlas m_AdditionalLightsCookieAtlas;
        LightCookieShaderData m_AdditionalLightsCookieShaderData;

        readonly Settings m_Settings;
        WorkMemory m_WorkMem;

        // Mapping: map[visibleLightIndex] = ShaderDataIndex
        // Mostly used by deferred rendering.
        int[] m_VisibleLightIndexToShaderDataIndex;

        // Parameters for rescaling cookies to fit into the atlas.
        const int k_MaxCookieSizeDivisor = 16;
        int  m_CookieSizeDivisor = 1;
        uint m_PrevCookieRequestPixelCount = 0xFFFFFFFF;
        HashSet<int> m_UniqueCookieTextureIDs;

        internal bool IsKeywordLightCookieEnabled { get; private set; }

        public LightCookieManager(ref Settings settings)
        {
            m_Settings = settings;
            m_WorkMem = new WorkMemory();
        }

        void InitAdditionalLights(int size)
        {
            if (m_Settings.atlas.useMips && m_Settings.atlas.isPow2)
            {
                // TODO: MipMaps still have sampling artifacts. FIX FIX

                // Supports mip padding for correct filtering at the edges.
                m_AdditionalLightsCookieAtlas = new PowerOfTwoTextureAtlas(
                    m_Settings.atlas.resolution.x,
                    4,
                    m_Settings.atlas.format,
                    FilterMode.Bilinear,
                    "Universal Light Cookie Pow2 Atlas",
                    true);
            }
            else
            {
                // No mip padding support.
                m_AdditionalLightsCookieAtlas = new Texture2DAtlas(
                    m_Settings.atlas.resolution.x,
                    m_Settings.atlas.resolution.y,
                    m_Settings.atlas.format,
                    FilterMode.Bilinear,
                    false,
                    "Universal Light Cookie Atlas",
                    false); // to support mips, use Pow2Atlas
            }


            m_AdditionalLightsCookieShaderData = new LightCookieShaderData(size, m_Settings.useStructuredBuffer);
            const int mainLightCount = 1;
            m_VisibleLightIndexToShaderDataIndex = new int[m_Settings.maxAdditionalLights + mainLightCount];

            m_CookieSizeDivisor = 1;
            m_PrevCookieRequestPixelCount = 0xFFFFFFFF;
            m_UniqueCookieTextureIDs = new HashSet<int>();
        }

        public bool isInitialized() => m_AdditionalLightsCookieAtlas != null && m_AdditionalLightsCookieShaderData != null;

        /// <summary>
        /// Release LightCookieManager resources.
        /// </summary>
        public void Dispose()
        {
            m_AdditionalLightsCookieAtlas?.Release();
            m_AdditionalLightsCookieShaderData?.Dispose();
        }

        // by VisibleLight
        public int GetLightCookieShaderDataIndex(int visibleLightIndex)
        {
            if (!isInitialized())
                return -1;
            return m_VisibleLightIndexToShaderDataIndex[visibleLightIndex];
        }

        public void Setup(ScriptableRenderContext ctx, CommandBuffer cmd, ref LightData lightData)
        {
            using var profScope = new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.LightCookies));

            // Main light, 1 directional, bound directly
            bool isMainLightAvailable = lightData.mainLightIndex >= 0;
            if (isMainLightAvailable)
            {
                var mainLight = lightData.visibleLights[lightData.mainLightIndex];
                isMainLightAvailable = SetupMainLight(cmd, ref mainLight);
            }

            // Additional lights, N spot and point lights in atlas
            bool isAdditionalLightsAvailable = lightData.additionalLightsCount > 0;
            if (isAdditionalLightsAvailable)
                isAdditionalLightsAvailable = SetupAdditionalLights(cmd, ref lightData);

            // Main and additional lights are merged into one keyword to reduce variants.
            IsKeywordLightCookieEnabled = isMainLightAvailable || isAdditionalLightsAvailable;
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.LightCookies, IsKeywordLightCookieEnabled);
        }

        bool SetupMainLight(CommandBuffer cmd, ref VisibleLight visibleMainLight)
        {
            var mainLight = visibleMainLight.light;
            var cookieTexture = mainLight.cookie;
            bool isMainLightCookieEnabled = cookieTexture != null;

            if (isMainLightCookieEnabled)
            {
                Matrix4x4 cookieUVTransform = Matrix4x4.identity;
                float cookieFormat = (float)GetLightCookieShaderFormat(cookieTexture.graphicsFormat);

                if (mainLight.TryGetComponent(out UniversalAdditionalLightData additionalLightData))
                    GetLightUVScaleOffset(ref additionalLightData, ref cookieUVTransform);

                Matrix4x4 cookieMatrix = s_DirLightProj * cookieUVTransform *
                    visibleMainLight.localToWorldMatrix.inverse;

                cmd.SetGlobalTexture(ShaderProperty.mainLightTexture, cookieTexture);
                cmd.SetGlobalMatrix(ShaderProperty.mainLightWorldToLight, cookieMatrix);
                cmd.SetGlobalFloat(ShaderProperty.mainLightCookieTextureFormat, cookieFormat);
            }

            return isMainLightCookieEnabled;
        }

        private LightCookieShaderFormat GetLightCookieShaderFormat(GraphicsFormat cookieFormat)
        {
            switch (cookieFormat)
            {
                default:
                    return LightCookieShaderFormat.RGB;
                // A8, A16 GraphicsFormat does not expose yet.
                case (GraphicsFormat)54:
                case (GraphicsFormat)55:
                    return LightCookieShaderFormat.Alpha;
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_UInt:
                case GraphicsFormat.R8_SNorm:
                case GraphicsFormat.R8_SInt:
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16_UInt:
                case GraphicsFormat.R16_SNorm:
                case GraphicsFormat.R16_SInt:
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R32_UInt:
                case GraphicsFormat.R32_SInt:
                case GraphicsFormat.R32_SFloat:
                    return LightCookieShaderFormat.Red;
            }
        }

        private void GetLightUVScaleOffset(ref UniversalAdditionalLightData additionalLightData, ref Matrix4x4 uvTransform)
        {
            Vector2 uvScale  = Vector2.one / additionalLightData.lightCookieSize;
            Vector2 uvOffset = additionalLightData.lightCookieOffset;

            if (Mathf.Abs(uvScale.x) < half.MinValue)
                uvScale.x = Mathf.Sign(uvScale.x) * half.MinValue;
            if (Mathf.Abs(uvScale.y) < half.MinValue)
                uvScale.y = Mathf.Sign(uvScale.y) * half.MinValue;

            uvTransform = Matrix4x4.Scale(new Vector3(uvScale.x, uvScale.y, 1));
            uvTransform.SetColumn(3, new Vector4(-uvOffset.x * uvScale.x, -uvOffset.y * uvScale.y, 0, 1));
        }

        bool SetupAdditionalLights(CommandBuffer cmd, ref LightData lightData)
        {
            int maxLightCount = Math.Min(m_Settings.maxAdditionalLights, lightData.visibleLights.Length);
            m_WorkMem.Resize(maxLightCount);

            int validLightCount = FilterAndValidateAdditionalLights(ref lightData, m_WorkMem.lightMappings);

            // Early exit if no valid cookie lights
            if (validLightCount <= 0)
                return false;

            // Lazy init GPU resources
            if (!isInitialized())
                InitAdditionalLights(validLightCount);

            // Update Atlas
            var validLights = new WorkSlice<LightCookieMapping>(m_WorkMem.lightMappings, validLightCount);
            int validUVRectCount = UpdateAdditionalLightsAtlas(cmd, ref validLights, m_WorkMem.uvRects);

            // Upload shader data
            var validUvRects = new WorkSlice<Vector4>(m_WorkMem.uvRects, validUVRectCount);
            UploadAdditionalLights(cmd, ref lightData, ref validLights, ref validUvRects);

            bool isAdditionalLightsEnabled = validUvRects.length > 0;

            return isAdditionalLightsEnabled;
        }

        int FilterAndValidateAdditionalLights(ref LightData lightData, LightCookieMapping[] validLightMappings)
        {
            int skipMainLightIndex = lightData.mainLightIndex;
            int lightBufferOffset = 0;
            int validLightCount = 0;

            // Warn on dropped lights

            int maxLights = Math.Min(lightData.visibleLights.Length, validLightMappings.Length);
            for (int i = 0; i < maxLights; i++)
            {
                if (i == skipMainLightIndex)
                {
                    lightBufferOffset -= 1;
                    continue;
                }

                Light light = lightData.visibleLights[i].light;

                // Skip lights without a cookie texture
                if (light.cookie == null)
                    continue;

                // Only spot and point lights are supported.
                // Directional lights basically work,
                // but would require a lot of constants for the uv transform parameters
                // and there are very few use cases for multiple global cookies.
                var lightType = lightData.visibleLights[i].lightType;
                if (!(lightType == LightType.Spot ||
                      lightType == LightType.Point))
                {
                    Debug.LogWarning($"Additional {lightType.ToString()} light called '{light.name}' has a light cookie which will not be visible.", light);
                    continue;
                }

                // TODO: check if this is necessary
                // Skip vertex lights, no support
                if (light.renderMode == LightRenderMode.ForceVertex)
                {
                    Debug.LogWarning($"Additional {lightType.ToString()} light called '{light.name}' is a vertex light and its light cookie will not be visible.", light);
                    continue;
                }

                Assertions.Assert.IsTrue(i < ushort.MaxValue);

                LightCookieMapping lp;
                lp.visibleLightIndex = (ushort)i;
                lp.lightBufferIndex  = (ushort)(i + lightBufferOffset);
                lp.legacyLight = light;

                validLightMappings[validLightCount++] = lp;
            }

            return validLightCount;
        }

        int UpdateAdditionalLightsAtlas(CommandBuffer cmd, ref WorkSlice<LightCookieMapping> validLightMappings, Vector4[] textureAtlasUVRects)
        {
#if VERBOSE_DEBUG
            Debug.Log($"---------------------------------------------------------------------------------------");
#endif
            bool atlasReset = false;

            int cookieSizeDivisorApprox = 1;
            uint cookieRequestPixelCount = 0;
            {
                var atlasSize = m_AdditionalLightsCookieAtlas.AtlasTexture.referenceSize;
                uint atlasPixelCount = (uint)atlasSize.x * (uint)atlasSize.y;
                cookieRequestPixelCount = ComputeCookieRequestPixelCount(ref validLightMappings);

                float requestAtlasRatio = cookieRequestPixelCount / (float)atlasPixelCount;
                cookieSizeDivisorApprox = ApproximateCookieSizeDivisor(requestAtlasRatio);
#if VERBOSE_DEBUG
                Debug.Log($"Cookie edge divisor approx {cookieSizeDivisorApprox}, current {m_CookieSizeDivisor}. Request {cookieRequestPixelCount}, Capacity {atlasPixelCount}, Ratio {requestAtlasRatio}");
#endif
            }

            // Try to recover resolution and scale the cookies back up.
            // If the cookies "should fit" and we have less requested pixels
            // than the last time we found the correct divisor (guard against retrying every frame).
            if (cookieSizeDivisorApprox < m_CookieSizeDivisor &&
                cookieRequestPixelCount < m_PrevCookieRequestPixelCount)
            {
#if VERBOSE_DEBUG
                Debug.Log($"Atlas scale up with new divisor {Mathf.Max(cookieSizeDivisorApprox, 1)}. approx {cookieSizeDivisorApprox} < current {m_CookieSizeDivisor}");
#endif
                m_AdditionalLightsCookieAtlas.ResetAllocator();
                atlasReset = true;

                m_CookieSizeDivisor = cookieSizeDivisorApprox;
            }

            // Sort in-place by cookie size for better atlas allocation efficiency
            validLightMappings.Sort(LightCookieMapping.s_CompareByCookieSize);

            // Get cached atlas uv rectangles.
            // If there's new cookies, first try to add at current scaling level.
            // If it doesn't fit, scale down and rebuild the atlas until it fits.
            int uvRectCount = 0;
            for (int i = 0; i < validLightMappings.length; i++)
            {
                var lcm = validLightMappings[i];

                Light light = lcm.legacyLight;
                Texture cookie = light.cookie;

                // NOTE: Currently we blit directly on addition (on atlas fetch cache miss).
                //   This can be costly if there are many resize rebuilds (in case "out-of-space", which shouldn't be a common case).
                //   If rebuilds become a problem, we could try to just allocate and blit only when we have a fully valid allocation.
                //   It would also make sense to do atlas operations only for unique textures and then reuse the results for similar cookies.
                Vector4 uvScaleOffset = Vector4.zero;
                if (cookie.dimension == TextureDimension.Cube)
                {
                    Assertions.Assert.IsTrue(light.type == LightType.Point);
                    uvScaleOffset = FetchCube(cmd, cookie, m_CookieSizeDivisor);
                }
                else
                {
                    Assertions.Assert.IsTrue(light.type == LightType.Spot || light.type == LightType.Directional, "Light type needs 2D texture!");
                    uvScaleOffset = Fetch2D(cmd, cookie, m_CookieSizeDivisor);
                }

                bool isCached = uvScaleOffset != Vector4.zero;
                if (!isCached)
                {
                    if (atlasReset)
                    {
                        if (m_CookieSizeDivisor > k_MaxCookieSizeDivisor)
                        {
                            Debug.LogWarning($"Light cookies atlas is extremely full! Some of the light cookies were discarded. Increase light cookie atlas space or reduce the amount of unique light cookies.");
                            return uvRectCount;
                        }

                        // Reduce cookie size even further and try to rebuild.
                        m_CookieSizeDivisor++;
                        m_PrevCookieRequestPixelCount = cookieRequestPixelCount;
#if VERBOSE_DEBUG
                        Debug.Log($"Increase cookie divisor to: {m_CookieSizeDivisor}");
#endif
                    }
                    else
                    {
                        // Reduce cookie size to approximate value try to rebuild.
                        m_CookieSizeDivisor = Mathf.Max(m_CookieSizeDivisor + 1, cookieSizeDivisorApprox);
                        m_PrevCookieRequestPixelCount = cookieRequestPixelCount;
#if VERBOSE_DEBUG
                        Debug.Log($"Atlas full, increase divisor and try again: {m_CookieSizeDivisor}");
#endif
                    }

                    // Clear atlas and try to rebuild it.
                    // NOTE:

                    // Clear atlas allocs
                    m_AdditionalLightsCookieAtlas.ResetAllocator();
                    atlasReset = true;
#if VERBOSE_DEBUG
                    Debug.Log($"Atlas reset, try again divisor: {m_CookieSizeDivisor}");
#endif

                    // Try to reinsert in priority order
                    uvRectCount = 0;
                    i = -1; // Incremented right after continue
                    continue;
                }

                // Adjust atlas UVs for OpenGL
                if (!SystemInfo.graphicsUVStartsAtTop)
                    uvScaleOffset.w = 1.0f - uvScaleOffset.w - uvScaleOffset.y;
#if VERBOSE_DEBUG
                Debug.Log($"i: {i}, '{cookie.name}' {cookie.width}x{cookie.height} Rect: {uvScaleOffset}");
#endif

                uvRectCount++;
                textureAtlasUVRects[lcm.lightBufferIndex] = uvScaleOffset;
            }

            // Restore linear buffer order for shader data setup and upload.
            // (Alternatively, we could use the mappings to do scattered read/writes).
            validLightMappings.Sort(LightCookieMapping.s_CompareByBufferIndex);

            return uvRectCount;
        }

        uint ComputeCookieRequestPixelCount(ref WorkSlice<LightCookieMapping> validLightMappings)
        {
            uint requestPixelCount = 0;
            m_UniqueCookieTextureIDs.Clear();
            for (int i = 0; i < validLightMappings.length; i++)
            {
                var lcm = validLightMappings[i];
                //Light light = lightData.visibleLights[lcm.visibleLightIndex].light;
                Texture cookie = lcm.legacyLight.cookie;
                int cookieID = cookie.GetInstanceID();

                // Consider only unique textures as atlas request pixels
                if (m_UniqueCookieTextureIDs.Contains(cookieID))
                    continue;
                m_UniqueCookieTextureIDs.Add(cookieID);

                int pixelCookieCount = cookie.width * cookie.height;
                requestPixelCount += (uint)pixelCookieCount;
            }

            return requestPixelCount;
        }

        int ApproximateCookieSizeDivisor(float requestAtlasRatio)
        {
            // (Edge / N)^2 == 1/N^2 of area.
            // Ratio/N^2 == 1, sqrt(Ratio) == N, for "1:1" ratio.
            return (int)Mathf.Max(Mathf.Ceil(Mathf.Sqrt(requestAtlasRatio)), 1);
        }

        Vector4 Fetch2D(CommandBuffer cmd, Texture cookie, int cookieSizeDivisor = 1)
        {
            Assertions.Assert.IsTrue(cookie != null);
            Assertions.Assert.IsTrue(cookie.dimension == TextureDimension.Tex2D);

            Vector4 uvScaleOffset = Vector4.zero;

            var scaledWidth = Mathf.Max(cookie.width / cookieSizeDivisor, 4);
            var scaledHeight = Mathf.Max(cookie.height / cookieSizeDivisor, 4);
            Vector2 scaledCookieSize = new Vector2(scaledWidth, scaledHeight);

            // Check if texture is present
            bool isCached = m_AdditionalLightsCookieAtlas.IsCached(out uvScaleOffset, cookie);
            if (isCached)
            {
                // Update contents if required
                m_AdditionalLightsCookieAtlas.UpdateTexture(cmd, cookie, ref uvScaleOffset);
            }
            else
            {
                // Allocate new
                m_AdditionalLightsCookieAtlas.AllocateTexture(cmd, ref uvScaleOffset, cookie, scaledWidth, scaledHeight);
            }

            AdjustUVRect(ref uvScaleOffset, cookie, ref scaledCookieSize);
            return uvScaleOffset;
        }

        Vector4 FetchCube(CommandBuffer cmd, Texture cookie, int cookieSizeDivisor = 1)
        {
            Assertions.Assert.IsTrue(cookie != null);
            Assertions.Assert.IsTrue(cookie.dimension == TextureDimension.Cube);

            Vector4 uvScaleOffset = Vector4.zero;

            // Scale octahedral projection, so that cube -> oct2D pixel count match better.
            int scaledOctCookieSize = Mathf.Max(ComputeOctahedralCookieSize(cookie) / cookieSizeDivisor, 4);

            // Check if texture is present
            bool isCached = m_AdditionalLightsCookieAtlas.IsCached(out uvScaleOffset, cookie);
            if (isCached)
            {
                // Update contents if required
                m_AdditionalLightsCookieAtlas.UpdateTexture(cmd, cookie, ref uvScaleOffset);
            }
            else
            {
                // Allocate new
                m_AdditionalLightsCookieAtlas.AllocateTexture(cmd, ref uvScaleOffset, cookie, scaledOctCookieSize, scaledOctCookieSize);
            }

            // Cookie size in the atlas might not match CookieTexture size.
            // UVRect adjustment must be done with size in atlas.
            var scaledCookieSize = Vector2.one * scaledOctCookieSize;
            AdjustUVRect(ref uvScaleOffset, cookie, ref scaledCookieSize);
            return uvScaleOffset;
        }

        int ComputeOctahedralCookieSize(Texture cookie)
        {
            // Map 6*WxH pixels into 2W*2H pixels, so 4/6 ratio or 66% of cube pixels.
            int octCookieSize = Math.Max(cookie.width, cookie.height);
            if (m_Settings.atlas.isPow2)
                octCookieSize = octCookieSize * Mathf.NextPowerOfTwo((int)m_Settings.cubeOctahedralSizeScale);
            else
                octCookieSize = (int)(octCookieSize * m_Settings.cubeOctahedralSizeScale + 0.5f);
            return octCookieSize;
        }

        private void AdjustUVRect(ref Vector4 uvScaleOffset, Texture cookie, ref Vector2 cookieSize)
        {
            if (uvScaleOffset != Vector4.zero)
            {
                if (m_Settings.atlas.useMips)
                {
                    // Payload texture is inset
                    var potAtlas = (m_AdditionalLightsCookieAtlas as PowerOfTwoTextureAtlas);
                    var mipPadding = potAtlas == null ? 1 : potAtlas.mipPadding;
                    var paddingSize = Vector2.one * (int)Mathf.Pow(2, mipPadding) * 2;
                    uvScaleOffset = PowerOfTwoTextureAtlas.GetPayloadScaleOffset(cookieSize, paddingSize, uvScaleOffset);
                }
                else
                {
                    // Shrink by 0.5px to clamp sampling atlas neighbors (no padding)
                    ShrinkUVRect(ref uvScaleOffset, 0.5f, ref cookieSize);
                }
            }
        }

        private void ShrinkUVRect(ref Vector4 uvScaleOffset, float amountPixels, ref Vector2 cookieSize)
        {
            var shrinkOffset = Vector2.one * amountPixels / cookieSize;
            var shrinkScale = (cookieSize - Vector2.one * (amountPixels * 2)) / cookieSize;
            uvScaleOffset.z += uvScaleOffset.x * shrinkOffset.x;
            uvScaleOffset.w += uvScaleOffset.y * shrinkOffset.y;
            uvScaleOffset.x *= shrinkScale.x;
            uvScaleOffset.y *= shrinkScale.y;
        }

        void UploadAdditionalLights(CommandBuffer cmd, ref LightData lightData, ref WorkSlice<LightCookieMapping> validLightMappings, ref WorkSlice<Vector4> validUvRects)
        {
            Assertions.Assert.IsTrue(m_AdditionalLightsCookieAtlas != null);
            Assertions.Assert.IsTrue(m_AdditionalLightsCookieShaderData != null);

            cmd.SetGlobalTexture(ShaderProperty.additionalLightsCookieAtlasTexture, m_AdditionalLightsCookieAtlas.AtlasTexture);
            cmd.SetGlobalFloat(ShaderProperty.additionalLightsCookieAtlasTextureFormat, (float)GetLightCookieShaderFormat(m_AdditionalLightsCookieAtlas.AtlasTexture.rt.graphicsFormat));

            // Resize and clear visible light to shader data mapping
            if (m_VisibleLightIndexToShaderDataIndex.Length < lightData.visibleLights.Length)
                m_VisibleLightIndexToShaderDataIndex = new int[lightData.visibleLights.Length];

            // Clear
            int len = Math.Min(m_VisibleLightIndexToShaderDataIndex.Length, lightData.visibleLights.Length);
            for (int i = 0; i < len; i++)
                m_VisibleLightIndexToShaderDataIndex[i] = -1;

            // Resize or init shader data.
            m_AdditionalLightsCookieShaderData.Resize(m_Settings.maxAdditionalLights);

            var worldToLights = m_AdditionalLightsCookieShaderData.worldToLights;
            var cookieEnableBits = m_AdditionalLightsCookieShaderData.cookieEnableBits;
            var atlasUVRects = m_AdditionalLightsCookieShaderData.atlasUVRects;
            var lightTypes = m_AdditionalLightsCookieShaderData.lightTypes;

            // Set all rects to "Invalid" zero area (Vector4.zero).
            Array.Clear(atlasUVRects, 0, atlasUVRects.Length);
            // Set all cookies disabled
            cookieEnableBits.Clear();

            // TODO: technically, we don't need to upload constants again if we knew the lights, atlas (rects) or visible order haven't changed.
            // TODO: but detecting that, might be as time consuming as just doing the work.

            // Fill shader data. Layout should match primary light data for additional lights.
            // Currently it's the same as visible lights, but main light(s) dropped.
            for (int i = 0; i < validUvRects.length; i++)
            {
                int visIndex = validLightMappings[i].visibleLightIndex;
                int bufIndex = validLightMappings[i].lightBufferIndex;

                // Update the mapping
                m_VisibleLightIndexToShaderDataIndex[visIndex] = bufIndex;

                var visLight = lightData.visibleLights[visIndex];

                // Update the (cpu) data
                lightTypes[bufIndex] = (int)visLight.lightType;
                worldToLights[bufIndex] = visLight.localToWorldMatrix.inverse;
                atlasUVRects[bufIndex] = validUvRects[i];
                cookieEnableBits[bufIndex] = true;

                //Debug.Log($"setData: i:{i}, bufIndex:{bufIndex.ToString()} rect:{atlasUVRects[bufIndex].ToString()}");

                // Spot projection
                if (visLight.lightType == LightType.Spot)
                {
                    // VisibleLight.localToWorldMatrix only contains position & rotation.
                    // Multiply projection for spot light.
                    float spotAngle = visLight.spotAngle;
                    float spotRange = visLight.range;
                    var perp = Matrix4x4.Perspective(spotAngle, 1, 0.001f, spotRange);

                    // Cancel embedded camera view axis flip (https://docs.unity3d.com/2021.1/Documentation/ScriptReference/Matrix4x4.Perspective.html)
                    perp.SetColumn(2, perp.GetColumn(2) * -1);

                    // world -> light local -> light perspective
                    worldToLights[bufIndex] = perp * worldToLights[bufIndex];
                }
            }

            // Apply changes and upload to GPU
            m_AdditionalLightsCookieShaderData.Apply(cmd);
        }
    }
}
