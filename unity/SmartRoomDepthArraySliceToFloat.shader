Shader "Hidden/SmartRoom/DepthArraySliceToFloat_Resource"
{
    Properties
    {
        _SourceDepthArray ("Source Depth Array", 2DArray) = "" {}
        _ArraySlice ("Array Slice", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "HLSLSupport.cginc"

            UNITY_DECLARE_TEX2DARRAY(_SourceDepthArray);
            float _ArraySlice;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 uv = float3(i.uv, _ArraySlice);
                float depthValue = UNITY_SAMPLE_TEX2DARRAY(_SourceDepthArray, uv).r;
                return float4(depthValue, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
