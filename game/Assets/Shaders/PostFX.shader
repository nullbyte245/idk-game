// Three blit passes driven by PostProcessStack: bright pass, separable gaussian, composite.
//
// The project renders in Gamma color space, so the source texture arrives gamma encoded. Every pass
// that does maths on light decodes to linear first and the composite re-encodes at the very end;
// without that the bloom and the ACES curve would react to code values instead of to light.
Shader "Hidden/NeonRelay/PostFX"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ---------------------------------------------------------------- pass 0: bright pass
        Pass
        {
            Name "BrightPass"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_bright
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Threshold;
            float _Knee;

            fixed4 frag_bright (v2f_img i) : SV_Target
            {
                float3 c = pow(max(tex2D(_MainTex, i.uv).rgb, 0.0), 2.2);
                float brightest = max(c.r, max(c.g, c.b));
                float soft = saturate((brightest - _Threshold) / max(_Knee, 0.0001));
                return fixed4(c * soft * soft, 1.0);
            }
            ENDCG
        }

        // ---------------------------------------------------------------- pass 1: separable blur
        Pass
        {
            Name "Blur"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_blur
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float2 _BlurDir;
            float _BlurSpread;

            fixed4 frag_blur (v2f_img i) : SV_Target
            {
                // Weights sum to 1.0, so the taps need no normalisation.
                float2 step = _BlurDir * _MainTex_TexelSize.xy * _BlurSpread;
                float3 acc = tex2D(_MainTex, i.uv).rgb * 0.22702703;
                acc += (tex2D(_MainTex, i.uv + step).rgb + tex2D(_MainTex, i.uv - step).rgb) * 0.19459459;
                acc += (tex2D(_MainTex, i.uv + step * 2.0).rgb + tex2D(_MainTex, i.uv - step * 2.0).rgb) * 0.12162162;
                acc += (tex2D(_MainTex, i.uv + step * 3.0).rgb + tex2D(_MainTex, i.uv - step * 3.0).rgb) * 0.05405405;
                acc += (tex2D(_MainTex, i.uv + step * 4.0).rgb + tex2D(_MainTex, i.uv - step * 4.0).rgb) * 0.01621622;
                return fixed4(acc, 1.0);
            }
            ENDCG
        }

        // ---------------------------------------------------------------- pass 2: composite
        Pass
        {
            Name "Composite"
            CGPROGRAM
            #pragma vertex vert_composite
            #pragma fragment frag_composite
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct app_composite
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f_composite
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f_composite vert_composite (app_composite v)
            {
                v2f_composite o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            sampler2D _MainTex;
            sampler2D _BloomTex;
            sampler2D _CameraDepthTexture;
            float4 _MainTex_TexelSize;

            float _Exposure;
            float _Contrast;
            float _Saturation;
            float _BloomStrength;
            float _Vignette;
            float _Grain;
            float _Chromatic;
            float _AOStrength;
            float _AORadius;
            float _AOFalloff;
            // uv extent of one metre at one metre from the eye, per axis. Built in C# because the
            // projection matrix bound during OnRenderImage is the blit quad's, not the camera's.
            float2 _AOScale;

            float3 aces (float3 x)
            {
                // Narkowicz' fit of the ACES filmic curve.
                x *= 0.6;
                return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
            }

            float hash12 (float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float eye_depth (float4 screen, float2 offset)
            {
                float4 proj = screen;
                proj.xy += offset * screen.w;
                return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(proj)));
            }

            // Screen space occlusion from depth alone: a concave junction has neighbours further
            // from the eye than the pixel it sits in, a convex edge has neighbours closer. Only the
            // first case darkens, so corners where a wall meets the floor get a contact shadow.
            float ambient_occlusion (float4 screen)
            {
                if (_AOStrength <= 0.0) return 1.0;

                float center = eye_depth(screen, float2(0.0, 0.0));
                float2 radius = _AOScale * (_AORadius / max(center, 0.05));

                float occ = 0.0;
                occ += saturate((eye_depth(screen, float2(radius.x, 0.0)) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, float2(-radius.x, 0.0)) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, float2(0.0, radius.y)) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, float2(0.0, -radius.y)) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, radius * 0.7) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, -radius * 0.7) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, float2(radius.x, radius.y * 0.7)) - center) / _AOFalloff);
                occ += saturate((eye_depth(screen, float2(-radius.x, -radius.y * 0.7)) - center) / _AOFalloff);
                occ *= 0.125;

                // The sky sits on the far plane and would read as one enormous concavity.
                float distanceFade = saturate(1.0 - center / 40.0);
                return 1.0 - _AOStrength * occ * distanceFade;
            }

            fixed4 frag_composite (v2f_composite i) : SV_Target
            {
                float4 screen = i.screen;
                #if UNITY_UV_STARTS_AT_TOP
                // A flipped source target flips the depth texture with it.
                if (_MainTex_TexelSize.y < 0.0) screen.y = screen.w - screen.y;
                #endif

                float2 fromCentre = i.uv - 0.5;
                float r2 = dot(fromCentre, fromCentre);

                // Fringing grows quadratically from the centre, so the reticle area stays clean.
                float2 fringe = fromCentre * (_Chromatic * r2);
                float3 src;
                src.r = tex2D(_MainTex, i.uv + fringe).r;
                src.g = tex2D(_MainTex, i.uv).g;
                src.b = tex2D(_MainTex, i.uv - fringe).b;

                float3 color = pow(max(src, 0.0), 2.2);
                color += tex2D(_BloomTex, i.uv).rgb * _BloomStrength;
                color *= _Exposure;
                color *= ambient_occlusion(screen);

                float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                color = lerp(luma.xxx, color, _Saturation);
                color = aces(color);
                color = (color - 0.5) * _Contrast + 0.5;

                float vignette = saturate(1.0 - _Vignette * smoothstep(0.16, 0.62, r2));
                color *= vignette;

                // Grain belongs after the encode: in linear it would only touch the mid tones.
                float3 encoded = pow(max(color, 0.0), 1.0 / 2.2);
                float noise = hash12(i.uv * _ScreenParams.xy * 0.5 + _Time.y * 11.0);
                encoded += (noise - 0.5) * _Grain;
                return fixed4(encoded, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
