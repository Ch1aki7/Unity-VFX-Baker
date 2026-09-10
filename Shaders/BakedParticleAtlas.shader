Shader "Custom/BakedParticleAtlas"
{
    Properties
    {
        [MainTexture] _MainTex ("Atlas", 2D) = "white" {}
        _AnimTex ("Animation Data", 2D) = "black" {}
        _FrameNum ("Frame Count", Float) = 1
        _FrameTime ("Frame Time", Float) = 0.0333333
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Toggle] _ZWrite ("Z Write", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull [_Cull]
        ZWrite [_ZWrite]

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _AnimTex;
            float4 _MainTex_TexelSize;
            float _FrameNum;
            float _FrameTime;

            UNITY_INSTANCING_BUFFER_START(VFXInstance)
                // x = start time, y = instance alpha
                UNITY_DEFINE_INSTANCED_PROP(float4, _AnimationSetting)
            UNITY_INSTANCING_BUFFER_END(VFXInstance)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float alpha : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;

                float4 setting = UNITY_ACCESS_INSTANCED_PROP(VFXInstance, _AnimationSetting);
                float maxFrame = max(_FrameNum - 1.0, 0.0);
                float elapsed = clamp(_Time.y - setting.x, 0.0, maxFrame * _FrameTime);
                float frame = _FrameTime > 0.000001 ? floor(elapsed / _FrameTime) : 0.0;
                frame = clamp(frame, 0.0, maxFrame);

                float2 animUV = float2((frame + 0.5) / max(_FrameNum, 1.0), 0.25);

                // Row 0: RG = frame center, BA = frame scale.
                // Every asset uses the same 1x1 quad; frame geometry comes from this row.
                float4 transformData = tex2Dlod(_AnimTex, float4(animUV, 0, 0));
                float2 localXY = transformData.xy + transformData.zw * (v.uv - 0.5);

                // Camera-facing billboard. Object translation and scale are preserved,
                // while object rotation is replaced by the active camera basis.
                float3 objectOriginWS =
                    mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float objectScaleX = length(float3(
                    unity_ObjectToWorld._m00,
                    unity_ObjectToWorld._m10,
                    unity_ObjectToWorld._m20));
                float objectScaleY = length(float3(
                    unity_ObjectToWorld._m01,
                    unity_ObjectToWorld._m11,
                    unity_ObjectToWorld._m21));
                float3 cameraRightWS = normalize(float3(
                    UNITY_MATRIX_I_V._m00,
                    UNITY_MATRIX_I_V._m10,
                    UNITY_MATRIX_I_V._m20));
                float3 cameraUpWS = normalize(float3(
                    UNITY_MATRIX_I_V._m01,
                    UNITY_MATRIX_I_V._m11,
                    UNITY_MATRIX_I_V._m21));
                float3 worldPosition =
                    objectOriginWS +
                    cameraRightWS * (localXY.x * objectScaleX) +
                    cameraUpWS * (localXY.y * objectScaleY);
                o.position = mul(UNITY_MATRIX_VP, float4(worldPosition, 1.0));

                // Row 1: RG = atlas UV offset, BA = atlas UV size.
                animUV.y = 0.75;
                float4 uvData = tex2Dlod(_AnimTex, float4(animUV, 0, 0));
                float2 frameMinUV = uvData.xy + _MainTex_TexelSize.xy * 0.5;
                float2 frameSizeUV = max(uvData.zw - _MainTex_TexelSize.xy, 0.0);
                o.uv = frameMinUV + frameSizeUV * v.uv;
                o.alpha = setting.y;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv);
                color.a *= i.alpha;
                return color;
            }
            ENDCG
        }
    }
    FallBack Off
}
