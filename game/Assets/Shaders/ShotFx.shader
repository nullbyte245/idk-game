// Additive heat for everything a shot leaves behind: the flame at the muzzle, the dust puff where a
// round lands and the tracer between them. Unlit on purpose — a lit streak reads as a plastic rod, and
// the sun is meant to be the only thing in the arena that glows on its own.
//
// Vertex colour carries the brightness, so a LineRenderer gradient or a fading quad dims the effect by
// pushing rgb toward black; additive blending means black is literally transparent.
Shader "NeonRelay/Shot FX"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HDR] _Color ("Color", Color) = (1, 1, 1, 1)
        // 1 = round soft blob (flash, puff), 0 = ribbon that fades at its edges across the line (tracer, sparks).
        _Radial ("Radial Mask", Range(0, 1)) = 1
        _Core ("Hot Core", Range(0, 2)) = 0.6
        // A quad has no vertex colour channel, so it takes brightness from _Color alone; a line renderer
        // always supplies one. Off on the glow material, on on the streak material.
        _VertexColor ("Use Vertex Color", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Radial;
            float _Core;
            float _VertexColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0;
                float radial = saturate(1.0 - length(p));
                radial = radial * radial * (3.0 - 2.0 * radial);
                float ribbon = saturate(1.0 - abs(p.y));
                ribbon = pow(ribbon, 1.6);

                float mask = lerp(ribbon, radial, _Radial);
                float4 tex = tex2D(_MainTex, i.uv);
                float3 brightness = lerp(fixed3(1.0, 1.0, 1.0), i.color.rgb, _VertexColor);
                float heat = mask * tex.a * (1.0 + _Core * radial);

                return fixed4(tex.rgb * brightness * _Color.rgb * heat, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
